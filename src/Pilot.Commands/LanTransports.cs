using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// Where the LAN discovery transport is built, for <see cref="NodeTargets.DiscoverAsync"/> and the dashboards' LAN watch (#1104).
/// The real one listens on UDP on every interface, which asks Windows Firewall, so it goes through <see cref="NetworkBinds"/>
/// and a test replaces <see cref="Factory"/> with a fake. A test that starts a pilot program as a child process cannot reach
/// <see cref="Factory"/> in it, so it sets <see cref="OffVariable"/> in the child's environment instead.
/// </summary>
public static class LanTransports
{
    /// <summary>
    /// Set to <c>1</c> in a process's environment, its discovery runs on <see cref="Silent"/>: no socket, nothing heard. For a
    /// test that starts pilot, pilot-node or pilot-gui as a child, where the test assembly's guard and fake do not reach.
    /// </summary>
    public const string OffVariable = "PILOT_LAN_OFF";

    /// <summary>Builds a transport for the discovery port; a test swaps this for a fake.</summary>
    public static Func<int, ILanTransport> Factory { get; set; } = Default;

    /// <summary>The real every-interface UDP transport, refused in a test process by the <see cref="NetworkBinds"/> guard.</summary>
    public static ILanTransport Real(int port)
    {
        NetworkBinds.Check($"UdpLanTransport on UDP port {port}, every interface");
        return new UdpLanTransport(port);
    }

    /// <summary><see cref="Real"/>, or <see cref="Silent"/> when <see cref="OffVariable"/> is <c>1</c> in this process's environment.</summary>
    public static ILanTransport Default(int port) => For(port, Environment.GetEnvironmentVariable(OffVariable));

    /// <summary><see cref="Silent"/> when <paramref name="off"/> (a value of <see cref="OffVariable"/>) turns the transport off, else <see cref="Real"/>.</summary>
    public static ILanTransport For(int port, string? off) => IsOff(off) ? new Silent() : Real(port);

    /// <summary>Whether a value of <see cref="OffVariable"/> turns the real transport off: <c>1</c> or <c>true</c>.</summary>
    public static bool IsOff(string? value) =>
        value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>A transport for <paramref name="port"/> from the current <see cref="Factory"/>.</summary>
    public static ILanTransport Create(int port) => Factory(port);

    /// <summary>A discovery transport with no socket: hears nothing, and what it is asked to broadcast goes nowhere.</summary>
    public sealed class Silent : ILanTransport
    {
        public Task BroadcastAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event Action<DiscoveryDatagram>? DatagramReceived
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
