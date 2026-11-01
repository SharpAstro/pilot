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
            foreach (var other in others.Where(o => o.Files is not null))
            {
                var unforeseen = Unforeseen(flight, own, other);
                if (unforeseen.Length > bestFiles.Length)
                {
                    (best, bestFiles) = (other, unforeseen);
                }
            }
            if (best is null)
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
                RingLegEnd(now, leg.Name, best, bestFiles);
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
    /// The files both change that the planner did not foresee and no answer on this pair has covered: the shared files, less the planned
    /// ones (a flight whose planner never asked has no planned files, and every shared file counts), less the files of an answered row.
    /// </summary>
    private static string[] Unforeseen(FlightRow flight, IReadOnlyList<string> own, Touched other)
    {
        if (other.Files is not { } theirs)
        {
            return [];
        }
        var code = OtherCode(other);
        var covered = OverlapChoice.Normalise(flight.PlannedFiles ?? [])
            .Concat(OverlapChoice.Normalise(flight.Overlaps.Where(o => o.Answer != OverlapAnswer.Pending && string.Equals(o.Other, code, StringComparison.OrdinalIgnoreCase)).SelectMany(o => o.Files ?? [])))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. OverlapChoice.Judge(own, theirs).Shared.Where(f => !covered.Contains(f))];
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
        var isPr = other.FlightCode is null;
        // The plane holds work, so a fold (which retires it) is never offered; a PR is no flight to connect to.
        List<CardChoice> choices = [];
        if (!isPr)
        {
            choices.Add(new CardChoice { Label = $"Connect to {code}", Verb = Verbs.ConnectOverlap, Target = target, Args = [code] });
        }
        choices.Add(new CardChoice { Label = $"Plan {flight.Code} in parallel with {code}", Verb = Verbs.Separate, Target = target, Args = [code] });
        var at = isPr ? $"open PR {code}" : other.Node is null ? code : $"{code} on {other.Node}";
        var detail = new System.Text.StringBuilder();
        detail.Append($"Found at {leg}: {flight.Code}'s plane now changes {files.Length} file{(files.Length == 1 ? "" : "s")} that {at} changes too, which its plan did not foresee.");
        if (!isPr)
        {
            detail.Append($" Connect holds the next leg of {flight.Code}, not {leg}, which stays done, until {code} has landed.");
        }
        detail.Append(" Plan in parallel flies both; the second to land resolves the conflicts.");
        detail.Append($" The next leg of {flight.Code} waits for your answer.");
        detail.Append($" Both change {files.Length}: {string.Join(", ", names)}.");
        state.RaiseCard(CardBin.Ring, OverlapKind, target, $"{flight.Code}/{leg} overlaps {at} ({named})", detail.ToString(), choices);
    }
}
