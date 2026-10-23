using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A Stop in a flight's plane with files not committed is sent back to commit them, once per turn; a second stop is let
/// through and rings. Every other stop goes through in silence. Over a real git repo of the test's own.
/// </summary>
public sealed class StopGateTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly StopGate _gate;

    public StopGateTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        Git(Repo, "worktree", "add", "-q", "-b", "flight/tia7", Plane);
        // Claude Code's own worktree for a background session, beside the plane: no flight of the node's.
        Git(Repo, "worktree", "add", "-q", "-b", "worktree-other", Theirs);
        _gate = new StopGate(_state, new Planes(new GitCli()));
        Fly(Flight());
    }

    private string Repo => Path.Combine(_root, "tianwen");

    private string Plane => Path.Combine(Repo, ".claude", "worktrees", "TIA7");

    private string Theirs => Path.Combine(Repo, ".claude", "worktrees", "other");

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private FlightRow Flight() => new()
    {
        Code = "TIA7",
        Repo = "tianwen",
        RepoPath = Repo,
        Issue = 7,
        Branch = "flight/tia7",
        Worktree = Plane,
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private void Fly(FlightRow flight, NodeState? state = null)
    {
        state ??= _state;
        state.PutFlight(flight);
        state.PutLeg(new LegRow { Flight = flight.Code, Name = "parser", Order = 1, Goal = "parse", DoneWhen = "it parses", State = LegState.Flying });
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private static string StopJson(string cwd, bool active = false, string name = "Stop", string extra = "") =>
        $$"""{"session_id":"{{AgentRows.SessionA}}","hook_event_name":"{{name}}","cwd":{{JsonSerializer.Serialize(cwd)}},"stop_hook_active":{{(active ? "true" : "false")}}{{extra}}}""";

    private Task<string?> Decide(string cwd, bool active = false, string name = "Stop", string extra = "") =>
        _gate.DecideAsync(JsonDocument.Parse(StopJson(cwd, active, name, extra)).RootElement, Ct);

    private CardRow[] Rings() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == StopGate.Kind)];

    [Fact]
    public async Task AStopInADirtyPlaneIsBlockedWithAReasonNamingTheFile()
    {
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        var decision = JsonDocument.Parse((await Decide(Plane)).ShouldNotBeNull()).RootElement;

        decision.GetProperty("decision").GetString().ShouldBe("block");
        var reason = decision.GetProperty("reason").GetString().ShouldNotBeNull();
        reason.ShouldContain("1 file not committed");
        reason.ShouldContain("stray.txt");
        reason.ShouldContain("commit them");
        // Told, not rung: the session is still at it.
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task AFolderInsideThePlaneCountsAsThePlane()
    {
        Directory.CreateDirectory(Path.Combine(Plane, "src"));
        File.WriteAllText(Path.Combine(Plane, "src", "stray.cs"), "//\n");

        // git names a folder it does not track, not the files in it, as finish-leg does.
        (await Decide(Path.Combine(Plane, "src"))).ShouldNotBeNull().ShouldContain("?? src/");
    }

    [Fact]
    public async Task ASecondStopOnTheSameFilesIsLetThroughAndRings()
    {
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");
        (await Decide(Plane)).ShouldNotBeNull();

        (await Decide(Plane, active: true)).ShouldBeNull();

        var ring = Rings().ShouldHaveSingleItem();
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Subject.ShouldBe(Verbs.LegTarget("TIA7", "parser"));
        ring.Detail.ShouldNotBeNull().ShouldContain("stray.txt");
        var again = ring.Choices.ShouldHaveSingleItem();
        again.Verb.ShouldBe(Verbs.Say);
        again.Target.ShouldBe(Verbs.SessionTarget(AgentRows.SessionA));

        // Committed at last: the next stop goes through and the ring closes.
        Git(Plane, "add", ".");
        Git(Plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "stray");
        (await Decide(Plane)).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task ACleanPlaneLetsTheStopThrough()
    {
        (await Decide(Plane)).ShouldBeNull();
        (await Decide(Plane, active: true)).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASessionOutsideAPlaneIsNeverBlocked()
    {
        File.WriteAllText(Path.Combine(Repo, "stray.txt"), "main checkout\n");
        File.WriteAllText(Path.Combine(Theirs, "stray.txt"), "a worktree the node did not make\n");
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        (await Decide(Repo)).ShouldBeNull();
        (await Decide(Theirs)).ShouldBeNull();
        (await Decide(_root)).ShouldBeNull();
        // A folder whose name only starts like the plane's is not in it.
        (await Decide(Plane + "x")).ShouldBeNull();
        Rings().ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyAStopOfTheSessionsOwnInALiveFlightIsAnswered()
    {
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        (await Decide(Plane, name: "UserPromptSubmit")).ShouldBeNull();
        (await Decide(Plane, name: "SubagentStop")).ShouldBeNull();
        (await Decide(Plane, extra: ""","agent_id":"a1" """)).ShouldBeNull();
        (await _gate.DecideAsync(JsonDocument.Parse("""{"hook_event_name":"Stop"}""").RootElement, Ct)).ShouldBeNull();
        (await _gate.DecideAsync(JsonDocument.Parse("[]").RootElement, Ct)).ShouldBeNull();

        _state.PutFlight(Flight() with { State = FlightState.Landed, LandedAt = DateTimeOffset.UnixEpoch });
        (await Decide(Plane)).ShouldBeNull();
    }

    [Fact]
    public async Task TheHookPrintsTheNodesDecisionAndNothingOnceLetThrough()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Fly(Flight(), node.State);
        File.WriteAllText(Path.Combine(Plane, "stray.txt"), "left\n");

        // As Claude Code runs it: the Stop on stdin, the decision on stdout.
        var first = new StringWriter();
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(Encoding.UTF8.GetBytes(StopJson(Plane))), first)).ShouldBe(0);
        var decision = JsonDocument.Parse(first.ToString()).RootElement;
        decision.GetProperty("decision").GetString().ShouldBe("block");
        decision.GetProperty("reason").GetString().ShouldNotBeNull().ShouldContain("stray.txt");

        var second = new StringWriter();
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(Encoding.UTF8.GetBytes(StopJson(Plane, active: true))), second)).ShouldBe(0);
        second.ToString().ShouldBeEmpty();
        node.State.Cards(openOnly: true).ShouldContain(c => c.Kind == StopGate.Kind && c.Bin == CardBin.Ring);

        // Outside a plane the hook prints nothing, as for every other event.
        var outside = new StringWriter();
        (await HookCommands.HookAsync(node.SocketPath, new MemoryStream(Encoding.UTF8.GetBytes(StopJson(Repo))), outside)).ShouldBe(0);
        outside.ToString().ShouldBeEmpty();
    }
}
