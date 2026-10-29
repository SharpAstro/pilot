using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The conversation with the watchtower as one chat feed: across the watchtowers there have been, with a divider where a fresh one began, and what it waits on.</summary>
public sealed class WatchtowerChatTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string First = "aaaa0001-0000-4000-8000-000000000001";
    private const string Second = "bbbb0002-0000-4000-8000-000000000002";

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly TempProfile _profile = new();
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-chat-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;
    private readonly TranscriptBook _book;
    private readonly WatchtowerChat _chat;

    public WatchtowerChatTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = new Watchtower(_state, _workers, (command, _) => Task.FromResult(_state.Complete(command, null)), _options, _time) { Pilot = "pilot" };
        _book = new TranscriptBook(_profile.Profile);
        _chat = new WatchtowerChat(_tower, _book);
    }

    public void Dispose()
    {
        _profile.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private void Row(string job, string session, SessionState state) =>
        _state.PutSession(new SessionRow
        {
            Id = job,
            SessionId = session,
            Name = "du1-watchtower",
            Kind = SessionKind.Background,
            Cwd = _options.WatchtowerFolder,
            Role = SessionRole.Watchtower,
            ContextTokens = 10_000,
            ContextWindow = 200_000,
            State = state,
            SpendWeighted = 0,
            LastResponseAt = _time.GetUtcNow(),
            StartedAt = _time.GetUtcNow(),
        });

    private static string Said(string who, string text)
    {
        var content = who == "user" ? $"\"{text}\"" : "[{\"type\":\"text\",\"text\":\"" + text + "\"}]";
        return $$"""{"type":"{{who}}","timestamp":"2026-10-20T09:00:00Z","message":{"role":"{{who}}","content":""" + content + "}}";
    }

    /// <summary>Two watchtowers with a conversation each: the first ended, the second running.</summary>
    private async Task TwoWatchtowersAsync()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Idle);
        _profile.Append(_profile.TranscriptPath(First),
            Said("user", "Let's launch a flight for the sky map"),
            Said("assistant", "On it."));
        _chat.Page(-1);

        _time.Advance(TimeSpan.FromHours(3));
        (await _tower.RestartAsync("its brief changed", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Ended);
        Row("job00002", Second, SessionState.Idle);
        _profile.Append(_profile.TranscriptPath(Second),
            Said("user", "[node] A card opened: c1 rings"),
            Said("assistant", "Handled it."));
    }

    [Fact]
    public async Task TheFeedRunsAcrossTwoWatchtowersOldestFirstWithADividerWhereTheFreshOneBegan()
    {
        await TwoWatchtowersAsync();

        var page = _chat.Page(-1);

        page.Entries.Select(e => (e.Index, e.Kind, e.Text)).ShouldBe([
            (0, TranscriptKind.User, "Let's launch a flight for the sky map"),
            (1, TranscriptKind.Assistant, "On it."),
            (2, TranscriptKind.Divider, "A fresh watchtower started: its brief changed"),
            (3, TranscriptKind.User, "A card opened: c1 rings"),
            (4, TranscriptKind.Assistant, "Handled it."),
        ]);
        page.Total.ShouldBe(5);
        page.Entries[2].At.ShouldBe(Start.AddHours(3));
    }

    [Fact]
    public async Task AReadAfterAnIndexGivesWhatCameAfterAcrossTheDividerToo()
    {
        await TwoWatchtowersAsync();

        _chat.Page(0).Entries.Select(e => e.Index).ShouldBe([1, 2, 3, 4]);
        _chat.Page(2).Entries.Select(e => (e.Index, e.Text)).ShouldBe([(3, "A card opened: c1 rings"), (4, "Handled it.")]);
        var end = _chat.Page(4);
        end.Entries.ShouldBeEmpty();
        end.Total.ShouldBe(5);

        // The running one says more: the same index carries on.
        _profile.Append(_profile.TranscriptPath(Second), Said("assistant", "Anything else?"));
        _chat.Page(4).Entries.Select(e => (e.Index, e.Text)).ShouldBe([(5, "Anything else?")]);
    }

    [Fact]
    public async Task TheNodesOwnMessagesAreNotesAndAPersonsAreNot()
    {
        await TwoWatchtowersAsync();

        var entries = _chat.Page(-1).Entries;
        entries.Where(e => e.Kind == TranscriptKind.User).Select(e => (e.Text, e.Note)).ShouldBe([
            ("Let's launch a flight for the sky map", false),
            ("A card opened: c1 rings", true),
        ]);
    }

    [Fact]
    public async Task ACompactionIsANoteAndTheRelaysClosingLineIsLeftOffWhatWasSaid()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Idle);
        _profile.Append(_profile.TranscriptPath(First),
            """{"type":"user","message":{"role":"user","content":"[node] wake up\n\n""" + ClaudeCliDelivery.RelayNote + "\"}}",
            """{"type":"user","message":{"role":"user","content":"<command-name>/compact</command-name><command-args>keep the leg</command-args>"}}""",
            Said("user", "and what about the docs?"));

        _chat.Page(-1).Entries.Select(e => (e.Text, e.Note)).ShouldBe([("wake up", true), ("/compact keep the leg", true), ("and what about the docs?", false)]);
    }

    [Fact]
    public async Task TheFeedSurvivesARestartOfTheNodeWhoseRowsAreGone()
    {
        await TwoWatchtowersAsync();
        _chat.Page(-1).Total.ShouldBe(5);

        // A node started afresh: no session rows, only what the first one kept in its data folder.
        var state = new NodeState(new InMemoryJournal(), _time);
        var tower = new Watchtower(state, _workers, (command, _) => Task.FromResult(state.Complete(command, null)), _options, _time) { Pilot = "pilot" };
        var page = new WatchtowerChat(tower, new TranscriptBook(_profile.Profile)).Page(-1);

        page.Total.ShouldBe(5);
        page.Entries.Select(e => e.Kind).ShouldBe([TranscriptKind.User, TranscriptKind.Assistant, TranscriptKind.Divider, TranscriptKind.User, TranscriptKind.Assistant]);
    }

    [Fact]
    public void ARowOfItsNameAnOlderNodeStartedIsAWatchtowerToo()
    {
        // No record kept: only rows, as a node from before the chat left them.
        Row("job00001", First, SessionState.Ended);
        Row("job00002", Second, SessionState.Idle);
        _profile.Append(_profile.TranscriptPath(First), Said("user", "old"));
        _profile.Append(_profile.TranscriptPath(Second), Said("user", "new"));

        _chat.Page(-1).Entries.Select(e => (e.Kind, e.Text)).ShouldBe([
            (TranscriptKind.User, "old"),
            (TranscriptKind.Divider, "A fresh watchtower started"),
            (TranscriptKind.User, "new"),
        ]);
    }

    private const string AskReply = @"Which repo should the flight fly in?\n- tianwen\n- pilot";

    private static string Asked(string id, string reply) =>
        $$$"""{"type":"assistant","message":{"id":"{{{id}}}","role":"assistant","content":[{"type":"text","text":"On it.\n<ask>\n{{{reply}}}\n</ask>"}]}}""";

    [Fact]
    public async Task ItSaysWhetherItIsWorkingAndWhatItHasAskedInAnAskBlockAndNobodyHasAnswered()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Idle);
        _profile.Append(_profile.TranscriptPath(First),
            Said("user", "launch a flight for the sky map"),
            Asked("m1", AskReply),
            """{"type":"queue-operation","operation":"enqueue","content":"and tell me when it is up"}""");

        var asking = _chat.Page(-1);

        asking.PendingQuestion.ShouldBe("Which repo should the flight fly in?");
        asking.Choices.ShouldBe(["tianwen", "pilot"]);
        asking.Queued.ShouldBe(["and tell me when it is up"]);
        // The reply shows in the feed as what it said and the question, without the block's markup or the choices (they are the buttons).
        asking.Entries.Last().ShouldSatisfyAllConditions(e => e.Kind.ShouldBe(TranscriptKind.Assistant), e => e.Text.ShouldBe("On it.\nWhich repo should the flight fly in?"));

        // A choice's click is a tell: the turn ended on the question, so the row is idle and the tell resumes it, never queues behind a blocked call.
        SessionMessenger.Choose(_state.Sessions().Single(r => r.Id == "job00001"))
            .ShouldBe(SayWays.Resumed);
        _profile.Append(_profile.TranscriptPath(First), Said("user", "pilot"));

        var answered = _chat.Page(-1);
        answered.PendingQuestion.ShouldBeNull();
        answered.Choices.ShouldBeNull();
    }

    [Fact]
    public async Task ACardTheNodeSaysWhileAQuestionWaitsLeavesItPending()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Idle);
        _profile.Append(_profile.TranscriptPath(First), Asked("m1", AskReply), Said("user", "[node] A card opened: c1 rings"));

        _chat.Page(-1).PendingQuestion.ShouldBe("Which repo should the flight fly in?");

        // The card resumed the watchtower and it acts on it (a new message id: a tool call, then its words): the question still waits for the person.
        _profile.Append(_profile.TranscriptPath(First),
            """{"type":"assistant","message":{"id":"m2","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"pilot cards"}}]}}""",
            """{"type":"assistant","message":{"id":"m2","role":"assistant","content":[{"type":"text","text":"Noted the card."}]}}""");

        var afterTheCard = _chat.Page(-1);
        afterTheCard.PendingQuestion.ShouldBe("Which repo should the flight fly in?");
        afterTheCard.Choices.ShouldBe(["tianwen", "pilot"]);

        // The person saying something is what ends it.
        _profile.Append(_profile.TranscriptPath(First), Said("user", "pilot"));

        var answered = _chat.Page(-1);
        answered.PendingQuestion.ShouldBeNull();
        answered.Choices.ShouldBeNull();
    }

    private static string Taken(string prompt) =>
        $$$"""{"type":"attachment","attachment":{"type":"queued_command","prompt":"{{{prompt}}}"}}""";

    [Fact]
    public async Task APersonsAnswerTakenMidTurnEndsTheQuestionAfterACardResumedTheSession()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Working);
        _profile.Append(_profile.TranscriptPath(First), Asked("m1", AskReply), Said("user", "[node] A card opened: c1 rings"));
        _chat.Page(-1).PendingQuestion.ShouldBe("Which repo should the flight fly in?");

        // The person clicks a choice while the card's turn is still running: the tell is relayed mid-turn, as an attachment.
        _profile.Append(_profile.TranscriptPath(First), Taken("pilot"));

        var answered = _chat.Page(-1);
        answered.PendingQuestion.ShouldBeNull();
        answered.Choices.ShouldBeNull();
    }

    [Fact]
    public async Task ATaskFinishingOrAHandBackAfterAQuestionLeavesItPending()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Working);
        _profile.Append(_profile.TranscriptPath(First), Asked("m1", AskReply),
            Taken("<task-notification>build finished</task-notification>"),
            Taken("<agent-message>planner is done</agent-message>"),
            Said("user", "<task-notification>another finished</task-notification>"));

        _chat.Page(-1).PendingQuestion.ShouldBe("Which repo should the flight fly in?");
    }

    [Fact]
    public async Task AQuestionTheSessionMovedOnFromIsNoLongerPending()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Idle);
        _profile.Append(_profile.TranscriptPath(First), Asked("m1", AskReply));
        _chat.Page(-1).PendingQuestion.ShouldBe("Which repo should the flight fly in?");

        // The person said something else in the chat: that answers or drops the question.
        _profile.Append(_profile.TranscriptPath(First), Said("user", "never mind"));

        _chat.Page(-1).PendingQuestion.ShouldBeNull();
    }

    [Fact]
    public async Task ANewReplyAfterTheQuestionMeansTheSessionMovedOn()
    {
        (await _tower.RestartAsync("first", Ct)).ShouldBeNull();
        Row("job00001", First, SessionState.Working);
        _profile.Append(_profile.TranscriptPath(First), Asked("m1", AskReply),
            """{"type":"assistant","message":{"id":"m2","role":"assistant","content":[{"type":"text","text":"Never mind, I picked tianwen."}]}}""");

        _chat.Page(-1).PendingQuestion.ShouldBeNull();
    }

    [Fact]
    public void TheWatchtowerIsStartedWithoutAskUserQuestionWhichWouldBlockABackgroundSession()
    {
        var args = ClaudeWorkerLauncher.Arguments(new WorkerStart("w", "/r", null, "brief", null, null, RemoteControl: true, DisallowedTools: ["AskUserQuestion"]));

        args.SkipWhile(a => a != "--disallowedTools").Skip(1).First().ShouldBe("AskUserQuestion");
    }

    [Fact]
    public async Task TheRouteServesTheFeedOverTheWire()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var profile = Path.Combine(node.Folder, "claude", "projects", "proj-a");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(node.Options.DataFolder);
        File.WriteAllLines(Path.Combine(node.Options.DataFolder, "watchtower-sessions"),
        [
            $"job00001\t{First}\t{Start:O}\tfirst",
            $"job00002\t{Second}\t{Start.AddHours(1):O}\tits brief changed",
        ]);
        File.WriteAllText(Path.Combine(profile, First + ".jsonl"), Said("user", "hello") + "\n" + Said("assistant", "hi") + "\n");
        File.WriteAllText(Path.Combine(profile, Second + ".jsonl"), Said("user", "[node] a card") + "\n");

        var envelope = await node.Client.GetChatAsync(-1, Ct);

        envelope.Success.ShouldBeTrue(envelope.Error);
        var page = envelope.Response.ShouldNotBeNull();
        page.Entries.Select(e => (e.Index, e.Kind, e.Text, e.Note)).ShouldBe([
            (0, TranscriptKind.User, "hello", false),
            (1, TranscriptKind.Assistant, "hi", false),
            (2, TranscriptKind.Divider, "A fresh watchtower started: its brief changed", false),
            (3, TranscriptKind.User, "a card", true),
        ]);
        page.Total.ShouldBe(4);
        page.Working.ShouldBeFalse();
        (await node.Client.GetChatAsync(2, Ct)).Response!.Entries.Select(e => e.Index).ShouldBe([3]);
    }

    [Fact]
    public void TheBriefSaysOtherNodesAreReadWithNodeAndActedOnThroughTheirWatchtower()
    {
        // DE1's watchtower said it could not read SE1's cards (2026-10-29).
        var brief = Briefs.Watchtower("de1-watchtower", [], [], [], "pilot", machine: "DE1");

        brief.ShouldContain("never say you cannot see another node's flights or cards");
        brief.ShouldContain("`pilot cards --node SE1`");
        brief.ShouldContain("`pilot tell --node <name>");
        brief.ShouldContain("Never edit or push another node's plane");
    }

    [Fact]
    public void TheBriefMakesItTheAssistantInTheWindowAnswersInPlainWordsAndLaunchesAFlightInThreeSteps()
    {
        var brief = Briefs.Watchtower("du1-watchtower", [], [], [], "pilot");

        // The person's assistant in the window as well as the fleet's lookout, and the node's own messages told from theirs.
        brief.ShouldContain("you are the controller's assistant in the window too");
        brief.ShouldContain("A message that begins `[node]` is the node's");
        brief.ShouldContain("Answer in plain words");
        // Launching a flight: create-issue, create-flight, plan-flight, said back with links; the planner covers "already flying".
        var issue = brief.IndexOf("`pilot issue create", StringComparison.Ordinal);
        var flight = brief.IndexOf("`pilot flight create <issue number>", StringComparison.Ordinal);
        var plan = brief.IndexOf("`pilot flight plan <code>` starts its planner", StringComparison.Ordinal);
        issue.ShouldBeGreaterThan(0);
        flight.ShouldBeGreaterThan(issue);
        plan.ShouldBeGreaterThan(flight);
        brief.ShouldContain("planner's own overlap check");
        brief.ShouldContain("Say back in a line or two what you made, with links");
        // A leg asked for in the chat comes back as the plan card.
        brief.ShouldContain("\"Plan a leg on <code>: …\"");
        brief.ShouldContain("`pilot leg plan <code> <name> --goal … --done-when …");
        brief.ShouldContain("`pilot flight planned <code> \"<the plan in a line>\"`");
        brief.ShouldContain("plan card comes back");
        // A choice that is theirs is asked as one; what is theirs alone comes back as the Allow card, never silently.
        // The tool blocks a background session, so it is asked in an ask block instead.
        brief.ShouldContain("Never use the AskUserQuestion tool");
        // A choice names what it acts on: "Leave it" after a list of six items left nobody sure which (2026-10-27).
        brief.ShouldContain("Leave PIL338's leg waits-are-said alone for now");
        brief.ShouldContain("<ask>");
        brief.ShouldContain("Allow card");
        brief.ShouldContain("Never go round it with gh or git directly");
    }

    [Fact]
    public void TheBriefFitsOnACommandLineWhateverCardsAreOpenRingsFirstEachInOneShortLine()
    {
        // 300 open cards each with a 2,000-character detail: whole, they made a 46,000-character brief, and every watchtower
        // started with it died on ENAMETOOLONG (DE1, 2026-10-29).
        var at = new DateTimeOffset(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
        var detail = new string('x', 2000);
        CardRow Card(int i, CardBin bin) => new() { Id = $"c{i}", Bin = bin, Kind = "stop", Subject = $"flight/PIL{i}", Title = $"PIL{i} needs something " + new string('y', 300), Detail = detail, At = at, State = CardState.Open };
        var cards = Enumerable.Range(1, 290).Select(i => Card(i, CardBin.ForYou)).Concat(Enumerable.Range(291, 10).Select(i => Card(i, CardBin.Ring))).ToList();

        var brief = Briefs.Watchtower("de1-watchtower", [], [], cards, "pilot", machine: "DE1");

        brief.Length.ShouldBeLessThan(Briefs.MaxWatchtowerBrief + 200);
        brief.ShouldNotContain(detail);
        // The rings first, though they were opened last, and the rest named by count with where to read them.
        brief.IndexOf("- c291 [ring]", StringComparison.Ordinal).ShouldBeLessThan(brief.IndexOf("- c1 [for you]", StringComparison.Ordinal));
        brief.ShouldMatch(@"- and \d+ more: `pilot cards` lists them\.");
        // One line a card, its title cut short.
        brief.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("- c", StringComparison.Ordinal)).ShouldAllBe(l => l.Length < 260);
    }

    [Fact]
    public void TheBriefGivesTheWatchtowerAFixerOnAutopilotThroughItsOwnCliAndLeavesWhatTheNodeRefusesToThePerson()
    {
        var brief = Briefs.Watchtower("de1-watchtower", [], [], [], "C:/p/pilot.exe --node-socket s", machine: "DE1");

        // Its own CLI, as every other command of the brief, never a bare pilot.
        brief.ShouldContain("`C:/p/pilot.exe --node-socket s review fix <owner/repo#n>`");
        // And the fixer is among what it may do on autopilot.
        brief.ShouldContain("start a fixer on a PR outside a flight whose review asked for changes (`C:/p/pilot.exe --node-socket s review fix <owner/repo#n>`)");
        brief.ShouldContain("The node refuses it past its third round, on a design question and on another author's PR");
        // Nothing promised that the node refuses it: a design question and a capped flight stay the person's.
        brief.ShouldContain("are the person's: say so on the card and leave it to them.");
    }
}
