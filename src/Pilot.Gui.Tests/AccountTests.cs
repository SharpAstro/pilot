using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Whose node this is: the account its sessions run under, in the Sessions header and on the ? card.</summary>
public sealed class AccountTests
{
    private static readonly AccountRow Ada = new() { Email = "ada@example.org", Organization = "Example", Plan = "team" };

    [Fact]
    public void TheSessionsHeaderSaysWhoseSessionsAndUsageTheseAre()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var sessions = new SessionsView<RgbaImage>(vessel);
        var screen = new SessionsScreen([], "live", Live.At, Usage: "5h 42% (resets 14:00)", Account: Ada.Line());

        var described = CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored));

        described.ShouldContain("ada@example.org (Example, team) · usage 5h 42%");
    }

    [Fact]
    public void TheQuestionMarkCardSaysTheNodesAccount()
    {
        var node = new NodeInfo
        {
            WireVersion = NodeWire.Version, Version = "2.0.7", MachineName = "SE2", MachinePrefix = "SE2", ProcessId = 7, StartedAt = Live.At,
            DataFolder = "/d", SocketPath = "/s", Stream = "x", Account = Ada,
        };

        AboutFacts.For("pilot-gui", node, "/s").ShouldContain(("account", "ada@example.org (Example, team)"));
        AboutFacts.For("pilot-gui", node with { Account = null }, "/s").ShouldContain(("account", "no login on record"));
        AboutFacts.For("pilot-gui", null, "/s").ShouldContain(("account", "—"));
    }
}
