using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node keeps when each session last sent a command, for the retell's "has it taken turns" test (#1137).</summary>
public sealed class NodeCommandsLastCommandTests
{
    [Fact]
    public async Task ACommandFromASessionSetsItsLastCommandAtButTheLegWatchDoesNot()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var commands = new NodeCommands(new ServiceCollection().AddSingleton<TimeProvider>(TimeProvider.System).BuildServiceProvider(), state);
        var ct = TestContext.Current.CancellationToken;

        commands.LastCommandAt("de1-x-w1").ShouldBeNull();
        await commands.RunAsync(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["hi"], Who = "de1-x-w1" }, ct);
        await commands.RunAsync(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["hi"], Who = LegWatch.Who }, ct);

        commands.LastCommandAt("de1-x-w1").ShouldNotBeNull();
        commands.LastCommandAt(LegWatch.Who).ShouldBeNull();
    }
}
