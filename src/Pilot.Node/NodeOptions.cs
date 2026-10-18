using System.Diagnostics.CodeAnalysis;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>How a node was told to run: its command line, read by hand so the AOT binary carries no parser.</summary>
/// <param name="SocketPath">Where it listens (<c>--node-socket</c>, else <see cref="NodeSocket.EnvironmentVariable"/>, else the default).</param>
/// <param name="DataFolder">Where it keeps its state (<c>--data</c>, else <see cref="NodeSocket.DefaultDataFolder"/>).</param>
/// <param name="RetainedDeltas">How many deltas it holds for a resuming client; tests make it small.</param>
public sealed record NodeOptions(string SocketPath, string DataFolder, int RetainedDeltas = NodeState.DefaultRetainedDeltas)
{
    public const string Usage = "usage: pilot-node [--node-socket <path>] [--data <folder>]";

    public static bool TryParse(string[] args, [NotNullWhen(true)] out NodeOptions? options, [NotNullWhen(false)] out string? error)
    {
        string? socket = null;
        string? data = null;
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

        options = new NodeOptions(socketPath, Path.GetFullPath(data ?? NodeSocket.DefaultDataFolder));
        return true;
    }
}
