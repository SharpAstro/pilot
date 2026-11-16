using System.Runtime.CompilerServices;
using LAN.Lib;
using Pilot.Commands;
using Pilot.Contracts;

namespace Pilot.Node.Tests;

/// <summary>
/// No test process binds beyond loopback (#1104): a bind on every interface makes Windows Firewall ask. Installed at module load,
/// the guard refuses the real transport and the node's every-address listen through <see cref="NetworkBinds"/>, and discovery
/// runs on a transport that hears nothing and opens no socket. A test that needs peers sets <see cref="LanTransports.Factory"/>.
/// </summary>
internal static class LanBindGuard
{
    [ModuleInitializer]
    internal static void Install()
    {
        NetworkBinds.Guard = what => throw new InvalidOperationException(NetworkBinds.Refusal(what));
        LanTransports.Factory = _ => new SilentLanTransport();
    }
}

/// <summary>A discovery transport with no socket: hears nothing, and what it is asked to broadcast goes nowhere.</summary>
internal sealed class SilentLanTransport : ILanTransport
{
    public Task BroadcastAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public event Action<DiscoveryDatagram>? DatagramReceived
    {
        add { }
        remove { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
