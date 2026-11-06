using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>A finish whose check failed only on tests the flight's change does not touch (#752).</summary>
public sealed partial class LegCommander
{
    /// <summary>
    /// Sorts a failed check's failing tests into the flight's and not (<see cref="UnrelatedFailures"/>). A check stops at its first
    /// failed test (PIL731), so one that is not the flight's could hide one that is: the check runs again with the unrelated classes
    /// left out, up to <see cref="UnrelatedFailures.MostReruns"/> times, and passes only when that run passes. Then each is matched to
    /// an open issue or gets one filed, and the run comes back passed with the report line that names them. Anything else (a test
    /// that is the flight's, a build error, a timeout, output with no failed test, GitHub not answering) comes back failed, as it was.
    /// Called under the lock.
    /// </summary>
    private async Task<(CheckRun Run, string? NotOurs)> UnrelatedAsync(FlightRow flight, LegRow leg, string check, CheckRun run, CancellationToken cancellationToken)
    {
        if (run.StoppedByBudget || UnrelatedFailures.FailedTests(run.Tail) is not { } failed)
        {
            return (run, null);
        }
        var diff = await OffLockAsync(null, () => git.RunAsync(["diff", "--name-only", $"{flight.Base}...HEAD"], flight.Worktree, cancellationToken));
        if (!diff.Ok)
        {
            return (run, null);
        }
        var classes = UnrelatedFailures.ClassesOf(diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var names = UnrelatedFailures.FilterNames(check);

        var found = new List<(string Test, string? Job, string Output)>();
        var current = run;
        for (var rerun = 0; ; rerun++)
        {
            if (failed.Any(t => UnrelatedFailures.IsFlights(t, classes, names)))
            {
                return (current, null);
            }
            var job = LatestCheckJob(flight.Code, leg.Name);
            foreach (var test in failed.Where(t => found.All(f => f.Test != t)))
            {
                found.Add((test, job, current.Tail));
            }
            if (rerun == UnrelatedFailures.MostReruns
                || UnrelatedFailures.Excluding(check, [.. found.Select(f => UnrelatedFailures.ClassOf(f.Test)).Distinct()]) is not { } without)
            {
                return (current, null);
            }
            current = await OffLockAsync(null, () => checks.RunAsync(without, flight.Worktree, cancellationToken, flight.Code, leg.Name));
            if (current.Passed)
            {
                break;
            }
            if (current.StoppedByBudget || UnrelatedFailures.FailedTests(current.Tail) is not { } next)
            {
                return (current, null);
            }
            failed = next;
        }
        if (UnchangedFinishing(flight.Code, leg) is null)
        {
            throw new FlightException($"{flight.Code}/{leg.Name} changed while its check ran: nothing was recorded, look again and finish again");
        }

        var listed = new List<(string Test, int Issue)>();
        try
        {
            var open = await OffLockAsync(null, () => github.IssuesAsync(flight.RepoPath, cancellationToken));
            foreach (var (test, job, output) in found)
            {
                var issue = UnrelatedFailures.Matching(open, test)
                    ?? await OffLockAsync(null, () => github.CreateIssueAsync(flight.RepoPath, UnrelatedFailures.IssueTitle(test, flight.Code),
                        UnrelatedFailures.IssueBody(test, flight.Code, leg.Name, job, output), cancellationToken));
                listed.Add((test, issue.Number));
            }
        }
        catch (FlightException ex)
        {
            return (run with { Tail = $"{run.Tail}\nIt failed only on tests outside the flight's change, but GitHub could not be asked for their issues: {ex.Message}" }, null);
        }
        return (current, UnrelatedFailures.ReportLine(listed));
    }

    /// <summary>The id of the leg's latest check job, for an issue to name; null when its checks ran as no job (a test's runner).</summary>
    private string? LatestCheckJob(string code, string leg) =>
        state.Jobs().Where(j => j.Kind == JobKind.Check && j.Flight == code && j.Leg == leg)
            .OrderByDescending(j => j.StartedAt).Select(j => j.Id).FirstOrDefault();
}
