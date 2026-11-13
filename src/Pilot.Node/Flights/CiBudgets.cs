using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// PIL795: what a flight's PR has spent of GitHub Actions, and when that is too much. The node counts it from the Actions API
/// (<see cref="IGitHub.CiRunsAsync"/>): runner-minutes (the sum of every job of every run on the branch), runs, failed and cancelled
/// runs, and how long the CI has been red. Past <c>flight-ci-budget</c> runner-hours, or red for more than
/// <see cref="NodeSettings.FlightCiRedHours"/> hours, the node stops pushing for the flight and rings the owner.
/// </summary>
public static class CiBudgets
{
    /// <summary>The ring of a flight whose CI spend passed its budget (or stayed red too long): the numbers, and continue, land what is green, or split.</summary>
    /// <summary>PIL1023: what a dismissed CI stop card says, and what the card itself says before anyone presses dismiss.</summary>
    public static string DismissedStays(string code) =>
        $"{code}'s CI stop stays; pushes stay stopped until you answer 'pilot flight ci-budget {code} {Verbs.CiContinue}' (or {Verbs.CiLandGreen}, {Verbs.CiSplit}).";

    /// <summary>The sentence on the cap card that says dismissing it does not lift the stop.</summary>
    public static string DismissKeepsStop(string code) =>
        $"Dismissing this card does not lift the stop: pushes stay stopped until 'pilot flight ci-budget {code} {Verbs.CiContinue}' (or {Verbs.CiLandGreen}, {Verbs.CiSplit}).";

    public const string CapKind = "ci-cap";

    /// <summary>The Handled line a non-<c>continue</c> answer leaves, so the controller finds it.</summary>
    public const string AnsweredKind = "ci-cap-answered";

    /// <summary>How long a flight's CI may stay red, in a row, before the node stops pushing for it.</summary>
    public static TimeSpan RedBound { get; } = TimeSpan.FromHours(NodeSettings.FlightCiRedHours);

    private static bool Failed(CiRun run) => run.Conclusion is "failure" or "timed_out" or "startup_failure";

    /// <summary>
    /// The flight's CI as these runs say it, over <paramref name="before"/> (which keeps what the node decided: the extra budgets, the
    /// stop and the answer). Cancelled runs count as spend, never as red or green.
    /// </summary>
    public static FlightCi Count(IReadOnlyList<CiRun> runs, FlightCi? before, DateTimeOffset now) =>
        (before ?? new FlightCi()) with
        {
            RunnerMinutes = runs.Sum(r => r.RunnerMinutes),
            Runs = runs.Count,
            Failed = runs.Count(Failed),
            Cancelled = runs.Count(r => r.Conclusion == "cancelled"),
            RedSince = RedSince(runs),
            CountedAt = now,
        };

    /// <summary>
    /// When the CI went red, if it is red: some workflow's latest finished run (cancelled ones aside) failed, and it has not been green
    /// since; the time its first failure of that unbroken streak finished, the earliest of the red workflows. Null when none is red.
    /// </summary>
    public static DateTimeOffset? RedSince(IReadOnlyList<CiRun> runs)
    {
        DateTimeOffset? since = null;
        foreach (var workflow in runs.Where(r => r.CompletedAt is not null && r.Conclusion is not (null or "cancelled" or "skipped")).GroupBy(r => r.Name))
        {
            var finished = workflow.OrderByDescending(r => r.CompletedAt).ToList();
            if (!Failed(finished[0]))
            {
                continue;
            }
            var start = finished.TakeWhile(Failed).Last().CompletedAt!.Value;
            since = since is { } earlier && earlier < start ? earlier : start;
        }
        return since;
    }

    /// <summary>The kind of the node's own card about a flight's CI going red (PrChecks). It is about the CI, not a wait on a person, so it never holds the red clock.</summary>
    public const string ChecksFailedKind = "pr-checks-failed";

    /// <summary>
    /// PIL1023: whether the flight waits on a person, from its cards. A card in For you or Ring about the flight or one of its legs is a
    /// wait while open, except the cap card itself and the CI's own card (<see cref="ChecksFailedKind"/>), which the node raises about the
    /// CI and which would otherwise hold the clock that raises them. Handled-bin cards are the node's record, never a wait.
    /// </summary>
    public static PersonWait WaitOnPerson(IEnumerable<CardRow> cards, string code, DateTimeOffset? since)
    {
        var flight = Verbs.FlightTarget(code);
        var open = false;
        DateTimeOffset? ended = null;
        foreach (var card in cards)
        {
            if (card.Bin is not (CardBin.ForYou or CardBin.Ring) || card.Kind is CapKind or AnsweredKind or ChecksFailedKind
                || !(card.Subject == flight || card.Subject.StartsWith(flight + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            if (card.State == CardState.Open)
            {
                open = true;
            }
            else if (card.ClosedAt is { } closed && (since is null || closed > since) && (ended is null || closed > ended))
            {
                ended = closed;
            }
        }
        return new PersonWait(open, ended);
    }

    /// <summary>Whether a flight waits on a person now, and when the latest such wait ended (null if none did).</summary>
    public readonly record struct PersonWait(bool Waiting, DateTimeOffset? LastEnded);

    /// <summary>
    /// Why the flight's pushes should stop, in numbers, or null while it is within its budget: its runner-hours past
    /// <paramref name="budgetHours"/> times (1 + the extra budgets granted), or its CI red for more than <see cref="RedBound"/>. The red
    /// clock counts from the latest of when it went red, when the owner last said continue, and when the flight's last wait on a person
    /// ended (<paramref name="wait"/>, PIL1023); while a wait is open it does not run, so no red reason. Runner-hours ignore the wait.
    /// </summary>
    public static string? Why(FlightCi ci, int budgetHours, DateTimeOffset now, PersonWait wait = default)
    {
        var cap = budgetHours * (1 + ci.Extra);
        if (ci.RunnerHours > cap)
        {
            return $"its PR's CI has used {Hours(ci.RunnerHours)} runner-hours, past the budget of {cap} ({Numbers(ci)})";
        }
        if (ci.RedSince is { } red && !wait.Waiting)
        {
            var since = red;
            if (ci.RedAcknowledgedAt is { } acknowledged && acknowledged > since)
            {
                since = acknowledged;
            }
            if (wait.LastEnded is { } ended && ended > since)
            {
                since = ended;
            }
            if (now - since > RedBound)
            {
                return $"its PR's CI has been red for {Hours((now - since).TotalHours)} hours, past the bound of {NodeSettings.FlightCiRedHours} ({Numbers(ci)})";
            }
        }
        return null;
    }

    /// <summary>The spend in one line: <c>4.3 runner-hours, 31 runs, 9 failed, 2 cancelled</c>.</summary>
    public static string Numbers(FlightCi ci) =>
        $"{Hours(ci.RunnerHours)} runner-hours, {ci.Runs} {(ci.Runs == 1 ? "run" : "runs")}, {ci.Failed} failed, {ci.Cancelled} cancelled"
        + (ci.RedSince is { } red ? $", red since {red.ToLocalTime():HH:mm}" : "");

    private static string Hours(double hours) => hours.ToString("0.0", CultureInfo.InvariantCulture);
}
