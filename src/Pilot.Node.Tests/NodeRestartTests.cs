using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary><c>pilot node restart</c>: the node starts its successor and stops, says what it left running, and refuses when it is not a pilot-node.</summary>
public sealed class NodeRestartTests
{
    private const string Node = "/opt/pilot/pilot-node";

    private static CommandRequest Restart(string who, params string[] why) =>
        new() { Verb = Verbs.RestartNode, Target = Verbs.NodeTarget, Args = why, Who = who };

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }

    private static (NodeSettingsCommands Commands, NodeState State, FakeLifetime Lifetime, List<string> Successors) Build(string nodePath = Node, bool starts = true)
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var lifetime = new FakeLifetime();
        var successors = new List<string>();
        var restarter = new NodeRestarter(lifetime, NullLogger<NodeRestarter>.Instance)
        {
            NodePath = nodePath,
            RestartedBySystemd = _ => false,
            StartSuccessor = path =>
            {
                successors.Add(path);
                return starts;
            },
        };
        // Only the restart is asked of it: the setup, the jobs and the role models are for the settings it also runs.
        var commands = new NodeSettingsCommands(new NodeOptions("/tmp/n.sock", Path.GetTempPath()), state, restarter, null!, TimeProvider.System, null!, null!);
        return (commands, state, lifetime, successors);
    }

    [Fact]
    public void ARestartStartsTheSuccessorStopsTheNodeAndLogsAHandledLineNamingTheCaller()
    {
        var (commands, state, lifetime, successors) = Build();

        var result = commands.RestartNode(Restart("se1-pil329-node", "the", "claude", "settings", "changed"));

        result.Ok.ShouldBeTrue();
        successors.ShouldBe([Node]);
        lifetime.Stopped.ShouldBeTrue();
        result.Way.ShouldBe("the node restarts for se1-pil329-node: the claude settings changed");
        var card = state.Cards().Single(c => c.Kind == "restart");
        (card.Bin, card.Title).ShouldBe((CardBin.Handled, result.Way));
    }

    [Fact]
    public void ARestartWithoutAReasonSaysOnlyWhoAskedAndHowManyJobsCarryOn()
    {
        var (commands, state, lifetime, _) = Build();
        foreach (var id in new[] { "j1", "j2" })
        {
            state.PutJob(new JobRow { Id = id, State = JobState.Running, Cwd = "/tmp", Command = "sleep 60", LogPath = "/tmp/j.log", Timeout = TimeSpan.Zero, StartedAt = DateTimeOffset.UtcNow });
        }
        state.PutJob(new JobRow { Id = "j3", State = JobState.Succeeded, Cwd = "/tmp", Command = "true", LogPath = "/tmp/j.log", Timeout = TimeSpan.Zero, StartedAt = DateTimeOffset.UtcNow });

        var result = commands.RestartNode(Restart("cli"));

        result.Way.ShouldBe("the node restarts for cli; 2 jobs carry on under the next node");
        lifetime.Stopped.ShouldBeTrue();
    }

    [Fact]
    public void ABinaryThatIsNotPilotNodeRefusesAndStopsNothing()
    {
        var (commands, state, lifetime, successors) = Build(nodePath: "/usr/share/dotnet/dotnet");

        var result = commands.RestartNode(Restart("cli"));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("the node did not restart: dotnet is not pilot-node, so it cannot start a successor of itself");
        successors.ShouldBeEmpty();
        lifetime.Stopped.ShouldBeFalse();
        state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public void ASuccessorThatDoesNotStartLeavesTheNodeRunning()
    {
        var (commands, state, lifetime, successors) = Build(starts: false);

        var result = commands.RestartNode(Restart("cli"));

        result.Error.ShouldBe($"the node did not restart: {Node} did not start, so this node runs on");
        successors.ShouldBe([Node]);
        lifetime.Stopped.ShouldBeFalse();
        state.Cards().ShouldBeEmpty();
    }
}
