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
        var invariant = await OffLockAsync(null, () => Task.Run(() => UnrelatedFailures.InvariantClasses(flight.Worktree), cancellationToken));

        var found = new List<(string Test, string? Job, string Output)>();
        var current = run;
        for (var rerun = 0; ; rerun++)
        {
            // An invariant guard fails when the change forgets what it guards, without touching the guard: always the flight's.
            if (failed.Any(t => UnrelatedFailures.IsFlights(t, classes, names) || UnrelatedFailures.IsInvariant(t, invariant)))
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
            // A failure no open issue names is someone else's only when it fails on the base too; else the leg fails as before.
            var unknown = found.Where(f => UnrelatedFailures.Matching(open, f.Test) is null).Select(f => f.Test).ToList();
            if (unknown.Count > 0 && await OffLockAsync(null, () => PassingOnBaseAsync(flight, check, unknown, cancellationToken)) is { } passing)
            {
                return (run with { Tail = $"{run.Tail}\nIt failed only on tests outside the flight's change, but {passing}, so it is taken as the flight's." }, null);
            }
            // With failure investigations (PIL716), each is filed as one, as a failure in CI or the merge queue is: one per signature
            // across all three, priority high, its flight started and a For-you card. Only an open issue that names the test itself
            // becomes the investigation; a looser match (the class alone) is no reason to raise it to one.
            var investigations = Investigations ?? Alerts?.Investigations;
            var head = investigations is null ? ""
                : (await OffLockAsync(null, () => git.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], flight.Worktree, cancellationToken))) is { Ok: true } rev ? rev.Output.Trim() : "";
            foreach (var (test, job, output) in found)
            {
                if (investigations is not null)
                {
                    var site = new FailureSite(flight.Repo, flight.RepoPath, flight.PullRequest, flight.Code, head, FailureWhere.LegCheck, flight.PullRequestUrl);
                    var named = UnrelatedFailures.MatchingTest(open, test);
                    if (await OffLockAsync(null, () => investigations.InvestigateTestAsync(site, test, output, named, cancellationToken)) is { Issue: > 0 } finding)
                    {
                        // A repeat of a failure another flight or PR hit is told to the tower, as one read from CI or the queue is. The
                        // leg passes whatever it answers (the alert holds the flights hit before, not this one), so no go-around is burnt.
                        if (Alerts is { } alerts && (finding.Others || alerts.Open().Any(a => a.Signature == finding.Signature)))
                        {
                            alerts.AlertOnPassedCheck(site, finding);
                        }
                        listed.Add((test, finding.Issue));
                        continue;
                    }
                }
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

    /// <summary>
    /// Runs the check for each of <paramref name="tests"/> alone in a throwaway detached worktree at the flight's base, which is removed
    /// whatever happens. Null when every one fails there (so it is not the flight's); else what was not shown, in words.
    /// </summary>
    private async Task<string?> PassingOnBaseAsync(FlightRow flight, string check, IReadOnlyList<string> tests, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-base-check-" + Guid.NewGuid().ToString("N")[..12]);
        var added = false;
        try
        {
            var add = await git.RunAsync(["worktree", "add", "--detach", folder, flight.Base], flight.Worktree, cancellationToken);
            if (!add.Ok)
            {
                return $"the base {flight.Base} could not be checked out to try it there ({add.Why})";
            }
            added = true;
            foreach (var test in tests)
            {
                if (UnrelatedFailures.Only(check, test) is not { } only)
                {
                    return $"{test} could not be run alone on the base";
                }
                var onBase = await checks.RunAsync(only, folder, cancellationToken, flight.Code);
                if (onBase.Passed || UnrelatedFailures.FailedTests(onBase.Tail) is not { } failed || !failed.Contains(test))
                {
                    return $"{test} was not shown failing on the base {flight.Base}";
                }
            }
            return null;
        }
        finally
        {
            if (added)
            {
                await git.RunAsync(["worktree", "remove", "--force", folder], flight.Worktree, CancellationToken.None);
            }
            await git.RunAsync(["worktree", "prune"], flight.Worktree, CancellationToken.None);
        }
    }

    /// <summary>The id of the leg's latest check job, for an issue to name; null when its checks ran as no job (a test's runner).</summary>
    private string? LatestCheckJob(string code, string leg) =>
        state.Jobs().Where(j => j.Kind == JobKind.Check && j.Flight == code && j.Leg == leg)
            .OrderByDescending(j => j.StartedAt).Select(j => j.Id).FirstOrDefault();
}
