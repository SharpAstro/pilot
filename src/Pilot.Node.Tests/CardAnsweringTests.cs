using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A card answered while its answer is still underway (#792): the node marks the card (<see cref="CardRow.Answering"/>: which choice, who
/// pressed it, when, what it waits on) before the choice runs, a second press of the same choice joins it with an ok answer instead of
/// "refused", another choice is refused saying which answer is underway, and the mark ends with the answer. The owner's press of
/// Resolve the conflict, two minutes after the controller's CLI had pressed the same button and it sat queued, was told it was refused.
/// </summary>
public sealed class CardAnsweringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string Subject = "flight/TIA1";
    private const string Why = "the PR conflicts with its base: resolve it keeping both sides' work";

    // The merge-failed card's go-around: a choice that keeps its card open while it runs (the conflict card these tests first used is
    // gone since #869, which resolves a conflict without ringing).
    private const string FixLastLeg = "Fix its last leg";

    /// <summary>
    /// The real repos of a test that runs the go-around through to git (tagged Slow): seeded through <see cref="SeededRepos"/>, which starts
    /// its git through <see cref="TestEnvironment.Start"/> so the process-start guard (#933) sees it, and clears its folder when disposed.
    /// </summary>
    private static SeededRepos RealRepos() => new(Path.Combine(Path.GetTempPath(), "p2a-" + Guid.NewGuid().ToString("N")[..8]), realGit: true);

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static CommandRequest Answer(string who, CardRow card, string choice) =>
        new() { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = who };

    private static Task<CommandResult> Press(NodeHarness node, string who, CardRow card, string choice) =>
        node.App.Services.GetRequiredService<NodeCommands>().RunAsync(Answer(who, card, choice), Ct);

    /// <summary>Records what the node sends its clients, as the stream carries it.</summary>
    private sealed class RecordingSink(List<NodeEvent> events) : INodeEventSink
    {
        public int Capacity => 1024;

        public bool TryWrite(byte[] message)
        {
            lock (events)
            {
                events.Add(System.Text.Json.JsonSerializer.Deserialize(message, PilotJsonContext.Default.NodeEvent)!);
            }
            return true;
        }

        public void Overflowed()
        {
        }
    }

    // ---- the card's own state, no node around it ----

    [Fact]
    public void AnAnswerTakenIsOnTheCardAndOnTheStreamAndEndsWithTheCard()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var card = state.RaiseCard(CardBin.Ring, LegCommander.MergeFailed, Subject, "TIA1's PR #63 conflicts with its base");
        var events = new List<NodeEvent>();
        using var subscription = state.Subscribe(state.Seq, state.Stream, new RecordingSink(events));

        var (taken, underway) = state.BeginAnswering(card.Id, "Resolve the conflict", "cli", "TIA1 is being merged", TimeSpan.FromMinutes(10));

        underway.ShouldBeNull();
        taken.ShouldNotBeNull();
        taken.ShouldBe(new CardAnswering { Choice = "Resolve the conflict", By = "cli", At = taken.At, Waiting = "TIA1 is being merged" });
        state.Cards().Single().Answering.ShouldBe(taken);
        // One event, and it reached the stream whole: a client shows the answer in progress from it.
        var changed = events.OfType<CardChangedEvent>().ShouldHaveSingleItem();
        changed.Card.Answering.ShouldNotBeNull().ShouldBe(taken);
        changed.Card.State.ShouldBe(CardState.Open);

        state.CloseCard(card.Id, "Resolve the conflict", "cli").ShouldBeTrue();

        var closed = state.Cards().Single();
        (closed.State, closed.Outcome).ShouldBe((CardState.Closed, "Resolve the conflict"));
        closed.Answering.ShouldBeNull();
        events.OfType<CardChangedEvent>().Last().Card.Answering.ShouldBeNull();
    }

    [Fact]
    public void AnAnswerTakenWhileOneIsUnderwayGetsTheFirstAndChangesNothing()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var card = state.RaiseCard(CardBin.Ring, LegCommander.MergeFailed, Subject, "TIA1's PR #63 conflicts with its base");
        var (first, _) = state.BeginAnswering(card.Id, "Resolve the conflict", "cli", null, TimeSpan.FromMinutes(10));
        var seq = state.Seq;

        var (taken, underway) = state.BeginAnswering(card.Id, "Resolve the conflict", "gui", null, TimeSpan.FromMinutes(10));

        taken.ShouldBeNull();
        underway.ShouldBe(first);
        state.Seq.ShouldBe(seq, "a press that joins writes nothing");
        state.Cards().Single().Answering.ShouldBe(first);
    }

    [Fact]
    public void AnAnswerThatIsNoLongerLiveDoesNotShieldTheCardAndItsEndNeverClearsTheOneThatReplacedIt()
    {
        var clock = new ShiftedClock();
        var state = new NodeState(new InMemoryJournal(), clock);
        var card = state.RaiseCard(CardBin.Ring, LegCommander.MergeFailed, Subject, "TIA1's PR #63 conflicts with its base");
        var stuck = TimeSpan.FromMinutes(10);
        var (old, _) = state.BeginAnswering(card.Id, "Resolve the conflict", "cli", null, stuck);
        clock.Advance(stuck + TimeSpan.FromSeconds(1));

        var (replacement, underway) = state.BeginAnswering(card.Id, "Resolve the conflict", "gui", null, stuck);

        underway.ShouldBeNull();
        replacement.ShouldNotBeNull().By.ShouldBe("gui");
        // The first answer finally ends: it is not the one the card carries now.
        state.ClearAnswering(card.Id, old!).ShouldBeFalse();
        state.Cards().Single().Answering.ShouldBe(replacement);
        state.ClearAnswering(card.Id, replacement!).ShouldBeTrue();
        state.Cards().Single().Answering.ShouldBeNull();
        state.Cards().Single().State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void ACardThatIsNotOpenTakesNoAnswer()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var card = state.RaiseCard(CardBin.Ring, LegCommander.MergeFailed, Subject, "TIA1's PR #63 conflicts with its base");
        state.CloseCard(card.Id, "dismissed", "cli");

        foreach (var id in new[] { card.Id, "c99" })
        {
            var (taken, underway) = state.BeginAnswering(id, "Resolve the conflict", "gui", null, TimeSpan.FromMinutes(10));
            taken.ShouldBeNull();
            underway.ShouldBeNull();
        }
    }

    [Fact]
    public void AnAnswerUnderwayWhenTheNodeStoppedIsNotSaidToBeUnderwayWhenItComesBack()
    {
        var journal = new InMemoryJournal();
        var first = new NodeState(journal, TimeProvider.System);
        var card = first.RaiseCard(CardBin.Ring, LegCommander.MergeFailed, Subject, "TIA1's PR #63 conflicts with its base");
        first.BeginAnswering(card.Id, "Resolve the conflict", "cli", null, TimeSpan.FromMinutes(10)).Taken.ShouldNotBeNull();

        // The journal holds the mark, as it holds every change: it is the answer's runner that is gone with the node.
        var restarted = new NodeState(journal, TimeProvider.System);

        var back = restarted.Cards().Single();
        back.State.ShouldBe(CardState.Open);
        back.Answering.ShouldBeNull();
    }

    // ---- a go-around from the conflict card, on a node ----

    /// <summary>
    /// TIA1 with its one leg done, its review passed and PR #63 open, in a repo of <paramref name="repos"/> (the node's in-memory model, or real
    /// git for a Slow test); a PR-ready card (a done leg under a passed review goes around only while an open card offers it) and the merge-failed
    /// card with two go-around choices.
    /// </summary>
    private static (CardRow Ready, CardRow Conflicts) Conflicted(NodeHarness node, SeededRepos repos)
    {
        var repo = repos.Seed("tianwen");
        repos.Run(repo, "branch", "flight/tia1");
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = repo, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(Path.GetDirectoryName(repo)!, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        var leg = Verbs.LegTarget("TIA1", "parser");
        var ready = node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, Subject, "TIA1's PR #63", "Planets: 1 leg done.",
        [
            new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [PrHead] },
            new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = leg, Args = ["redo it"] },
        ], link: PrUrl);
        var conflicts = node.State.RaiseCard(CardBin.Ring, LegCommander.MergeFailed, Subject, "TIA1's PR #63 did not merge", "The merge failed.",
        [
            new CardChoice { Label = FixLastLeg, Verb = Verbs.GoAround, Target = leg, Args = [Why] },
            new CardChoice { Label = "Redo leg parser from scratch", Verb = Verbs.GoAround, Target = leg, Args = ["redo it from scratch"] },
        ], link: PrUrl);
        return (ready, conflicts);
    }

    /// <summary>The go-around's PR sent back to draft held until the returned source is set: the answer sits inside the flight's calls, as a queued one does.</summary>
    private static (TaskCompletionSource Open, TaskCompletionSource Reached) HoldTheDraft(NodeHarness node)
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.GitHub.OnCall = call =>
        {
            if (call != nameof(IGitHub.SetDraftAsync))
            {
                return Task.CompletedTask;
            }
            reached.TrySetResult();
            return open.Task.WaitAsync(TimeSpan.FromSeconds(20));
        };
        return (open, reached);
    }

    private static async Task Reaches(TaskCompletionSource reached) =>
        (await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(20), Ct))).ShouldBe(reached.Task, "the call was not reached");

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGuiPressOfAGoAroundWhileTheClisIdenticalAnswerIsStillInsideTheGoAroundsCallingWindowGetsOkNotRefused()
    {
        using var repos = RealRepos();
        await using var node = await NodeHarness.StartAsync(Ct);
        var (_, conflicts) = Conflicted(node, repos);
        var (open, reached) = HoldTheDraft(node);
        var cli = Press(node, "cli", conflicts, FixLastLeg);
        await Reaches(reached);
        var underway = node.State.Cards().Single(c => c.Id == conflicts.Id);
        underway.State.ShouldBe(CardState.Open);
        underway.Answering.ShouldNotBeNull().ShouldSatisfyAllConditions(
            a => a.Choice.ShouldBe(FixLastLeg),
            a => a.By.ShouldBe("cli"));

        var gui = await Press(node, "gui", conflicts, FixLastLeg);

        // Not "Fix its last leg was refused: TIA1 is being sent round already": it is on its way, and who started it is said.
        gui.Ok.ShouldBeTrue(gui.Error);
        gui.Made.ShouldBe($"already on its way, started by cli at {Local(underway.Answering!.At)}");
        node.Workers.Started.ShouldNotContain(w => w.Brief.Contains("keeping both sides' work"), "a press that joins runs nothing");
        open.SetResult();
        var done = await cli;

        done.Ok.ShouldBeTrue(done.Error);
        // The PR went back to draft once, for the first press: the one that joined sent nothing (the fake records a call as it ends).
        node.GitHub.DraftCalls.Count(d => d == (63, true)).ShouldBe(1);
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Flying);
        node.Workers.Started.Count(w => w.Brief.Contains("keeping both sides' work")).ShouldBe(1);
        var closed = node.State.Cards().Single(c => c.Id == conflicts.Id);
        (closed.State, closed.Outcome, closed.ClosedBy).ShouldBe((CardState.Closed, FixLastLeg, "cli"));
        closed.Answering.ShouldBeNull();
    }

    private static CardRow CappedFlightCard(NodeHarness node, string kind)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "r", Issue = 1, Branch = "flight/tia1", Worktree = "w", Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Ci = new FlightCi { CappedWhy = "past it", CappedAt = DateTimeOffset.UtcNow },
        });
        return node.State.RaiseCard(CardBin.ForYou, kind, Subject, "TIA1: CI stopped", "d",
            [new CardChoice { Label = "Allow more CI time", Verb = Verbs.CiBudget, Target = Subject, Args = [Verbs.CiContinue] }]);
    }

    [Fact]
    public async Task DismissingTheCiCapCardClosesItKeepsTheStopAndSaysPushesStayStoppedUntilTheCiBudgetCommand()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = CappedFlightCard(node, CiBudgets.CapKind);

        var result = await node.App.Services.GetRequiredService<NodeCommands>().RunAsync(
            new CommandRequest { Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id), Who = "gui" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldNotBeNull().ShouldSatisfyAllConditions(
            m => m.ShouldContain("TIA1"),
            m => m.ShouldContain("pushes stay stopped"),
            m => m.ShouldContain("pilot flight ci-budget TIA1 continue"));
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Closed);
        node.State.Flights().Single(f => f.Code == "TIA1").Ci.ShouldNotBeNull().CappedWhy.ShouldBe("past it");
    }

    [Fact]
    public async Task DismissingAnyOtherKindOfCardAnswersAsBefore()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = CappedFlightCard(node, "some-other-kind");

        var result = await node.App.Services.GetRequiredService<NodeCommands>().RunAsync(
            new CommandRequest { Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id), Who = "gui" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBeNull();
    }

    [Fact]
    public void TheCiCapCardsWordingSaysDismissingItKeepsPushesStopped()
    {
        CardSummaries.For(CiBudgets.CapKind, Subject, "t", []).ShouldContain("does not lift the stop");
        CardSummaries.For(CiBudgets.CapKind, Subject, "t", []).ShouldContain("pilot flight ci-budget TIA1 continue");
        CiBudgets.DismissKeepsStop("TIA1").ShouldContain("pilot flight ci-budget TIA1 continue");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ADifferentChoiceMeanwhileIsRefusedSayingWhichAnswerIsUnderwayAndADismissWaitsForIt()
    {
        using var repos = RealRepos();
        await using var node = await NodeHarness.StartAsync(Ct);
        var (_, conflicts) = Conflicted(node, repos);
        var (open, reached) = HoldTheDraft(node);
        var cli = Press(node, "cli", conflicts, FixLastLeg);
        await Reaches(reached);
        var at = node.State.Cards().Single(c => c.Id == conflicts.Id).Answering!.At;

        var other = await Press(node, "gui", conflicts, "Redo leg parser from scratch");
        var dismiss = await node.App.Services.GetRequiredService<NodeCommands>().RunAsync(
            new CommandRequest { Verb = Verbs.Dismiss, Target = Verbs.CardTarget(conflicts.Id), Args = ["not needed"], Who = "gui" }, Ct);

        other.Ok.ShouldBeFalse();
        other.Error.ShouldNotBeNull().ShouldContain(FixLastLeg);
        other.Error.ShouldContain("is underway");
        other.Error.ShouldContain($"pressed by cli at {Local(at)}");
        dismiss.Ok.ShouldBeFalse();
        dismiss.Error.ShouldNotBeNull().ShouldContain("is being answered");
        // Neither touched the answer underway: it still holds the card and still goes through.
        node.State.Cards().Single(c => c.Id == conflicts.Id).Answering.ShouldNotBeNull().Choice.ShouldBe(FixLastLeg);
        open.SetResult();
        (await cli).Ok.ShouldBeTrue();
        node.Workers.Started.Count(w => w.Brief.Contains("keeping both sides' work")).ShouldBe(1);
        node.Workers.Started.ShouldNotContain(w => w.Brief.Contains("redo it from scratch"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAnswerRefusedWhileTheFlightHasCallsOutSaysWhatItWaitsOnOnTheStreamAndLeavesNoMark()
    {
        using var repos = RealRepos();
        await using var node = await NodeHarness.StartAsync(Ct);
        var (ready, conflicts) = Conflicted(node, repos);
        var (open, reached) = HoldTheDraft(node);
        var cli = Press(node, "cli", conflicts, FixLastLeg);
        await Reaches(reached);
        var events = new List<NodeEvent>();
        using var subscription = node.State.Subscribe(node.State.Seq, node.State.Stream, new RecordingSink(events));

        // Another card of the flight is answered meanwhile: the flight has calls out, so its merge is refused for them.
        var merge = await Press(node, "gui", ready, "Merge");

        merge.Ok.ShouldBeFalse();
        merge.Error.ShouldNotBeNull().ShouldContain("being sent round");
        var readyNow = node.State.Cards().Single(c => c.Id == ready.Id);
        readyNow.State.ShouldBe(CardState.Open);
        readyNow.Answering.ShouldBeNull();
        CardChangedEvent[] changes;
        lock (events)
        {
            changes = [.. events.OfType<CardChangedEvent>().Where(e => e.Card.Id == ready.Id)];
        }
        // It said so on the stream while it ran, what it waited on, and then said it was over. The merge's own card changes while it
        // ran carry the one mark too, so what the stream holds is one answer, however many events say it.
        var marked = changes.Select(e => e.Card.Answering).Where(a => a is not null).Distinct().ShouldHaveSingleItem()!;
        (marked.Choice, marked.By, marked.Waiting).ShouldBe(("Merge", "gui", "TIA1 is being sent round"));
        changes.Last().Card.Answering.ShouldBeNull();
        open.SetResult();
        (await cli).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AnAnswerThatIsRefusedBeforeItRunsLeavesNoMarkOnItsCard()
    {
        // In memory: the node's git and the seeded repo are the model, so the test starts no real process (#933).
        await using var node = await NodeHarness.StartOnModelAsync(Ct);
        var (_, conflicts) = Conflicted(node, node.Repos!);
        // A go-around needs a why: refused, the card stays for the person to try again.
        var blank = node.State.UpdateChoices(conflicts.Id, [new CardChoice { Label = FixLastLeg, Verb = Verbs.GoAround, Target = Verbs.LegTarget("TIA1", "parser"), Args = [" "] }]);
        blank.ShouldBeTrue();

        var refused = await Press(node, "gui", conflicts, FixLastLeg);

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("needs why");
        var card = node.State.Cards().Single(c => c.Id == conflicts.Id);
        card.State.ShouldBe(CardState.Open);
        card.Answering.ShouldBeNull();
        // And pressed again it is taken afresh and refused afresh, never told it is on its way.
        var again = await Press(node, "gui", conflicts, FixLastLeg);
        again.Made.ShouldBeNull();
        again.Error.ShouldNotBeNull().ShouldContain("needs why");
    }

    [Fact]
    public async Task AnAnswerWhoseRunThrowsClearsItsMarkAndLeavesTheCardOpen()
    {
        // In memory, as above: whatever git the go-around asks before its gh call is answered by the model.
        await using var node = await NodeHarness.StartOnModelAsync(Ct);
        var (_, conflicts) = Conflicted(node, node.Repos!);
        // Not a refusal: the node is cut off in the middle of its gh call.
        node.GitHub.OnCall = call => call == nameof(IGitHub.SetDraftAsync) ? throw new InvalidOperationException("the node is stopping") : Task.CompletedTask;

        await Should.ThrowAsync<InvalidOperationException>(Press(node, "gui", conflicts, FixLastLeg));

        var card = node.State.Cards().Single(c => c.Id == conflicts.Id);
        card.State.ShouldBe(CardState.Open);
        card.Answering.ShouldBeNull();
    }

    // ---- an answer that closes its card first ----

    [Fact]
    public async Task ACloseFirstAnswerPressedAgainRightAfterIsToldWhoAnsweredAndWhenNotThatTheCardIsClosed()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.RaiseCard(CardBin.ForYou, "question", "node", "Keep a note?", choices:
        [
            new CardChoice { Label = "Note it", Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["noted from a card"] },
            new CardChoice { Label = "Say no", Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["no"] },
        ]);
        var card = node.State.Cards().Single();

        var first = await Press(node, "cli", card, "Note it");
        var again = await Press(node, "gui", card, "Note it");
        var byNumber = await Press(node, "gui", card, "1");
        var other = await Press(node, "gui", card, "Say no");

        first.Ok.ShouldBeTrue(first.Error);
        var closed = node.State.Cards().Single();
        again.Ok.ShouldBeTrue(again.Error);
        again.Made.ShouldBe($"already answered by cli at {Local(closed.ClosedAt!.Value)}");
        byNumber.Made.ShouldBe(again.Made);
        // Another choice than the one that closed it is a refusal as before, and nothing was run again.
        other.Ok.ShouldBeFalse();
        other.Error.ShouldBe("card c1 is closed already (Note it)");
        node.State.Snapshot().Notes.ShouldHaveSingleItem().Text.ShouldBe("noted from a card");
    }

    [Fact]
    public async Task PilotAnswerSaysAPressThatJoinedAnAnswerAlreadyTakenWasJoinedNotRun()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = node.State.RaiseCard(CardBin.ForYou, "question", "node", "Keep a note?", choices:
            [new CardChoice { Label = "Note it", Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["noted from a card"] }]);

        async Task<string> Cli()
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await Pilot.Commands.PilotCommand.RunAsync(["--node-socket", node.SocketPath, "answer", card.Id, "1"], node.Folder, output, error, Ct);
            exit.ShouldBe(0, $"{output}{error}");
            return output.ToString();
        }

        var first = await Cli();
        var again = await Cli();

        first.ShouldNotContain("joined:");
        var closed = node.State.Cards().Single();
        // What the GUI and TUI log for the same answer: the caller can tell a press that joined from one that ran.
        again.ShouldContain($"joined: already answered by {closed.ClosedBy} at {Local(closed.ClosedAt!.Value)}");
        node.State.Snapshot().Notes.ShouldHaveSingleItem();
    }

    [Fact]
    public void ACloseFirstAnswerPressedAgainAfterItsWindowIsRefusedAsClosedAsBefore()
    {
        var clock = new ShiftedClock();
        var state = new NodeState(new InMemoryJournal(), clock);
        var commands = new NodeCommands(new ServiceCollection().AddSingleton<TimeProvider>(clock).BuildServiceProvider(), state);
        var raised = state.RaiseCard(CardBin.ForYou, "question", "node", "Keep a note?", choices:
            [new CardChoice { Label = "Note it", Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["noted from a card"] }]);
        state.CloseCard(raised.Id, "Note it", "cli");
        var card = state.Cards().Single();

        var soon = commands.ClosedAlready(Answer("gui", card, "Note it"), card, ["Note it"]);
        clock.Advance(NodeCommands.RepeatAnswerWindow + TimeSpan.FromSeconds(1));
        var later = commands.ClosedAlready(Answer("gui", card, "Note it"), card, ["Note it"]);

        soon.Ok.ShouldBeTrue(soon.Error);
        soon.Made.ShouldBe($"already answered by cli at {Local(card.ClosedAt!.Value)}");
        later.Ok.ShouldBeFalse();
        later.Error.ShouldBe($"card {card.Id} is closed already (Note it)");
    }
}
