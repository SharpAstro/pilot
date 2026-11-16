using System.Runtime.CompilerServices;
using Pilot.Commands;
using Pilot.Contracts;

namespace Pilot.Gui.Tests;

/// <summary>
/// No test process binds beyond loopback (#1104), as in Pilot.Node.Tests: installed at module load, the guard refuses the real
/// transport through <see cref="NetworkBinds"/>, and the dashboards' LAN watch runs on a transport with no socket.
/// A test that needs peers sets <see cref="LanTransports.Factory"/> or uses <c>LanWatch.Over</c>.
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
