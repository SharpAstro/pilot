using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The designer session of a design (PIL1151), on an in-memory node's parts with a fake launcher and fake time: one designer per design, started with the
/// contract, resumed (never started twice) on a later brief piece, held by the usage limit, and kept to its design's <c>draft/</c>. No real Claude session starts.
/// </summary>
public sealed class DesignerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly string _data = Path.Combine(Path.GetTempPath(), "p2dsn-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly DesignStore _store;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly Designers _designers;
    private readonly DesignCommands _commands;

    public DesignerTests()
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
        _commands = new DesignCommands(_state, _store, _designers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            TestDirs.DeleteTree(_data);
        }
    }

    private string New(string brief) =>
        _commands.Run(new CommandRequest { Verb = Verbs.DesignNew, Target = Verbs.NodeTarget, Args = ["", "", brief], Who = "gui" }).Made.ShouldNotBeNull();

    private void Brief(string id, string words) =>
        _commands.Run(new CommandRequest { Verb = Verbs.DesignBrief, Target = Verbs.DesignTarget(id), Args = [words], Who = "gui" }).Ok.ShouldBeTrue();

    private void ListedAs(string job, string name, SessionState sessionState = SessionState.Idle) =>
        _state.PutSession(new SessionRow
        {
            Id = job, SessionId = job + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Designer,
            ContextTokens = 1, ContextWindow = 200_000, State = sessionState, SpendWeighted = 0, StartedAt = Start,
        });

    private void Usage(double fiveHour, TimeSpan resetsIn) =>
        _state.PutUsage(new UsageRow
        {
            ReadAt = _time.GetUtcNow(),
            FiveHour = new UsageWindow { UsedPercent = fiveHour, ResetsAt = _time.GetUtcNow() + resetsIn },
        });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ANewDesignWithABlankBriefStartsNoDesigner(string brief)
    {
        // The store refuses a blank brief, and the refused command must not have asked for a designer on the way.
        _commands.Run(new CommandRequest { Verb = Verbs.DesignNew, Target = Verbs.NodeTarget, Args = ["", "", brief], Who = "gui" }).Ok.ShouldBeFalse();
        await _designers.Settled;
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task ANewDesignAsksForExactlyOneDesignerWithTheContractAndTheRightName()
    {
        var id = New("A dashboard for the observatory, dark, with a night list.");
        await _designers.Settled;

        var start = _workers.Started.ShouldHaveSingleItem();
        start.Name.ShouldBe("de1-dsn1-designer");
        start.RepoPath.ShouldBe(_store.FolderOf(id));
        start.Plane.ShouldBeNull();
        start.Model.ShouldBe("opus");
        start.Reason.ShouldBe(SessionStartReasons.Designer);
        // The contract: the brief so far, the prototype format and the starter prototype, the loop, and when it may publish.
        start.Brief.ShouldContain("A dashboard for the observatory, dark, with a night list.");
        start.Brief.ShouldContain("prototype.json");
        start.Brief.ShouldContain("\"entry\":\"home\"");
        start.Brief.ShouldContain("design draft DSN-1");
        start.Brief.ShouldContain("design check DSN-1");
        start.Brief.ShouldContain("design render DSN-1");
        start.Brief.ShouldContain("design publish DSN-1");
        start.Brief.ShouldContain("Publish only when `design check` says clean");
        start.Brief.ShouldContain("say in your reply what is in the way");
        start.Brief.ShouldContain("draft/");

        // The state is on the design, so a restart finds the session.
        var row = new DesignStore(_data, _time).Find(id).ShouldNotBeNull();
        row.DesignerName.ShouldBe("de1-dsn1-designer");
        row.DesignerJob.ShouldBe("job00001");
        row.DesignerState.ShouldBe(DesignerStates.Working);
        SessionRoles.Resolve(row.DesignerName).Role.ShouldBe(SessionRole.Designer);
    }

    [Fact]
    public async Task ASecondAskWhileOneRunsIsDeliveredToItAndStartsNoSecond()
    {
        var id = New("first words");
        await _designers.Settled;
        ListedAs("job00001", "de1-dsn1-designer", SessionState.Working);

        Brief(id, "make the night list wider");
        Brief(id, "and darker");
        await _designers.Settled;

        _workers.Started.Count.ShouldBe(1);
        _said.Count.ShouldBe(2);
        _said.ShouldAllBe(c => c.Target == Verbs.SessionTarget("job00001") && c.Who == "node");
        _said[0].Args[0].ShouldContain("make the night list wider");
        _said[1].Args[0].ShouldContain("and darker");
        // The brief stays the one record, verbatim.
        _store.Detail(id).Brief.ShouldNotBeNull().ShouldContain("make the night list wider");
    }

    [Fact]
    public async Task AnAskBeforeTheStartIsListedWaitsForItAndStillStartsNoSecond()
    {
        var id = New("first words");
        await _designers.Settled;

        Brief(id, "second words");
        await _designers.Settled;
        _workers.Started.Count.ShouldBe(1);
        _said.ShouldBeEmpty();
        _store.Find(id)!.DesignerPending.ShouldBe(["second words"]);

        ListedAs("job00001", "de1-dsn1-designer");
        _designers.Sweep();
        await _designers.Settled;

        _workers.Started.Count.ShouldBe(1);
        _said.ShouldHaveSingleItem().Args[0].ShouldContain("second words");
        _store.Find(id)!.DesignerPending.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADesignerWhoseTurnIsDoneIsResumedByTheNodesSayNotStartedAgain()
    {
        var id = New("first words");
        await _designers.Settled;
        ListedAs("job00001", "de1-dsn1-designer", SessionState.Ended);
        _designers.Sweep();
        await _designers.Settled;
        _store.Find(id)!.DesignerState.ShouldBe(DesignerStates.Idle);

        Brief(id, "one more change");
        await _designers.Settled;

        _workers.Started.Count.ShouldBe(1);
        _said.ShouldHaveSingleItem().Args[0].ShouldContain("one more change");
        _store.Find(id)!.DesignerState.ShouldBe(DesignerStates.Working);
    }

    private string IdleDesigner(string brief = "first words")
    {
        var id = New(brief);
        _designers.Settled.GetAwaiter().GetResult();
        ListedAs("job00001", "de1-dsn1-designer", SessionState.Idle);
        _designers.Sweep();
        _designers.Settled.GetAwaiter().GetResult();
        _store.Find(id)!.DesignerState.ShouldBe(DesignerStates.Idle);
        return id;
    }

    [Fact]
    public async Task AnIdleDesignerIsLeftAloneBeforeTheKeepTimeAndEndedByTheNodeAfterIt()
    {
        var id = IdleDesigner();
        _time.Advance(TimeSpan.FromMinutes(29));
        _designers.Sweep();
        await _designers.Settled;
        _workers.StopReasons.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));
        _designers.Sweep();
        await _designers.Settled;

        var stop = _workers.StopReasons.ShouldHaveSingleItem();
        stop.ShouldBe(("job00001", SessionEndReasons.DesignerIdle, "node"));
        _store.Find(id)!.DesignerState.ShouldBe(DesignerStates.Idle);
    }

    [Fact]
    public async Task AnIdleDesignerWhoseSessionRowIsGoneIsNotEndedAgain()
    {
        // A row marked Gone is already closed by the ledger: the idle sweep must not stop it a second time.
        IdleDesigner();
        _state.PutSession(_state.Sessions().Single(s => s.Id == "job00001") with { Gone = true });
        _time.Advance(TimeSpan.FromMinutes(31));
        _designers.Sweep();
        await _designers.Settled;

        _workers.StopReasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheKeepSettingSetsHowLongAndZeroEndsItAtOnce()
    {
        var short_ = new Designers(_state, _store, _workers, new StartGate(_state, _time), new RoleModels(null), "DE1", _time) { Run = "pilot", Keep = () => TimeSpan.Zero };
        _store.New(null, null, "words", []);
        ListedAs("job00002", "de1-dsn1-designer", SessionState.Idle);
        _store.Update("DSN-1", r => r with { DesignerName = "de1-dsn1-designer", DesignerState = DesignerStates.Idle, DesignerIdleSince = _time.GetUtcNow(), DesignerSessions = ["job00002-0000-4000-8000-000000000000"] });
        short_.Sweep();
        await short_.Settled;
        _workers.StopReasons.ShouldHaveSingleItem().Reason.ShouldBe(SessionEndReasons.DesignerIdle);
    }

    [Fact]
    public async Task ADesignerStillWorkingIsNeverEndedHoweverLongItTakes()
    {
        var id = New("first words");
        await _designers.Settled;
        ListedAs("job00001", "de1-dsn1-designer", SessionState.Working);
        _time.Advance(TimeSpan.FromHours(5));
        _designers.Sweep();
        await _designers.Settled;

        _workers.StopReasons.ShouldBeEmpty();
        _store.Find(id)!.DesignerState.ShouldBe(DesignerStates.Working);

        // A turn that began after an idle spell clears the mark: it is not ended on the old time either.
        ListedAs("job00001", "de1-dsn1-designer", SessionState.Idle);
        _designers.Sweep();
        await _designers.Settled;
        Brief(id, "more");
        await _designers.Settled;
        _store.Find(id)!.DesignerIdleSince.ShouldBeNull();
        ListedAs("job00001", "de1-dsn1-designer", SessionState.Working);
        _time.Advance(TimeSpan.FromHours(5));
        _designers.Sweep();
        await _designers.Settled;
        _workers.StopReasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task APieceAfterTheEndResumesTheEndedDesignerThroughSayAndStartsNoSecond()
    {
        var id = IdleDesigner();
        _time.Advance(TimeSpan.FromMinutes(31));
        _designers.Sweep();
        await _designers.Settled;
        _workers.StopReasons.Count.ShouldBe(1);
        ListedAs("job00001", "de1-dsn1-designer", SessionState.Ended);

        Brief(id, "after the end");
        await _designers.Settled;

        _workers.Started.Count.ShouldBe(1);
        _said.ShouldHaveSingleItem().Args[0].ShouldContain("after the end");
        _store.Find(id)!.DesignerState.ShouldBe(DesignerStates.Working);
        _store.Find(id)!.DesignerIdleSince.ShouldBeNull();
    }

    private string Hook(string session, string tool, string cwd, string path) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = session, ["tool_name"] = tool, ["cwd"] = cwd,
            ["tool_input"] = new Dictionary<string, string> { [tool == "NotebookEdit" ? "notebook_path" : "file_path"] = path },
        });

    private string? Decide(string hook, string cli = "/opt/pilot dir/pilot")
    {
        using var doc = JsonDocument.Parse(hook);
        return new DesignerGuard(_state, _store) { Cli = cli }.Decide(doc.RootElement);
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    public void ADesignersWriteOutsideDraftIsDeniedAndOneInsideIsLetThrough(string tool)
    {
        var id = New("words");
        ListedAs("job00001", "de1-dsn1-designer");
        var session = "job00001-0000-4000-8000-000000000000";
        var folder = _store.FolderOf(id);

        var inside = Decide(Hook(session, tool, folder, Path.Combine(folder, "draft", "prototype.json")));
        inside.ShouldBeNull();
        Decide(Hook(session, tool, folder, Path.Combine("draft", "NOTES.md"))).ShouldBeNull();

        foreach (var outside in new[] { Path.Combine(folder, "BRIEF.md"), Path.Combine(folder, "versions", "v1", "prototype.json"), Path.Combine(folder, "draft", "..", "design.json"), Path.Combine(_data, "other.txt") })
        {
            var denied = Decide(Hook(session, tool, folder, outside)).ShouldNotBeNull();
            using var answer = JsonDocument.Parse(denied);
            var output = answer.RootElement.GetProperty("hookSpecificOutput");
            output.GetProperty("hookEventName").GetString().ShouldBe("PreToolUse");
            output.GetProperty("permissionDecision").GetString().ShouldBe("deny");
            output.GetProperty("permissionDecisionReason").GetString().ShouldNotBeNull().ShouldContain("draft/");
        }
    }

    private string ShellHook(string session, string tool, string cwd, string command) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = session, ["tool_name"] = tool, ["cwd"] = cwd,
            ["tool_input"] = new Dictionary<string, string> { ["command"] = command },
        });

    [Theory]
    [InlineData("Bash", "echo hi > BRIEF.md")]
    [InlineData("Bash", "cat a.txt | tee ../design.json")]
    [InlineData("Bash", "pilot design check DSN-1; rm -rf .")]
    [InlineData("Bash", "git commit -am x")]
    // The owner's publish is the owner's, in every spelling System.CommandLine takes.
    [InlineData("Bash", "pilot design publish DSN-1 --why x --owner")]
    [InlineData("Bash", "pilot design publish DSN-1 --why x --owner=true")]
    [InlineData("Bash", "pilot design publish DSN-1 --why x --owner:true")]
    [InlineData("PowerShell", "Set-Content -Path ..\\BRIEF.md -Value x")]
    [InlineData("PowerShell", "Get-Content a.txt > b.txt")]
    [InlineData("Bash", "cat $(echo x)")]
    // Escaped quotes that a quote-pairing pattern read as one string, hiding the redirect between them (bash runs it).
    [InlineData("Bash", "ls \\\" > ../design.json \\\"")]
    [InlineData("Bash", "ls \\' > ../design.json \\'")]
    [InlineData("Bash", "cat \\\" < ../x \\\"")]
    [InlineData("Bash", "cat \"a\\\" > ../BRIEF.md \\\"b\"")]
    [InlineData("Bash", "cat draft/x \"unclosed > ../y")]
    // PowerShell closes a string at a curly quote as at a straight one.
    [InlineData("PowerShell", "Get-Content \"a” > ../BRIEF.md “b\"")]
    // A program named by a path could be a script the designer wrote in draft/ (Git Bash runs one with no exec bit), whatever its last name.
    [InlineData("Bash", "draft/cat draft/NOTES.md")]
    [InlineData("Bash", "./pilot design draft DSN-1")]
    [InlineData("PowerShell", "draft\\pilot design check DSN-1")]
    [InlineData("Bash", "../DSN-1/draft/ls")]
    [InlineData("Bash", "~/AppData/Local/pilot2/designs/DSN-1/draft/cat x")]
    [InlineData("Bash", "/c/Users/x/AppData/Local/pilot2/designs/DSN-1/draft/pilot design draft DSN-1")]
    [InlineData("Bash", "'draft\\cat' x")]
    [InlineData("PowerShell", "C:cat x")]
    // The CLI's own path with a step back through another folder is not the CLI as written.
    [InlineData("Bash", "\"/opt/pilot dir/../pilot dir/pilot\" design check DSN-1")]
    // In double quotes bash reads a backslash before a quote, a backslash or $ as an escape.
    [InlineData("Bash", "cat \"a\\\\\" draft/x")]
    public void ADesignersShellWriteIsDenied(string tool, string command)
    {
        var id = New("words");
        ListedAs("job00001", "de1-dsn1-designer");
        var denied = Decide(ShellHook("job00001-0000-4000-8000-000000000000", tool, _store.FolderOf(id), command)).ShouldNotBeNull();
        using var answer = JsonDocument.Parse(denied);
        var output = answer.RootElement.GetProperty("hookSpecificOutput");
        output.GetProperty("permissionDecision").GetString().ShouldBe("deny");
        output.GetProperty("permissionDecisionReason").GetString().ShouldNotBeNull().ShouldSatisfyAllConditions(
            reason => reason.ShouldContain("no backslash outside quotes"),
            reason => reason.ShouldContain("/opt/pilot dir/pilot"));
    }

    [Theory]
    // The node's CLI where the user name has a space: Briefs.Run quotes it, and bash reads the backslashes in double quotes as they are.
    [InlineData("Bash", "\"C:\\Users\\John Smith\\pilot2\\pilot.exe\" design draft DSN-1")]
    [InlineData("Bash", "\"C:/Users/John Smith/pilot2/pilot\" design check DSN-1")]
    [InlineData("Bash", "pilot.exe design show DSN-1")]
    [InlineData("PowerShell", "pilot design render DSN-1")]
    public void TheNodesOwnCliByItsPathIsLetThrough(string tool, string command)
    {
        var id = New("words");
        ListedAs("job00001", "de1-dsn1-designer");
        Decide(ShellHook("job00001-0000-4000-8000-000000000000", tool, _store.FolderOf(id), command), cli: "C:\\Users\\John Smith\\pilot2\\pilot.exe").ShouldBeNull();
    }

    [Theory]
    // The node's CLI where the path holds no space: Briefs.Run writes it unquoted, backslashes and all, and in PowerShell that runs as it is.
    [InlineData("PowerShell", "C:\\Users\\JohnSmith\\pilot2\\pilot.exe design draft DSN-1")]
    [InlineData("PowerShell", "C:\\Users\\JohnSmith\\pilot2\\pilot.exe --node-socket \"C:\\tmp\\n.sock\" design show DSN-1")]
    [InlineData("Bash", "C:/Users/JohnSmith/pilot2/pilot.exe design check DSN-1")]
    public void TheNodesOwnCliWithNoSpaceUnquotedIsLetThrough(string tool, string command)
    {
        Briefs.Run("C:\\Users\\JohnSmith\\pilot2\\pilot.exe", null).ShouldBe("C:\\Users\\JohnSmith\\pilot2\\pilot.exe");
        var id = New("words");
        ListedAs("job00001", "de1-dsn1-designer");
        Decide(ShellHook("job00001-0000-4000-8000-000000000000", tool, _store.FolderOf(id), command), cli: "C:\\Users\\JohnSmith\\pilot2\\pilot.exe").ShouldBeNull();
    }

    [Fact]
    public void TheBriefNamesEveryNodeKindTheLoaderAcceptsWithItsPropertiesAndChildCounts()
    {
        var detail = new DesignDetail { Row = new DesignRow { Id = "DSN-1" }, Folder = "f", DraftFolder = "f/draft" };
        var brief = Briefs.Designer(detail, "{}", null, "pilot");
        foreach (var (name, kind) in DIR.Lib.Prototyping.PrototypeVocabulary.Kinds)
        {
            var line = brief.Split('\n').FirstOrDefault(l => l.StartsWith($"- `{name}`:", StringComparison.Ordinal)).ShouldNotBeNull(name);
            foreach (var property in kind.Properties)
            {
                line.ShouldContain($"`{property.Name}`", customMessage: name);
                if (property.Required)
                {
                    line.ShouldContain($"`{property.Name}` (", customMessage: name);
                }
                foreach (var value in property.Values)
                {
                    line.ShouldContain($"\"{value}\"", customMessage: name);
                }
            }
            line.ShouldContain(kind.MaxChildren == 0 ? "no children" : kind.MaxChildren < 0 ? "children" : kind.MaxChildren + " children", customMessage: name);
            (kind.SupportsBind == line.Contains("takes `bind`")).ShouldBeTrue(name);
        }
        foreach (var property in DIR.Lib.Prototyping.PrototypeVocabulary.Common)
        {
            brief.ShouldContain($"`{property.Name}`");
        }
        foreach (var role in DIR.Lib.Prototyping.PrototypeVocabulary.ColorRoles)
        {
            brief.ShouldContain($"`{role}`");
        }
    }

    [Fact]
    public void TheBriefAsksForAShortWhyAndSaysPublishTakesTheDocumentThePrototypeItsScreensAndTheRenders()
    {
        var detail = new DesignDetail { Row = new DesignRow { Id = "DSN-1" }, Folder = "f", DraftFolder = "f/draft" };
        var brief = Briefs.Designer(detail, "{}", null, "pilot");
        brief.ShouldContain("--why \"<what changed>\"");
        brief.ShouldContain($"at most {DesignWhys.MaxLength} characters");
        brief.ShouldContain("only `DESIGN.md`, `prototype.json`, `screens/`, `NOTES.md` (when there is one) and `renders/`");
        brief.ShouldContain("nothing needs deleting");
        brief.ShouldNotContain("brief|message|fix");
        Briefs.DesignerPiece("DSN-1", "words").ShouldContain("--why");
    }

    [Fact]
    public void TheBriefAsksForAShortTitleOfAtMostFortyCharactersInTheDocumentsHeading()
    {
        var brief = Briefs.Designer(new DesignDetail { Row = new DesignRow { Id = "DSN-1" }, Folder = "f", DraftFolder = "f/draft" }, "{}", null, "pilot");

        brief.ShouldContain("Give the design a short title");
        brief.ShouldContain("at most 40 characters, a name and not a sentence");
        brief.ShouldContain("gets a proper one at its next version");
        brief.ShouldContain("`design check` refuses a document with none or one over 40 characters");

        var old = Briefs.Designer(new DesignDetail { Row = new DesignRow { Id = "DSN-2", Title = string.Concat(Enumerable.Repeat("oldcut ", 12)).Trim() }, Folder = "f", DraftFolder = "f/draft" }, "{}", null, "pilot");
        old.ShouldContain("designer of DSN-2:");
        old.ShouldNotContain("oldcut");
    }

    [Fact]
    public void TheBriefDescribesTheDocumentAndAsksForAnObjectiveOnEveryScreen()
    {
        var detail = new DesignDetail { Row = new DesignRow { Id = "DSN-1", Title = "Disk alert" }, Folder = "f", DraftFolder = "f/draft" };

        var starter = Briefs.Designer(detail, "{}", null, "pilot");
        starter.ShouldContain("draft/DESIGN.md");
        starter.ShouldContain("draft/screens/<id>.json");
        starter.ShouldContain("`screen id=<screen id> size=<size>`");
        starter.ShouldContain("Every screen needs an objective");
        starter.ShouldContain("`design check` refuses a cell without one");
        starter.ShouldContain("This screen has no objective. Say in one or two lines what it shows.");
        starter.ShouldContain("the starter DESIGN.md");
        starter.ShouldContain("# Disk alert");
        starter.ShouldContain("Do it yourself, in this session: no subagents.");

        var current = Briefs.Designer(detail, "{}", "# Disk alert\n\n**Objective:** Warns.\n", "pilot");
        current.ShouldContain("The current version's DESIGN.md:");
        current.ShouldContain("**Objective:** Warns.");
        current.ShouldNotContain("the starter DESIGN.md");
    }

    [Theory]
    [InlineData("Bash", "pilot design check DSN-1")]
    [InlineData("Bash", "\"/opt/pilot dir/pilot\" --node-socket \"/tmp/n.sock\" design publish DSN-1 --why message")]
    [InlineData("Bash", "ls draft")]
    [InlineData("PowerShell", "Get-Content draft/NOTES.md")]
    // A quoted operator, a backslash inside single quotes in bash, and a Windows path in PowerShell stay allowed.
    [InlineData("Bash", "grep 'a > b' draft/NOTES.md")]
    [InlineData("Bash", "ls 'draft\\sub'")]
    [InlineData("PowerShell", "Get-Content draft\\NOTES.md")]
    [InlineData("PowerShell", "Select-String -Pattern \"a|b\" draft/NOTES.md")]
    public void ADesignersDesignVerbAndReadOnlyCommandAreLetThrough(string tool, string command)
    {
        var id = New("words");
        ListedAs("job00001", "de1-dsn1-designer");
        Decide(ShellHook("job00001-0000-4000-8000-000000000000", tool, _store.FolderOf(id), command)).ShouldBeNull();
    }

    [Fact]
    public async Task TheDesignerIsStartedWithASynchronousGuardHookThatSurvivesAResume()
    {
        var settings = HookCommands.DesignerSettings("/opt/pilot-node", "/tmp/n.sock");
        var designers = new Designers(_state, _store, _workers, new StartGate(_state, _time), new RoleModels(null), "DE1", _time) { Run = "pilot", GuardSettings = settings };
        _store.New(null, null, "words", []);
        designers.Ask("DSN-1", null);
        await designers.Settled;

        var start = _workers.Started.Last();
        start.Settings.ShouldBe(settings);
        var args = ClaudeWorkerLauncher.SessionArguments(start);
        args.ShouldContain("--settings");
        args[args.ToList().IndexOf("--settings") + 1].ShouldBe(settings);

        using var doc = JsonDocument.Parse(settings);
        var entry = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0];
        entry.GetProperty("matcher").GetString().ShouldBe(DesignerGuard.Matcher);
        var handler = entry.GetProperty("hooks")[0];
        handler.TryGetProperty("async", out _).ShouldBeFalse();
        handler.GetProperty("args")[0].GetString().ShouldBe("hook");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheHookCommandPrintsTheDenyForADesignersShellWriteAndStaysQuietForADesignVerb()
    {
        await using var node = await NodeHarness.StartAsync(TestContext.Current.CancellationToken);
        node.State.PutSession(new SessionRow
        {
            Id = "job00001", SessionId = "job00001-0000-4000-8000-000000000000", Name = "de1-dsn1-designer", Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Designer,
            ContextTokens = 1, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });
        var denied = new StringWriter();
        var write = ShellHook("job00001-0000-4000-8000-000000000000", "Bash", "/x", "echo x > /etc/y");
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(write)), denied, TimeSpan.FromSeconds(30))).ShouldBe(0);
        JsonDocument.Parse(denied.ToString()).RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString().ShouldBe("deny");

        var allowed = new StringWriter();
        var check = ShellHook("job00001-0000-4000-8000-000000000000", "Bash", "/x", "pilot design check DSN-1");
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(check)), allowed, TimeSpan.FromSeconds(30))).ShouldBe(0);
        allowed.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnexpectedFaultInOneAskDoesNotStopTheAsksAfterIt()
    {
        var calls = 0;
        var faulty = new Designers(_state, _store, new ThrowingLauncher(() => calls++), new StartGate(_state, _time), new RoleModels(null), "DE1", _time) { Run = "pilot" };
        _store.New(null, null, "words", []);
        faulty.Ask("DSN-1", null);
        faulty.Ask("DSN-1", "more");
        await faulty.Settled;
        calls.ShouldBe(2);
    }

    private sealed class ThrowingLauncher(Action called) : IWorkerLauncher
    {
        public Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
        {
            called();
            throw new InvalidOperationException("boom");
        }

        public Task StopAsync(string job, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void ASessionThatIsNotADesignerIsNeverHeldByTheGuard()
    {
        var id = New("words");
        var folder = _store.FolderOf(id);
        _state.PutSession(new SessionRow
        {
            Id = "ad000000", SessionId = "ad000000-0000-4000-8000-000000000000", Name = "someone", Kind = SessionKind.Interactive, Cwd = folder, Role = SessionRole.AdHoc,
            ContextTokens = 1, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });

        Decide(Hook("ad000000-0000-4000-8000-000000000000", "Write", folder, Path.Combine(folder, "BRIEF.md"))).ShouldBeNull();
    }

    [Fact]
    public async Task WithTheUsageLimitOpenTheStartIsHeldTheDesignSaysUntilWhenAndItStartsOnceTheLimitEnds()
    {
        Usage(95, TimeSpan.FromHours(2));

        var id = New("held brief");
        await _designers.Settled;

        _workers.Started.ShouldBeEmpty();
        var held = _store.Find(id).ShouldNotBeNull();
        held.DesignerState.ShouldBe(DesignerStates.Held);
        held.DesignerHeldUntil.ShouldBe(Start.AddHours(2));
        held.DesignerWhy.ShouldStartWith("held until ");
        held.DesignerWhy.ShouldContain("5-hour window");

        // A piece of the brief written meanwhile is kept for the designer, and no start is made.
        Brief(id, "still held");
        await _designers.Settled;
        _workers.Started.ShouldBeEmpty();
        _store.Find(id)!.DesignerPending.ShouldBe(["still held"]);

        // The window resets: the figures are low, and the sweep starts the designer.
        _time.Advance(TimeSpan.FromHours(3));
        Usage(5, TimeSpan.FromHours(5));
        _designers.Sweep();
        await _designers.Settled;

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("de1-dsn1-designer");
        var running = _store.Find(id).ShouldNotBeNull();
        running.DesignerState.ShouldBe(DesignerStates.Working);
        running.DesignerHeldUntil.ShouldBeNull();
        running.DesignerPending.ShouldBeEmpty();
        // The designer's own brief carries the whole BRIEF.md, the piece written while it was held included.
        _workers.Started[0].Brief.ShouldContain("still held");
    }

    [Fact]
    public void TheDesignerRoleHasItsOwnRangeSettingWithOpusAsItsBuiltInDefault()
    {
        RoleModels.Roles.ShouldContain(RoleModels.Designer);
        RoleModels.RangeKey(RoleModels.Designer).ShouldBe("designer-range");
        var models = new RoleModels(null);
        models.Model(RoleModels.Designer).ShouldBe("opus");
        models.Effort(RoleModels.Designer).ShouldBeNull();
        RoleModels.BuiltInWhy(RoleModels.Designer).ShouldNotBeNullOrEmpty();
        SessionRoles.Resolve("de1-dsn12-designer").Role.ShouldBe(SessionRole.Designer);
        SessionRoles.Resolve("de1-dsn12-other").Role.ShouldBe(SessionRole.Worker);
    }
}
