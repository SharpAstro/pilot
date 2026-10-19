using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>What a session's hooks make of it: its state the moment it changes, what it waits on, and what stopped it.</summary>
public sealed class HookBookTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

    private static JsonElement Hook(string name, string extra = "") =>
        JsonDocument.Parse($$"""{"session_id":"{{AgentRows.SessionA}}","hook_event_name":"{{name}}","cwd":"/work"{{extra}}}""").RootElement;

    private static HookState? After(params JsonElement[] hooks)
    {
        var book = new HookBook();
        foreach (var hook in hooks)
        {
            book.Apply(hook, At);
        }
        return book.Of(AgentRows.SessionA);
    }

    [Theory]
    [InlineData("SessionStart", SessionState.Idle)]
    [InlineData("UserPromptSubmit", SessionState.Working)]
    [InlineData("PreToolUse", SessionState.Working)]
    [InlineData("Stop", SessionState.Idle)]
    [InlineData("SessionEnd", SessionState.Ended)]
    public void EachEventPutsTheSessionInItsState(string name, SessionState state) =>
        After(Hook(name)).ShouldNotBeNull().State.ShouldBe(state);

    [Fact]
    public void AStartThatArrivesLateDoesNotUndoTheTurnAfterIt()
    {
        // As a background session's async hooks reached a live node: the prompt first, then the start.
        After(Hook("UserPromptSubmit"), Hook("SessionStart"))!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void APermissionPromptIsWaitingWithItsWords()
    {
        var hook = After(Hook("Notification", ",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission to use Bash\"")).ShouldNotBeNull();

        hook.State.ShouldBe(SessionState.Waiting);
        hook.WaitingFor.ShouldBe("Claude needs your permission to use Bash");
        After(Hook("PermissionRequest", ",\"tool_name\":\"Bash\"")).ShouldNotBeNull().WaitingFor.ShouldBe("permission to use Bash");
    }

    [Fact]
    public void AnApiErrorOrAUsageLimitStopsItUntilItWorksAgain()
    {
        After(Hook("StopFailure", ",\"error\":\"rate_limit\"")).ShouldNotBeNull().ApiError.ShouldBe("rate_limit");
        After(Hook("Notification", ",\"notification_type\":\"quota_auto_resume_fired\"")).ShouldNotBeNull().ApiError.ShouldBe("usage_limit_reached");
        // The idle prompt after a stop keeps it; a new turn clears it.
        After(Hook("StopFailure", ",\"error\":\"overloaded\""), Hook("Notification", ",\"notification_type\":\"idle_prompt\""))!.ApiError.ShouldBe("overloaded");
        After(Hook("StopFailure", ",\"error\":\"overloaded\""), Hook("UserPromptSubmit"))!.ApiError.ShouldBeNull();
    }

    [Fact]
    public void ASubagentsHooksAndUnknownEventsChangeNothing()
    {
        After(Hook("UserPromptSubmit"), Hook("PreToolUse", ",\"agent_id\":\"a1\",\"agent_type\":\"Explore\""), Hook("Stop", ",\"agent_id\":\"a1\""))!
            .State.ShouldBe(SessionState.Working);
        After(Hook("Stop"), Hook("CwdChanged"), Hook("Notification", ",\"notification_type\":\"auth_success\""))!.State.ShouldBe(SessionState.Idle);
        new HookBook().Apply(JsonDocument.Parse("""{"hook_event_name":"Stop"}""").RootElement, At).ShouldBeNull();
    }
}

/// <summary>A row takes its running session's latest hook over the listing, and a stop from the transcript.</summary>
public sealed class HookRowTests : IDisposable
{
    private readonly TempProfile _profile = new();

    public void Dispose() => _profile.Dispose();

    private static ClaudeAgentRow Agent(int? pid, string? status = "idle") =>
        ClaudeAgentsJson.Parse(AgentRows.List(AgentRows.Background(AgentRows.SessionB, "x", "done", pid: pid, status: status)))!.Single();

    [Fact]
    public void ARunningSessionsHookHasTheLastWordOnItsState()
    {
        var hook = new HookState("Notification", DateTimeOffset.UtcNow, SessionState.Waiting, "permission to use Bash", null);

        var row = SessionRowBuilder.Build(Agent(4343), TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc, hook);

        row.State.ShouldBe(SessionState.Waiting);
        row.WaitingFor.ShouldBe("permission to use Bash");
        // A session whose process is gone is the listing's, whatever its last hook said.
        SessionRowBuilder.Build(Agent(null, status: null), TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc, hook).State.ShouldBe(SessionState.Ended);
    }

    [Fact]
    public void AUsageLimitInTheTranscriptStopsTheRowUntilARealReply()
    {
        var path = _profile.TranscriptPath(AgentRows.SessionB);
        File.WriteAllText(path,
            """{"type":"assistant","uuid":"u1","timestamp":"2026-10-18T11:00:00Z","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":5}}}""" + "\n" +
            """{"type":"assistant","uuid":"u2","timestamp":"2026-10-18T11:59:28Z","message":{"id":"m2","model":"<synthetic>","content":[{"type":"text","text":"You've hit your session limit"}]},"apiError":"usage_limit_reached","apiErrorParams":{"rate_limit_info":{"status":"rejected","resetsAt":1792324800,"rateLimitType":"five_hour"}}}""" + "\n");
        var transcripts = new SessionTranscripts(_profile.Profile, AgentRows.SessionB);

        var row = SessionRowBuilder.Build(Agent(4343), transcripts.Refresh(), null, null, SessionRoleInfo.AdHoc);

        row.ApiError.ShouldBe("usage_limit_reached");
        row.ApiErrorResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1792324800));
        row.Model.ShouldBe("claude-opus-5-5");

        File.AppendAllText(path, """{"type":"assistant","uuid":"u3","timestamp":"2026-10-18T13:01:00Z","message":{"id":"m3","model":"claude-opus-5-5","usage":{"input_tokens":12,"output_tokens":5}}}""" + "\n");
        SessionRowBuilder.Build(Agent(4343), transcripts.Refresh(), null, null, SessionRoleInfo.AdHoc).ApiError.ShouldBeNull();
    }
}

/// <summary>The installer: pilot's hooks in a settings file, idempotent, beside whatever else is there, and out again.</summary>
public sealed class HookInstallerTests
{
    private const string Node = @"C:\Program Files\pilot\pilot-node.exe";

    private const string Theirs = """
        {
          "model": "opus",
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "notify-send done" } ] } ],
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "guard.sh" } ] } ]
          }
        }
        """;

    private static JsonObject Parsed(string text) => JsonNode.Parse(text)!.AsObject();

    [Fact]
    public void InstallAddsAnAsyncExecFormHookPerEventAndKeepsEverythingElse()
    {
        var installed = Parsed(HookCommands.Install(Theirs, Node, socket: null));

        installed["model"]!.GetValue<string>().ShouldBe("opus");
        var hooks = installed["hooks"]!.AsObject();
        hooks.Select(h => h.Key).ShouldBe(HookCommands.Events, ignoreOrder: true);
        foreach (var name in HookCommands.Events)
        {
            var ours = hooks[name]!.AsArray().Select(e => e!["hooks"]![0]!).Single(h => h["command"]!.GetValue<string>() == Node);
            ours["args"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["hook"]);
            ours["async"]!.GetValue<bool>().ShouldBeTrue();
        }
        // Theirs stay, in their place.
        hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>().ShouldBe("notify-send done");
        hooks["PreToolUse"]![0]!["matcher"]!.GetValue<string>().ShouldBe("Bash");
        hooks["Stop"]!.AsArray().Last()!.AsObject().ContainsKey("matcher").ShouldBeFalse();
        hooks["PreToolUse"]!.AsArray().Last()!["matcher"]!.GetValue<string>().ShouldBe("*");
    }

    [Fact]
    public void ASecondInstallChangesNothingAndANewPathReplacesTheOld()
    {
        var once = HookCommands.Install(Theirs, Node, socket: null);

        HookCommands.Install(once, Node, socket: null).ShouldBe(once);

        var moved = Parsed(HookCommands.Install(once, @"D:\pilot\pilot-node.exe", socket: @"D:\s\node.sock"));
        var stop = moved["hooks"]!["Stop"]!.AsArray().Select(e => e!["hooks"]![0]!).ToList();
        stop.Count.ShouldBe(2);
        stop[1]["command"]!.GetValue<string>().ShouldBe(@"D:\pilot\pilot-node.exe");
        stop[1]["args"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["hook", "--node-socket", @"D:\s\node.sock"]);
    }

    [Fact]
    public void RemoveTakesOnlyPilotsOutAndDropsWhatItEmptied()
    {
        var removed = Parsed(HookCommands.Remove(HookCommands.Install(Theirs, Node, socket: null)));

        JsonNode.DeepEquals(removed, Parsed(Theirs)).ShouldBeTrue();
        HookCommands.Remove(Theirs).ShouldBe(Theirs);
        Parsed(HookCommands.Remove(HookCommands.Install("", Node, null))).ContainsKey("hooks").ShouldBeFalse();
        HookCommands.Remove("").ShouldBe("");
    }

    [Fact]
    public void TheCommandWritesTheFileAndSaysWhat()
    {
        var file = Path.Combine(Path.GetTempPath(), "p2h-" + Guid.NewGuid().ToString("N")[..8], "settings.json");
        var output = new StringWriter();
        try
        {
            HookCommands.Hooks(["install", "--settings", file], Node, output, TextWriter.Null).ShouldBe(0);
            HookCommands.Hooks(["install", "--settings", file], Node, output, TextWriter.Null).ShouldBe(0);
            HookCommands.Hooks(["remove", "--settings", file], Node, output, TextWriter.Null).ShouldBe(0);

            output.ToString().ShouldContain("installed for SessionStart");
            output.ToString().ShouldContain("already in");
            output.ToString().ShouldContain("removed");
            File.ReadAllText(file).Trim().ShouldBe("{}");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}

/// <summary>A hook posted to a live node changes its session's row on the stream.</summary>
public sealed class HookRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AHookPostedToTheNodeChangesTheSessionsStateOnTheStream()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, budget.Token);
        (await events.ReadAsync(budget.Token)).ShouldBeOfType<SnapshotEvent>();
        node.Agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-hook", "done", pid: 4343, status: "idle"));
        (await events.ReadAsync(budget.Token)).ShouldBeOfType<SessionAddedEvent>().Session.State.ShouldBe(SessionState.Idle);

        // As `pilot-node hook` sends it: the hook's stdin, untouched.
        var stdin = new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"session_id":"{{AgentRows.SessionB}}","hook_event_name":"Notification","notification_type":"permission_prompt","message":"Claude needs your permission to use Bash","cwd":"/work"}"""));
        (await HookCommands.HookAsync(["--node-socket", node.SocketPath], stdin)).ShouldBe(0);

        NodeEvent? next;
        do
        {
            next = await events.ReadAsync(budget.Token);
        }
        while (next is not SessionChangedEvent);
        var changed = ((SessionChangedEvent)next).Session;
        changed.State.ShouldBe(SessionState.Waiting);
        changed.WaitingFor.ShouldBe("Claude needs your permission to use Bash");
    }

    [Fact]
    public async Task TheHookCommandIsFailOpenWithNoNode()
    {
        var socket = Path.Combine(Path.GetTempPath(), "p2h-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        (await HookCommands.HookAsync(["--node-socket", socket], new MemoryStream("{}"u8.ToArray()))).ShouldBe(0);
        (await HookCommands.HookAsync([], new MemoryStream("not json"u8.ToArray()))).ShouldBe(0);
    }
}
