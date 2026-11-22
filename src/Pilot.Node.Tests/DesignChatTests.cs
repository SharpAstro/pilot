using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The chat of a design (PIL1151) on an in-memory node's parts with a fake designer and fake time: the owner's say is kept in BRIEF.md verbatim and reaches the one
/// designer, its reply and a published version read after the client's last index, the status says what it is doing, a picture is kept and named, and a node rebuilt
/// on the same data folder reads the same chat. No real Claude session starts.
/// </summary>
public sealed class DesignChatTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private const string Job = "job00001";
    private const string Session = Job + "-0000-4000-8000-000000000000";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "p2dsc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TempProfile _profile = new();
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly DesignStore _store;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly Designers _designers;
    private readonly DesignChat _chat;
    private readonly DesignCommands _commands;

    public DesignChatTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _store = new DesignStore(_data, _time);
        _designers = new Designers(_state, _store, _workers, new StartGate(_state, _time), new RoleModels(null), "DE1", _time)
        {
            Run = "pilot",
            Say = (command, _) =>
            {
                _said.Add(command);
                return Task.FromResult(_state.Complete(command, null));
            },
        };
        _chat = new DesignChat(_store, new TranscriptBook(_profile.Profile), () => _state.Sessions());
        _commands = new DesignCommands(_state, _store, _designers, _chat);
    }

    public void Dispose()
    {
        _profile.Dispose();
        if (Directory.Exists(_data))
        {
            TestDirs.DeleteTree(_data);
        }
    }

    private CommandResult Run(string verb, string id, params string[] args) =>
        _commands.Run(new CommandRequest { Verb = verb, Target = Verbs.DesignTarget(id), Args = args, Who = "gui" });

    private async Task<string> DesignWithDesignerAsync()
    {
        var id = _commands.Run(new CommandRequest { Verb = Verbs.DesignNew, Target = Verbs.NodeTarget, Args = ["", "", "a night list"], Who = "gui" }).Made.ShouldNotBeNull();
        await _designers.Settled;
        Listed(SessionState.Working);
        _designers.Sweep();
        await _designers.Settled;
        return id;
    }

    private const string Designer = "de1-dsn1-designer";

    /// <summary>A row that is not the designer (a Gone or a Failed one), listed under the designer's name.</summary>
    private const string Other = "job00002";

    private void Listed(SessionState state, bool gone = false, DateTimeOffset? lastResponse = null, string job = Job, DateTimeOffset? started = null) =>
        _state.PutSession(new SessionRow
        {
            Id = job, SessionId = job == Job ? Session : job + "-s", Name = Designer, Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Designer,
            ContextTokens = 1, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = started ?? Start, Gone = gone,
            LastResponseAt = lastResponse ?? Start.AddMinutes(2),
        });

    /// <summary>
    /// Lists <see cref="Other"/> started after the live row, so <see cref="NodeState.Sessions"/> (newest start first) gives it before the live one and a
    /// <c>FirstOrDefault</c> by name meets it first: the Gone and Failed skip in <see cref="DesignChat"/> is then what the test turns on, not the order.
    /// </summary>
    private void ListedFirst(SessionState state, bool gone = false, DateTimeOffset? lastResponse = null)
    {
        Listed(state, gone, lastResponse, job: Other, started: Start.AddMinutes(3));
        _state.Sessions().Where(s => s.Name == Designer).Select(s => s.Id).ShouldBe([Other, Job], "the row to skip is the one met first");
    }

    private bool Working(string id) => Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull().Working;

    /// <summary>A design whose designer's turn started at 09:05, with its live row (<see cref="Job"/>) still listed Working.</summary>
    private async Task<string> TurnStartedAsync()
    {
        var id = await DesignWithDesignerAsync();
        _store.Update(id, r => r with { DesignerTurnStartedAt = Start.AddMinutes(5) });
        return id;
    }

    [Fact]
    public async Task AGoneIdleRowAloneDoesNotEndAFreshlyStartedDesignersTurn()
    {
        var id = await TurnStartedAsync();
        _state.RemoveSession(Job);
        // The previous designer's row, gone, with a Stop after this turn's start: read as the designer, it would end the turn.
        Listed(SessionState.Idle, gone: true, lastResponse: Start.AddMinutes(10), job: Other);
        Working(id).ShouldBeTrue("a fresh start stays Working until its own row is listed");
    }

    [Fact]
    public async Task AGoneIdleRowListedFirstDoesNotEndTheLiveDesignersTurn()
    {
        var id = await TurnStartedAsync();
        ListedFirst(SessionState.Idle, gone: true, lastResponse: Start.AddMinutes(10));
        Working(id).ShouldBeTrue("the live row, still Working, is the designer; the Gone Idle one before it is not");
    }

    [Fact]
    public async Task AGoneRowListedFirstIsPassedSoTheLiveRowsIdleEndsTheTurn()
    {
        var id = await TurnStartedAsync();
        // A designer that died mid-turn: the tracker keeps its last state, Working, and marks it gone.
        ListedFirst(SessionState.Working, gone: true, lastResponse: Start.AddMinutes(4));
        Listed(SessionState.Idle, lastResponse: Start.AddMinutes(6));
        Working(id).ShouldBeFalse("the live row's Idle, after the turn's start, is read past the Gone one");
    }

    [Fact]
    public async Task AFailedRowAloneDoesNotEndTheTurn()
    {
        var id = await TurnStartedAsync();
        _state.RemoveSession(Job);
        Listed(SessionState.Failed, lastResponse: Start.AddMinutes(10), job: Other);
        Working(id).ShouldBeTrue("a Failed row alone ends nothing: Live reads only an Idle or Ended row, so a fresh start stays Working until its own row is listed");
    }

    [Fact]
    public async Task AFailedRowListedFirstIsPassedSoTheLiveRowsIdleEndsTheTurn()
    {
        var id = await TurnStartedAsync();
        ListedFirst(SessionState.Failed, lastResponse: Start.AddMinutes(10));
        Working(id).ShouldBeTrue("the live row is still Working");
        Listed(SessionState.Idle, lastResponse: Start.AddMinutes(6));
        Working(id).ShouldBeFalse("the live row's Idle, after the turn's start, is read past the Failed one");
    }

    [Fact]
    public async Task AnIdleOlderThanTheTurnsStartIsThePreviousTurnsEndNotThisOnes()
    {
        var id = await DesignWithDesignerAsync();
        _store.Update(id, r => r with { DesignerTurnStartedAt = Start.AddMinutes(5) });
        Listed(SessionState.Idle, lastResponse: Start.AddMinutes(1));
        Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull().Working.ShouldBeTrue();
        Listed(SessionState.Idle, lastResponse: Start.AddMinutes(6));
        Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull().Working.ShouldBeFalse();
    }

    private void DesignerReplies(string when, string text) =>
        _profile.Append(_profile.TranscriptPath(Session),
            $$$"""{"type":"assistant","timestamp":"{{{when}}}","message":{"role":"assistant","content":[{"type":"text","text":"{{{text}}}"}]}}""");

    [Fact]
    public async Task TheChatStopsWorkingAtOnceWhenTheDesignersTurnEndsWithoutASweep()
    {
        var id = await DesignWithDesignerAsync();
        _store.Update(id, r => r with
        {
            DesignerPhase = DesignerPhases.Published, Current = 1, Versions = 1,
            History = [new DesignVersionRow { Number = 1, At = Start.AddMinutes(1), Why = "brief" }], DesignerTurnStartedAt = Start,
        });

        var during = Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull();
        during.Working.ShouldBeTrue();

        // The session's turn ends; no sweep runs, yet the page follows the tracker.
        Listed(SessionState.Idle);
        var after = Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull();
        after.Working.ShouldBeFalse();
        after.Status.ShouldBe("published v1");
    }

    [Fact]
    public async Task ASayIsAppendedToTheBriefVerbatimAndReachesTheOneDesignerResumed()
    {
        var id = await DesignWithDesignerAsync();

        Run(Verbs.DesignSay, id, "make the night list wider,\n  and *darker*").Ok.ShouldBeTrue();
        await _designers.Settled;

        _workers.Started.Count.ShouldBe(1);
        var said = _said.ShouldHaveSingleItem();
        said.Target.ShouldBe(Verbs.SessionTarget(Job));
        said.Args[0].ShouldContain("make the night list wider,\n  and *darker*");
        _store.Detail(id).Brief.ShouldNotBeNull().ShouldContain("## 2026-11-17T09:00:00Z\n\nmake the night list wider,\n  and *darker*\n");

        var page = Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull();
        var entry = page.Entries.Skip(1).ShouldHaveSingleItem();
        page.Entries[0].Text.ShouldBe("a night list");
        entry.Kind.ShouldBe(TranscriptKind.User);
        entry.Text.ShouldBe("make the night list wider,\n  and *darker*");
        entry.Index.ShouldBe(1);
    }

    [Fact]
    public async Task TheDesignersReplyAndAPublishedVersionReadAfterTheLastIndexTheClientHad()
    {
        var id = await DesignWithDesignerAsync();
        _store.Update(id, r => r with { Current = 1, Versions = 1, History = [new DesignVersionRow { Number = 1, At = Start.AddMinutes(-1), Why = "brief" }] });
        Run(Verbs.DesignSay, id, "first change").Ok.ShouldBeTrue();
        await _designers.Settled;
        var first = Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull();
        first.Total.ShouldBe(3);

        _time.Advance(TimeSpan.FromMinutes(5));
        DesignerReplies("2026-11-17T09:05:00Z", "Widened the list.");
        _time.Advance(TimeSpan.FromMinutes(1));
        _store.Update(id, r => r with
        {
            Current = 2, Versions = 2,
            History = [new DesignVersionRow { Number = 1, At = Start.AddMinutes(-1), Why = "brief" }, new DesignVersionRow { Number = 2, At = Start.AddMinutes(6), Why = "message" }],
        });

        var later = Run(Verbs.DesignChat, id, (first.Total - 1).ToString()).DesignChat.ShouldNotBeNull();
        later.Entries.Select(e => e.Text).ShouldBe(["Widened the list.", "published v2: message"]);
        later.Entries.Select(e => e.Index).ShouldBe([3, 4]);
        later.Total.ShouldBe(5);

        var whole = Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull();
        whole.Entries.Select(e => e.Text).ShouldBe(["published v1: brief", "a night list", "first change", "Widened the list.", "published v2: message"]);
        whole.Entries[4].Kind.ShouldBe(TranscriptKind.Divider);
    }

    [Fact]
    public async Task AFreeTextWhyReadsOnTheVersionLineAsPublishedVWhy()
    {
        var id = await DesignWithDesignerAsync();
        _store.Update(id, r => r with { Current = 1, Versions = 1, History = [new DesignVersionRow { Number = 1, At = Start, Why = "added the bind rows to the settings screen" }] });
        Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull().Entries.Select(e => e.Text).ShouldBe(["a night list", "published v1: added the bind rows to the settings screen"]);
    }

    [Fact]
    public async Task TheStatusReadsDraftingCheckingRenderingPublishedAndHeldUntil()
    {
        var id = await DesignWithDesignerAsync();
        Status(id).ShouldBe("drafting");

        // A real design-check on the starter draft counts its findings: the empty objectives of the design and of its one screen
        // (the design is given a title first, as a heading of the id alone is a third finding).
        _store.SetTitle(id, "Night list");
        Run(Verbs.DesignDraft, id).Ok.ShouldBeTrue();
        Run(Verbs.DesignCheck, id);
        Status(id).ShouldBe("checking: 2 findings");
        _store.Update(id, r => r with { CheckFindings = 3 });
        Status(id).ShouldBe("checking: 3 findings");

        _store.Update(id, r => r with { DesignerPhase = DesignerPhases.Rendering });
        Status(id).ShouldBe("rendering");

        _store.Update(id, r => r with { DesignerPhase = DesignerPhases.Published, Current = 1, Versions = 1 });
        Status(id).ShouldBe("published v1");

        _store.Update(id, r => r with { DesignerState = DesignerStates.Idle });
        Status(id).ShouldBe("published v1");
        Run(Verbs.DesignChat, id).DesignChat!.Working.ShouldBeFalse();

        _store.Update(id, r => r with { DesignerState = DesignerStates.Held, DesignerHeldUntil = Start.AddHours(2).AddMinutes(30) });
        // In the node's local time, as every other held time pilot says (#1158 R1.5: the chat alone said UTC).
        Status(id).ShouldBe($"held until {Start.AddHours(2).AddMinutes(30).ToLocalTime():HH:mm}");
    }

    private string? Status(string id) => Run(Verbs.DesignChat, id).DesignChat!.Status;

    [Fact]
    public async Task ASayWhileTheUsageLimitHoldsIsKeptAndDeliveredOnceItEnds()
    {
        var id = await DesignWithDesignerAsync();
        _state.PutUsage(new UsageRow { ReadAt = Start, FiveHour = new UsageWindow { UsedPercent = 95, ResetsAt = Start.AddHours(2) } });

        Run(Verbs.DesignSay, id, "while held").Ok.ShouldBeTrue();
        await _designers.Settled;

        _said.ShouldBeEmpty();
        Status(id).ShouldStartWith($"held until {Start.AddHours(2).ToLocalTime():HH:mm}");
        _store.Find(id)!.DesignerPending.ShouldBe(["while held"]);
        Run(Verbs.DesignChat, id).DesignChat!.Entries.Skip(1).ShouldHaveSingleItem().Text.ShouldBe("while held");

        _time.Advance(TimeSpan.FromHours(3));
        _state.PutUsage(new UsageRow { ReadAt = _time.GetUtcNow(), FiveHour = new UsageWindow { UsedPercent = 5, ResetsAt = _time.GetUtcNow().AddHours(5) } });
        _designers.Sweep();
        await _designers.Settled;

        _said.ShouldHaveSingleItem().Args[0].ShouldContain("while held");
        _workers.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task APictureWithASayIsKeptWithTheDesignAndNamedToTheDesigner()
    {
        var id = await DesignWithDesignerAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

        Run(Verbs.DesignSay, id, "like this", "../sketch one.png", Convert.ToBase64String(png)).Ok.ShouldBeTrue();
        await _designers.Settled;

        var kept = Directory.GetFiles(Path.Combine(_store.FolderOf(id), "pictures")).ShouldHaveSingleItem();
        Path.GetFileName(kept).ShouldBe("1-sketch_one.png");
        File.ReadAllBytes(kept).ShouldBe(png);
        _said.ShouldHaveSingleItem().Args[0].ShouldContain(kept);
        Run(Verbs.DesignChat, id).DesignChat!.Entries.Skip(1).ShouldHaveSingleItem().Text.ShouldBe("like this\n[picture: pictures/1-sketch_one.png]");
    }

    [Fact]
    public async Task TheChatReadsTheSameAfterTheNodeIsRebuiltOnTheSameDataFolder()
    {
        var id = await DesignWithDesignerAsync();
        Run(Verbs.DesignSay, id, "kept words").Ok.ShouldBeTrue();
        await _designers.Settled;
        DesignerReplies("2026-11-17T09:02:00Z", "Done.");
        _store.Update(id, r => r with { History = [new DesignVersionRow { Number = 1, At = Start.AddMinutes(3), Why = "brief" }], Current = 1, Versions = 1 });
        var before = Run(Verbs.DesignChat, id).DesignChat.ShouldNotBeNull();

        // A new store, chat and transcript book on the same folders: nothing of the chat lived in memory.
        var again = new DesignChat(new DesignStore(_data, _time), new TranscriptBook(_profile.Profile)).Page(id, -1);

        again.Entries.Select(e => (e.Index, e.Kind, e.Text, e.At)).ShouldBe(before.Entries.Select(e => (e.Index, e.Kind, e.Text, e.At)));
        again.Entries.Select(e => e.Text).ShouldBe(["a night list", "kept words", "Done.", "published v1: brief"]);
        again.Status.ShouldBe(before.Status);
    }

    [Fact]
    public async Task TheNodesOwnPromptsAndTheDesignersToolCallsAreNotInTheChat()
    {
        var id = await DesignWithDesignerAsync();
        _profile.Append(_profile.TranscriptPath(Session),
            """{"type":"user","timestamp":"2026-11-17T09:00:30Z","message":{"role":"user","content":"[node] the owner added to the brief"}}""",
            """{"type":"assistant","timestamp":"2026-11-17T09:00:40Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"pilot design check DSN-1"}}]}}""");
        DesignerReplies("2026-11-17T09:00:50Z", "Looking.");

        Run(Verbs.DesignChat, id).DesignChat!.Entries.Select(e => e.Text).ShouldBe(["a night list", "Looking."]);
    }

    [Fact]
    public async Task TheFirstBriefIsEntryZeroOfKindUser()
    {
        var id = await DesignWithDesignerAsync();

        var entry = Run(Verbs.DesignChat, id).DesignChat!.Entries.ShouldHaveSingleItem();
        entry.Index.ShouldBe(0);
        entry.Kind.ShouldBe(TranscriptKind.User);
        entry.Text.ShouldBe("a night list");
    }

    [Fact]
    public async Task ToolErrorsHookErrorsAndARefusedCheckAreNotChatEntriesButTheDesignersWordsAre()
    {
        var id = await DesignWithDesignerAsync();
        _profile.Append(_profile.TranscriptPath(Session),
            """{"type":"assistant","timestamp":"2026-11-17T09:00:10Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"x"}}]}}""",
            """{"type":"user","timestamp":"2026-11-17T09:00:11Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"<tool_use_error>File has not been read yet.</tool_use_error>"}]}}""",
            """{"type":"user","timestamp":"2026-11-17T09:00:12Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","is_error":true,"content":"PreToolUse:Bash hook error: [pilot hook] a designer writes only under draft/"}]}}""",
            """{"type":"user","timestamp":"2026-11-17T09:00:13Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t3","is_error":true,"content":"Exit code 1\nrefused: design-check DSN-1: 2 findings\nhome | 80x24 | overlap: a on b"}]}}""",
            """{"type":"user","timestamp":"2026-11-17T09:00:14Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t4","content":"ok"}]}}""");
        DesignerReplies("2026-11-17T09:00:20Z", "Fixing the overlap.");
        _store.Update(id, r => r with { DesignerPhase = DesignerPhases.Checking, CheckFindings = 2 });

        var page = Run(Verbs.DesignChat, id).DesignChat!;

        page.Entries.Select(e => e.Text).ShouldBe(["a night list", "Fixing the overlap."]);
        page.Status.ShouldBe("checking: 2 findings");
    }

    [Fact]
    public async Task AfterATurnThatPublishedNothingTheStatusSaysIdleAndNamesTheCurrentVersionAsCurrent()
    {
        var id = await DesignWithDesignerAsync();
        _store.Update(id, r => r with { Current = 2, Versions = 2, DesignerPhase = DesignerPhases.Published, History = [new DesignVersionRow { Number = 2, At = Start.AddMinutes(-5), Why = "before" }] });
        Run(Verbs.DesignSay, id, "try a wider list").Ok.ShouldBeTrue();
        await _designers.Settled;
        Status(id).ShouldBe("drafting");

        _time.Advance(TimeSpan.FromMinutes(2));
        Listed(SessionState.Idle);
        _designers.Sweep();
        await _designers.Settled;

        Status(id).ShouldBe("idle: the last turn published nothing, v2 is current");
    }

    [Fact]
    public async Task AfterATurnThatPublishedTheStatusStillNamesTheVersion()
    {
        var id = await DesignWithDesignerAsync();
        _time.Advance(TimeSpan.FromMinutes(2));
        _store.Update(id, r => r with { Current = 1, Versions = 1, DesignerPhase = DesignerPhases.Published, History = [new DesignVersionRow { Number = 1, At = _time.GetUtcNow(), Why = "first" }] });

        Listed(SessionState.Idle);
        _designers.Sweep();
        await _designers.Settled;

        Status(id).ShouldBe("published v1");
        _store.Find(id)!.DesignerTurnPublished.ShouldBe(true);
    }

    [Fact]
    public void ASayOnADesignThatIsNotThereIsRefused()
    {
        Run(Verbs.DesignSay, "DSN-9", "words").Ok.ShouldBeFalse();
        Run(Verbs.DesignChat, "DSN-9").Ok.ShouldBeFalse();
    }
}
