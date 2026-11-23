using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What starts an investigator run (PIL1285).</summary>
public enum InvestigationTrigger
{
    /// <summary>A find-the-cause task (<see cref="CardKinds.RepeatTask"/>): the same card or failure came a second time.</summary>
    Repeat,

    /// <summary>A flight in the air past the stale bound (<see cref="StaleSection"/>).</summary>
    Stale,

    /// <summary>A card on its way to the ring: it waits in For you, quietly, until the run's outcome.</summary>
    Ring,

    /// <summary>The digest: a run reads it and may file or act.</summary>
    Digest,
}

/// <summary>What became of a trigger.</summary>
public enum InvestigationStart
{
    /// <summary>A run was started on the subject.</summary>
    Started,

    /// <summary>A run on the same subject was live: the trigger joined it.</summary>
    Joined,

    /// <summary>The per-day bound was reached: a held card rings saying so, any other trigger rings one bound card.</summary>
    Bound,

    /// <summary>The start gate holds starts (the 5-hour window, an outage): a held card rings saying so, any other trigger is taken again later.</summary>
    Held,

    /// <summary>The switch is off: nothing is started.</summary>
    Off,
}

/// <summary>One live investigator run: its subject, the session, what triggered it, the evidence it was given and the cards it holds back from the ring.</summary>
public sealed class InvestigationRun
{
    /// <summary>The subject's key: a flight code (<c>PIL12</c>), a card id (<c>c2737</c>) or <c>digest</c>.</summary>
    public required string Key { get; init; }

    /// <summary>What the subject is as a target (<c>flight/PIL12</c>, <c>card/c2737</c>, <c>node</c>): where its Handled line and decision go.</summary>
    public required string Target { get; init; }

    /// <summary>The session's name, <c>&lt;machine&gt;-investigate-&lt;subject&gt;</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The session's job id once it started; null while it starts.</summary>
    public string? Job { get; set; }

    public required DateTimeOffset StartedAt { get; init; }

    public List<InvestigationTrigger> Triggers { get; } = [];

    public List<string> Evidence { get; } = [];

    /// <summary>The cards on their way to the ring that wait for this run's outcome.</summary>
    public List<string> Held { get; } = [];
}

/// <summary>
/// Autopilot's thinking tier (PIL1285, the second comment of #1285): the node starts an investigator, a short Opus session with the
/// evidence in its brief, on a find-the-cause task, a flight past its stale bound, a card about to ring the person, and the digest. A run
/// ends with exactly one outcome, reported back by <see cref="Verbs.Investigated"/>: an issue plus a flight, an action recorded with its
/// reason, or one ring to the person that says what was tried and what is needed. Each outcome is a Handled line and a decision by
/// <see cref="DecisionByKinds"/> <c>investigator</c>, and the node ends the session through its own path. With the switch on, a card
/// bound for the ring waits in For you until a run decides it (<see cref="Holds"/>); with it off nothing here runs and every ring is as before.
/// One run per subject at a time (a second trigger joins it), under the <see cref="StartGate"/> and <see cref="RoleModels.AutopilotInvestigations"/> a local day.
/// </summary>
public sealed partial class Investigator(NodeState state, IWorkerLauncher workers, RoleModels models, StartGate gate, FailureLedger failures, TimeProvider time)
{
    /// <summary>The Handled line an outcome leaves.</summary>
    public const string HandledKind = "investigation";

    /// <summary>The one ring of a run whose subject has no held card (a stale flight, a repeat, the digest).</summary>
    public const string RingKind = "investigator-ring";

    /// <summary>The one ring that says the per-day bound is reached, for a trigger with no card of its own to ring.</summary>
    public const string BoundKind = "investigator-bound";

    /// <summary>The Handled card the start gate raises while it holds an investigator's start.</summary>
    public const string GateKind = "investigator-held";

    /// <summary>The decision's kind and its <see cref="DecisionRow.ByKind"/>.</summary>
    public const string DecisionKind = "investigation";
    public const string By = "investigator";

    /// <summary>The three endings, as <see cref="Verbs.Investigated"/> takes them.</summary>
    public const string IssueOutcome = "issue";
    public const string ActionOutcome = "action";
    public const string RingOutcome = "ring";
    public static readonly IReadOnlyList<string> Outcomes = [IssueOutcome, ActionOutcome, RingOutcome];

    /// <summary>What a card's detail starts with once a run decided it needs nothing of the person.</summary>
    public const string QuietPrefix = "Not a ring (investigator): ";

    /// <summary>What a ringing card's detail starts with: the run's words.</summary>
    public const string TriedPrefix = "What was tried and what is needed (investigator): ";

    /// <summary>The Handled line of a run that started: it counts toward the day's bound, and its <c>seen:</c> lines are the triggers it took, kept past a restart.</summary>
    public const string StartKind = "investigation-start";

    /// <summary>The Handled line of a trigger that joined a live run, with its <c>seen:</c> line.</summary>
    public const string JoinKind = "investigation-join";

    private const string SeenLead = "seen: ";

    /// <summary>
    /// The words put in front of the detail of a card waiting for an investigator. They mark the card as held, so a node that restarted
    /// while a run was live still finds it and rings it (<see cref="Look"/>); a decision takes them off. They are looked for anywhere in
    /// the detail, not only at its start: a card on a flight the owner holds gets the hold's words in front of them (#771).
    /// </summary>
    public const string HeldLead = "Held for an investigator (it rings only if one decides it must): ";

    /// <summary>How long a held card may wait with no run holding it before a look rings it (a card just raised is between its raise and its hold).</summary>
    private static readonly TimeSpan HeldGrace = TimeSpan.FromMinutes(2);

    /// <summary>Whether a card's detail carries <see cref="HeldLead"/>, wherever it sits.</summary>
    public static bool IsHeld(string? detail) => detail is not null && detail.Contains(HeldLead, StringComparison.Ordinal);

    /// <summary>The detail without <see cref="HeldLead"/>, wherever it sits.</summary>
    public static string Unheld(string? detail)
    {
        if (detail is null)
        {
            return "";
        }
        var at = detail.IndexOf(HeldLead, StringComparison.Ordinal);
        return at < 0 ? detail : detail[..at] + detail[(at + HeldLead.Length)..];
    }

    /// <summary>The machine's code, lower case, that starts every session name.</summary>
    public string Machine { get; init; } = "node";

    /// <summary>Whether the switch is on.</summary>
    public Func<bool> On { get; set; } = () => models.Autopilot;

    /// <summary>The checkout a run of a flight's subject starts in (its repo's main checkout); with null, or for a subject of no flight, <see cref="DefaultFolder"/>.</summary>
    public Func<string, string?>? RepoOf { get; set; }

    /// <summary>Where a run about no flight starts (the watchtower's folder on a node).</summary>
    public string DefaultFolder { get; init; } = ".";

    /// <summary>The pilot command a brief writes its commands with.</summary>
    public string Pilot { get; init; } = "pilot";

    /// <summary>How long a run may go without an outcome before the node ends it and rings what it held.</summary>
    public TimeSpan RunFor { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>Where a start, an outcome and a fall back are noted; null notes none.</summary>
    public Action<string>? Log { get; set; }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, InvestigationRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTimeOffset> _starts = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<Task> _running = [];
    private bool _seeded;
    private bool _recovered;

    /// <summary>The live runs.</summary>
    public IReadOnlyList<InvestigationRun> Runs
    {
        get
        {
            lock (_lock)
            {
                return [.. _runs.Values];
            }
        }
    }

    /// <summary>Completes when every start begun so far has been made.</summary>
    public Task SettledAsync()
    {
        lock (_lock)
        {
            return Task.WhenAll(_running);
        }
    }

    /// <summary>The runs started this local day.</summary>
    public int StartedToday
    {
        get
        {
            var today = Day(time.GetUtcNow());
            lock (_lock)
            {
                Seed();
                return _starts.Count(s => Day(s) == today);
            }
        }
    }

    // What an earlier node left (its start and join lines) is what this one knows of the day's starts and the triggers taken: a restart
    // neither starts the day's count again from 0 nor takes a find-the-cause task or a stale flight a second time. Called with the lock held.
    private void Seed()
    {
        if (_seeded)
        {
            return;
        }
        _seeded = true;
        foreach (var card in state.Cards().Where(c => c.Kind is StartKind or JoinKind))
        {
            if (card.Kind == StartKind)
            {
                _starts.Add(card.At);
            }
            foreach (var line in (card.Detail ?? "").Split('\n').Where(l => l.StartsWith(SeenLead, StringComparison.Ordinal)))
            {
                _seen.Add(line[SeenLead.Length..].Trim());
            }
        }
    }

    private DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, time.LocalTimeZone).DateTime);

    /// <summary>
    /// Whether a card of <paramref name="kind"/> raised to ring waits for an investigator instead (<see cref="NodeState.RaiseCard"/>): with the
    /// switch on and a bound above 0, every ring but the investigator's own and what a session asks the person directly.
    /// </summary>
    public bool Holds(string kind, IReadOnlyList<CardChoice>? choices) =>
        On() && models.AutopilotInvestigations > 0
        && kind is not (RingKind or BoundKind or Watchtower.AskKind or CardKinds.AssistantAsk);

    /// <summary>A card just filed in For you on its way to the ring: one run decides whether it rings; past the bound or held by the gate it rings now, saying why.</summary>
    public void Hold(CardRow card)
    {
        var (key, target) = SubjectOf(card.Subject, card.Id);
        var detail = Unheld(card.Detail);
        var evidence = $"Card {card.Id} ({card.Kind}) about {card.Subject}, raised {Stamp(card.At)} to ring: {card.Title}"
            + (string.IsNullOrWhiteSpace(detail) ? "" : $"\n{detail.Trim()}")
            + (card.Choices.Length > 0 ? $"\nIt offers: {string.Join("; ", card.Choices.Select(c => $"{c.Label} ({c.Verb} {c.Target} {string.Join(' ', c.Args ?? [])})".Trim()))}" : "");
        var began = Begin(InvestigationTrigger.Ring, key, target, [evidence], card.Id);
        if (began is InvestigationStart.Bound or InvestigationStart.Held or InvestigationStart.Off)
        {
            var why = began switch
            {
                InvestigationStart.Bound => $"the investigator bound of {models.AutopilotInvestigations} runs a day is reached, so it rings as it would have",
                InvestigationStart.Held => "no investigator could start now (the start gate holds starts), so it rings as it would have",
                _ => "the switch is off, so it rings as it would have",
            };
            RingCard(card.Id, $"Rung without an investigation: {why}.");
        }
    }

    /// <summary>
    /// A trigger on <paramref name="key"/>: joins the live run on it, or starts one under the bound and the gate. <paramref name="held"/> is a
    /// card that waits for the outcome. Never throws for a start that fails: the held cards ring and say so.
    /// </summary>
    public InvestigationStart Begin(InvestigationTrigger trigger, string key, string target, IReadOnlyList<string> evidence, string? held = null, string? seen = null)
    {
        if (!On())
        {
            return InvestigationStart.Off;
        }
        InvestigationRun run;
        lock (_lock)
        {
            Seed();
            if (_runs.TryGetValue(key, out var live))
            {
                live.Triggers.Add(trigger);
                live.Evidence.AddRange(evidence);
                if (held is not null && !live.Held.Contains(held))
                {
                    live.Held.Add(held);
                }
                Log?.Invoke($"investigator: {trigger} on {key} joins {live.Name}");
                // Said as a line, so a restart knows the trigger was taken.
                state.RaiseCard(CardBin.Handled, JoinKind, target, $"A {trigger} trigger on {key} joined {live.Name}", SeenDetail($"{trigger} on {key} ({live.Name}).", seen));
                return InvestigationStart.Joined;
            }
            var today = Day(time.GetUtcNow());
            if (_starts.Count(s => Day(s) == today) >= models.AutopilotInvestigations)
            {
                Log?.Invoke($"investigator: {trigger} on {key} is past the bound of {models.AutopilotInvestigations} a day");
                if (held is null)
                {
                    RingBound(trigger, key, target);
                }
                return InvestigationStart.Bound;
            }
            if (gate.HoldBecause($"investigate {key}", models.StartLimit, models.WeekLimit, GateKind, target, $"An investigator of {key} waits to start") is { } heldBy)
            {
                Log?.Invoke($"investigator: {trigger} on {key} held ({heldBy.Say})");
                return InvestigationStart.Held;
            }
            run = new InvestigationRun { Key = key, Target = target, Name = NameOf(key), StartedAt = time.GetUtcNow() };
            run.Triggers.Add(trigger);
            run.Evidence.AddRange(evidence);
            if (held is not null)
            {
                run.Held.Add(held);
            }
            _runs[key] = run;
            _starts.Add(run.StartedAt);
            state.RaiseCard(CardBin.Handled, StartKind, target, $"An investigator of {key} started on {trigger}", SeenDetail($"{run.Name} on {string.Join(", ", run.Triggers)}.", seen));
        }
        // Started outside the lock: a launcher that fails at once rings the held cards, which goes through the node's state.
        var start = StartAsync(run);
        lock (_lock)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(start);
        }
        return InvestigationStart.Started;
    }

    private static string SeenDetail(string said, string? seen) => seen is null ? said : $"{said}\n{SeenLead}{seen}";

    private async Task StartAsync(InvestigationRun run)
    {
        try
        {
            var flight = run.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? run.Key : null;
            var folder = flight is not null && RepoOf?.Invoke(flight) is { Length: > 0 } repo ? repo : DefaultFolder;
            string[] evidence;
            lock (_lock)
            {
                evidence = [.. run.Evidence, .. Context(run)];
            }
            var brief = Briefs.Investigator(run.Name, run.Key, run.Target, run.Triggers, evidence, Pilot);
            var job = await workers.StartAsync(new WorkerStart(run.Name, folder, null, brief, models.Model(RoleModels.Investigator), models.Effort(RoleModels.Investigator),
                Reason: SessionStartReasons.Investigator), CancellationToken.None).ConfigureAwait(false);
            bool live;
            lock (_lock)
            {
                run.Job = job;
                live = _runs.TryGetValue(run.Key, out var current) && ReferenceEquals(current, run);
            }
            Log?.Invoke($"investigator: {run.Name} started ({job}) on {string.Join(", ", run.Triggers)}");
            if (!live)
            {
                // The run was released while its session was starting (the switch went off, the time ran out): nothing else would stop it.
                await StopAsync(job, $"{SessionEndReasons.Person}: the run ended while its session started").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"investigator: {run.Name} could not start ({ex.Message})");
            End(run, null);
            foreach (var id in run.Held)
            {
                RingCard(id, $"Rung without an investigation: the investigator could not start ({ex.Message}).");
            }
        }
    }

    /// <summary>What the node adds to a flight's evidence: its status lines, its legs, its open cards, its jobs and the failures the ledger holds for it.</summary>
    private IEnumerable<string> Context(InvestigationRun run)
    {
        if (!run.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) || state.Flights().FirstOrDefault(f => string.Equals(f.Code, run.Key, StringComparison.OrdinalIgnoreCase)) is not { } flight)
        {
            yield break;
        }
        yield return $"Flight {flight.Code} ({flight.Repo}): {flight.State}{(flight.OnHold is { } hold ? $", on hold by {hold.By} ({hold.Reason})" : "")}{(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}";
        foreach (var leg in state.Legs().Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
        {
            yield return $"Leg {leg.Name}: {leg.State}, {leg.GoArounds} go-around(s){(leg.Worker is { } worker ? $", worker {worker}" : "")}";
        }
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Subject.StartsWith(Verbs.FlightTarget(flight.Code), StringComparison.Ordinal)).Take(10))
        {
            yield return $"Open card {card.Id} ({card.Kind}, {card.Bin}): {card.Title}";
        }
        foreach (var job in state.Jobs().Where(j => j.Flight == flight.Code).OrderByDescending(j => j.StartedAt).Take(8))
        {
            yield return $"Job {job.Id}: {job.State}{(job.ExitCode is { } code ? $" (exit {code})" : "")}, started {Stamp(job.StartedAt)}: {Cut(job.Command)}";
        }
        foreach (var failure in failures.All().Where(r => string.Equals(r.Flight, flight.Code, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.At).Take(8))
        {
            yield return $"Failure {failure.Signature} at {failure.Where} {Stamp(failure.At)}, head {failure.Head}, test {(failure.Test.Length > 0 ? failure.Test : "none")}: {Cut(failure.Message)}";
        }
    }

    /// <summary>
    /// The run's one outcome, from <paramref name="who"/> (the investigator's session name): recorded as a Handled line and a decision with the why,
    /// the held cards rung once (ring) or left in For you with the reason, and the session ended by the node. Returns why it is refused, or null.
    /// </summary>
    public string? Outcome(string who, string subject, string outcome, string why)
    {
        why = why.Trim();
        if (!Outcomes.Contains(outcome))
        {
            return $"{Verbs.Investigated} takes {string.Join(", ", Outcomes)}, not {outcome}";
        }
        if (why.Length == 0)
        {
            return outcome == RingOutcome
                ? $"{Verbs.Investigated} ring needs what was tried and what is needed: they are the ring's words"
                : $"{Verbs.Investigated} {outcome} needs why, naming the evidence";
        }
        if (outcome != RingOutcome && !Standing.NamesEvidence(why))
        {
            return outcome == IssueOutcome
                ? $"{Verbs.Investigated} issue names the issue it filed (#123) and the flight it created in its why"
                : $"{Verbs.Investigated} action names the evidence it acted on (a job id such as j962, a card id, a commit, a run) in its why";
        }
        InvestigationRun? run;
        lock (_lock)
        {
            run = _runs.Values.FirstOrDefault(r => string.Equals(r.Name, who, StringComparison.Ordinal))
                ?? (_runs.TryGetValue(KeyOf(subject), out var bySubject) ? bySubject : null);
        }
        if (run is null)
        {
            return $"no investigation of {subject} is running: an outcome is reported once, by the run the node started";
        }
        var words = outcome switch
        {
            IssueOutcome => "filed an issue and a flight",
            ActionOutcome => "acted",
            _ => "rang the person",
        };
        state.RaiseCard(CardBin.Handled, HandledKind, run.Target, $"The investigator of {run.Key} {words}", $"{why}\nTriggered by: {string.Join(", ", run.Triggers.Distinct())} ({run.Name}).");
        state.RecordDecision(run.Target, DecisionKind, $"What does {run.Key} need ({string.Join(", ", run.Triggers.Distinct())})?", [.. Outcomes], outcome, who, By, why);
        if (outcome == RingOutcome)
        {
            var rung = run.Held.Count(id => RingCard(id, TriedPrefix + why));
            if (rung == 0)
            {
                // One ring per decision: the subject has no card waiting, so the ring is the run's own.
                state.RaiseCard(CardBin.Ring, RingKind, run.Target, $"{run.Key} needs you: the investigator could not settle it", TriedPrefix + why,
                    [new CardChoice { Label = "Hand it to the watchtower", Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = [$"The investigator's ring on {run.Key}: {Cut(why)} Take it up as I would."] }]);
            }
        }
        else
        {
            foreach (var id in run.Held)
            {
                QuietCard(id, why);
            }
        }
        End(run, $"{SessionEndReasons.Investigated}: {outcome}");
        Log?.Invoke($"investigator: {run.Name} ended with {outcome} ({Cut(why)})");
        return null;
    }

    /// <summary>
    /// One look: runs past <see cref="RunFor"/> ended with their held cards rung, and the triggers taken: new find-the-cause tasks, flights past
    /// the stale bound, the open digest. Off, a live run's held cards ring as they would have and the run is ended.
    /// </summary>
    public void Look()
    {
        var now = time.GetUtcNow();
        if (!On())
        {
            foreach (var run in Runs)
            {
                Release(run, "the switch was turned off before it decided", SessionEndReasons.Person);
            }
            RingStranded(now, "the switch is off");
            return;
        }
        foreach (var run in Runs.Where(r => now - r.StartedAt >= RunFor))
        {
            Release(run, $"the investigator gave no outcome in {Waited.Coarse(RunFor)}", SessionEndReasons.InvestigatorTimedOut);
        }

        RingStranded(now, null);
        var cards = state.Cards();
        foreach (var task in cards.Where(c => c.Kind == CardKinds.RepeatTask && now - c.At < TimeSpan.FromDays(1)).OrderBy(c => c.At))
        {
            var (key, target) = SubjectOf(task.Subject, task.Id);
            Take($"repeat:{task.Id}", InvestigationTrigger.Repeat, key, target, $"Find-the-cause task {task.Id} raised {Stamp(task.At)}: {task.Title}\n{task.Detail}");
        }
        foreach (var item in StaleSection.Read(state, now, models.Stale).Where(i => i.Kind == StaleKind.Flight && i.Flight is not null))
        {
            var since = (now - item.Age).ToUnixTimeSeconds() / 60;
            Take($"stale:{item.Flight}:{since}", InvestigationTrigger.Stale, item.Flight!, Verbs.FlightTarget(item.Flight!), $"Past the stale bound: {StaleSection.Line(item, state.Flights())}");
        }
        foreach (var digest in cards.Where(c => c.Kind == Digest.Kind && c.State == CardState.Open))
        {
            Take($"digest:{digest.Id}", InvestigationTrigger.Digest, "digest", Verbs.NodeTarget, $"The digest {digest.Id}, raised {Stamp(digest.At)}: {digest.Title}\n{digest.Detail}");
        }
    }

    // A card held for an investigator that no live run holds is stranded: its run died with the node (an update, a settings save) or
    // never began. The first look of a node rings what the last one left, saying so; a later look rings one that has waited past the
    // grace, which is more than the instant between a card's raise and its hold. Without this the person would never hear of it.
    private void RingStranded(DateTimeOffset now, string? why)
    {
        bool first;
        HashSet<string> holding;
        lock (_lock)
        {
            first = !_recovered;
            _recovered = true;
            holding = [.. _runs.Values.SelectMany(r => r.Held)];
        }
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Bin == CardBin.ForYou && IsHeld(c.Detail) && !holding.Contains(c.Id)))
        {
            if (first || now - card.At >= HeldGrace)
            {
                RingCard(card.Id, $"Rung without an investigation: {why ?? (first ? "the node restarted before an investigator decided it" : "no investigator holds it")}.");
            }
        }
    }

    // A trigger the look found: taken once, unless the gate held it (it is taken again at a later look).
    private void Take(string seen, InvestigationTrigger trigger, string key, string target, string evidence)
    {
        lock (_lock)
        {
            Seed();
            if (_seen.Contains(seen))
            {
                return;
            }
        }
        if (Begin(trigger, key, target, [evidence], seen: seen) is InvestigationStart.Held or InvestigationStart.Off)
        {
            return;
        }
        lock (_lock)
        {
            _seen.Add(seen);
        }
    }

    private void Release(InvestigationRun run, string why, string reason)
    {
        foreach (var id in run.Held)
        {
            RingCard(id, $"Rung without an investigation: {why}.");
        }
        state.RaiseCard(CardBin.Handled, HandledKind, run.Target, $"The investigator of {run.Key} ended with no outcome", $"{why} ({run.Name}); what it held rang as it would have.");
        End(run, $"{reason}: {why}");
    }

    private void End(InvestigationRun run, string? reason)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(run.Key, out var live) && ReferenceEquals(live, run))
            {
                _runs.Remove(run.Key);
            }
        }
        if (reason is not null && run.Job is { } job)
        {
            lock (_lock)
            {
                _running.RemoveAll(t => t.IsCompleted);
                _running.Add(StopAsync(job, reason));
            }
        }
    }

    private async Task StopAsync(string job, string reason)
    {
        try
        {
            await workers.StopAsync(job, reason, "node", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"investigator: ending {job} failed ({ex.Message})");
        }
    }

    // Rings a held card once, with the words in front of its detail; false when it is closed, gone or rung already.
    private bool RingCard(string id, string words)
    {
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Id == id) is not { Bin: not CardBin.Ring } now)
        {
            return false;
        }
        var detail = Unheld(now.Detail);
        state.UpdateCard(now.Id, now.Title, words + (string.IsNullOrWhiteSpace(detail) ? "" : "\n\n" + detail), now.Choices, now.Link, bin: CardBin.Ring);
        return true;
    }

    private void QuietCard(string id, string why)
    {
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Id == id) is not { Bin: not CardBin.Ring } now)
        {
            return;
        }
        var detail = Unheld(now.Detail);
        state.UpdateCard(now.Id, now.Title, QuietPrefix + why + (string.IsNullOrWhiteSpace(detail) ? "" : "\n\n" + detail), now.Choices, now.Link, bin: CardBin.ForYou);
    }

    // The bound's one ring of the day, for a trigger with no card of its own; an open one is the same card (RaiseCard's own rule).
    private void RingBound(InvestigationTrigger trigger, string key, string target) =>
        state.RaiseCard(CardBin.Ring, BoundKind, Verbs.NodeTarget, $"The investigator bound of {models.AutopilotInvestigations} runs a day is reached",
            $"No investigator was started for {trigger} on {key} ({target}): the node started {models.AutopilotInvestigations} today. Raise the bound or look at it yourself.",
            [new CardChoice { Label = "Allow twice as many today", Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.AutopilotInvestigationsKey, (Math.Max(1, models.AutopilotInvestigations) * 2).ToString(CultureInfo.InvariantCulture)] }]);

    /// <summary>The key and the target of a subject: a flight's code for anything in a flight, else the card's id (or <paramref name="fallback"/>).</summary>
    public static (string Key, string Target) SubjectOf(string subject, string fallback)
    {
        if (subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            var code = subject[Verbs.FlightTargetPrefix.Length..].Split('/')[0];
            if (code.Length > 0)
            {
                return (code, Verbs.FlightTarget(code));
            }
        }
        return (fallback, Verbs.CardTarget(fallback));
    }

    private static string KeyOf(string subject) =>
        subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) || subject.StartsWith(Verbs.CardTargetPrefix, StringComparison.Ordinal)
            ? subject[(subject.IndexOf('/') + 1)..].Split('/')[0]
            : subject;

    /// <summary>The session name of a run on <paramref name="key"/>: <c>&lt;machine&gt;-investigate-&lt;subject&gt;</c>, lower case.</summary>
    public string NameOf(string key) => $"{Machine.ToLowerInvariant()}-investigate-{Slug().Replace(key.ToLowerInvariant(), "-").Trim('-')}";

    [GeneratedRegex("[^a-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Cut(string? text)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        return line.Length <= 240 ? line : line[..240] + "...";
    }
}

/// <summary>Takes up the investigator's triggers and its runs out of time every minute (<see cref="Investigator.Look"/>).</summary>
public sealed class InvestigatorSweep(Investigator investigator) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    investigator.Look();
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    investigator.Log?.Invoke($"investigator: a look failed ({ex.Message})");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
