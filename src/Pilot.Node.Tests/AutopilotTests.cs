using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An assistant's your-only request and the autopilot rule (#208): off, it raises a For-you confirm card the person allows or
/// denies and the asking session is told which; on, it runs at once, recorded with the session and the message it acted on,
/// and said on a Handled card. A worker's is refused either way, and the person's own commands are as they were.
/// </summary>
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
        node.GitHub.Pr = new PullRequest(number, "OPEN", PrHead, $"https://github.com/x/tianwen/pull/{number}");
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

    [Fact]
    public async Task WithAutopilotOffAnAssistantsMergeRaisesAForYouConfirmCardAndDoesNotMerge()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);

        var asked = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        asked.Ok.ShouldBeFalse();
        asked.Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
        var card = Ask(node);
        (card.Bin, card.Title, card.Subject, card.AskedBy, card.Why).ShouldBe((CardBin.ForYou, "pilot-33 asks to merge #63 (TIA1)", "flight/TIA1", Assistant, Why));
        card.Detail.ShouldNotBeNull().ShouldContain(Why);
        card.Choices.Select(c => (c.Label, c.Verb, c.Target)).ShouldBe([("Allow", Verbs.Merge, "flight/TIA1"), ("Deny", Verbs.Dismiss, Verbs.CardTarget(card.Id))]);
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
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        var card = Ask(node);

        var allowed = await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow"));

        allowed.Error.ShouldBeNull();
        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        var closed = node.State.Cards().Single(c => c.Id == card.Id);
        (closed.State, closed.Outcome, closed.ClosedBy).ShouldBe((CardState.Closed, "Allow", "gui"));
        var told = node.Delivery.Relays.ShouldHaveSingleItem();
        told.Name.ShouldBe(Assistant);
        told.Text.ShouldContain("gui allowed your request to merge #63 (TIA1)");
        told.Text.ShouldContain("done");
        // The merge ran as the person's, not as the assistant: nothing of it is marked autopilot.
        BlackBox(node).Single(l => l.Verb == Verbs.Merge && l.Ok).ShouldSatisfyAllConditions(l => l.Who.ShouldBe("gui"), l => l.Autopilot.ShouldBeFalse());
    }

    [Fact]
    public async Task DenyRefusesAndSaysSoToTheAskingSession()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        var card = Ask(node);

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Deny"))).Error.ShouldBeNull();

        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
        var closed = node.State.Cards().Single(c => c.Id == card.Id);
        (closed.State, closed.Outcome, closed.ClosedBy).ShouldBe((CardState.Closed, "Deny", "gui"));
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

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Deny"))).Error.ShouldBeNull();

        node.Delivery.Relays.Select(r => r.Name).ShouldBe([Assistant, Other], ignoreOrder: true);
        node.Delivery.Relays.ShouldAllBe(r => r.Text.Contains("gui denied your request to merge #63 (TIA1)"));
    }

    [Fact]
    public async Task DismissingTheAskIsDenyingIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        (await Send(node, Command(Verbs.Dismiss, Verbs.CardTarget(Ask(node).Id), "gui"))).Error.ShouldBeNull();

        node.GitHub.Merged.ShouldBeEmpty();
        node.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain("denied");
    }

    [Fact]
    public async Task AnAssistantsAskToDismissARingCardIsAllowedByAllowAndLeavesTheRingOpenOnDeny()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await ListAssistantAsync(node);
        var ring = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR is ready");

        // Autopilot off: the assistant's dismissal of a ring card is asked, not done.
        (await Send(node, Command(Verbs.Dismiss, Verbs.CardTarget(ring.Id), Assistant, agent: true, Why))).Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        var denied = AskAmong(node);
        // Allow is the dismissal of the ring card, Deny the dismissal of the ask itself: the verb is the same, the target is not.
        denied.Choices.Select(c => (c.Label, c.Verb, c.Target)).ShouldBe([("Allow", Verbs.Dismiss, Verbs.CardTarget(ring.Id)), ("Deny", Verbs.Dismiss, Verbs.CardTarget(denied.Id))]);

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(denied.Id), "gui", args: "Deny"))).Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain("gui denied your request to dismiss");

        // Asked again and allowed: the ring card is closed, and the session is told it was done.
        await Send(node, Command(Verbs.Dismiss, Verbs.CardTarget(ring.Id), Assistant, agent: true, Why));
        var allowed = AskAmong(node);
        allowed.Id.ShouldNotBe(denied.Id);
        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(allowed.Id), "gui", args: "Allow"))).Error.ShouldBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards().Single(c => c.Id == allowed.Id).Outcome.ShouldBe("Allow");
        var told = node.Delivery.Relays.Last();
        told.Text.ShouldContain("gui allowed your request to dismiss");
        told.Text.ShouldContain("done");
    }

    [Fact]
    public async Task AnAssistantsAnswerToARingCardIsAskedAndRunsTheChosenButtonOnAllow()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await ListAssistantAsync(node);
        var ring = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1 stalled", null,
            [new CardChoice { Label = "Retry", Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["for-you", "retried"] }]);

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(ring.Id), Assistant, agent: true, Why, "Retry"))).Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.State.Cards(openOnly: true).ShouldNotContain(c => c.Title == "retried");
        var ask = AskAmong(node);
        ask.Title.ShouldContain("asks to answer");

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(ask.Id), "gui", args: "Allow"))).Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards(openOnly: true).ShouldContain(c => c.Title == "retried");
        node.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain("allowed your request to answer");
    }

    [Fact]
    public async Task AnAssistantOrTheLookoutPressingAllowOrDenyIsRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));
        var card = Ask(node);
        var lookout = Lookout(node);

        foreach (var (who, agent) in new[] { (Assistant, true), ("mcp", true), (lookout, false), (lookout, true) })
        {
            var refusal = who == lookout ? "not the watchtower's" : "the person's";
            foreach (var choice in new[] { "Allow", "Deny" })
            {
                (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), who, agent, args: choice))).Error.ShouldNotBeNull().ShouldContain(refusal);
            }
            (await Send(node, Command(Verbs.Dismiss, Verbs.CardTarget(card.Id), who, agent))).Ok.ShouldBeFalse();
        }

        // Autopilot on does not lift it either: an ask raised before it was switched on is still the person's to answer.
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["autopilot", "on"]))).Error.ShouldBeNull();
        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), Assistant, agent: true, args: "Allow"))).Error.ShouldNotBeNull().ShouldContain("the person's");
        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Cards(openOnly: true).ShouldHaveSingleItem().Id.ShouldBe(card.Id);
    }

    [Fact]
    public async Task WithAutopilotOnTheSameMergeGoesThroughRecordedWithTheSessionAndMessageAndAHandledCardSaysIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["autopilot", "on"]))).Error.ShouldBeNull();

        var merged = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        merged.Error.ShouldBeNull();
        (merged.Autopilot, merged.Why).ShouldBe((true, Why));
        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        var line = BlackBox(node).Single(l => l.Verb == Verbs.Merge);
        (line.Who, line.Ok, line.Autopilot, line.Why).ShouldBe((Assistant, true, true, Why));
        var handled = node.State.Cards().Single(c => c.Kind == CardKinds.AutopilotRan);
        (handled.Bin, handled.State, handled.Title, handled.AskedBy, handled.Why).ShouldBe((CardBin.Handled, CardState.Closed, "pilot-33 merged #63 (TIA1) on autopilot", Assistant, Why));
        handled.Detail.ShouldNotBeNull().ShouldContain(Why);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AssistantAsk);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANoAutoMergeReposFlightIsAskedNotMergedByAnAssistantEvenWithAutopilotOnButThePersonsOwnMergeWorks()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["autopilot", "on"]))).Error.ShouldBeNull();
        var saved = await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["no-auto-merge-repos", "tianwen"]));
        saved.Error.ShouldBeNull();

        var asked = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, Why, Head));

        asked.Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.GitHub.Merged.ShouldBeEmpty();
        var card = Ask(node);
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "flight/TIA1"));
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AutopilotRan);

        // The person's own merge, by Allow on the card or by hand, is not held.
        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow"))).Error.ShouldBeNull();
        node.GitHub.Merged.Single().Number.ShouldBe(63);

        // Another repo's flight still goes through on autopilot.
        Ready(node, "TIA2", 64);
        node.State.PutFlight(node.State.Flights().Single(f => f.Code == "TIA2") with { Repo = "orbit" });
        (await Send(node, Command(Verbs.Merge, "flight/TIA2", Assistant, agent: true, Why, Head))).Error.ShouldBeNull();
    }

    [Fact]
    public async Task ANoAutoMergeReposPrMergeIsAskedNotRunByAnAssistantEvenWithAutopilotOn()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["autopilot", "on"]))).Error.ShouldBeNull();
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["no-auto-merge-repos", "widget"]))).Error.ShouldBeNull();

        var asked = await Send(node, Command(Verbs.Merge, "pr/acme/widget#7", Assistant, agent: true, Why, Head));

        asked.Error.ShouldNotBeNull().ShouldContain("waits on their card");
        node.GitHub.Merged.ShouldBeEmpty();
        var card = Ask(node);
        card.Title.ShouldContain("merge acme/widget#7");
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AutopilotRan);
    }

    [Fact]
    public async Task AutopilotOnWithNoMessageSaysSoAndARefusedCommandLeavesNoHandledCard()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(node.Folder, "plane"), Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["autopilot", "on"]))).Error.ShouldBeNull();

        // Run, and refused by the flight (never reviewed): the black box has it as an autopilot run that failed, and no card says it was done.
        var refused = await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, args: Head));

        refused.Error.ShouldNotBeNull().ShouldContain("has not been reviewed");
        refused.Autopilot.ShouldBeTrue();
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AutopilotRan);

        Ready(node);
        (await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, args: Head))).Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Kind == CardKinds.AutopilotRan).Detail.ShouldBe("No message given");
    }

    [Fact]
    public async Task AWorkersMergeIsStillRefusedWithAutopilotOn()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        const string worker = "du1-tia1-parser";
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = worker });
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", args: ["autopilot", "on"]))).Error.ShouldBeNull();

        foreach (var agent in new[] { false, true })
        {
            (await Send(node, Command(Verbs.Merge, "flight/TIA1", worker, agent, Why, Head))).Error.ShouldBe("merge is the controller's, not a flight session's: leave it to them");
        }

        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AssistantAsk || c.Kind == CardKinds.AutopilotRan);
    }

    [Fact]
    public async Task AHumanRunCliMergeWorksAsNowWithAutopilotOffAndNoCardIsRaised()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);

        var merged = await Send(node, Command(Verbs.Merge, "flight/TIA1", "cli", agent: false, args: Head));

        merged.Error.ShouldBeNull();
        merged.Autopilot.ShouldBeFalse();
        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.AssistantAsk || c.Kind == CardKinds.AutopilotRan);
    }

    [Fact]
    public async Task AnAssistantWithNoSessionRowAsksAndTheCardSaysItWasNotTold()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", "mcp", agent: true, args: Head));
        var card = Ask(node);

        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow"))).Error.ShouldBeNull();

        node.GitHub.Merged.Single().Number.ShouldBe(63);
        node.Delivery.Calls.ShouldBeEmpty();
        node.State.Cards().Single(c => c.Id == card.Id).Outcome.ShouldNotBeNull().ShouldContain("was not told");
    }

    [Fact]
    public async Task AnAllowTheFlightRefusesLeavesTheCardOpenAndTheSessionUntold()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await ListAssistantAsync(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, args: "def56789"));
        var card = Ask(node);

        // The PR's head is not the one the assistant reviewed: the merge says so, and the ask stays for another try.
        (await Send(node, Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", args: "Allow"))).Error.ShouldNotBeNull().ShouldContain("not the def56789 you reviewed");

        node.GitHub.Merged.ShouldBeEmpty();
        Ask(node).Id.ShouldBe(card.Id);
        node.Delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnotherYourOnlyAskAboutTheSameFlightReplacesTheOpenOne()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Ready(node);
        await Send(node, Command(Verbs.Merge, "flight/TIA1", Assistant, agent: true, args: Head));
        var first = Ask(node);

        await Send(node, Command(Verbs.Land, "flight/TIA1", Assistant, agent: true));

        var open = Ask(node);
        open.Id.ShouldNotBe(first.Id);
        open.Title.ShouldBe("pilot-33 asks to land #63 (TIA1)");
        node.State.Cards().Single(c => c.Id == first.Id).Outcome.ShouldBe("replaced by a newer ask");
    }

    [Fact]
    public async Task ASettingAnAssistantAsksForIsAskedNotSet()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var asked = await Send(node, Command(Verbs.Set, Verbs.NodeTarget, Assistant, agent: true, "the user wants the cap raised", "max-jobs", "6"));

        asked.Error.ShouldNotBeNull().ShouldContain("waits on their card");
        var card = Ask(node);
        card.Title.ShouldBe("pilot-33 asks to set max-jobs 6");
        // A different key is its own ask; the same key's would be this one.
        await Send(node, Command(Verbs.Set, Verbs.NodeTarget, Assistant, agent: true, null, "lan", "on"));
        node.State.Cards(openOnly: true).Count(c => c.Kind == CardKinds.AssistantAsk).ShouldBe(2);
        // Autopilot is never among them: an assistant asking to change it is refused outright.
        (await Send(node, Command(Verbs.Set, Verbs.NodeTarget, Assistant, agent: true, null, "autopilot", "on"))).Error.ShouldNotBeNull().ShouldContain("autopilot is the person's to change");
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
