using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An assistant's your-only request (#208): it runs at once, recorded with the session and the message it acted on, and said on a
/// Handled card; a no-auto-merge repo's merge raises a For-you confirm card the person allows or denies, and the asking session is
/// told which. A worker's is refused, and the person's own commands are as they were.
/// </summary>
[Trait("Category", "Slow")]
public sealed class AutopilotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Assistant = "pilot-33";
    private const string Head = "cafe0000";
    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string Why = "Seb said: merge it once green";

    private static string Lookout(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static CommandRequest Command(string verb, string target, string who, bool agent = false, string? why = null, params string[] args) =>
        new() { Verb = verb, Target = target, Args = args, Who = who, Agent = agent, Why = why };

    private static async Task<CommandResult> Send(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull();

    /// <summary>TIA1, reviewed and passed, its PR #63 open at <see cref="PrHead"/>: what a merge needs, short of a plane (it has none on disk).</summary>
    private static void Ready(NodeHarness node, string code = "TIA1", int number = 63)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = node.Folder, Issue = int.Parse(code[3..]), Branch = $"flight/{code.ToLowerInvariant()}",
            Worktree = Path.Combine(node.Folder, "plane-" + code), Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
            PullRequest = number, PullRequestUrl = $"https://github.com/x/tianwen/pull/{number}",
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.GitHub.Pr = GhAnswers.PullRequestByBranch(number: number, head: PrHead, url: $"https://github.com/x/tianwen/pull/{number}");
    }

    /// <summary>The assistant as the node lists it: an interactive session of that name, so a say reaches it over the relay.</summary>
    private static async Task ListAssistantAsync(NodeHarness node, string name = Assistant)
    {
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, name));
        for (var i = 0; i < 100 && (await node.Client.GetSessionsAsync(Ct)).Length < 1; i++)
        {
            await Task.Delay(50, Ct);
        }
    }

    private static CardRow Ask(NodeHarness node)
    {
        var card = node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Kind.ShouldBe(CardKinds.AssistantAsk);
        return card;
    }

    /// <summary>The one assistant's ask among the open cards, with other cards (a ring) open beside it.</summary>
    private static CardRow AskAmong(NodeHarness node) => node.State.Cards(openOnly: true).Single(c => c.Kind == CardKinds.AssistantAsk);

    internal static BlackBoxLine[] BlackBox(NodeHarness node)
    {
        using var box = new StreamReader(new FileStream(Path.Combine(node.Options.DataFolder, "store", FileJournal.BlackBoxFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return [.. box.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => System.Text.Json.JsonSerializer.Deserialize(l, PilotJsonContext.Default.BlackBoxLine)!)];
    }

    /// <summary>The one case an assistant's your-only command still waits on a card: a merge in a repo set to no-auto-merge.</summary>
    private static async Task NoAutoMergeAsync(NodeHarness node) =>
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["no-auto-merge-repos", "tianwen"]))).Error.ShouldBeNull();

    [Fact]
    public async Task ANoAutoMergeReposAssistantsMergeRaisesAForYouConfirmCardAndDoesNotMerge()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);

        var asked = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        asked.Ok.ShouldBeFalse();
        asked.Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
        var card = Ask(node);
        card.Kind.ShouldBe(CardKinds.AssistantAsk);
        (card.Bin, card.Title, card.Subject, card.AskedBy, card.Why).ShouldBe((CardBin.ForYou, "pilot-33 asks to merge #63 (TIA1)", "flight/TIA1", Assistant, Why));
        card.Detail.ShouldNotBeNull().ShouldContain(Why);
        card.Choices.Select(c => (c.Label, c.Verb, c.Target)).ShouldBe([("Allow it", Verbs.Merge, "flight/TIA1"), ("Deny it", Verbs.Dismiss, Verbs.CardTarget(card.Id))]);
        card.Choices[0].Args.ShouldBe([Head]);
        // The same ask again is the same card, not a second.
        (await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head))).Error.ShouldNotBeNull().ShouldContain($"card {card.Id}");
        node.State.Cards(openOnly: true).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AllowFromTheGuiMergesAsThePersonAndTheAskingSessionIsToldTheOutcome()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        var card = Ask(node);

        var allowed = await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow it"));

        allowed.Error.ShouldBeNull();
        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        var closed = node.State.Cards().Single(c => c.Id == card.Id);
        (closed.State, closed.Outcome, closed.ClosedBy).ShouldBe((CardState.Closed, "Allow it", "gui"));
        var told = node.Delivery.Relays.ShouldHaveSingleItem();
        told.Name.ShouldBe(Assistant);
        told.Text.ShouldContain("gui allowed your request to merge #63 (TIA1)");
        told.Text.ShouldContain("done");
        // The merge ran as the person's, not as the assistant: nothing of it is marked as the node's.
        BlackBox(node).Single(l => l.Verb == Verbs.Merge && l.Ok).ShouldSatisfyAllConditions(l => l.Who.ShouldBe("gui"), l => l.Autopilot.ShouldBeFalse());
    }

    [Fact]
    public async Task DenyRefusesAndSaysSoToTheAskingSession()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        var card = Ask(node);

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Deny it"))).Error.ShouldBeNull();

        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
        var closed = node.State.Cards().Single(c => c.Id == card.Id);
        (closed.State, closed.Outcome, closed.ClosedBy).ShouldBe((CardState.Closed, "Deny it", "gui"));
        node.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain("gui denied your request to merge #63 (TIA1)");
        // Asked again, it is a fresh card.
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        Ask(node).Id.ShouldNotBe(card.Id);
    }

    [Fact]
    public async Task ASecondSessionAskingTheSameThingWhileTheCardIsOpenIsToldWhatTheyAnswerToo()
    {
        const string Other = "pilot-34";
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, Assistant), AgentRows.Interactive(AgentRows.SessionB, 4243, Other));
        for (var i = 0; i < 100 && (await node.Client.GetSessionsAsync(Ct)).Length < 2; i++)
        {
            await Task.Delay(50, Ct);
        }
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        var card = Ask(node);

        // The second asks the same command and gets the first's card, and is on it.
        (await Send(node, Command(Verbs.Merge, "flight/TIA1", Other, agent: true, Why, Head))).Error.ShouldNotBeNull().ShouldContain($"card {card.Id}");
        // Asking again from the same session adds nobody.
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Other, agent: true, Why, Head));
        var open = Ask(node);
        open.Id.ShouldBe(card.Id);
        open.AskedBy.ShouldBe(Assistant);
        open.AlsoAskedBy.ShouldBe([Other]);

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Deny it"))).Error.ShouldBeNull();

        node.Delivery.Relays.Select(r => r.Name).ShouldBe([Assistant, Other], ignoreOrder: true);
        node.Delivery.Relays.ShouldAllBe(r => r.Text.Contains("gui denied your request to merge #63 (TIA1)"));
    }

    [Fact]
    public async Task DismissingTheAskIsDenyingIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        (await Send(node, Command(Verbs.Dismiss, Verbs.CardTarget(Ask(node).Id), "gui"))).Error.ShouldBeNull();

        node.GitHub.Merged.ShouldBeEmpty();
        node.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain("denied");
    }

    [Fact]
    public async Task AnAssistantOrTheLookoutPressingAllowOrDenyIsRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        var card = Ask(node);
        var lookout = Lookout(node);

        foreach (var (who, agent) in new[] { (Assistant, true), ("mcp", true), (lookout, false), (lookout, true) })
        {
            // The lookout's answer runs as an assistant's now, so the ask's own refusal is the one it meets.
            var refusal = "the person's";
            foreach (var choice in new[] { "Allow it", "Deny it" })
            {
                (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), who, agent, args: choice))).Error.ShouldNotBeNull().ShouldContain(refusal);
            }
            (await Send(node, Command(Verbs.Dismiss, Verbs.CardTarget(card.Id), who, agent))).Ok.ShouldBeFalse();
        }

        // An assistant may not answer it either: an open ask is the person's to answer.
        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), Assistant, agent: true, args: "Allow it"))).Error.ShouldNotBeNull().ShouldContain("the person's");
        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Cards(openOnly: true).ShouldHaveSingleItem().Id.ShouldBe(card.Id);
    }

    /// <summary>An assistant's press of the first-run maintenance card is done, as the README says (the review of #1126).</summary>
    [Fact]
    public async Task AnAssistantsPressOnTheFirstRunMaintenanceCardIsDone()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = node.State.RaiseCard(CardBin.ForYou, IdleMaintenance.FirstRunKind, Verbs.NodeTarget, "Run the first idle disk sweep?", null,
        [
            new CardChoice { Label = IdleMaintenance.RunLabel, Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.MaintenanceEveryKey, "1d"] },
            new CardChoice { Label = IdleMaintenance.KeepOffLabel, Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.MaintenanceEveryKey, "off"] },
        ]);

        var pressed = await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), Assistant, agent: true, Why, IdleMaintenance.KeepOffLabel));

        pressed.Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AssistantAsk);
    }

    [Fact]
    public async Task TheSameMergeGoesThroughRecordedWithTheSessionAndMessageAndAHandledCardSaysIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);

        var merged = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        merged.Error.ShouldBeNull();
        (merged.Autopilot, merged.Why).ShouldBe((true, Why));
        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        var line = BlackBox(node).Single(l => l.Verb == Verbs.Merge);
        (line.Who, line.Ok, line.Autopilot, line.Why).ShouldBe((Assistant, true, true, Why));
        var handled = node.State.Cards().Single(c => c.Kind == CardKinds.NodeRan);
        (handled.Bin, handled.State, handled.Title, handled.AskedBy, handled.Why).ShouldBe((CardBin.Handled, CardState.Closed, "The node did for pilot-33: merged #63 (TIA1)", Assistant, Why));
        handled.Detail.ShouldNotBeNull().ShouldContain(Why);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AssistantAsk);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANoAutoMergeReposFlightIsAskedNotMergedByAnAssistantButThePersonsOwnMergeWorks()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        var saved = await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["no-auto-merge-repos", "tianwen"]));
        saved.Error.ShouldBeNull();

        var asked = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        asked.Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.GitHub.Merged.ShouldBeEmpty();
        var card = Ask(node);
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "flight/TIA1"));
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.NodeRan);

        // The person's own merge, by Allow on the card or by hand, is not held.
        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow it"))).Error.ShouldBeNull();
        node.GitHub.Merged.Single().Number.ShouldBe(63);

        // Another repo's flight still goes through at the assistant's word.
        Ready(node, "TIA2", 64);
        node.State.PutFlight(node.State.Flights().Single(f => f.Code == "TIA2") with { Repo = "orbit" });
        (await Send(node, Command(Verbs.Merge, "flight/TIA2", Assistant, agent: true, Why, Head))).Error.ShouldBeNull();
    }

    [Fact]
    public async Task ANoAutoMergeReposPrMergeIsAskedNotRunByAnAssistant()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["no-auto-merge-repos", "widget"]))).Error.ShouldBeNull();

        var asked = await Send(node, Command(Verbs.Merge, "pr/acme/widget#7", Assistant, agent: true, Why, Head));

        asked.Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.GitHub.Merged.ShouldBeEmpty();
        var card = Ask(node);
        card.Title.ShouldContain("merge acme/widget#7");
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.NodeRan);
    }

    [Fact]
    public async Task AnAssistantsCommandWithNoMessageSaysSoAndARefusedCommandLeavesNoHandledCard()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(node.Folder, "plane"), Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });

        // Run, and refused by the flight (never reviewed): the black box has it as an assistant's run that failed, and no card says it was done.
        var refused = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, args: Head));

        refused.Error.ShouldNotBeNull().ShouldContain("has not been reviewed");
        refused.Autopilot.ShouldBeTrue();
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.NodeRan);

        Ready(node);
        (await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, args: Head))).Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Kind == CardKinds.NodeRan).Detail.ShouldBe("No message given");
    }

    [Fact]
    public async Task AWorkersMergeIsStillRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        const string worker = "du1-tia1-parser";
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = worker });

        foreach (var agent in new[] { false, true })
        {
            (await Send(node, Command(Verbs.Merge, "flight/TIA1", worker, agent, Why, Head))).Error.ShouldStartWith("merge is the controller's, not a flight session's: ");
        }

        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AssistantAsk || c.Kind == CardKinds.NodeRan);
    }

    [Fact]
    public async Task AHumanRunCliMergeRunsAsThePersonsAndNoCardIsRaised()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);

        var merged = await Send(node, Command(Verbs.Merge, "flight/TIA1", "cli", agent: false, args: Head));

        merged.Error.ShouldBeNull();
        merged.Autopilot.ShouldBeFalse();
        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AssistantAsk || c.Kind == CardKinds.NodeRan);
    }

    [Fact]
    public async Task AnAssistantWithNoSessionRowAsksAndTheCardSaysItWasNotTold()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", "mcp", agent: true, args: Head));
        var card = Ask(node);

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow it"))).Error.ShouldBeNull();

        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.Delivery.Calls.ShouldBeEmpty();
        node.State.Cards().Single(c => c.Id == card.Id).Outcome.ShouldNotBeNull().ShouldContain("was not told");
    }

    [Fact]
    public async Task AnAllowTheFlightRefusesLeavesTheCardOpenAndTheSessionUntold()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await NoAutoMergeAsync(node);
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, args: "def56789"));
        var card = Ask(node);

        // The PR's head is not the one the assistant reviewed: the merge says so, and the ask stays for another try.
        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow it"))).Error.ShouldNotBeNull().ShouldContain("reviewed def56789");

        node.GitHub.Merged.ShouldBeEmpty();
        Ask(node).Id.ShouldBe(card.Id);
        node.Delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCliCarriesWhyToTheNodeAndTheBlackBoxKeepsIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);

        // Whether the pilot run here is taken for an assistant's (CLAUDECODE in the environment) or a person's, the message is kept.
        var output = new StringWriter();
        await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "flight", "merge", "TIA1", Head, "--why", Why], node.Folder, output, output, Ct);

        BlackBox(node).Last(l => l.Verb == Verbs.Merge).Why.ShouldBe(Why, output.ToString());
    }
}
