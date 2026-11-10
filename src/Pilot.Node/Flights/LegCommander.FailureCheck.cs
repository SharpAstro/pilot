using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a leg's failed check hands to the failure investigations and alerts (PIL716): the leg's own changed files tell a failing
/// test it touches from one it does not, and a flight held for an investigation has its check run again when that lands.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The failures looked into: a failing test the leg's work does not touch is filed as an investigation. Null for one built without.</summary>
    public FailureInvestigations? Investigations { get; set; }

    /// <summary>The tower's alerts: a failure that comes again holds the flights it hit, which get no go-around. Null for one built without.</summary>
    public FailureAlerts? Alerts { get; set; }

    /// <summary>
    /// A flight held for an investigation (<see cref="FailureAlerts"/>) has its failed leg check run again once the investigation lands: the
    /// leg's verify, as the node would give it. Returns what to say of it, for the Handled line of the release.
    /// </summary>
    public async Task<string> RerunHeldCheckAsync(string code, CancellationToken cancellationToken)
    {
        if (state.Legs(code).OrderBy(l => l.Order).FirstOrDefault(l => l.State == LegState.Failed && l.Check is { Length: > 0 }) is not { } leg)
        {
            return "no failed leg of it needs its check run again";
        }
        var result = await RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = Verbs.LegTarget(code, leg.Name), Args = ["verified", "its check run again once the investigation it was held for landed"], Who = "node" }, cancellationToken);
        if (result.Later is { } later)
        {
            result = await later;
        }
        return result.Ok ? $"{leg.Name}'s check passed and the leg is done" : $"{leg.Name}'s check was run again and the leg stays failed: {LegCommander.OneLine(result.Error ?? "no reason given")}";
    }

    /// <summary>
    /// A leg's failed check looked into for a failing test the leg's own work does not touch (a test it does touch is the leg's to
    /// fix). Never throws and never changes what the finish does with the failure.
    /// </summary>
    private async Task InvestigateCheckAsync(FlightRow flight, string output, CancellationToken cancellationToken)
    {
        if (Investigations is null && Alerts is null)
        {
            return;
        }
        try
        {
            var head = (await git.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], flight.Worktree, cancellationToken)) is { Ok: true } rev ? rev.Output.Trim() : "";
            var site = new FailureSite(flight.Repo, flight.RepoPath, flight.PullRequest, flight.Code, head, FailureWhere.LegCheck, flight.PullRequestUrl);
            // A repeat holds the flight: the alert names it, and no go-around is sent for it until the investigation lands.
            if (Alerts is { } alerts)
            {
                await alerts.LookAtCheckAsync(site, output, () => PlaneChangedFilesAsync(flight, cancellationToken), cancellationToken);
            }
            else
            {
                await Investigations!.LookAtCheckAsync(site, output, () => PlaneChangedFilesAsync(flight, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"failures: {flight.Code}'s failed check could not be looked into: {ex.Message}");
        }
    }

    /// <summary>The files the plane's work changes against its base: its commits, its uncommitted changes and its new files.</summary>
    private async Task<string[]> PlaneChangedFilesAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var baseRef = flight.Base.Length > 0 ? flight.Base : "origin/main";
        var mergeBase = await git.RunAsync(["merge-base", "HEAD", baseRef], flight.Worktree, cancellationToken);
        if (!mergeBase.Ok || mergeBase.Output.Trim().Length == 0)
        {
            throw new FlightException($"the merge base of the plane and {baseRef} could not be read");
        }
        var changed = await git.RunAsync(["diff", "--name-only", mergeBase.Output.Trim()], flight.Worktree, cancellationToken);
        var added = await git.RunAsync(["ls-files", "--others", "--exclude-standard"], flight.Worktree, cancellationToken);
        if (!changed.Ok)
        {
            throw new FlightException("the plane's changed files could not be read");
        }
        return [.. changed.Output.Split('\n').Concat(added.Ok ? added.Output.Split('\n') : []).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)];
    }
}
