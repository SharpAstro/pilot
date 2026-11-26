using System.Globalization;

namespace Pilot.Buddy;

/// <summary>
/// The command line of <c>pilot-buddy</c>: <c>[--node-socket &lt;path&gt;] [--node &lt;host:port&gt;] [--capture &lt;out.png&gt; [--hold 3s]] [--exit-after 5s]</c>;
/// <c>pilot-buddy mcp</c> is the watchtower's stdio server and is handled before this. Hand parsed: the buddy is a tiny binary and this is four options.
/// </summary>
public sealed record BuddyOptions
{
    /// <summary>The node's socket; null takes <c>PILOT_NODE_SOCKET</c>, then the per-user default (as the GUI does).</summary>
    public string? NodeSocket { get; init; }

    /// <summary>A node on the LAN, <c>host:port</c> (the GUI's <c>--node</c>, also taken as <c>--remote</c>); null is this machine's.</summary>
    public string? Node { get; init; }

    /// <summary>Open the window, draw the scene for <see cref="Hold"/>, write the picture of the screen under it here and exit (the GUI lane's proof).</summary>
    public string? Capture { get; init; }

    /// <summary>How long a capture keeps the window up before it looks.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Close by itself after this long, so a check needs no hands.</summary>
    public TimeSpan? ExitAfter { get; init; }

    /// <summary>Parses <paramref name="args"/>; null with the reason in <paramref name="error"/> on a mistake, and for <c>--help</c> (<paramref name="isHelp"/> is then true and the text is in <paramref name="error"/>).</summary>
    public static BuddyOptions? Parse(IReadOnlyList<string> args, out string? error, out bool isHelp)
    {
        isHelp = false;
        error = null;
        var options = new BuddyOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Next(string name)
            {
                if (i + 1 >= args.Count)
                {
                    error = $"{name} needs a value";
                    return string.Empty;
                }
                return args[++i];
            }

            switch (args[i])
            {
                case "--help" or "-h" or "-?":
                    isHelp = true;
                    error = Help;
                    return null;
                case "--node-socket":
                    options = options with { NodeSocket = Next("--node-socket") };
                    break;
                case "--node" or "--remote":
                    options = options with { Node = Next(args[i]) };
                    break;
                case "--capture":
                    options = options with { Capture = Next("--capture") };
                    break;
                case "--hold":
                    if (!TryParseDuration(Next("--hold"), out var hold) && error is null)
                    {
                        error = "--hold takes a duration such as 3s or 500ms";
                    }
                    options = options with { Hold = hold };
                    break;
                case "--exit-after":
                    if (!TryParseDuration(Next("--exit-after"), out var after) && error is null)
                    {
                        error = "--exit-after takes a duration such as 5s, 500ms or 2m";
                    }
                    options = options with { ExitAfter = after };
                    break;
                default:
                    error = $"unknown option {args[i]}";
                    break;
            }
            if (error is not null)
            {
                error += Environment.NewLine + "pilot-buddy --help lists the options";
                return null;
            }
        }
        return options;
    }

    /// <summary>The help text.</summary>
    public const string Help =
        "pilot-buddy: the desktop cat of pilot v2 (one per desktop)\n"
        + "  --node-socket <path>   the node's socket; else PILOT_NODE_SOCKET, else this user's default\n"
        + "  --node <host:port>     a node on the LAN (also --remote)\n"
        + "  --capture <out.png>    open the window, wait --hold (3s), write the screen under it to the PNG and exit\n"
        + "  --hold <time>          with --capture: how long the window is up before the capture\n"
        + "  --exit-after <time>    close by itself after this long (5s, 500ms, 2m)\n"
        + "  mcp                    the watchtower's stdio MCP server (buddy_say, buddy_point, buddy_state, buddy_sleep)\n";

    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        var (number, unit) = value.EndsWith("ms", StringComparison.Ordinal) ? (value[..^2], 1d)
            : value.EndsWith('s') ? (value[..^1], 1000d)
            : value.EndsWith('m') ? (value[..^1], 60_000d)
            : (value, 1000d);
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }
        duration = TimeSpan.FromMilliseconds(amount * unit);
        return true;
    }
}
