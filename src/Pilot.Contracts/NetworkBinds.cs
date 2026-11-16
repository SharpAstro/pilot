namespace Pilot.Contracts;

/// <summary>
/// The one seam every bind beyond loopback goes through (#1104): the LAN discovery transport (UDP on every interface), the node's
/// LAN port on every address, and the node's announcing discovery. A bind beyond loopback makes Windows Firewall ask, so a test
/// process must never make one: the test assemblies set <see cref="Guard"/> at module load, and it throws naming this seam.
/// </summary>
public static class NetworkBinds
{
    /// <summary>Set by the test assemblies; null in production. Called with what is about to bind, it throws to refuse.</summary>
    public static Action<string>? Guard { get; set; }

    /// <summary>Called by the code that is about to bind beyond loopback, with a short description of the bind.</summary>
    public static void Check(string what) => Guard?.Invoke(what);

    /// <summary>The message a test guard throws: names the seam and how to give a test a fake or loopback transport.</summary>
    public static string Refusal(string what) =>
        $"A test process may not bind beyond loopback ({what}). Every such bind goes through Pilot.Contracts.NetworkBinds: "
        + "give the test a fake transport (Pilot.Commands.LanTransports.Factory, set for the test and restored after) or, for a node, "
        + "NodeOptions with LanLoopbackOnly: true and LanAnnounce: false (NodeHarness does this).";
}
