using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>How a node was told to run: its command line as <see cref="NodeCommandLine"/> read it.</summary>
/// <param name="SocketPath">Where it listens (<c>--node-socket</c>, else <see cref="NodeSocket.EnvironmentVariable"/>, else the default).</param>
/// <param name="DataFolder">Where it keeps its state (<c>--data</c>, else <see cref="NodeSocket.DefaultDataFolder"/>).</param>
/// <param name="RetainedDeltas">How many deltas it holds for a resuming client; tests make it small.</param>
/// <param name="MachinePrefix">The machine's prefix in session names (<c>--machine</c>, else <c>PILOT_MACHINE</c>, else from its name).</param>
/// <param name="Watchtower">Whether it keeps a watchtower session (<c>--no-watchtower</c> turns it off; a test node keeps none).</param>
/// <param name="WatchtowerFolderGiven">Where the watchtower runs (<c>--watchtower-folder</c>, else <c>PILOT_WATCHTOWER_FOLDER</c>, else under the data folder).</param>
/// <param name="Lan">Whether it listens on the LAN too (<c>--lan</c>): seeing for anyone who reaches the port, commands for a grant.</param>
/// <param name="LanPort">The TCP port it listens on for the LAN (<c>--lan-port</c>).</param>
/// <param name="LanAnnounce">Whether it announces itself on the LAN (LAN.Lib's UDP beacon) when it listens there; a test node does not.</param>
/// <param name="UpdatePolicy">What an installed release does when a newer one is out (<c>--update-policy</c>); a test node does not look.</param>
/// <param name="ReleaseFeed">A folder of release zips to update from instead of GitHub (<c>--release-feed</c>, hidden): offline, or trying an update.</param>
/// <param name="RepoRootsGiven">Folders whose git clones the new-flight wizard offers (<c>--repo-root</c>), beside the flights' own repos' folders.</param>
public sealed record NodeOptions(
    string SocketPath, string DataFolder, int RetainedDeltas = NodeState.DefaultRetainedDeltas, string? MachinePrefix = null, bool Watchtower = true, string? WatchtowerFolderGiven = null,
    bool Lan = false, int LanPort = NodeWire.DefaultLanPort, bool LanAnnounce = true, int MaxJobs = 1, double JobMemoryGigabytes = 2,
    UpdatePolicy UpdatePolicy = UpdatePolicy.Off, string? ReleaseFeed = null, IReadOnlyList<string>? RepoRootsGiven = null)
{
    /// <summary>
    /// The folder the watchtower runs in. Claude Code starts no background session in a folder never trusted, so the
    /// default one needs <c>claude</c> run there once; the node rings with that when it is refused.
    /// </summary>
    public string WatchtowerFolder => Path.GetFullPath(WatchtowerFolderGiven
        ?? (Environment.GetEnvironmentVariable("PILOT_WATCHTOWER_FOLDER") is { Length: > 0 } folder ? folder : Path.Combine(DataFolder, "watchtower")));

    /// <summary>The repo roots given, none by default.</summary>
    public IReadOnlyList<string> RepoRoots => RepoRootsGiven ?? [];

    /// <summary>The prefix in use: the one given, else <c>PILOT_MACHINE</c>, else the machine name's first two letters and 1.</summary>
    public string Machine => MachinePrefixes.Normalise(MachinePrefix ?? Environment.GetEnvironmentVariable("PILOT_MACHINE"))
        ?? MachinePrefixes.FromName(Environment.MachineName);
}

/// <summary>A machine's prefix in session names: two letters and a number, in capitals (DU1, SL1).</summary>
public static class MachinePrefixes
{
    /// <summary><paramref name="value"/> in capitals when it is two letters and a number, else null.</summary>
    public static string? Normalise(string? value) =>
        value is { Length: >= 3 } v && char.IsAsciiLetter(v[0]) && char.IsAsciiLetter(v[1]) && v[2..].All(char.IsAsciiDigit)
            ? v.ToUpperInvariant()
            : null;

    /// <summary>The first two letters of the machine's name and 1 (SEB-SURFACE SE1), or PC1 for a name with fewer.</summary>
    public static string FromName(string machineName)
    {
        var letters = new string([.. machineName.Where(char.IsAsciiLetter).Take(2).Select(char.ToUpperInvariant)]);
        return (letters.Length == 2 ? letters : "PC") + "1";
    }
}
