using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// Where the LAN discovery transport is built, for <see cref="NodeTargets.DiscoverAsync"/> and the dashboards' LAN watch (#1104).
/// The real one listens on UDP on every interface, which asks Windows Firewall, so it goes through <see cref="NetworkBinds"/>
/// and a test replaces <see cref="Factory"/> with a fake.
/// </summary>
public static class LanTransports
{
    /// <summary>Builds a transport for the discovery port; a test swaps this for a fake.</summary>
    public static Func<int, ILanTransport> Factory { get; set; } = Real;

    /// <summary>The real every-interface UDP transport, refused in a test process by the <see cref="NetworkBinds"/> guard.</summary>
    public static ILanTransport Real(int port)
    {
        NetworkBinds.Check($"UdpLanTransport on UDP port {port}, every interface");
        return new UdpLanTransport(port);
    }

    /// <summary>A transport for <paramref name="port"/> from the current <see cref="Factory"/>.</summary>
    public static ILanTransport Create(int port) => Factory(port);
}
