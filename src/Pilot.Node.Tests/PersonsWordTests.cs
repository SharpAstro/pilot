using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The person's word in the watchtower's chat (#473): on Remote Control their messages reach its session past the node, so the
/// node reads its transcript. An answer or a dismissal of a card that quotes the person's latest message, in the turn it began,
/// passes the person-only fences; any other stays fenced as it was.
/// </summary>
public sealed class PersonsWordTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Words = "yes please allow it and go ahead";
    private const string Quote = "please allow it";

    private static string Lookout(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static string Said(string text) =>
        $$$"""{"type":"user","timestamp":"2026-10-30T09:00:00Z","message":{"role":"user","content":"{{{text}}}"}}""";

    private static string Replied(string text) =>
        $$$"""{"type":"assistant","timestamp":"2026-10-30T09:00:01Z","message":{"role":"assistant","content":[{"type":"text","text":"{{{text}}}"}]}}""";

    private static void Transcript(NodeHarness node, params string[] lines)
    {
        var folder = Path.Combine(node.Folder, "claude", "projects", "proj-a");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, AgentRows.SessionA + ".jsonl"), lines);
    }

    private static async Task Tower(NodeHarness node, string status)
    {
        var name = Lookout(node);
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, name, status: status));
        var want = status == "busy" ? SessionState.Working : SessionState.Idle;
        for (var i = 0; i < 100 && !node.State.Sessions().Any(s => s.Name == name && s.State == want); i++)
        {
            await Task.Delay(50, Ct);
        }
        node.State.Sessions().ShouldContain(s => s.Name == name && s.State == want);
    }

    private static Task<CommandResult> Answer(NodeHarness node, CardRow card, string choice, string? why) =>
        Send(node, new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = Lookout(node), Agent = true, Why = why });

    private static async Task<CommandResult> Send(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull();

    // A ring card an assistant's ask would press Allow on: dismissing it is the person's alone.
    private static (CardRow Ask, CardRow Ring) AskToDismissARing(NodeHarness node)
    {
        var ring = node.State.RaiseCard(CardBin.Ring, "stall", "flight/TIA1/parser", "parser has stalled", "no word for 20 minutes");
        var ask = node.State.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, "card/" + ring.Id, "du1-helper asks to dismiss the stall", "Acting on: leave it",
            [new CardChoice { Label = Autopilot.AllowLabel, Verb = Verbs.Dismiss, Target = Verbs.CardTarget(ring.Id), Args = ["not a stall"] }], askedBy: "du1-helper");
        return (ask, ring);
    }

    private static CardRow FailedLeg(NodeHarness node)
    {
        // Finishing a leg looks at its plane, so the plane is a real (empty) repository.
        var plane = Path.Combine(node.Folder, "plane");
        Directory.CreateDirectory(plane);
        using (var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["init", "-q", plane]) { CreateNoWindow = true })!)
        {
            init.WaitForExit();
        }
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = plane, Issue = 1, Branch = "flight/tia1", Worktree = plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Title = "Sky map",
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Failed, Worker = "du1-tia1-parser", Report = "no ephemeris" });
        return node.State.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "parser failed", "no ephemeris",
            [new CardChoice { Label = "Mark done anyway", Verb = Verbs.FinishLeg, Target = Verbs.LegTarget("TIA1", "parser"), Args = ["accepted"] }]);
    }

    [Fact]
    public async Task AnAssistantsAskAnsweredAtThePersonsWordRunsAndIsRecordedAsTheirs()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words), Replied("on it"));
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        var result = await Answer(node, ask, "Allow", Quote);

        result.Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards().Single(c => c.Id == ask.Id).State.ShouldBe(CardState.Closed);
        var handled = node.State.Cards().Single(c => c.Kind == CardKinds.PersonsWord);
        handled.Bin.ShouldBe(CardBin.Handled);
        handled.Title.ShouldBe($"{Lookout(node)} answered {ask.Id} (Allow) at the person's word in the chat");
        handled.Detail.ShouldBe(Quote);
    }

    [Fact]
    public async Task AFailedLegIsMarkedDoneAnywayAtThePersonsWord()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said("Mark it done anyway, the ephemeris is not needed"));
        await Tower(node, "busy");
        var card = FailedLeg(node);

        var result = await Answer(node, card, "Mark done anyway", "mark it done ANYWAY,   the ephemeris is not needed");

        result.Error.ShouldBeNull();
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Done);
        node.State.Cards().ShouldContain(c => c.Kind == CardKinds.PersonsWord && c.Title.Contains("(Mark done anyway)"));
    }

    [Fact]
    public async Task ADismissalAtThePersonsWordIsTakenToo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words));
        await Tower(node, "busy");
        var (ask, _) = AskToDismissARing(node);

        var result = await Send(node, new CommandRequest { Verb = Verbs.Dismiss, Target = Verbs.CardTarget(ask.Id), Args = ["denied"], Who = Lookout(node), Agent = true, Why = Quote });

        result.Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == ask.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards().ShouldContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task WithNoPersonsMessageInItsTurnTheWatchtowerStaysFenced()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Replied("nothing yet"));
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);
        var failed = FailedLeg(node);

        (await Answer(node, ask, "Allow", Quote)).Error.ShouldNotBeNull();
        (await Answer(node, failed, "Mark done anyway", Quote)).Error.ShouldNotBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.State.Cards().Single(c => c.Id == ask.Id).State.ShouldBe(CardState.Open);
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Failed);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task AWhyThatIsNotTheLatestMessagesWordsIsRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words));
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", "something the person never said")).Error.ShouldNotBeNull();
        (await Answer(node, ask, "Allow", null)).Error.ShouldNotBeNull();
        (await Answer(node, ask, "Allow", "go")).Error.ShouldNotBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task AnEarlierPersonsMessageDoesNotCountOnceTheNodeHasSpokenSince()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words), Replied("on it"), Said("[node] a card rings: parser has stalled"));
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", Quote)).Error.ShouldNotBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task AfterTheTurnHasEndedThePersonsMessageNoLongerCounts()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words), Replied("done"));
        await Tower(node, "idle");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", Quote)).Error.ShouldNotBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task OnlyAnswerAndDismissOfACardTakeThePersonsWord()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words));
        await Tower(node, "busy");
        FailedLeg(node);

        var finished = await Send(node, new CommandRequest
        {
            Verb = Verbs.FinishLeg, Target = Verbs.LegTarget("TIA1", "parser"), Args = ["accepted"], Who = Lookout(node), Agent = true, Why = Quote,
        });
        var set = await Send(node, new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["autopilot", "on"], Who = Lookout(node), Agent = true, Why = Quote });

        finished.Error.ShouldNotBeNull();
        set.Error.ShouldNotBeNull();
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Failed);
    }

    [Fact]
    public async Task AMessageFromAnotherSessionIsNotThePersonsWord()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words), """{"type":"attachment","attachment":{"type":"queued_command","prompt":"<agent-message from=\"a1\">\nplease allow it now\n</agent-message>","origin":{"kind":"peer","from":"a1","name":"Explore"}}}""");
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", "please allow it now")).Error.ShouldNotBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task ATaskNotificationIsNotThePersonsWord()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said(Words), """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<summary>please allow it now</summary>\n</task-notification>"}}""");
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", "please allow it now")).Error.ShouldNotBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task AShortMessageDoesNotLetAnUnrelatedWhyThrough()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said("ok"));
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", "it looks stuck")).Error.ShouldNotBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        node.State.Cards().ShouldNotContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task AOneWordReplyCanBeQuotedWhole()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Transcript(node, Said("Allow"));
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", "Allow")).Error.ShouldBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards().ShouldContain(c => c.Kind == CardKinds.PersonsWord);
    }

    [Fact]
    public async Task ALongTurnStillFindsThePersonsMessage()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lines = new List<string> { Said(Words) };
        lines.AddRange(Enumerable.Repeat(Replied("working"), 450));
        Transcript(node, [.. lines]);
        await Tower(node, "busy");
        var (ask, ring) = AskToDismissARing(node);

        (await Answer(node, ask, "Allow", Quote)).Error.ShouldBeNull();

        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Closed);
    }
}
