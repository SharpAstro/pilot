using System.Net;
using System.Net.Sockets;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A button pressed again while the node has not answered its first press sends nothing and says so: a Retry move pressed twice put
/// the second press's refusal ("PIL437 is landed (moved to DE1)") on the page above the first press's ok (SE1, 2026-10-29).
/// </summary>
public sealed class ActionRunningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task ASecondPressWhileTheFirstRunsSendsNothingAndAPressAfterItIsSentAgain()
    {
        // A node that takes the request and never answers it, until the first press is given up.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var accepted = listener.AcceptTcpClientAsync(Ct).AsTask();
        using var client = NodeClient.Remote(endpoint, "token");
        var move = new FlightAction("Retry move", Verbs.MoveFlight, "flight/PIL437", []);
        using var first = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var pressed = DashboardCommands.ActAsync(client, move, "gui", first.Token);
        var held = await accepted;

        (await DashboardCommands.ActAsync(client, move, "gui", Ct)).ShouldBe("already running: Retry move");
        // Another flight's move is its own button.
        var other = DashboardCommands.ActAsync(client, move with { Target = "flight/PIL438" }, "gui", first.Token);
        other.IsCompleted.ShouldBeFalse();

        first.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(pressed);
        await Should.ThrowAsync<OperationCanceledException>(other);

        // Its first press over, the button is sent again: the node has gone, so this one fails, having tried. The node's end of the
        // first connection closes too, or on Linux the press went down that live connection and waited for an answer for good.
        held.Dispose();
        listener.Stop();
        (await DashboardCommands.ActAsync(client, move, "gui", Ct)).ShouldStartWith("failed: Retry move: ");
    }
}
