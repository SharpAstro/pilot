using System.Runtime.CompilerServices;
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
        LanTransports.Factory = _ => new LanTransports.Silent();
    }
}
