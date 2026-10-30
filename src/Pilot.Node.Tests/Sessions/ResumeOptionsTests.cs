using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// A resume of a session Claude Code has no respawn flags for (a compaction's continuation) carries the options the node
/// started it with, so it wakes on its own model and not the person's default; one with flags on file is resumed plainly.
/// Run on an in-memory node, a fake delivery and a temp profile, never the live node.
/// </summary>
public sealed class ResumeOptionsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Recent => DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;
    private readonly StartOptionsBook _book = new StartOptionsBook();
    private readonly SessionMessenger _messenger;

    public ResumeOptionsTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, new StringWriter(), _book);
    }

    public void Dispose() => _profile.Dispose();

    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    public static TheoryData<string, WorkerStart, string> Roles => new()
    {
        { "watchtower", new WorkerStart("de1-watchtower", "/data/watchtower", null, "b", "sonnet", "low", RemoteControl: true, DisallowedTools: ["AskUserQuestion"]),
            "--remote-control -n de1-watchtower --permission-mode auto --strict-mcp-config --model sonnet --effort low --disallowedTools AskUserQuestion" },
        { "worker, attempt 1", new WorkerStart("de1-pil1-worker", "/r/pilot", "pil1", "b", "sonnet", "medium"),
            "-n de1-pil1-worker --permission-mode auto --strict-mcp-config --model sonnet --effort medium" },
        { "worker, ladder step", new WorkerStart("de1-pil1-worker", "/r/pilot", "pil1", "b", "opus", "high"),
            "-n de1-pil1-worker --permission-mode auto --strict-mcp-config --model opus --effort high" },
        { "planner", new WorkerStart("de1-pil1-planner", "/r/pilot", "pil1", "b", "opus", "high"),
            "-n de1-pil1-planner --permission-mode auto --strict-mcp-config --model opus --effort high" },
        { "reviewer, standard", new WorkerStart("de1-pil1-review", "/r/pilot", "pil1", "b", "sonnet", "medium"),
            "-n de1-pil1-review --permission-mode auto --strict-mcp-config --model sonnet --effort medium" },
        { "reviewer, deep", new WorkerStart("de1-pil1-review", "/r/pilot", "pil1", "b", "opus", "xhigh"),
            "-n de1-pil1-review --permission-mode auto --strict-mcp-config --model opus --effort xhigh" },
        { "validator", new WorkerStart("de1-pil1-validator", "/r/pilot", "pil1", "b", "sonnet", "low"),
            "-n de1-pil1-validator --permission-mode auto --strict-mcp-config --model sonnet --effort low" },
        { "fixer", new WorkerStart("de1-pr7-fix-1", "/r/pilot", "pr7", "b", "sonnet", "high"),
            "-n de1-pr7-fix-1 --permission-mode auto --strict-mcp-config --model sonnet --effort high" },
    };

    private async Task<ResumeRequest> ResumeAsync(string sessionId, string name)
    {
        _agents.Json = AgentRows.List(AgentRows.Background(sessionId, name, "done", cwd: Cwd, startedAt: Recent));
        await _tracker.PollAsync(Ct);
        var row = _state.Sessions().Single();
        var result = await _messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = ["go on"], Who = "tests" }, Ct);
        result.Ok.ShouldBeTrue(result.Error);
        return _delivery.Resumes.ShouldHaveSingleItem();
    }

    private void Started(WorkerStart start, string sessionId) =>
        _book.Record(new SessionStartOptions(start.Name, sessionId[..8], [.. ClaudeWorkerLauncher.SessionArguments(start)]));

    private void RespawnFlags(string sessionId, string json)
    {
        var folder = Path.Combine(_profile.Profile.Root, "jobs", sessionId[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "state.json"), json);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task ASessionWithNoRespawnFlagsIsResumedWithTheOptionsItWasStartedWith(string role, WorkerStart start, string flags)
    {
        role.ShouldNotBeEmpty();
        Started(start, AgentRows.SessionB);
        RespawnFlags(AgentRows.SessionB, """{"status":"idle"}""");

        var request = await ResumeAsync(AgentRows.SessionB, start.Name);

        ClaudeCliDelivery.ResumeArguments(request).ShouldBe(["--bg", "--resume", AgentRows.SessionB, .. flags.Split(' ')]);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task ASessionWithRespawnFlagsIsResumedPlainly(string role, WorkerStart start, string flags)
    {
        role.ShouldNotBeEmpty();
        flags.ShouldNotBeEmpty();
        Started(start, AgentRows.SessionB);
        RespawnFlags(AgentRows.SessionB, """{"respawnFlags":["--model","sonnet"]}""");

        var request = await ResumeAsync(AgentRows.SessionB, start.Name);

        ClaudeCliDelivery.ResumeArguments(request).ShouldBe(["--bg", "--resume", AgentRows.SessionB]);
    }

    [Fact]
    public async Task ACompactionsContinuationFindsTheOptionsOfTheSessionItContinues()
    {
        var start = new WorkerStart("de1-watchtower", "/data/watchtower", null, "b", "sonnet", "low", RemoteControl: true);
        Started(start, AgentRows.SessionA);
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "de1-watchtower", "done", cwd: Cwd, startedAt: Recent));
        await _tracker.PollAsync(Ct);
        _state.PutSession(_state.Sessions().Single() with { EarlierSessionIds = [AgentRows.SessionA] });

        var result = await _messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(AgentRows.SessionB[..8]), Args = ["go on"], Who = "tests" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        var request = _delivery.Resumes.ShouldHaveSingleItem();
        request.SessionId.ShouldBe(AgentRows.SessionB);
        ClaudeCliDelivery.ResumeArguments(request).ShouldBe(
            ["--bg", "--resume", AgentRows.SessionB, "--remote-control", "-n", "de1-watchtower", "--permission-mode", "auto", "--strict-mcp-config", "--model", "sonnet", "--effort", "low"]);
    }

    [Fact]
    public async Task ASessionTheNodeDidNotStartIsResumedPlainly()
    {
        var request = await ResumeAsync(AgentRows.SessionC, "someone-elses");

        ClaudeCliDelivery.ResumeArguments(request).ShouldBe(["--bg", "--resume", AgentRows.SessionC]);
    }

    [Fact]
    public void ARespawnFlagsReadIsNoneForAMissingFileBrokenJsonOrNoFlags()
    {
        _profile.Profile.HasRespawnFlags(AgentRows.SessionA).ShouldBeFalse();
        RespawnFlags(AgentRows.SessionA, "{ not json");
        _profile.Profile.HasRespawnFlags(AgentRows.SessionA).ShouldBeFalse();
        RespawnFlags(AgentRows.SessionA, """{"respawnFlags":[]}""");
        _profile.Profile.HasRespawnFlags(AgentRows.SessionA).ShouldBeFalse();
        RespawnFlags(AgentRows.SessionA, """{"respawnFlags":["--model","opus"]}""");
        _profile.Profile.HasRespawnFlags(AgentRows.SessionA).ShouldBeTrue();
    }

    [Fact]
    public void TheBookKeepsItsStartsInItsFileAcrossARestart()
    {
        var file = Path.Combine(_profile.Folder, "start-options.json");
        new StartOptionsBook(file).Record(new SessionStartOptions("de1-x", "a1b2c3d4", ["-n", "de1-x", "--model", "sonnet"]));

        var found = new StartOptionsBook(file).Find([AgentRows.SessionA], []).ShouldNotBeNull();

        found.Arguments.ShouldBe(["-n", "de1-x", "--model", "sonnet"]);
        new StartOptionsBook(file).Find([], ["de1-x"]).ShouldNotBeNull().JobId.ShouldBe("a1b2c3d4");
    }
}
