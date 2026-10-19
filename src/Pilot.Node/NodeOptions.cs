using System.Diagnostics.CodeAnalysis;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>How a node was told to run: its command line, read by hand so the AOT binary carries no parser.</summary>
/// <param name="SocketPath">Where it listens (<c>--node-socket</c>, else <see cref="NodeSocket.EnvironmentVariable"/>, else the default).</param>
/// <param name="DataFolder">Where it keeps its state (<c>--data</c>, else <see cref="NodeSocket.DefaultDataFolder"/>).</param>
/// <param name="RetainedDeltas">How many deltas it holds for a resuming client; tests make it small.</param>
/// <param name="MachinePrefix">The machine's prefix in session names (<c>--machine</c>, else <c>PILOT_MACHINE</c>, else from its name).</param>
public sealed record NodeOptions(string SocketPath, string DataFolder, int RetainedDeltas = NodeState.DefaultRetainedDeltas, string? MachinePrefix = null)
{
    public const string Usage = "usage: pilot-node [--node-socket <path>] [--data <folder>] [--machine <XY1>]";

    /// <summary>The prefix in use: the one given, else <c>PILOT_MACHINE</c>, else the machine name's first two letters and 1.</summary>
    public string Machine => MachinePrefixes.Normalise(MachinePrefix ?? Environment.GetEnvironmentVariable("PILOT_MACHINE"))
        ?? MachinePrefixes.FromName(Environment.MachineName);

    public static bool TryParse(string[] args, [NotNullWhen(true)] out NodeOptions? options, [NotNullWhen(false)] out string? error)
    {
        string? socket = null;
        string? data = null;
        string? machine = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--node-socket" when i + 1 < args.Length:
                    socket = args[++i];
                    break;
                case "--data" when i + 1 < args.Length:
                    data = args[++i];
                    break;
                case "--machine" when i + 1 < args.Length:
                    machine = args[++i];
                    if (MachinePrefixes.Normalise(machine) is null)
                    {
                        options = null;
                        error = $"--machine takes two letters and a number, as in DU1, not {machine}";
                        return false;
                    }
                    break;
                case "-h" or "--help":
                    options = null;
                    error = Usage;
                    return false;
                default:
                    options = null;
                    error = $"Unknown or incomplete argument {args[i]}\n{Usage}";
                    return false;
            }
        }

        var socketPath = NodeSocket.Resolve(socket);
        if (!NodeSocket.TryValidate(socketPath, out error))
        {
            options = null;
            return false;
        }

        options = new NodeOptions(socketPath, Path.GetFullPath(data ?? NodeSocket.DefaultDataFolder), MachinePrefix: machine);
        return true;
    }
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
