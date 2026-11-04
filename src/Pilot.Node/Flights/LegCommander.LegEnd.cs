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
                if (best is not null)
                {
                    RingLegEnd(now, leg.Name, best, bestFiles);
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

    /// <summary>Records the pending overlap (added, or an answered pair's row made pending again with the new files) and raises its card.</summary>
    private void RingLegEnd(FlightRow flight, string leg, Touched other, string[] files)
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
        List<CardChoice> choices =
        [
            new CardChoice { Label = $"Connect to {code}", Verb = Verbs.ConnectOverlap, Target = target, Args = [code] },
            new CardChoice { Label = $"Plan {flight.Code} in parallel with {code}", Verb = Verbs.Separate, Target = target, Args = [code] },
        ];
        var at = other.Node is null ? code : $"{code} on {other.Node}";
        var detail = new System.Text.StringBuilder();
        detail.Append($"Found at {leg}: {flight.Code}'s plane now changes {files.Length} file{(files.Length == 1 ? "" : "s")} that {at} changes too, which its plan did not foresee.");
        detail.Append($" Connect holds the next leg of {flight.Code}, not {leg}, which stays done, until {code} has landed.");
        detail.Append(" Plan in parallel flies both; the second to land resolves the conflicts.");
        detail.Append($" The next leg of {flight.Code} waits for your answer.");
        detail.Append($" Both change {files.Length}: {string.Join(", ", names)}.");
        state.RaiseCard(CardBin.Ring, OverlapKind, target, $"{flight.Code}/{leg} overlaps {at} ({named})", detail.ToString(), choices);
    }
}
