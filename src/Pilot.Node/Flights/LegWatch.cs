using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's watch on every flying leg's worker, the reason v2 exists: a flight never derails silently. A worker the
/// usage limit stopped is resumed once the limit resets; one that went idle with its leg unfinished is nudged, and rung
/// for when nudging did not help; one that waits on a person, failed, or never showed up is rung for at once. Every
/// such card closes by itself when the worker is working again, and a go-around or a finished leg closes them too.
/// </summary>
/// <remarks>
/// What it has resumed and nudged is kept in memory only: a node restarted mid-flight may nudge a worker once more than
/// it would have, which costs a turn, never a silent stall.
/// </remarks>
public sealed class LegWatch(NodeState state, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, TimeProvider time) : BackgroundService
{
    /// <summary>The kinds of card the watch raises, all about a leg; the watch closes them when its worker works again.</summary>
    public static readonly string[] Kinds = [Stopped, Idle, Waiting, Failed, Missing];

    public const string Stopped = "worker-stopped";
    public const string Idle = "worker-idle";
    public const string Waiting = "worker-waiting";
    public const string Failed = "worker-failed";
    public const string Missing = "worker-missing";

    /// <summary>A worker past its leg's budget: not a stall, so not among <see cref="Kinds"/>; it closes when the budget is extended or the leg ends.</summary>
    public const string OverBudget = "over-budget";

    /// <summary>A flight's reviewer that stopped, or never showed up, without its verdict.</summary>
    public const string ReviewStalled = "review-stalled";

    /// <summary>The kind of ring for a leg's validator that stopped without its answer.</summary>
    public const string ValidationStalled = "validation-stalled";

    /// <summary>Who the watch's commands are from, in the command log and on the cards it closes.</summary>
    public const string Who = "node";

    /// <summary>How often it looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after its limit resets a stopped worker is resumed: the reset time is the API's, not this clock's.</summary>
    public TimeSpan ResetMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a worker sits idle on an unfinished leg before it is nudged: a turn that just ended may be followed by its report.</summary>
    public TimeSpan IdleGrace { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a started worker has to show up in the session listing.</summary>
    public TimeSpan ShowUpGrace { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a background session waits on its person before it rings rather than being a card for you.</summary>
    public TimeSpan RingAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a session waits on its person before it is a card at all: a prompt answered at its terminal within it is no
    /// card, where every one was a card raised and closed again within a look or two, piling up in Handled.
    /// </summary>
    public TimeSpan CardAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How many times one worker is nudged before the watch rings instead.</summary>
    public int MaxNudges { get; init; } = 2;

    /// <summary>How a worker runs the CLI, socket and all, for the nudge: <see cref="Briefs.Run"/>, as its brief says it.</summary>
    public string Pilot { get; init; } = "pilot";

    // By worker job: the reset each was resumed after, and how often it was nudged.
    private readonly Dictionary<string, DateTimeOffset> _resumedAfter = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nudges = new Dictionary<string, int>(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await LookAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The watch must outlive anything one look throws: a dead watch is the silent derail itself.
                await Console.Error.WriteLineAsync($"leg watch: one look failed, looking again: {ex.Message}");
            }
        }
    }

    /// <summary>One look at every flying leg's worker, and every flight's reviewer, acting on what it finds.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sessions = state.Sessions();
        LookAtReviews(sessions, now);
        LookAtValidators(sessions, now);
        LookAtWaiting(sessions, now);
        foreach (var leg in state.Legs().Where(l => l.State == LegState.Flying && l.WorkerJob is not null))
        {
            var subject = Verbs.LegTarget(leg.Flight, leg.Name);
            var worker = sessions.FirstOrDefault(s => s.Id == leg.WorkerJob);
            if (worker is null)
            {
                if (leg.StartedAt is { } started && now - started > ShowUpGrace)
                {
                    Ring(leg, Missing, $"{Name(leg)}'s worker {leg.Worker} never showed up",
                        $"started {Ago(now, started)} ago as job {leg.WorkerJob}, and no session of that id is running on this machine");
                }
                continue;
            }
            state.CloseCardsAbout(subject, "it showed up", Missing);
            if (worker.Budget is { } budget && worker.SpendWeighted > budget)
            {
                var more = budget + budget / 2;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                state.RaiseCard(CardBin.Ring, OverBudget, subject, $"{Name(leg)}'s worker is past its budget",
                    string.Create(inv, $"{worker.SpendWeighted:N0} of {budget:N0} weighted tokens; it keeps working until you choose"),
                    [
                        new CardChoice { Label = string.Create(inv, $"Extend to {more:N0}"), Verb = Verbs.ExtendBudget, Target = subject, Args = [more.ToString(System.Globalization.CultureInfo.InvariantCulture)] },
                        new CardChoice { Label = "Go around", Verb = Verbs.GoAround, Target = subject, Args = [string.Create(inv, $"the last worker spent {worker.SpendWeighted:N0} of a {budget:N0} budget")] },
                    ]);
            }

            switch (worker.State)
            {
                case SessionState.Working:
                    state.CloseCardsAbout(subject, "working again", Kinds);
                    break;
                case SessionState.Waiting:
                    Ring(leg, Waiting, $"{Name(leg)}'s worker waits on you",
                        worker.WaitingFor is { Length: > 0 } what ? what : "a question or a permission", worker);
                    break;
                case SessionState.Failed:
                    Ring(leg, Failed, $"{Name(leg)}'s worker failed", worker.ApiError is { } why ? $"the API said {why}" : null);
                    break;
                case SessionState.Idle or SessionState.Ended when worker.ApiError is { } error:
                    await StoppedAsync(leg, worker, error, now, cancellationToken);
                    break;
                case SessionState.Idle or SessionState.Ended:
                    await IdleAsync(leg, worker, now, cancellationToken);
                    break;
            }
        }
    }

    private async Task StoppedAsync(LegRow leg, SessionRow worker, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = leg.WorkerJob!;
        if (worker.ApiErrorResetsAt is not { } resets)
        {
            Ring(leg, Stopped, $"{Name(leg)}'s worker was stopped by the API", $"{error}, with no time it clears", worker);
            return;
        }
        if (now < resets + ResetMargin)
        {
            // Nothing to do yet but say so: the card is the controller's to resume sooner (extra usage) or go around.
            state.RaiseCard(CardBin.ForYou, Stopped, Subject(leg), $"{Name(leg)}'s worker hit the usage limit; it resumes at {resets.ToLocalTime():HH:mm}",
                $"{error}; the node resumes it {ResetMargin.TotalMinutes:0} minutes after the limit resets", Choices(leg, worker, resumeLabel: "Resume now"));
            return;
        }
        if (_resumedAfter.TryGetValue(job, out var done) && done >= resets)
        {
            // Resumed for this reset already and stopped again on the same one: the resume did not take.
            Ring(leg, Stopped, $"{Name(leg)}'s worker is still stopped after its limit reset", $"{error}; resumed once at {done.ToLocalTime():HH:mm} to no effect", worker);
            return;
        }

        _resumedAfter[job] = resets;
        var result = await SayAsync(worker, "The usage limit that stopped you has reset. Carry on with your leg from where you left off.", cancellationToken);
        if (result.Ok)
        {
            state.CloseCardsAbout(Subject(leg), "resumed after the limit reset", Stopped);
            state.RaiseCard(CardBin.Handled, "worker-resumed", Subject(leg), $"{Name(leg)}'s worker resumed after its usage limit reset");
        }
        else
        {
            Ring(leg, Stopped, $"{Name(leg)}'s worker could not be resumed after its limit reset", result.Error, worker);
        }
    }

    private async Task IdleAsync(LegRow leg, SessionRow worker, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = worker.LastResponseAt ?? worker.StartedAt;
        // A worker idle while a job of its own runs or waits its turn is waiting, as it was told to: the job wakes it, so it
        // is not stalled. Its own by row, or by name: a worker Claude Code continued as a copy is a row of its own, while
        // the job it handed over is still the first row's (TIA1739's j13 rang c205 to c207 as stalls, 2026-10-23).
        if (now - since < IdleGrace || state.Jobs().Any(j => j.State is JobState.Running or JobState.Queued
                && (j.Owner == worker.Id || string.Equals(j.OwnerName, worker.Name, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }
        var job = leg.WorkerJob!;
        var nudges = _nudges.GetValueOrDefault(job);
        if (nudges >= MaxNudges)
        {
            Ring(leg, Idle, $"{Name(leg)}'s worker stopped with the leg unfinished",
                $"idle for {Ago(now, since)}, nudged {nudges} {(nudges == 1 ? "time" : "times")} to no avail{(leg.Report is { Length: > 0 } r ? $"; last report: {r.Split('\n')[0]}" : "")}", worker);
            return;
        }

        _nudges[job] = nudges + 1;
        var result = await SayAsync(worker, Nudge(leg), cancellationToken);
        if (result.Ok)
        {
            state.RaiseCard(CardBin.Handled, "worker-nudged", Subject(leg), $"{Name(leg)}'s worker went idle with the leg unfinished; nudged ({nudges + 1} of {MaxNudges})");
        }
        else
        {
            Ring(leg, Idle, $"{Name(leg)}'s worker stopped with the leg unfinished, and could not be nudged", result.Error, worker);
        }
    }

    /// <summary>
    /// A reviewer working is left alone; one that never showed up, failed, ended, or sat idle past the grace without its
    /// verdict rings, with a fresh review as the way on.
    /// </summary>
    private void LookAtReviews(SessionRow[] sessions, DateTimeOffset now)
    {
        foreach (var flight in state.Flights().Where(f => f.State != FlightState.Landed && f.Review is { State: ReviewState.Reviewing, ReviewerJob: not null }))
        {
            var subject = Verbs.FlightTarget(flight.Code);
            LookAtReviewer(sessions, now, flight.Review!.ReviewerJob!, flight.Review.StartedAt, subject, flight.Code, Verbs.StartReview);
        }
        // Any PR's reviewer (#113) is watched as a flight's is.
        foreach (var review in state.PrReviews().Where(r => r is { State: PrReviewState.Reviewing, ReviewerJob: not null }))
        {
            var subject = Verbs.PrTarget(review.Repo, review.Number);
            LookAtReviewer(sessions, now, review.ReviewerJob!, review.StartedAt, subject, $"{review.Repo}#{review.Number}", Verbs.ReviewPr);
        }
    }

    /// <summary>
    /// A leg's validator (#34) that never shows up, fails, or stops without its GO or NO-GO rings, with the leg's ways on:
    /// started anyway, or taken out. Unwatched, its leg waited for an answer that was not coming (the review of #160).
    /// </summary>
    private void LookAtValidators(SessionRow[] sessions, DateTimeOffset now)
    {
        foreach (var leg in state.Legs().Where(l => l is { State: LegState.Planned, Validation: LegValidation.Pending, ValidatorJob: not null }))
        {
            var subject = Subject(leg);
            var startedAt = state.Cards().Where(c => c.Kind == LegCommander.ValidatingKind && c.Subject == subject).Select(c => c.At).DefaultIfEmpty(now).Max();
            var row = sessions.FirstOrDefault(s => s.Id == leg.ValidatorJob);
            string? why = row switch
            {
                null when now - startedAt > ShowUpGrace => $"started {Ago(now, startedAt)} ago and never showed up",
                null => null,
                { State: SessionState.Working or SessionState.Waiting } => null,
                { State: SessionState.Failed } => "it failed",
                _ when now - (row.LastResponseAt ?? row.StartedAt) >= IdleGrace => $"it stopped {Ago(now, row.LastResponseAt ?? row.StartedAt)} ago without its GO or NO-GO",
                _ => null,
            };
            if (why is not null)
            {
                state.RaiseCard(CardBin.Ring, ValidationStalled, subject, $"{Name(leg)}'s validation stalled", why,
                [
                    new CardChoice { Label = "Go anyway", Verb = Verbs.StartLeg, Target = subject, Args = ["anyway"] },
                    new CardChoice { Label = "Remove leg", Verb = Verbs.RemoveLeg, Target = subject, Args = ["its validation stalled"] },
                ]);
            }
        }
    }

    /// <summary>
    /// Every session waiting on its person that no leg already rings for (a flying leg's worker has its own, see
    /// <see cref="Waiting"/>) is a card from this node: For you, or a ring once a background one (nobody's terminal, so
    /// nobody sees the prompt) has waited over <see cref="RingAfter"/>, once it has waited <see cref="CardAfter"/>. The card
    /// closes when the session stops waiting or its process is gone. A card dismissed is not raised again for the same wait,
    /// nor rung for: the person has seen it. The wait's start tells one wait from the next.
    /// </summary>
    private void LookAtWaiting(SessionRow[] sessions, DateTimeOffset now)
    {
        var workers = state.Legs().Where(l => l.State == LegState.Flying && l.WorkerJob is not null).Select(l => l.WorkerJob).ToHashSet(StringComparer.Ordinal);
        var waiting = sessions.Where(s => s is { State: SessionState.Waiting, Gone: false, ApiError: null, Archived: false } && !workers.Contains(s.Id))
            .ToDictionary(s => Verbs.SessionTarget(s.Id), StringComparer.Ordinal);
        var cards = state.Cards();
        foreach (var card in cards.Where(c => c.State == CardState.Open && c.Kind == CardKinds.SessionWaiting && !waiting.ContainsKey(c.Subject)))
        {
            var row = sessions.FirstOrDefault(s => Verbs.SessionTarget(s.Id) == card.Subject);
            state.CloseCardsAbout(card.Subject, row is null ? "the session is gone" : row.Gone ? "its process is gone" : "it stopped waiting", CardKinds.SessionWaiting);
        }
        foreach (var (subject, session) in waiting)
        {
            var since = session.WaitingSince ?? session.LastResponseAt ?? session.StartedAt;
            if (now - since < CardAfter)
            {
                continue;
            }
            var carded = cards.Where(c => c.Kind == CardKinds.SessionWaiting && c.Subject == subject && c.At >= since).ToArray();
            var rings = session.Kind == SessionKind.Background && session.WaitingSince is { } at && now - at > RingAfter;
            // Rung already, or dismissed by its person (the node closes only its own For-you card on the way to a ring).
            if (carded.Any(c => c.Bin == CardBin.Ring || c is { State: CardState.Closed, ClosedBy: not "node" }) || (carded.Length > 0 && !rings))
            {
                continue;
            }
            if (rings)
            {
                state.CloseCardsAbout(subject, "it has waited over " + Waited.Say(RingAfter) + ", so it rings", CardKinds.SessionWaiting);
            }
            var what = session.WaitingFor is { Length: > 0 } w ? w : "a question or a permission";
            var age = session.WaitingSince is { } started ? $" · since {Waited.Say(now - started)}" : "";
            state.RaiseCard(rings ? CardBin.Ring : CardBin.ForYou, CardKinds.SessionWaiting, subject, $"{session.Name} waits on {what}{age}", session.Asking, link: session.RemoteControlUrl);
        }
    }

    private void LookAtReviewer(SessionRow[] sessions, DateTimeOffset now, string job, DateTimeOffset startedAt, string subject, string name, string again)
    {
        var row = sessions.FirstOrDefault(s => s.Id == job);
        string? why = row switch
        {
            null when now - startedAt > ShowUpGrace => $"started {Ago(now, startedAt)} ago and never showed up",
            null => null,
            { State: SessionState.Working or SessionState.Waiting } => null,
            { State: SessionState.Failed } => "it failed",
            _ when now - (row.LastResponseAt ?? row.StartedAt) >= IdleGrace => $"it stopped {Ago(now, row.LastResponseAt ?? row.StartedAt)} ago without its verdict",
            _ => null,
        };
        if (row is { State: SessionState.Working })
        {
            state.CloseCardsAbout(subject, "reviewing again", ReviewStalled);
        }
        if (why is not null)
        {
            state.RaiseCard(CardBin.Ring, ReviewStalled, subject, $"{name}'s review stalled", why,
                [new CardChoice { Label = "Review again", Verb = again, Target = subject }]);
        }
    }

    /// <summary>What an idle worker is told: its leg is not finished, and the two ways to finish it.</summary>
    internal string Nudge(LegRow leg)
    {
        var open = leg.Checklist.Count(i => !i.Done);
        var steps = leg.Checklist.Length == 0 ? "" : open == 0 ? " Every step is ticked." : $" {open} of {leg.Checklist.Length} steps are not ticked yet.";
        return $"Your leg `{leg.Name}` of {leg.Flight} is not finished yet.{steps} If there is more to do, carry on. When every step is done and "
            + $"the Done-when holds, run `{Pilot} leg finish done \"<one-line report>\"`; if you cannot go on, run `{Pilot} leg finish failed \"<why>\"`.";
    }

    private Task<CommandResult> SayAsync(SessionRow worker, string text, CancellationToken cancellationToken) =>
        run(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(worker.Id), Args = [text], Who = Who }, cancellationToken);

    private void Ring(LegRow leg, string kind, string title, string? detail, SessionRow? worker = null) =>
        state.RaiseCard(CardBin.Ring, kind, Subject(leg), title, detail, Choices(leg, worker, resumeLabel: kind == Idle ? "Nudge" : "Resume"));

    /// <summary>
    /// A resume (or nudge) when there is a worker to say it to, and a go-around, which starts a fresh one; a waiting
    /// worker is answered in its own session, so it gets the go-around only.
    /// </summary>
    private CardChoice[] Choices(LegRow leg, SessionRow? worker, string resumeLabel)
    {
        var goAround = new CardChoice
        {
            Label = "Go around",
            Verb = Verbs.GoAround,
            Target = Subject(leg),
            Args = [$"the last worker stalled ({worker?.ApiError ?? worker?.State.ToString().ToLowerInvariant() ?? "never showed up"})"],
        };
        if (worker is null || worker.State == SessionState.Waiting)
        {
            return [goAround];
        }
        var text = resumeLabel == "Nudge" ? Nudge(leg) : "Carry on with your leg from where you left off.";
        return [new CardChoice { Label = resumeLabel, Verb = Verbs.Say, Target = Verbs.SessionTarget(worker.Id), Args = [text] }, goAround];
    }

    private static string Subject(LegRow leg) => Verbs.LegTarget(leg.Flight, leg.Name);

    private static string Name(LegRow leg) => $"{leg.Flight}/{leg.Name}";

    private static string Ago(DateTimeOffset now, DateTimeOffset then)
    {
        var span = now - then;
        return span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{Math.Max(1, (int)span.TotalMinutes)} min";
    }
}
