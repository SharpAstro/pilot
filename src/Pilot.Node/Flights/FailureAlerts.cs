using System.Globalization;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What a look into a failed run says should be done about the PR it hit.</summary>
public enum FailureAnswer
{
    /// <summary>Nothing was read, or nothing can be done about it here: today's handling goes on.</summary>
    Unknown,

    /// <summary>The failing tests are the PR's own: nothing is retried and its handling is today's (the leg goes around).</summary>
    Touched,

    /// <summary>A first failure of a test the PR does not touch: retry it once (a queue removal re-queued, a failed CI job rerun).</summary>
    Retry,

    /// <summary>The retry has been made and its result is not in yet: wait for it.</summary>
    Retrying,

    /// <summary>The failure is on the alert and this flight is held for its investigation: no go-around, re-queue or rerun.</summary>
    Held,

    /// <summary>The failure is on the alert and this hand PR is named on it, not held.</summary>
    Listed,
}

/// <summary>What <see cref="FailureAlerts"/> decided for one look.</summary>
/// <param name="Answer">What to do about the PR.</param>
/// <param name="Finding">The failure the decision is about; null when none was read.</param>
public sealed record FailureDecision(FailureAnswer Answer, FailureFinding? Finding = null)
{
    /// <summary>Whether the failure is the one the investigation is for: the PR's own handling is not to go on.</summary>
    public bool Held => Answer == FailureAnswer.Held;
}

/// <summary>A flight held for an investigation: what it was held in, and why.</summary>
/// <param name="Flight">The held flight's code.</param>
/// <param name="Pr">Its PR; null for a leg check that ran before it had one.</param>
/// <param name="Where">Where it failed (<see cref="FailureWhere"/>), which says how it is released.</param>
/// <param name="Head">The head or queue entry it failed at.</param>
/// <param name="Reason">What the flight reads: <c>held for &lt;investigation code&gt;: &lt;test&gt;</c>.</param>
public sealed record FailureHold(string Flight, int? Pr, string Where, string Head, string Reason, DateTimeOffset At);

/// <summary>
/// The tower on alert for one failure (one signature in one repo): the investigation that looks for its cause, the flights held for it
/// and the hand PRs it is named on. Opened by the first repeat, joined by every later one, closed when the investigation flight lands.
/// </summary>
public sealed class FailureAlert
{
    /// <summary>The repo's folder name.</summary>
    public required string Repo { get; init; }

    /// <summary>The failure's signature (<see cref="FailureSignature"/>).</summary>
    public required string Signature { get; init; }

    /// <summary>The failed test's full name; empty for an error with no test.</summary>
    public required string Test { get; init; }

    /// <summary>The assertion message, or the error line.</summary>
    public required string Message { get; init; }

    /// <summary>The investigation flight's code.</summary>
    public required string Investigation { get; init; }

    /// <summary>The investigation's issue.</summary>
    public required int Issue { get; init; }

    /// <summary>When the alert opened.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The Ring card that says it; empty once it is gone.</summary>
    public string Card { get; set; } = "";

    /// <summary>The flights held for it.</summary>
    public List<FailureHold> Holds { get; } = [];

    /// <summary>The hand PRs it is named on (<c>repo#number</c>), which are listed, not held.</summary>
    public List<string> Listed { get; } = [];

    /// <summary>The latest run that showed it, 0 for none.</summary>
    public long Run { get; set; }

    /// <summary>How the failure reads on a card: the test's short name, or the error line.</summary>
    public string Short => Test.Length > 0 ? FailureSignature.ShortOf(FailureSignature.NameOf(Test)) : LegCommander.OneLine(Message);
}

/// <summary>
/// What a PR does once its failure is under investigation (PIL716, rules 1 and 2). A first failure in a test the PR does not touch
/// is retried once, so the PR is not blocked: a queue removal re-queues through the landing routine, a failed CI job runs again, for
/// a flight's PR and a reviewed hand PR, a line saying so and naming the investigation. A repeat of the same signature, in the same
/// PR after its retry or in another flight, puts the tower on alert: one Ring card to the watchtower (every open card that is not a
/// Handled line is told to it by the node's own messaging) naming the failure, its investigation and the flights, each flight held
/// (no go-around, no re-queue, no rerun) until the investigation lands, a hand PR listed and not held. A later match joins the open
/// alert. The holds are released when the investigation flight lands (<see cref="ReleaseLandedAsync"/>): re-queued, CI rerun, or the
/// leg check run again, as each was held from. Kept in <c>failure-alerts.json</c> in the node's data folder, so a restart keeps the holds.
/// </summary>
public sealed class FailureAlerts(NodeState state, IGitHub github, FailureInvestigations investigations, TimeProvider time, string? file = null)
{
    /// <summary>The Ring card of the tower on alert for a failure.</summary>
    public const string AlertKind = "failure-alert";

    /// <summary>The Handled line of a failure retried once while its investigation runs.</summary>
    public const string RetriedKind = "failure-retried";

    /// <summary>The Handled line of a flight released from its hold when the investigation landed.</summary>
    public const string ReleasedKind = "failure-released";

    /// <summary>How long after a retry the failed run is read again: the retry's own result, not the failure it replaced.</summary>
    public TimeSpan RetryLookAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The leg commander, to run a held leg check again; null for one built without.</summary>
    public LegCommander? Legs { get; set; }

    /// <summary>Runs a held flight's failed leg check again and says how it went; the commander's by default, a test's own to see it asked.</summary>
    public Func<string, CancellationToken, Task<string>>? RerunLegCheck { get; set; }

    /// <summary>Where this says what it would have said on stderr; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The failures the node looks into.</summary>
    public FailureInvestigations Investigations => investigations;

    private readonly object _lock = new();
    private Store? _store;

    // What the first look at a place decided (not a Retry, which the caller acts on), and what the look after its retry did.
    private readonly Dictionary<string, FailureDecision> _first = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FailureDecision> _after = new(StringComparer.Ordinal);

    /// <summary>The alerts file, in <paramref name="dataFolder"/>.</summary>
    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, "failure-alerts.json");

    private sealed class Store
    {
        public List<FailureAlert> Alerts { get; } = [];

        // A place's retry: when it was made and for which failure, by the place's key.
        public Dictionary<string, (DateTimeOffset At, string Signature)> Retried { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The alerts that are open now.</summary>
    public FailureAlert[] Open()
    {
        lock (_lock)
        {
            return [.. Load().Alerts];
        }
    }

    /// <summary>What holds <paramref name="flight"/>, or null when nothing does.</summary>
    public FailureHold? HoldOf(string flight)
    {
        lock (_lock)
        {
            return Load().Alerts.SelectMany(a => a.Holds).FirstOrDefault(h => h.Flight == flight);
        }
    }

    private static string PlaceKey(FailureSite site) => $"{site.Name}|{FailureLedger.IdentityOf(site.Flight, site.Pr)}|{site.Head}|{site.Where}";

    /// <summary>
    /// A PR's failed run, looked into (<see cref="FailureInvestigations.LookAtRunAsync"/>) and answered: at most once a place and head
    /// while nothing was retried, and once more after the retry, when its own result is read. A flight already held is not looked at.
    /// <paramref name="removedAt"/> says which removal a merge queue look is about: one after the retry is its result at once.
    /// </summary>
    public async Task<FailureDecision> LookAtRunAsync(FailureSite site, bool mergeGroup, CancellationToken cancellationToken, DateTimeOffset? removedAt = null)
    {
        if (site.Flight is { } flight && HoldOf(flight) is not null)
        {
            return new FailureDecision(FailureAnswer.Held);
        }
        var key = PlaceKey(site);
        (DateTimeOffset At, string Signature)? retried;
        lock (_lock)
        {
            retried = Load().Retried.TryGetValue(key, out var made) ? made : null;
            if (retried is null && _first.TryGetValue(key, out var first))
            {
                return first;
            }
            if (retried is not null && _after.TryGetValue(key, out var after))
            {
                return after;
            }
        }
        if (retried is { } was && (removedAt is { } removed ? removed <= was.At : time.GetUtcNow() - was.At < RetryLookAfter))
        {
            return new FailureDecision(FailureAnswer.Retrying);
        }
        var look = await investigations.LookAtRunAsync(site, mergeGroup, cancellationToken);
        return Settle(site, key, look, retried?.Signature);
    }

    /// <summary>A leg's failed check, looked into and answered: a leg check is never retried here, so it is held or not.</summary>
    public async Task<FailureDecision> LookAtCheckAsync(FailureSite site, string output, Func<Task<string[]>> changedFiles, CancellationToken cancellationToken)
    {
        if (site.Flight is { } flight && HoldOf(flight) is not null)
        {
            return new FailureDecision(FailureAnswer.Held);
        }
        var look = await investigations.LookAtCheckAsync(site, output, changedFiles, cancellationToken);
        return Settle(site, PlaceKey(site), look, null, cache: false);
    }

    private FailureDecision Settle(FailureSite site, string key, FailureLook look, string? retriedSignature, bool cache = true)
    {
        FailureDecision decision;
        if (!look.Read)
        {
            // Nothing read: after a retry its run may not have failed yet; before one, today's handling goes on.
            decision = retriedSignature is not null ? new FailureDecision(FailureAnswer.Retrying) : new FailureDecision(FailureAnswer.Unknown);
            if (retriedSignature is not null)
            {
                return decision;
            }
        }
        else if (look.Untouched.Count == 0)
        {
            decision = new FailureDecision(FailureAnswer.Touched);
        }
        else if (retriedSignature is not null)
        {
            decision = look.Untouched.FirstOrDefault(f => f.Signature == retriedSignature) is { } again ? Alert(site, again) : new FailureDecision(FailureAnswer.Unknown);
        }
        else
        {
            decision = FirstOf(site, look);
        }
        if (cache && decision.Answer != FailureAnswer.Retry)
        {
            lock (_lock)
            {
                (retriedSignature is null ? _first : _after)[key] = decision;
            }
        }
        return decision;
    }

    private FailureDecision FirstOf(FailureSite site, FailureLook look)
    {
        // An open alert for any of its failures: this PR joins it.
        foreach (var finding in look.Untouched)
        {
            if (OpenAlertFor(site.Name, finding.Signature) is not null)
            {
                return Alert(site, finding);
            }
        }
        // Another flight or PR hit the same failure before: the second occurrence is the repeat.
        if (look.Untouched.FirstOrDefault(f => f.Others) is { } other)
        {
            return Alert(site, other);
        }
        // A leg check has nothing to retry here: the first failure only files the investigation, as before.
        return new FailureDecision(site.Where == FailureWhere.LegCheck ? FailureAnswer.Unknown : FailureAnswer.Retry, look.Untouched[0]);
    }

    private FailureAlert? OpenAlertFor(string repo, string signature)
    {
        lock (_lock)
        {
            return Load().Alerts.FirstOrDefault(a => string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase) && a.Signature == signature);
        }
    }

    /// <summary>Notes that the retry of <paramref name="finding"/> at this place was made: a second failure of it there is the repeat.</summary>
    public void MarkRetried(FailureSite site, FailureFinding finding)
    {
        lock (_lock)
        {
            var store = Load();
            store.Retried[PlaceKey(site)] = (time.GetUtcNow(), finding.Signature);
            Save(store);
        }
    }

    /// <summary>
    /// The Handled line of a failure retried once, naming its investigation and the test; a flight's PR under its flight, a hand PR
    /// under its PR. <paramref name="what"/> says what was done (<c>queued again at 1234567</c>, <c>its failed checks run again</c>).
    /// </summary>
    public void SayRetried(FailureSite site, FailureFinding finding, string what)
    {
        var test = ShortOf(finding);
        var named = finding.Investigation.Length > 0 ? $"{finding.Investigation} investigates" : $"{site.Name}#{finding.Issue} investigates";
        var place = site.Flight is { Length: > 0 } flight ? $"{flight}'s PR #{site.Pr}" : $"{site.Name}#{site.Pr}";
        var subject = site.Flight is { Length: > 0 } code ? Verbs.FlightTarget(code) : Verbs.PrTarget(site.Name, site.Pr ?? 0);
        state.RaiseCard(CardBin.Handled, RetriedKind, subject, $"{place}: {what} once, {test} fails in a PR that does not touch it and {named}",
            $"{test} failed {Where(site.Where)} at {CommitRefs.Short(site.Head)}, a test the PR does not touch. It is a bug with a cause to find, filed as {site.Name}#{finding.Issue}"
            + (finding.Investigation.Length > 0 ? $" and flown as {finding.Investigation}" : "")
            + $". The PR is retried once so it is not blocked; if the same failure comes again the tower goes on alert and the flights hit by it are held until {(finding.Investigation.Length > 0 ? finding.Investigation : "the investigation")} lands.",
            link: site.PrUrl);
    }

    private static string Where(string where) => where switch
    {
        FailureWhere.Queue => "in the merge queue",
        FailureWhere.LegCheck => "in a leg check",
        _ => "in the PR's checks",
    };

    private static string ShortOf(FailureFinding finding) =>
        finding.Test.Length > 0 ? FailureSignature.ShortOf(FailureSignature.NameOf(finding.Test)) : LegCommander.OneLine(finding.Message);

    /// <summary>
    /// A re-queue of a flight's PR through the landing routine (<see cref="LegCommander.LandPrAsync"/>) at <paramref name="head"/>, by the
    /// repo's merge method.
    /// </summary>
    public Task<LandOutcome> RequeueAsync(string repoPath, int number, string head, CancellationToken cancellationToken) =>
        LegCommander.LandPrAsync(github, repoPath, number, head, Legs?.Models.MergeMethod ?? "squash", cancellationToken);

    // The investigation's flight, if the node knows it: landed means the failure is no longer under investigation.
    private FlightRow? InvestigationFlight(string code) => state.Flights().FirstOrDefault(f => f.Code == code);

    private FailureDecision Alert(FailureSite site, FailureFinding finding)
    {
        var flightSite = site.Flight is { Length: > 0 };
        // Nothing to hold the flights for: no investigation flight could be made, or it landed already. Today's handling goes on.
        if (finding.Investigation.Length == 0 || InvestigationFlight(finding.Investigation) is { State: FlightState.Landed } || site.Flight == finding.Investigation)
        {
            return new FailureDecision(FailureAnswer.Unknown, finding);
        }
        lock (_lock)
        {
            var store = Load();
            var alert = store.Alerts.FirstOrDefault(a => string.Equals(a.Repo, site.Name, StringComparison.OrdinalIgnoreCase) && a.Signature == finding.Signature);
            var opened = alert is null;
            alert ??= new FailureAlert
            {
                Repo = site.Name, Signature = finding.Signature, Test = finding.Test, Message = finding.Message, Investigation = finding.Investigation, Issue = finding.Issue, At = time.GetUtcNow(),
            };
            if (opened)
            {
                store.Alerts.Add(alert);
                // Every flight and hand PR the failure hit before this one is on the alert from the start.
                foreach (var record in investigations.Ledger.Of(site.Name, finding.Signature))
                {
                    Add(alert, record.Flight, record.Pr, record.Where, record.Head, site.Name);
                }
            }
            Add(alert, site.Flight, site.Pr, site.Where, site.Head, site.Name);
            if (finding.RunId > 0)
            {
                alert.Run = finding.RunId;
            }
            Save(store);
            Say(alert, opened, site);
        }
        return new FailureDecision(flightSite ? FailureAnswer.Held : FailureAnswer.Listed, finding);
    }

    // A flight that is still flying is held; a hand PR is listed. The alert's investigation flight is neither.
    private void Add(FailureAlert alert, string? flight, int? pr, string where, string head, string repo)
    {
        if (flight is { Length: > 0 })
        {
            if (flight == alert.Investigation || state.Flights().FirstOrDefault(f => f.Code == flight) is not { State: not FlightState.Landed })
            {
                return;
            }
            var hold = new FailureHold(flight, pr, where, head, $"held for {alert.Investigation}: {alert.Short}", time.GetUtcNow());
            var at = alert.Holds.FindIndex(h => h.Flight == flight);
            if (at >= 0)
            {
                alert.Holds[at] = hold with { At = alert.Holds[at].At };
            }
            else
            {
                alert.Holds.Add(hold);
            }
        }
        else if (pr is { } number && $"{repo}#{number}" is var listed && !alert.Listed.Contains(listed))
        {
            alert.Listed.Add(listed);
        }
    }

    // One Ring card a failure: raised when the alert opens, kept up to date as flights join. The watchtower is told it by the node's own
    // messaging, as every open card that is not a Handled line is.
    private void Say(FailureAlert alert, bool opened, FailureSite site)
    {
        var subject = Verbs.FlightTarget(alert.Investigation);
        var held = alert.Holds.Count;
        var title = $"{alert.Short} fails again: the tower is on alert, {alert.Investigation} investigates"
            + (held > 0 ? $", {held} {(held == 1 ? "flight" : "flights")} held" : "")
            + (alert.Listed.Count > 0 ? $", {alert.Listed.Count} hand {(alert.Listed.Count == 1 ? "PR" : "PRs")} listed" : "");
        var lines = new List<string>
        {
            $"{alert.Short} failed a second time in a PR that does not touch it ({Where(site.Where)}), so it is no chance failure: the cause is being looked for in {alert.Investigation} ({alert.Repo}#{alert.Issue}, priority high).",
            "",
            $"Test: {(alert.Test.Length > 0 ? alert.Test : "none, an error line")}",
            $"Assertion message: {alert.Message}",
            "",
            held > 0 ? "Held, with no go-around, re-queue or rerun until the investigation lands:" : "Held: none.",
        };
        lines.AddRange(alert.Holds.Select(h => $"- {h.Flight}{(h.Pr is { } pr ? $" (PR #{pr})" : "")}, failed {Where(h.Where)} at {CommitRefs.Short(h.Head)}"));
        if (alert.Listed.Count > 0)
        {
            lines.Add("");
            lines.Add($"Hand PRs it hit, listed and not held: {string.Join(", ", alert.Listed)}.");
        }
        lines.Add("");
        lines.Add($"When {alert.Investigation} lands each hold is released: a queue removal is re-queued, failed CI is run again, a leg check is run again.");
        var detail = string.Join('\n', lines);
        var summary = $"{alert.Short} has failed in more than one PR that does not touch it. {alert.Investigation} looks for the cause; the flights it hit are held, with no go-around, re-queue or rerun, until it lands. Nothing to do unless the investigation needs help.";
        if (!opened && state.Cards(openOnly: true).FirstOrDefault(c => c.Id == alert.Card) is { } card)
        {
            state.UpdateCard(card.Id, title, detail, card.Choices, card.Link, summary: summary);
            return;
        }
        alert.Card = state.RaiseCard(CardBin.Ring, AlertKind, subject, title, detail, summary: summary, link: site.PrUrl is { } url && url.LastIndexOf("/pull/", StringComparison.Ordinal) is var at and > 0
            ? $"{url[..at]}/issues/{alert.Issue.ToString(CultureInfo.InvariantCulture)}" : null).Id;
    }

    /// <summary>
    /// Releases the holds of every alert whose investigation flight has landed, each with a Handled line, and does what each was held
    /// from: a queue removal is re-queued through the landing routine, failed CI is run again, a leg check is run again. Closes the
    /// alert and its card. Run at every look.
    /// </summary>
    public async Task ReleaseLandedAsync(CancellationToken cancellationToken)
    {
        FailureAlert[] landed;
        lock (_lock)
        {
            landed = [.. Load().Alerts.Where(a => InvestigationFlight(a.Investigation) is { State: FlightState.Landed })];
        }
        foreach (var alert in landed)
        {
            FailureHold[] holds;
            lock (_lock)
            {
                holds = [.. alert.Holds];
                // The holds are let go first, so nothing below is refused as held.
                alert.Holds.Clear();
                Save(Load());
            }
            foreach (var hold in holds)
            {
                string said;
                try
                {
                    said = await ReleaseAsync(alert, hold, cancellationToken);
                }
                catch (Exception ex) when (ex is FlightException or IOException)
                {
                    said = $"it could not be redone: {ex.Message}";
                }
                state.RaiseCard(CardBin.Handled, ReleasedKind, Verbs.FlightTarget(hold.Flight), $"{hold.Flight} is released from its hold: {alert.Investigation} landed, {said}",
                    $"{hold.Flight} was {hold.Reason}, its failure {Where(hold.Where)} at {CommitRefs.Short(hold.Head)}. {alert.Investigation} landed, so the hold is released and {said}.");
            }
            state.CloseCardsAbout(Verbs.FlightTarget(alert.Investigation), $"{alert.Investigation} landed: the holds are released", AlertKind);
            if (alert.Card.Length > 0)
            {
                state.CloseCard(alert.Card, $"{alert.Investigation} landed: the holds are released", "node");
            }
            lock (_lock)
            {
                var store = Load();
                store.Alerts.Remove(alert);
                // What each was held from was just done again, which is its retry: a failure of it again is read after the time a retry is given.
                foreach (var hold in holds)
                {
                    var key = PlaceKey(new FailureSite(alert.Repo, "", hold.Pr, hold.Flight, hold.Head, hold.Where));
                    store.Retried[key] = (time.GetUtcNow(), alert.Signature);
                    _first.Remove(key);
                    _after.Remove(key);
                }
                Save(store);
            }
        }
    }

    private async Task<string> ReleaseAsync(FailureAlert alert, FailureHold hold, CancellationToken cancellationToken)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == hold.Flight) is not { State: FlightState.Flying } flight)
        {
            return "its flight is not flying any more, so there is nothing to redo";
        }
        switch (hold.Where)
        {
            case FailureWhere.Queue:
            {
                var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
                if (pr is not { State: "OPEN" } || (hold.Pr is { } held && pr.Number != held))
                {
                    return "its PR is not open any more, so it is not queued again";
                }
                var outcome = await RequeueAsync(flight.RepoPath, pr.Number, pr.Head, cancellationToken);
                return outcome.Kind switch
                {
                    LandKind.Queued => $"#{pr.Number} is queued again at {CommitRefs.Short(outcome.Head)}",
                    LandKind.Merged => $"#{pr.Number} merged at {CommitRefs.Short(outcome.Head)}",
                    LandKind.Armed => $"#{pr.Number}'s merge is armed at {CommitRefs.Short(outcome.Head)}",
                    LandKind.RetryLater => $"GitHub did not answer, so #{pr.Number} is queued again by its card",
                    _ => $"GitHub refused to queue #{pr.Number} again: {LegCommander.OneLine(outcome.Said ?? "no reason given")}",
                };
            }
            case FailureWhere.Ci:
            {
                if (hold.Pr is not { } number)
                {
                    return "it has no PR to run checks of";
                }
                var runs = await github.RerunFailedChecksAsync(flight.RepoPath, number, cancellationToken);
                return runs > 0 ? $"#{number}'s failed checks run again" : $"#{number} had no failed run to run again";
            }
            default:
                return RerunLegCheck is { } rerun ? await rerun(flight.Code, cancellationToken)
                    : Legs is null ? "its leg check is run again by its card" : await Legs.RerunHeldCheckAsync(flight.Code, cancellationToken);
        }
    }

    // ---- the file: alerts and retries, kept so a restart keeps the holds ----

    private Store Load()
    {
        if (_store is not null)
        {
            return _store;
        }
        var store = new Store();
        try
        {
            if (file is not null && File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("retried", out var retried))
                {
                    foreach (var item in retried.EnumerateArray())
                    {
                        if (Text(item, "key") is { } key && Text(item, "signature") is { } signature && Time(item, "at") is { } at)
                        {
                            store.Retried[key] = (at, signature);
                        }
                    }
                }
                if (root.TryGetProperty("alerts", out var alerts))
                {
                    foreach (var item in alerts.EnumerateArray())
                    {
                        if (ReadAlert(item) is { } alert)
                        {
                            store.Alerts.Add(alert);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A broken or unreadable file is an empty store: the next failure writes it afresh.
        }
        return _store = store;
    }

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement item, string name) =>
        Text(item, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static FailureAlert? ReadAlert(JsonElement item)
    {
        if (Text(item, "repo") is not { } repo || Text(item, "signature") is not { } signature || Text(item, "investigation") is not { } investigation || Time(item, "at") is not { } at)
        {
            return null;
        }
        var alert = new FailureAlert
        {
            Repo = repo, Signature = signature, Test = Text(item, "test") ?? "", Message = Text(item, "message") ?? "", Investigation = investigation,
            Issue = item.TryGetProperty("issue", out var issue) && issue.TryGetInt32(out var number) ? number : 0, At = at,
            Card = Text(item, "card") ?? "", Run = item.TryGetProperty("run", out var run) && run.TryGetInt64(out var id) ? id : 0,
        };
        if (item.TryGetProperty("holds", out var holds))
        {
            foreach (var hold in holds.EnumerateArray())
            {
                if (Text(hold, "flight") is { } flight && Time(hold, "at") is { } held)
                {
                    alert.Holds.Add(new FailureHold(flight, hold.TryGetProperty("pr", out var pr) && pr.TryGetInt32(out var n) ? n : null, Text(hold, "where") ?? "", Text(hold, "head") ?? "", Text(hold, "reason") ?? "", held));
                }
            }
        }
        if (item.TryGetProperty("listed", out var listed))
        {
            alert.Listed.AddRange(listed.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!));
        }
        return alert;
    }

    private void Save(Store store)
    {
        if (file is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temp = file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteStartArray("retried");
                foreach (var (key, made) in store.Retried)
                {
                    json.WriteStartObject();
                    json.WriteString("key", key);
                    json.WriteString("signature", made.Signature);
                    json.WriteString("at", made.At.ToString("O", CultureInfo.InvariantCulture));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("alerts");
                foreach (var a in store.Alerts)
                {
                    json.WriteStartObject();
                    json.WriteString("repo", a.Repo);
                    json.WriteString("signature", a.Signature);
                    json.WriteString("test", a.Test);
                    json.WriteString("message", a.Message);
                    json.WriteString("investigation", a.Investigation);
                    json.WriteNumber("issue", a.Issue);
                    json.WriteString("at", a.At.ToString("O", CultureInfo.InvariantCulture));
                    json.WriteString("card", a.Card);
                    json.WriteNumber("run", a.Run);
                    json.WriteStartArray("holds");
                    foreach (var h in a.Holds)
                    {
                        json.WriteStartObject();
                        json.WriteString("flight", h.Flight);
                        if (h.Pr is { } pr)
                        {
                            json.WriteNumber("pr", pr);
                        }
                        json.WriteString("where", h.Where);
                        json.WriteString("head", h.Head);
                        json.WriteString("reason", h.Reason);
                        json.WriteString("at", h.At.ToString("O", CultureInfo.InvariantCulture));
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteStartArray("listed");
                    foreach (var l in a.Listed)
                    {
                        json.WriteStringValue(l);
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"failures: could not write {file}: {ex.Message}");
        }
    }
}
