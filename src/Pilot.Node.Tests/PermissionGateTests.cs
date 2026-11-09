using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node answers a plane's worker's permission prompt itself when the call only acts inside that plane (entering it, reading or
/// editing a file under it), and keeps the session working; every other call is left to the person. And when the hook was missed, a
/// worker still seen waiting on such a call a look later is sent round once with a brief that avoids the prompt, then rings.
/// </summary>
public sealed class PermissionGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = TestTemp.NewPath();
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly PermissionGate _gate;

    public PermissionGateTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _gate = new PermissionGate(_state);
        Fly("TIA7", FlightState.Flying);
    }

    private string Plane(string code = "TIA7") => Path.Combine(_root, "tianwen", ".claude", "worktrees", code);

    private void Fly(string code, FlightState flightState) =>
        _state.PutFlight(new FlightRow
        {
            Code = code,
            Repo = "tianwen",
            RepoPath = Path.Combine(_root, "tianwen"),
            Issue = 7,
            Branch = "flight/" + code.ToLowerInvariant(),
            Worktree = Plane(code),
            Base = "origin/main",
            State = flightState,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

    private static string Hook(string cwd, string tool, JsonObject? input, string name = "PermissionRequest", string? agent = null)
    {
        var hook = new JsonObject
        {
            ["session_id"] = AgentRows.SessionA,
            ["hook_event_name"] = name,
            ["cwd"] = cwd,
            ["tool_name"] = tool,
        };
        if (input is not null)
        {
            // A JsonNode has one parent: a caller reusing its input across hooks needs a copy each time.
            hook["tool_input"] = input.DeepClone();
        }
        if (agent is not null)
        {
            hook["agent_id"] = agent;
        }
        return hook.ToJsonString();
    }

    private async Task<string?> Decide(string json) => await _gate.DecideAsync(JsonDocument.Parse(json).RootElement, Ct);

    private static JsonObject In(string key, string path) => new() { [key] = path };

    [Fact]
    public async Task EnterWorktreeIntoItsOwnPlaneIsAllowedInTheShapeClaudeCodeReads()
    {
        var answer = (await Decide(Hook(Plane(), "EnterWorktree", In("path", Plane())))).ShouldNotBeNull();

        var output = JsonDocument.Parse(answer).RootElement.GetProperty("hookSpecificOutput");
        output.GetProperty("hookEventName").GetString().ShouldBe("PermissionRequest");
        output.GetProperty("decision").GetProperty("behavior").GetString().ShouldBe("allow");
    }

    [Fact]
    public async Task EnterWorktreeIntoThePlaneFromAFolderInItAndWithATrailingSeparatorIsAllowed()
    {
        var sub = Path.Combine(Plane(), "src");

        (await Decide(Hook(sub, "EnterWorktree", In("path", Plane() + Path.DirectorySeparatorChar)))).ShouldNotBeNull();
        (await Decide(Hook(sub, "EnterWorktree", In("path", "..")))).ShouldNotBeNull();
        (await Decide(Hook(Plane(), "EnterWorktree", In("path", ".")))).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Read", "file_path", "src/a.cs")]
    [InlineData("Edit", "file_path", "src/a.cs")]
    [InlineData("Write", "file_path", "notes/new.md")]
    [InlineData("MultiEdit", "file_path", "src/a.cs")]
    [InlineData("NotebookEdit", "notebook_path", "n.ipynb")]
    [InlineData("Grep", "path", "src")]
    [InlineData("Glob", "path", "src")]
    public async Task AReadOrEditOfAPathUnderThePlaneIsAllowedAbsoluteOrRelativeToTheSessionsFolder(string tool, string key, string relative)
    {
        (await Decide(Hook(Plane(), tool, In(key, Path.Combine(Plane(), relative))))).ShouldNotBeNull();
        (await Decide(Hook(Plane(), tool, In(key, relative)))).ShouldNotBeNull();
        // Climbing out and back in is still inside.
        (await Decide(Hook(Plane(), tool, In(key, Path.Combine("src", "..", relative))))).ShouldNotBeNull();
    }

    [Fact]
    public async Task ASearchWithNoFolderSearchesTheSessionsOwnFolderInThePlane()
    {
        (await Decide(Hook(Plane(), "Grep", new JsonObject { ["pattern"] = "TODO" }))).ShouldNotBeNull();
        (await Decide(Hook(Path.Combine(Plane(), "src"), "Glob", new JsonObject { ["pattern"] = "**/*.cs" }))).ShouldNotBeNull();
    }

    [Fact]
    public async Task AGlobWhosePatternIsAPathOfItsOwnThatLeavesThePlaneIsLeftToThePerson()
    {
        (await Decide(Hook(Plane(), "Glob", new JsonObject { ["pattern"] = "../**/*.cs" }))).ShouldBeNull();
        (await Decide(Hook(Plane(), "Glob", new JsonObject { ["pattern"] = Path.Combine(_root, "**", "*.cs") }))).ShouldBeNull();
    }

    [Fact]
    public async Task EnterWorktreeElsewhereIsLeftToThePerson()
    {
        (await Decide(Hook(Plane(), "EnterWorktree", In("path", Plane("OTHER"))))).ShouldBeNull();
        (await Decide(Hook(Plane(), "EnterWorktree", In("path", Path.Combine(_root, "tianwen"))))).ShouldBeNull();
        // Under the plane is not the plane: it is a folder that is not a worktree to enter.
        (await Decide(Hook(Plane(), "EnterWorktree", In("path", Path.Combine(Plane(), "src"))))).ShouldBeNull();
        // A new worktree by name has no path to compare.
        (await Decide(Hook(Plane(), "EnterWorktree", new JsonObject { ["name"] = "scratch" }))).ShouldBeNull();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Edit")]
    [InlineData("Write")]
    public async Task APathOutsideThePlaneOrEscapingItByDotDotIsLeftToThePerson(string tool)
    {
        (await Decide(Hook(Plane(), tool, In("file_path", Path.Combine(_root, "tianwen", "README.md"))))).ShouldBeNull();
        (await Decide(Hook(Plane(), tool, In("file_path", Path.Combine("..", "..", "..", "README.md"))))).ShouldBeNull();
        (await Decide(Hook(Plane(), tool, In("file_path", Path.Combine(Plane(), "src", "..", "..", "TIA8", "x.cs"))))).ShouldBeNull();
        // A sibling whose name starts with the plane's is not under it.
        (await Decide(Hook(Plane(), tool, In("file_path", Plane() + "2" + Path.DirectorySeparatorChar + "x.cs")))).ShouldBeNull();
        // No path, no answer.
        (await Decide(Hook(Plane(), tool, new JsonObject()))).ShouldBeNull();
    }

    [Fact]
    public async Task APathWrittenInAnotherCaseIsInsideOnWindowsAndOutsideWhereCaseCounts()
    {
        var shouted = Path.Combine(Plane().ToUpperInvariant(), "a.cs");

        var answer = await Decide(Hook(Plane(), "Edit", In("file_path", shouted)));

        (answer is not null).ShouldBe(OperatingSystem.IsWindows());
    }

    [Theory]
    [InlineData("Bash", "command", "git push origin main")]
    [InlineData("PowerShell", "command", "Remove-Item -Recurse .")]
    [InlineData("WebFetch", "url", "https://example.com")]
    [InlineData("WebSearch", "query", "dotnet")]
    [InlineData("Skill", "skill", "run")]
    public async Task AnyOtherToolIsLeftToThePerson(string tool, string key, string value)
    {
        (await Decide(Hook(Plane(), tool, In(key, value)))).ShouldBeNull();
        // Even one that names a file in the plane.
        (await Decide(Hook(Plane(), tool, new JsonObject { [key] = value, ["file_path"] = Path.Combine(Plane(), "a.cs") }))).ShouldBeNull();
    }

    [Fact]
    public async Task ASubagentsCallAFolderThatIsNoPlaneALandedPlaneAnotherEventAndAnUnreadableHookAreLeftToThePerson()
    {
        var edit = In("file_path", Path.Combine(Plane(), "a.cs"));
        (await Decide(Hook(Plane(), "Edit", edit))).ShouldNotBeNull();

        (await Decide(Hook(Plane(), "Edit", edit, agent: "agent-1"))).ShouldBeNull();
        (await Decide(Hook(Path.Combine(_root, "tianwen"), "Edit", edit))).ShouldBeNull();
        (await Decide(Hook(Path.Combine(_root, "elsewhere"), "Edit", edit))).ShouldBeNull();
        (await Decide(Hook(Plane(), "Edit", edit, name: "PreToolUse"))).ShouldBeNull();
        (await Decide(Hook(Plane(), "Edit", null))).ShouldBeNull();
        (await Decide("""["not","an","object"]""")).ShouldBeNull();
        (await Decide("""{"hook_event_name":"PermissionRequest","tool_name":"Edit","tool_input":{"file_path":"a.cs"}}""")).ShouldBeNull();
        (await Decide("""{"hook_event_name":"PermissionRequest","tool_name":"Edit","cwd":"\u0000bad","tool_input":{"file_path":"a.cs"}}""")).ShouldBeNull();

        // A plane whose flight has landed is no plane the node flies.
        Fly("TIA7", FlightState.Landed);
        (await Decide(Hook(Plane(), "Edit", edit))).ShouldBeNull();
    }

    [Fact]
    public void TheSameQuestionOnTheLineASessionKeptOfItsCall()
    {
        var plane = Plane();

        PermissionGate.SafeInPlane(plane, "EnterWorktree", plane).ShouldBeTrue();
        PermissionGate.SafeInPlane(plane, "Edit", Path.Combine(plane, "a.cs")).ShouldBeTrue();
        PermissionGate.SafeInPlane(plane, "Read", Path.Combine(plane, "src", "..", "a.cs")).ShouldBeTrue();
        PermissionGate.SafeInPlane(plane, "EnterWorktree", Plane("OTHER")).ShouldBeFalse();
        PermissionGate.SafeInPlane(plane, "Edit", Path.Combine(plane, "..", "x.cs")).ShouldBeFalse();
        PermissionGate.SafeInPlane(plane, "Bash", plane).ShouldBeFalse();
        // A Glob or Grep keeps its pattern, which says nothing of its folder; no line, no path.
        PermissionGate.SafeInPlane(plane, "Grep", "TODO").ShouldBeFalse();
        PermissionGate.SafeInPlane(plane, "Edit", null).ShouldBeFalse();
    }

    // The fallback, over a leg's watch: the hook was missed, and the worker is seen waiting on the call.

    private readonly List<CommandRequest> _said = [];

    // A worker seen waiting long past one look (LegWatch.Interval, 30 s): the hook was missed, not on its way.
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(5);

    private LegWatch Watch(string? asking, string? waitingFor = "permission to use EnterWorktree", string? refuse = null, TimeSpan? waited = null)
    {
        var watch = new LegWatch(_state, (command, _) =>
        {
            _said.Add(command);
            return Task.FromResult(_state.Complete(command, refuse));
        }, _time) { Pilot = "pilot" };
        _state.PutLeg(new LegRow
        {
            Flight = "TIA7",
            Name = "parser",
            Order = 1,
            Goal = "Parse the ephemeris",
            DoneWhen = "The parser tests pass",
            State = LegState.Flying,
            Worker = "du1-tia7-parser",
            WorkerJob = "a1b2c3d4",
            StartedAt = Start,
        });
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4",
            SessionId = "a1b2c3d4-0000-4000-8000-000000000000",
            Name = "du1-tia7-parser",
            Kind = SessionKind.Background,
            Cwd = Plane(),
            Role = SessionRole.Worker,
            Flight = "TIA7",
            Leg = "parser",
            ContextTokens = 0,
            ContextWindow = 200_000,
            SpendWeighted = 0,
            State = SessionState.Waiting,
            WaitingFor = waitingFor,
            WaitingSince = _time.GetUtcNow() - (waited ?? LongWait),
            Asking = asking,
            LastResponseAt = Start,
            StartedAt = Start - (waited ?? LongWait),
        });
        return watch;
    }

    private CardRow[] Waiting() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == LegWatch.Waiting)];

    [Fact]
    public async Task AWorkerStillWaitingOnAnEnterWorktreeIntoItsPlaneIsSentRoundOnceWithTheCdBriefThenRings()
    {
        var watch = Watch($"EnterWorktree: {Plane()}");

        await watch.LookAsync(Ct);

        var around = _said.ShouldHaveSingleItem();
        (around.Verb, around.Target, around.Who).ShouldBe((Verbs.GoAround, "flight/TIA7/parser", "node"));
        var brief = around.Args[0];
        brief.ShouldContain("EnterWorktree");
        brief.ShouldContain($"cd {Plane()}");
        brief.ShouldContain("without the prompt");
        Waiting().ShouldBeEmpty();

        // Still waiting on it (the same call on the same leg): it rings with the words of a permission ask.
        await watch.LookAsync(Ct);

        _said.Count.ShouldBe(1);
        var card = Waiting().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldBe("TIA7/parser's worker wants to switch its folder to its own plane (EnterWorktree)");
    }

    [Fact]
    public async Task AWorkerWaitingOnAnEditUnderItsPlaneIsToldToUseThePlanePathAndALaterOneRings()
    {
        var file = Path.Combine(Plane(), "src", "a.cs");
        var watch = Watch($"Edit: {file}", "permission to use Edit");

        await watch.LookAsync(Ct);

        var brief = _said.ShouldHaveSingleItem().Args[0];
        brief.ShouldContain("(Edit)");
        // Why the prompt came, and the path that runs without one.
        brief.ShouldContain("only when it does not take the path as inside your folder");
        brief.ShouldContain($"the absolute path spelled from the plane, `{file}`");
        brief.ShouldNotContain('\u2014');
        Waiting().ShouldBeEmpty();

        await watch.LookAsync(Ct);
        var title = Waiting().ShouldHaveSingleItem().Title;
        title.ShouldStartWith("TIA7/parser's worker wants to edit ");
        title.ShouldEndWith("(Edit)");
    }

    [Fact]
    public async Task AnEditGivenByARelativePathIsToldThePathSpelledFromThePlane()
    {
        var watch = Watch($"Edit: {Path.Combine("src", "..", "src", "a.cs")}", "permission to use Edit");

        await watch.LookAsync(Ct);

        _said.ShouldHaveSingleItem().Args[0].ShouldContain($"`{Path.Combine(Plane(), "src", "a.cs")}`");
    }

    [Fact]
    public async Task AFreshWaitOnASafeCallIsNeitherSentRoundNorRungUntilItIsStillWaitingALookLater()
    {
        // The node may be answering it right now: a wait younger than one look is left to the next.
        var watch = Watch($"EnterWorktree: {Plane()}", waited: TimeSpan.FromSeconds(1));

        await watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Waiting().ShouldBeEmpty();

        // Still waiting on it a look later: the answer was missed, and the leg goes round.
        _time.Advance(watch.Interval);
        await watch.LookAsync(Ct);

        _said.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.GoAround);
        Waiting().ShouldBeEmpty();
    }

    [Fact]
    public async Task AFreshWaitOnACallThatNeedsAPersonRingsAtOnce()
    {
        var watch = Watch("Bash: git push origin main", "permission to use Bash", waited: TimeSpan.FromSeconds(1));

        await watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Waiting().ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public void APermissionRequestTheNodeAllowedLeavesTheSessionWorkingNotWaiting()
    {
        var hooks = new Pilot.Node.Sessions.HookBook();
        var edit = Hook(Plane(), "Edit", In("file_path", Path.Combine(Plane(), "a.cs")));

        hooks.Apply(JsonDocument.Parse(edit).RootElement, Start, allowed: true);

        var allowed = hooks.Of(AgentRows.SessionA).ShouldNotBeNull();
        allowed.State.ShouldBe(SessionState.Working);
        (allowed.WaitingFor, allowed.Asking).ShouldBe((null, null));

        // One the node left to the person waits on it, with the call it asks for.
        hooks.Apply(JsonDocument.Parse(Hook(Plane(), "Bash", In("command", "git push origin main"))).RootElement, Start);
        var asked = hooks.Of(AgentRows.SessionA).ShouldNotBeNull();
        asked.State.ShouldBe(SessionState.Waiting);
        asked.Asking.ShouldBe("Bash: git push origin main");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheNodeAnswersTheHookWithAllowAndTellsTheSessionWorkingNotWaiting()
    {
        // The hook's 2 s is for a live session; a loaded machine can take longer to start answering (StopGateTests).
        var patient = TimeSpan.FromSeconds(30);
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(_state.Flights().Single());
        var hooks = node.App.Services.GetRequiredService<Pilot.Node.Sessions.HookBook>();

        // As Claude Code runs it: the PermissionRequest on stdin, the decision on stdout.
        var allowed = new StringWriter();
        var edit = Hook(Plane(), "Edit", In("file_path", Path.Combine(Plane(), "a.cs")));
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(edit)), allowed, patient)).ShouldBe(0);

        JsonDocument.Parse(allowed.ToString()).RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString().ShouldBe("allow");
        hooks.Of(AgentRows.SessionA).ShouldNotBeNull().State.ShouldBe(SessionState.Working);

        // A call that reaches outside: no answer, and the session waits on the person.
        var left = new StringWriter();
        var push = Hook(Plane(), "Bash", In("command", "git push origin main"));
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(push)), left, patient)).ShouldBe(0);

        left.ToString().ShouldBeEmpty();
        hooks.Of(AgentRows.SessionA).ShouldNotBeNull().State.ShouldBe(SessionState.Waiting);
    }

    [Fact]
    public async Task OnlyThePlanesWorkerIsAllowedNotAReviewerOrAnotherFlightsSession()
    {
        var edit = In("file_path", Path.Combine(Plane(), "a.cs"));
        // No row yet: its first prompt can come before the tracker's first look, and a session in the plane is its worker.
        (await Decide(Hook(Plane(), "Edit", edit))).ShouldNotBeNull();

        Known(SessionRole.Worker, "TIA7");
        (await Decide(Hook(Plane(), "Edit", edit))).ShouldNotBeNull();

        Known(SessionRole.Reviewer, "TIA7");
        (await Decide(Hook(Plane(), "Edit", edit))).ShouldBeNull();
        (await Decide(Hook(Plane(), "Read", edit))).ShouldBeNull();

        Known(SessionRole.Planner, "TIA7");
        (await Decide(Hook(Plane(), "Edit", edit))).ShouldBeNull();

        Known(SessionRole.Worker, "TIA8");
        (await Decide(Hook(Plane(), "Edit", edit))).ShouldBeNull();
    }

    private void Known(SessionRole role, string flight) =>
        _state.PutSession(new SessionRow
        {
            Id = AgentRows.SessionA[..8],
            SessionId = AgentRows.SessionA,
            Name = "se1-tia7-x",
            Kind = SessionKind.Background,
            Cwd = Plane(),
            Role = role,
            Flight = flight,
            ContextTokens = 0,
            ContextWindow = 200_000,
            SpendWeighted = 0,
            State = SessionState.Working,
            StartedAt = Start,
        });

    [Theory]
    [InlineData("Bash: git push origin main", "permission to use Bash")]
    [InlineData("WebFetch: https://example.com", "permission to use WebFetch")]
    [InlineData("Edit: ../other/a.cs", "permission to use Edit")]
    [InlineData("EnterWorktree: /somewhere/else", "permission to use EnterWorktree")]
    [InlineData("Grep: TODO", "permission to use Grep")]
    [InlineData(null, "permission to use Bash")]
    public async Task AnyOtherPendingCallRingsAtOnceAndASafeOneWithAQuestionAlsoRings(string? asking, string waitingFor)
    {
        var watch = Watch(asking, waitingFor);

        await watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Waiting().ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public async Task ASafeCallTheGoAroundOfWhichIsRefusedRingsAtOnce()
    {
        var watch = Watch($"EnterWorktree: {Plane()}", refuse: "the cap is reached");

        await watch.LookAsync(Ct);

        _said.ShouldHaveSingleItem();
        Waiting().ShouldHaveSingleItem().Title.ShouldContain("EnterWorktree");
    }

    [Fact]
    public async Task AQuestionIsNeverSentRoundWhateverItsToolAsked()
    {
        var watch = Watch($"EnterWorktree: {Plane()}", "which plane do you want me to use?");

        await watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Waiting().ShouldHaveSingleItem();
    }
}
