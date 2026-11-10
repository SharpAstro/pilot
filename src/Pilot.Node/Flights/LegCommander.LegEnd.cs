using Pilot.Contracts;
using static System.Console;

namespace Pilot.Node.Flights;

/// <summary>
/// The overlap check at a leg's end (#564): the planning check (<c>touches</c>) only knows the files the planner foresaw, so when a leg is
/// done the files the flight has actually changed are compared with every other flight in the air and every open PR, and a shared file
/// nobody foresaw rings the overlap card. It costs one <c>git diff</c> of the plane and the comparison <c>touches</c> makes, and starts no session.
/// </summary>
public sealed partial class LegCommander
{
    // Flights whose leg-end overlap check is still to run or in progress, by code: how many holders (a finish command, an answer's re-check)
    // and when the mark was last set or refreshed. A holder clears only its own count, so a refused or repeated finish never opens the gap
    // for another. The time limit is only the leak guard, measured from the last refresh, which RecordDoneAsync gives when it puts the leg
    // done, so a leg's own long check before that never uses it up.
    private readonly Dictionary<string, (int Holders, DateTimeOffset At)> _legEndChecking = new(StringComparer.Ordinal);
    internal static readonly TimeSpan LegEndMarkLife = TimeSpan.FromMinutes(10);

    private void MarkLegEndChecking(string code)
    {
        lock (_legEndChecking)
        {
            _legEndChecking[code] = (_legEndChecking.TryGetValue(code, out var m) ? m.Holders + 1 : 1, time.GetUtcNow());
        }
    }

    /// <summary>Restarts the leak guard's clock of a held mark, leaving its holders as they are; with no mark there is nothing to refresh.</summary>
    private void RefreshLegEndChecking(string code)
    {
        lock (_legEndChecking)
        {
            if (_legEndChecking.TryGetValue(code, out var m))
            {
                _legEndChecking[code] = (m.Holders, time.GetUtcNow());
            }
        }
    }

    private void ClearLegEndChecking(string code)
    {
        lock (_legEndChecking)
        {
            if (!_legEndChecking.TryGetValue(code, out var m))
            {
                return;
            }
            if (m.Holders <= 1)
            {
                _legEndChecking.Remove(code);
            }
            else
            {
                _legEndChecking[code] = (m.Holders - 1, m.At);
            }
        }
    }

    private bool LegEndChecking(string code)
    {
        lock (_legEndChecking)
        {
            return _legEndChecking.TryGetValue(code, out var m) && time.GetUtcNow() - m.At < LegEndMarkLife;
        }
    }

    /// <summary>
    /// Compares what the flight has changed so far with the others and rings one overlap card for the other it shares the most unforeseen
    /// files with, recording a pending <see cref="OverlapRow"/> marked with the leg, so <see cref="Due"/> holds the next leg until it is answered.
    /// Run off the lock, before the flight goes on. A fetch or a diff that fails is logged and rings nothing; it never fails the finish.
    /// </summary>
    private async Task CheckLegEndOverlapAsync(string legTarget, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(legTarget);
        await CheckOverlapAsync(flight.Code, leg.Name, cancellationToken);
    }

    /// <summary>
    /// The check after the node pushes a go-around's or a base merge's head: what the push added may share a file with another flight. It rings
    /// like a leg's end, with the lock let go for it, and never fails the push.
    /// </summary>
    private Task CheckAfterPushAsync(FlightRow flight, CancellationToken cancellationToken) =>
        OffLockAsync(null, () => CheckOverlapAsync(flight.Code,
            state.Legs(flight.Code).Where(l => l.State == LegState.Done).OrderByDescending(l => l.Order).FirstOrDefault()?.Name ?? "the push", cancellationToken));

    internal async Task CheckOverlapAsync(string code, string legName, CancellationToken cancellationToken)
    {
        try
        {
            var flight = FlightOf(Verbs.FlightTarget(code));
            var leg = new { Name = legName };
            if (flight.State == FlightState.Landed || flight.Overlaps.Any(o => o.Answer == OverlapAnswer.Pending))
            {
                return;
            }
            var diff = await git.RunAsync(["diff", "--name-only", $"{flight.Base}...HEAD"], flight.Worktree, cancellationToken);
            if (diff.ExitCode != 0)
            {
                await Error.WriteLineAsync($"leg-end overlap: git diff of {flight.Code}'s plane failed, nothing checked: {FirstLine(diff.Error)}");
                return;
            }
            var own = OverlapChoice.Normalise(diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (own.Count == 0)
            {
                return;
            }

            var (others, _) = await GatherOthersAsync(flight, cancellationToken);
            Touched? best = null;
            string[] bestFiles = [];
            // A pair answered parallel is never rung again: its new shared files are recorded on its row (PIL589).
            var quiet = new List<(Touched Other, string[] Files)>();
            var prParallel = new List<(Touched Other, string[] Files)>();
            var mutual = new List<(Touched Other, string[] Files)>();
            var oneWay = new List<(Touched Other, string[] Files)>();
            Dictionary<string, int>? ownLines = null;
            var ownLinesRead = false;
            foreach (var other in others.Where(o => o.Files is not null))
            {
                var unforeseen = Unforeseen(flight, own, other);
                if (unforeseen.Length == 0)
                {
                    continue;
                }
                var otherCode = OtherCode(other);
                var answered = AnswerOnPair(flight, otherCode, other.Node);
                if (answered is { Answer: OverlapAnswer.Separate } || answered is { Reverse: true, Answer: OverlapAnswer.Connect })
                {
                    quiet.Add((other, unforeseen));
                }
                else if (other.Node is null && other.FlightCode is not null && HoldsPendingRowAbout(other.FlightCode, flight.Code))
                {
                    // Both hold a card on each other (PIL664): the node plans them in parallel itself, on both sides.
                    mutual.Add((other, unforeseen));
                }
                else if (other.FlightCode is null)
                {
                    // A PR no flight owns has no flight to connect to: the node answers parallel itself and leaves a note.
                    prParallel.Add((other, unforeseen));
                }
                else
                {
                    if (answered is null)
                    {
                        if (!ownLinesRead)
                        {
                            (ownLines, ownLinesRead) = (await OwnLinesAsync(flight, cancellationToken), true);
                        }
                        if (!OverlapChoice.Rings(unforeseen, ownLines, other.Lines))
                        {
                            // Small, one-way and not answered yet: the node plans in parallel itself and leaves a note (PIL726).
                            oneWay.Add((other, unforeseen));
                            continue;
                        }
                    }
                    if (unforeseen.Length > bestFiles.Length)
                    {
                        (best, bestFiles) = (other, unforeseen);
                    }
                }
            }
            if (best is null && quiet.Count == 0 && prParallel.Count == 0 && mutual.Count == 0 && oneWay.Count == 0)
            {
                return;
            }

            await _one.WaitAsync(cancellationToken);
            try
            {
                // Looked at again under the lock: a question asked meanwhile (by the planner of a flight that is replanning) is the one waiting.
                if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: not FlightState.Landed } now || now.Overlaps.Any(o => o.Answer == OverlapAnswer.Pending))
                {
                    return;
                }
                foreach (var (other, files) in quiet)
                {
                    now = RecordParallelFiles(now, leg.Name, other, files);
                }
                foreach (var (other, files) in prParallel)
                {
                    now = AnswerPrParallel(now, leg.Name, other, files);
                }
                foreach (var (other, files) in oneWay)
                {
                    now = AnswerOneWayParallel(now, leg.Name, other, files);
                }
                var goOn = new List<(FlightRow Other, OverlapRow Row)>();
                foreach (var (other, files) in mutual)
                {
                    // Looked at again under the lock: the other's row may have been answered since.
                    if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, other.FlightCode, StringComparison.OrdinalIgnoreCase)) is { } theirs
                        && theirs.Overlaps.FirstOrDefault(o => IsPendingAbout(o, now.Code)) is { } pending)
                    {
                        (now, var released) = AnswerMutualParallel(now, leg.Name, theirs, pending, files);
                        goOn.Add((released, pending));
                    }
                    else
                    {
                        best ??= other;
                        if (best == other)
                        {
                            bestFiles = files;
                        }
                    }
                }
                FlightRow? connected = null;
                if (best is not null)
                {
                    connected = await ConnectOrRingLegEndAsync(now, leg.Name, best, bestFiles, cancellationToken);
                }
                foreach (var (released, row) in goOn)
                {
                    try
                    {
                        await GoOnAfterParallelAnswerAsync(released, row, $"{released.Code} is planned in parallel with {now.Code} by the node", cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await Error.WriteLineAsync($"leg-end overlap: {released.Code} could not go on after the mutual parallel answer: {ex.Message}");
                    }
                }
                if (connected is not null)
                {
                    // As after a connect answered on a card: the check runs again (a second other flight is looked at) and the flight advances,
                    // which Due holds at the next leg until the other lands.
                    try
                    {
                        await GoOnAfterLegEndAnswerAsync(connected, $"{connected.Code} is connected by the node", cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await Error.WriteLineAsync($"leg-end overlap: {connected.Code} could not go on after the node connected it: {ex.Message}");
                    }
                }
            }
            finally
            {
                _one.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"leg-end overlap: the check of {code} failed, nothing rung: {ex.Message}");
        }
    }

    private static string OtherCode(Touched other) => other.FlightCode ?? $"#{other.PrNumber}";

    /// <summary>
    /// The answer on a pair from either side (PIL589): the flight's own non-pending row for the other, else, when the other is a flight of
    /// this node, the other's non-pending row naming this flight. <c>Reverse</c> says it was found on the other's side.
    /// </summary>
    private (OverlapAnswer Answer, IReadOnlyList<string> Files, bool Reverse, OverlapRow Row)? AnswerOnPair(FlightRow flight, string otherCode, string? otherNode)
    {
        if (flight.Overlaps.FirstOrDefault(o => o.Answer != OverlapAnswer.Pending && string.Equals(o.Other, otherCode, StringComparison.OrdinalIgnoreCase)) is { } own)
        {
            return (own.Answer, own.Files ?? [], false, own);
        }
        if (otherNode is not null)
        {
            return null;
        }
        var theirs = state.Flights().FirstOrDefault(f => string.Equals(f.Code, otherCode, StringComparison.OrdinalIgnoreCase))
            ?.Overlaps.FirstOrDefault(o => o.Node is null && o.Answer != OverlapAnswer.Pending && string.Equals(o.Other, flight.Code, StringComparison.OrdinalIgnoreCase));
        return theirs is null ? null : (theirs.Answer, theirs.Files ?? [], true, theirs);
    }

    /// <summary>
    /// The files both change that the planner did not foresee and no answer on this pair has covered: the shared files, less the planned
    /// ones (a flight whose planner never asked has no planned files, and every shared file counts), less the files of an answered row.
    /// </summary>
    private string[] Unforeseen(FlightRow flight, IReadOnlyList<string> own, Touched other)
    {
        if (other.Files is not { } theirs)
        {
            return [];
        }
        var code = OtherCode(other);
        // Files already answered from the other flight's card are covered too.
        IReadOnlyList<string> reverseFiles = AnswerOnPair(flight, code, other.Node) is { Reverse: true } rev ? rev.Files : [];
        var covered = OverlapChoice.Normalise(flight.PlannedFiles ?? [])
            .Concat(OverlapChoice.Normalise(flight.Overlaps.Where(o => o.Answer != OverlapAnswer.Pending && string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)).SelectMany(o => o.Files ?? [])))
            .Concat(OverlapChoice.Normalise(reverseFiles))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. OverlapChoice.Judge(own, theirs).Shared.Where(f => !covered.Contains(f))];
    }

    /// <summary>
    /// Adds the new shared files to the row of a pair answered parallel, which stays <see cref="OverlapAnswer.Separate"/>: no card rings
    /// and <see cref="Due"/> holds nothing. One console line says so.
    /// </summary>
    private FlightRow RecordParallelFiles(FlightRow flight, string leg, Touched other, string[] files)
    {
        var code = OtherCode(other);
        var existing = flight.Overlaps.FirstOrDefault(o => o.Answer == OverlapAnswer.Separate && string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            // Answered from the other's card (PIL589): the pair gets its own row, so the next check finds it on this side.
            var mirrored = new OverlapRow
            {
                Other = code, Node = null, Files = OverlapChoice.Normalise(files).Order(StringComparer.OrdinalIgnoreCase).ToArray(), Pick = Verbs.OverlapPickConnect,
                Why = $"after {leg} the plane also changes {string.Join(", ", ShortNames(files))}, which {other.Label} changes too; answered from {code}'s card, so no card rang",
                Answer = OverlapAnswer.Separate, AfterLeg = leg, At = time.GetUtcNow(),
            };
            var added = flight with { Overlaps = [.. flight.Overlaps.Where(o => !string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)), mirrored] };
            state.PutFlight(added);
            Error.WriteLine($"leg-end overlap: {flight.Code} and {code} were answered on {code}'s card; after {leg} {string.Join(", ", files)} is recorded on this side, no card rung");
            return added;
        }
        var union = OverlapChoice.Normalise([.. existing.Files ?? [], .. files]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var row = existing with { Files = union, AfterLeg = leg, Why = $"after {leg} the plane also changes {string.Join(", ", ShortNames(files))}, which {other.Label} changes too; answered parallel before, so no card rang" };
        var updated = flight with { Overlaps = [.. flight.Overlaps.Select(o => ReferenceEquals(o, existing) ? row : o)] };
        state.PutFlight(updated);
        Error.WriteLine($"leg-end overlap: {flight.Code} and {code} were answered parallel; after {leg} {string.Join(", ", files)} is recorded on that pair, no card rung");
        return updated;
    }

    /// <summary>Card kind of the note the node leaves when it answers 'plan in parallel' itself for an open PR no flight owns.</summary>
    public const string OverlapParallelKind = "overlap-parallel";

    /// <summary>
    /// An unforeseen overlap with an open PR no flight owns: there is no flight to connect to, so the node records the pair
    /// <see cref="OverlapAnswer.Separate"/> (no pending row, so <see cref="Due"/> holds nothing) and raises a handled card, not a ring.
    /// </summary>
    private FlightRow AnswerPrParallel(FlightRow flight, string leg, Touched pr, string[] files)
    {
        var code = OtherCode(pr);
        var union = OverlapChoice.Normalise([.. flight.Overlaps.Where(o => string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)).SelectMany(o => o.Files ?? []), .. files])
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var row = new OverlapRow
        {
            Other = code, Node = null, Files = union, Pick = Verbs.OverlapPickConnect,
            Why = $"after {leg} the plane changes {string.Join(", ", ShortNames(files))}, which {pr.Label} changes too; the node answered parallel because a PR has no flight to connect to",
            Answer = OverlapAnswer.Separate, AfterLeg = leg, At = time.GetUtcNow(),
        };
        var updated = flight with { Overlaps = [.. flight.Overlaps.Where(o => !string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)), row] };
        state.PutFlight(updated);

        var names = ShortNames(files);
        var named = names.Length > OverlapNamed ? $"{string.Join(", ", names.Take(OverlapNamed))} and {names.Length - OverlapNamed} more" : string.Join(", ", names);
        state.RaiseCard(CardBin.Handled, OverlapParallelKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}/{leg} planned in parallel with open PR {code} ({named})",
            $"Found at {leg}: {flight.Code}'s plane changes {files.Length} file{(files.Length == 1 ? "" : "s")} that open PR {code} changes too, which its plan did not foresee. "
            + $"A PR has no flight to connect to, so the node answered plan in parallel itself and {flight.Code} goes on. The second to land resolves the conflicts. Both change: {string.Join(", ", names)}.");
        Error.WriteLine($"leg-end overlap: {flight.Code} shares {string.Join(", ", files)} with open PR {code}; answered parallel by the node, no card rang");
        return updated;
    }

    /// <summary>
    /// A small unforeseen overlap, one-way, with another flight that has no answer on the pair yet (PIL726): the node records the pair
    /// <see cref="OverlapAnswer.Separate"/> on this side only and raises a handled card, not a ring, so <see cref="Due"/> holds nothing.
    /// </summary>
    private FlightRow AnswerOneWayParallel(FlightRow flight, string leg, Touched other, string[] files)
    {
        var code = OtherCode(other);
        var union = OverlapChoice.Normalise([.. flight.Overlaps.Where(o => string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)).SelectMany(o => o.Files ?? []), .. files])
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var limits = $"at most {OverlapChoice.RingMoreThanShared} shared files, and no shared source file changed by {OverlapChoice.HotLines}+ lines on both sides";
        var row = new OverlapRow
        {
            Other = code, Node = other.Node, Files = union, Pick = Verbs.OverlapPickConnect,
            Why = $"after {leg} the plane changes {string.Join(", ", ShortNames(files))}, which {other.Label} changes too; the node planned this small one-way overlap in parallel ({limits})",
            Answer = OverlapAnswer.Separate, AfterLeg = leg, At = time.GetUtcNow(),
        };
        var updated = flight with { Overlaps = [.. flight.Overlaps.Where(o => !string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)), row] };
        state.PutFlight(updated);

        var names = ShortNames(files);
        var named = names.Length > OverlapNamed ? $"{string.Join(", ", names.Take(OverlapNamed))} and {names.Length - OverlapNamed} more" : string.Join(", ", names);
        var at = other.Node is null ? code : $"{code} on {other.Node}";
        state.RaiseCard(CardBin.Handled, OverlapParallelKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}/{leg} planned in parallel with {at} ({named})",
            $"Found at {leg}: {flight.Code}'s plane changes {files.Length} file{(files.Length == 1 ? "" : "s")} that {at} changes too, which its plan did not foresee. "
            + $"The overlap is small ({limits}), so no card rang: the node planned in parallel itself and {flight.Code} goes on. The second to land resolves the conflicts. Both change: {string.Join(", ", names)}.");
        Error.WriteLine($"leg-end overlap: {flight.Code} shares {string.Join(", ", files)} with {at}; small, answered parallel by the node, no card rang");
        return updated;
    }

    private static bool IsPendingAbout(OverlapRow row, string code) =>
        row.Node is null && row.Answer == OverlapAnswer.Pending && string.Equals(row.Other, code, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the local flight <paramref name="holder"/> has a pending overlap row naming <paramref name="about"/>.</summary>
    private bool HoldsPendingRowAbout(string holder, string about) =>
        state.Flights().FirstOrDefault(f => string.Equals(f.Code, holder, StringComparison.OrdinalIgnoreCase))?.Overlaps.Any(o => IsPendingAbout(o, about)) == true;

    /// <summary>
    /// Two flights of this node hold an overlap card on each other (PIL664): the other's pending row becomes <see cref="OverlapAnswer.Separate"/>
    /// and its overlap card closes, this flight records a Separate row, and one handled card names both flights and the shared files.
    /// Returns this flight as recorded and the other as released; the caller lets the other go on.
    /// </summary>
    private (FlightRow Now, FlightRow Released) AnswerMutualParallel(FlightRow flight, string? leg, FlightRow other, OverlapRow pending, string[] files)
    {
        var shared = OverlapChoice.Normalise([.. pending.Files ?? [], .. files]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var released = other with { Overlaps = [.. other.Overlaps.Select(o => ReferenceEquals(o, pending) ? o with { Answer = OverlapAnswer.Separate, Files = shared, At = time.GetUtcNow() } : o)] };
        state.PutFlight(released);
        state.CloseCardsAbout(Verbs.FlightTarget(other.Code), $"the node planned {other.Code} in parallel with {flight.Code} itself, as both held a card on the other", OverlapKind);

        var union = OverlapChoice.Normalise([.. flight.Overlaps.Where(o => string.Equals(o.Other, other.Code, StringComparison.OrdinalIgnoreCase)).SelectMany(o => o.Files ?? []), .. shared])
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var row = new OverlapRow
        {
            Other = other.Code, Node = null, Files = union, Pick = Verbs.OverlapPickConnect,
            Why = (leg is null ? $"{flight.Code} and {other.Code} change {string.Join(", ", ShortNames(files))}" : $"after {leg} the plane changes {string.Join(", ", ShortNames(files))}, which {other.Code} changes too")
                + $"; the node answered a mutual overlap parallel, as {other.Code} held a card on {flight.Code} as well",
            Answer = OverlapAnswer.Separate, AfterLeg = leg, At = time.GetUtcNow(),
        };
        var updated = flight with { Overlaps = [.. flight.Overlaps.Where(o => !string.Equals(o.Other, other.Code, StringComparison.OrdinalIgnoreCase)), row] };
        state.PutFlight(updated);

        var names = ShortNames(shared);
        var named = names.Length > OverlapNamed ? $"{string.Join(", ", names.Take(OverlapNamed))} and {names.Length - OverlapNamed} more" : string.Join(", ", names);
        state.RaiseCard(CardBin.Handled, OverlapParallelKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code} and {other.Code} planned in parallel ({named})",
            $"{(leg is null ? "Found" : $"Found at {leg}")}: {flight.Code} and {other.Code} each held an overlap card on the other. "
            + $"The node answered plan in parallel itself on both sides and both go on. The second to land resolves the conflicts. Both change: {string.Join(", ", names)}.");
        Error.WriteLine($"leg-end overlap: {flight.Code} and {other.Code} held a card on each other for {string.Join(", ", shared)}; answered parallel by the node, no card rang");
        return (updated, released);
    }

    /// <summary>
    /// Pairs of local flights that each hold a pending row naming the other (PIL664), as a node finds them at a start or a sweep: each pair is
    /// answered parallel on both sides once, with one handled note. A one-way pending row is left alone.
    /// </summary>
    internal async Task AnswerMutualPendingAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            foreach (var code in state.Flights().Where(f => f.State != FlightState.Landed).Select(f => f.Code).ToList())
            {
                if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed } a)
                {
                    continue;
                }
                foreach (var mine in a.Overlaps.Where(o => o.Node is null && o.Answer == OverlapAnswer.Pending).ToList())
                {
                    if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, mine.Other, StringComparison.OrdinalIgnoreCase)) is not { State: not FlightState.Landed } b
                        || b.Code == a.Code || b.Overlaps.FirstOrDefault(o => IsPendingAbout(o, a.Code)) is not { } theirs)
                    {
                        continue;
                    }
                    var files = OverlapChoice.Normalise([.. mine.Files ?? [], .. theirs.Files ?? []]).ToArray();
                    // This flight's pending row is dropped and replaced by a Separate one; the other's is answered and its card closed.
                    var (now, released) = AnswerMutualParallel(a with { Overlaps = [.. a.Overlaps.Where(o => !ReferenceEquals(o, mine))] }, null, b, theirs, files);
                    state.CloseCardsAbout(Verbs.FlightTarget(now.Code), $"the node planned {now.Code} in parallel with {b.Code} itself, as both held a card on the other", OverlapKind);
                    foreach (var (flight, row, with) in new[] { (released, theirs, now.Code), (now, mine, released.Code) })
                    {
                        try
                        {
                            await GoOnAfterParallelAnswerAsync(flight, row, $"{flight.Code} is planned in parallel with {with} by the node", cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await Error.WriteLineAsync($"mutual overlap: {flight.Code} could not go on after the parallel answer: {ex.Message}");
                        }
                    }
                    break;
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Card kind of the Handled line the node leaves when it closes a pending overlap because the other flight has landed.</summary>
    public const string OverlapLandedKind = "overlap-landed";

    /// <summary>
    /// Pending overlap rows whose other flight has landed (PIL824): there is nothing to wait for, so the row is answered (connect, with no wait
    /// recorded), its overlap card closes with the reason, one Handled line says so, and the flight goes on as after any answer (a leg-end row
    /// advances, a planner-asked row has its planner restarted). A local other is read from its row (landed, not moved or folded); a peer's is
    /// asked of the peers off the lock, one row after another, each under the peers' budget: a slow peer holds no lock, but it delays the rows
    /// after it and the sweep that called this by up to that budget a row. A peer that does not answer leaves the row pending.
    /// </summary>
    internal async Task AnswerLandedPendingAsync(CancellationToken cancellationToken)
    {
        var peerRows = new List<(string Code, string Other, string Node)>();
        await _one.WaitAsync(cancellationToken);
        try
        {
            foreach (var code in state.Flights().Where(f => f.State != FlightState.Landed).Select(f => f.Code).ToList())
            {
                if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed } flight)
                {
                    continue;
                }
                foreach (var row in flight.Overlaps.Where(o => o.Answer == OverlapAnswer.Pending).ToList())
                {
                    if (row.Node is not null)
                    {
                        peerRows.Add((code, row.Other, row.Node));
                    }
                    else if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, row.Other, StringComparison.OrdinalIgnoreCase)) is { State: FlightState.Landed, MovedTo: null, FoldedInto: null })
                    {
                        await AnswerLandedAsync(flight, row, row.Other, cancellationToken);
                        break;
                    }
                }
            }
        }
        finally
        {
            _one.Release();
        }

        foreach (var (code, other, node) in peerRows)
        {
            try
            {
                if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed } asking
                    || !await flights.PeerFlightLandedAsync(asking, other, node, cancellationToken))
                {
                    continue;
                }
                await _one.WaitAsync(cancellationToken);
                try
                {
                    // Looked at again under the lock: the row may have been answered while the peers were asked.
                    if (state.Flights().FirstOrDefault(f => f.Code == code) is { State: not FlightState.Landed } now
                        && now.Overlaps.FirstOrDefault(o => o.Answer == OverlapAnswer.Pending && string.Equals(o.Other, other, StringComparison.OrdinalIgnoreCase) && o.Node is not null) is { } row)
                    {
                        await AnswerLandedAsync(now, row, $"{other} on {node}", cancellationToken);
                    }
                }
                finally
                {
                    _one.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Error.WriteLineAsync($"landed overlap: {code}'s row about {other} on {node} could not be looked at: {ex.Message}");
            }
        }
    }

    /// <summary>Answers a pending row whose other has landed, closes its card, says so on a Handled line and lets the flight go on. Called under the lock.</summary>
    private async Task AnswerLandedAsync(FlightRow flight, OverlapRow row, string at, CancellationToken cancellationToken)
    {
        var answered = row with { Answer = OverlapAnswer.Connect, At = time.GetUtcNow(), AnsweredByNode = OverlapRow.AnsweredByNodeLanded };
        var now = flight with { Overlaps = [.. flight.Overlaps.Select(o => ReferenceEquals(o, row) ? answered : o)] };
        state.PutFlight(now);
        var target = Verbs.FlightTarget(now.Code);
        state.CloseCardsAbout(target, $"{row.Other} had landed: there is nothing to wait for", OverlapKind);
        var told = $"{row.Other} had landed: {now.Code} goes on, no answer needed";
        state.RaiseCard(CardBin.Handled, OverlapLandedKind, target, told,
            $"{now.Code} overlapped {at}, which has landed since, so its work is in and there is nothing to wait for. The node closed the overlap card itself and {now.Code} goes on"
            + (row.AfterLeg is not null ? $"; {row.AfterLeg} stays done."
                : IsOnHold(now.Code) ? " once it is released (it is on hold, so its planner waits for the release)." : " (its planner is started again with the answer)."));
        Error.WriteLine($"landed overlap: {now.Code} overlapped {at}, which has landed; answered by the node, no card rang");
        try
        {
            await GoOnAfterParallelAnswerAsync(now, answered, told, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"landed overlap: {now.Code} could not go on after {row.Other} had landed: {ex.Message}");
        }
    }

    /// <summary>Card kind of the note the node leaves when it connects a flight to another itself, for an unforeseen overlap found at a leg's end.</summary>
    public const string OverlapConnectedKind = "overlap-connected";

    /// <summary>
    /// How long the other flight may have been still (<see cref="FlightRow.LastMovedAt"/>) before the node no longer connects to it by itself:
    /// waiting for a flight that has stood still this long may never end, so the person is asked (the card says how long it has been stuck).
    /// </summary>
    internal static readonly TimeSpan OverlapStuckAfter = TimeSpan.FromHours(2);

    /// <summary>
    /// An unforeseen overlap with another flight that no answer covers: the node connects this flight to it itself, as the answer to the
    /// overlap card would, and records it with a Handled card, not a ring. It rings (<see cref="RingLegEnd"/>) only when the other has not
    /// moved for <see cref="OverlapStuckAfter"/> or the connect is refused (it would cycle). A pair already connected has new files recorded
    /// on its row, with nothing to decide. Called under the lock, which is let go for the connect. Returns the flight to let go on (its next
    /// leg held by <see cref="Due"/> until the other lands), or null when it rang or only recorded files.
    /// </summary>
    private async Task<FlightRow?> ConnectOrRingLegEndAsync(FlightRow flight, string leg, Touched other, string[] files, CancellationToken cancellationToken)
    {
        var code = OtherCode(other);
        var existing = flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase));
        if (existing is { Answer: OverlapAnswer.Connect })
        {
            // The flight waits for the other already: nothing to decide, the new files are recorded on the pair.
            var more = OverlapChoice.Normalise([.. existing.Files ?? [], .. files]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var row = existing with { Files = more, AfterLeg = leg, Why = $"after {leg} the plane also changes {string.Join(", ", ShortNames(files))}, which {other.Label} changes too; connected before, so no card rang" };
            state.PutFlight(flight with { Overlaps = [.. flight.Overlaps.Select(o => ReferenceEquals(o, existing) ? row : o)] });
            Error.WriteLine($"leg-end overlap: {flight.Code} is connected to {code} already; after {leg} {string.Join(", ", files)} is recorded on that pair, no card rung");
            return null;
        }
        if (other.LastMovedAt is { } moved && time.GetUtcNow() - moved >= OverlapStuckAfter)
        {
            var still = time.GetUtcNow() - moved;
            RingLegEnd(flight, leg, other, files,
                $"{code} has not moved for {StillFor(still)}, so connecting to it may hold {flight.Code} for a long time; the node left the choice to you.", parallelFirst: true);
            return null;
        }

        string? told;
        try
        {
            told = await OffLockAsync(null, () => flights.ConnectToAsync(flight.Code, code, other.Node, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Refused (the other waits for this flight, so neither would leave) or the peer could not be asked: the person decides.
            if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is { State: not FlightState.Landed } current && !current.Overlaps.Any(o => o.Answer == OverlapAnswer.Pending))
            {
                RingLegEnd(current, leg, other, files, $"The node did not connect it itself: {ex.Message}.");
            }
            return null;
        }
        // Looked at again: the connect let the lock go.
        if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: not FlightState.Landed } now)
        {
            return null;
        }

        // The other may have landed between the check's read of it and the connect (a peer's connect says so and records no wait; a local
        // one is connected and released by the sweep): then there is no wait, and the card must not say the flight waits for it.
        var waits = other.Node is null
            ? state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { State: FlightState.Landed, MovedTo: null, FoldedInto: null }
            : now.PeerConnections.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        var union = OverlapChoice.Normalise([.. existing?.Files ?? [], .. files]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var connectedRow = new OverlapRow
        {
            Other = code, Node = other.Node, Files = union, Pick = Verbs.OverlapPickConnect,
            Why = $"after {leg} the plane changes {string.Join(", ", ShortNames(files))}, which {other.Label} changes too and the planner did not foresee; "
                + (waits ? $"the node connected it, as {code} is moving" : $"the node connected it, but {code} landed meanwhile, so there is nothing to wait for"),
            Answer = OverlapAnswer.Connect, AfterLeg = leg, At = time.GetUtcNow(),
            AnsweredByNode = waits ? OverlapRow.AnsweredByNodeConnected : OverlapRow.AnsweredByNodeLanded,
        };
        now = now with { Overlaps = [.. now.Overlaps.Where(o => !string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)), connectedRow] };
        state.PutFlight(now);

        var names = ShortNames(files);
        var named = names.Length > OverlapNamed ? $"{string.Join(", ", names.Take(OverlapNamed))} and {names.Length - OverlapNamed} more" : string.Join(", ", names);
        var at = other.Node is null ? code : $"{code} on {other.Node}";
        state.RaiseCard(CardBin.Handled, OverlapConnectedKind, Verbs.FlightTarget(now.Code),
            $"{now.Code}/{leg} connected to {at} ({named})",
            $"Found at {leg}: {now.Code}'s plane changes {files.Length} file{(files.Length == 1 ? "" : "s")} that {at} changes too, which its plan did not foresee. "
            + (waits
                ? $"{at} is moving, so no card rang: the node connected {now.Code} to it itself. {leg} stays done and the next leg of {now.Code} waits until {code} has landed. "
                  + $"To fly both in parallel instead, disconnect {now.Code} from {code} ({Verbs.AnywayFlag}). Both change: {string.Join(", ", names)}."
                : $"{at} landed while the node connected {now.Code} to it, so there is nothing to wait for and no card rang. {leg} stays done and {now.Code} goes on. Both change: {string.Join(", ", names)}."));
        Error.WriteLine($"leg-end overlap: {now.Code} shares {string.Join(", ", files)} with {at}; "
            + (waits ? "connected by the node" : "landed while it was connected, nothing to wait for") + $", no card rang{(told is null ? "" : $" ({told})")}");
        return now;
    }

    /// <summary>How long a flight has been still, as a card says it: minutes, hours, or days from two days on.</summary>
    private static string StillFor(TimeSpan span) =>
        span.TotalHours >= 48 ? $"{(int)span.TotalDays} days" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h" : $"{Math.Max(0, (int)span.TotalMinutes)} min";

    /// <summary>
    /// Records the pending overlap (added, or an answered pair's row made pending again with the new files) and raises its card. A
    /// <paramref name="reason"/> says why the node did not connect itself; the choices stay connect and plan in parallel, in parallel first
    /// when <paramref name="parallelFirst"/> (the other has stood still).
    /// </summary>
    private void RingLegEnd(FlightRow flight, string leg, Touched other, string[] files, string? reason = null, bool parallelFirst = false)
    {
        var code = OtherCode(other);
        var existing = flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase));
        var union = OverlapChoice.Normalise([.. existing?.Files ?? [], .. files]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var why = $"after {leg} the plane changes {string.Join(", ", ShortNames(files))}, which {other.Label} changes too and the planner did not foresee";
        var row = new OverlapRow { Other = code, Node = other.Node, Files = union, Pick = Verbs.OverlapPickConnect, Why = why, Answer = OverlapAnswer.Pending, AfterLeg = leg, At = time.GetUtcNow() };
        state.PutFlight(flight with { Overlaps = [.. flight.Overlaps.Where(o => !string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)), row] });

        var target = Verbs.FlightTarget(flight.Code);
        var names = ShortNames(files);
        var named = names.Length > OverlapNamed ? $"{string.Join(", ", names.Take(OverlapNamed))} and {names.Length - OverlapNamed} more" : string.Join(", ", names);
        // The plane holds work, so a fold (which retires it) is never offered. Only flights reach here: a PR is answered by AnswerPrParallel.
        CardChoice connect = new() { Label = $"Connect to {code}", Verb = Verbs.ConnectOverlap, Target = target, Args = [code] };
        CardChoice parallel = new() { Label = "Plan both in parallel", Verb = Verbs.Separate, Target = target, Args = [code] };
        List<CardChoice> choices = parallelFirst ? [parallel, connect] : [connect, parallel];
        var at = other.Node is null ? code : $"{code} on {other.Node}";
        var detail = new System.Text.StringBuilder();
        detail.Append($"Found at {leg}: {flight.Code}'s plane now changes {files.Length} file{(files.Length == 1 ? "" : "s")} that {at} changes too, which its plan did not foresee.");
        if (reason is not null)
        {
            detail.Append($" {reason}");
        }
        detail.Append($" Connect holds the next leg of {flight.Code}, not {leg}, which stays done, until {code} has landed.");
        detail.Append(" Plan in parallel flies both; the second to land resolves the conflicts.");
        detail.Append($" The next leg of {flight.Code} waits for your answer.");
        detail.Append($" Both change {files.Length}: {string.Join(", ", names)}.");
        state.RaiseCard(CardBin.Ring, OverlapKind, target, $"{flight.Code}/{leg} overlaps {at} ({named})", detail.ToString(), choices);
    }
}
