using System.CommandLine;
using System.Globalization;

namespace Pilot.Gui;

/// <summary>
/// The command line, on System.CommandLine: <c>pilot-gui [--node-socket &lt;path&gt; | --snapshot &lt;file.json&gt;] [--capture &lt;out.png&gt; [--size 1280x800]]
/// [--exit-after 5s]</c>, or <c>pilot-gui [--node-socket &lt;path&gt;] --say &lt;session&gt; &lt;text…&gt;</c>.
/// </summary>
internal sealed record GuiOptions
{
    /// <summary>A snapshot file to show instead of the live node; null shows the node.</summary>
    public string? Snapshot { get; init; }

    /// <summary>The node's socket; null takes <c>PILOT_NODE_SOCKET</c>, then the per-user default.</summary>
    public string? NodeSocket { get; init; }

    /// <summary>A node on the LAN, <c>host:port</c>; null takes <c>PILOT_NODE</c>, then this machine's on <see cref="NodeSocket"/>.</summary>
    public string? Node { get; init; }

    /// <summary>The session <see cref="SayText"/> goes to: a row id, a session id or its start, or a name.</summary>
    public string? SayTo { get; init; }

    /// <summary>With <see cref="SayTo"/>: say this to it through the node, print the result and exit, with no window.</summary>
    public string? SayText { get; init; }

    /// <summary>Render one frame on the CPU to this PNG (and a sibling <c>.rows.txt</c>), then exit without a window.</summary>
    public string? Capture { get; init; }

    /// <summary>The page a capture shows: home, sessions, flights, repos, prs, jobs, or chat.</summary>
    public PilotPage Page { get; init; } = PilotPage.Home;

    /// <summary>With <c>--page flights</c>: the flight whose page the capture opens, by code, as Enter on its row does in the window.</summary>
    public string? Flight { get; init; }

    /// <summary>
    /// With <c>--capture</c>: the region whose hover text the frame shows as due, as the window does after the pointer rests on
    /// it: a button's id (<c>home:flight:PIL258</c>) or a list row (<c>flights:3</c>, the list's id and the row's index).
    /// </summary>
    public string? Hover { get; init; }

    /// <summary>With <c>--page flights</c>: the view the capture draws, canvas, grid or list.</summary>
    public FlightsViewMode? View { get; init; }

    /// <summary>With <c>--flight</c>: drag over the page's text from the run that starts with the words before the <c>|</c> to the run that starts with those after it, so the capture shows the selection.</summary>
    public string? Select { get; init; }

    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 800;

    /// <summary>Close the window by itself after this long, so a check needs no hands.</summary>
    public TimeSpan? ExitAfter { get; init; }

    /// <summary>
    /// Parses <paramref name="args"/> on System.CommandLine, as tianwen's CLI does; on a mistake returns null and says
    /// what in <paramref name="error"/>, and for <c>--help</c> returns null with the help in <paramref name="error"/>.
    /// </summary>
    public static GuiOptions? Parse(IReadOnlyList<string> args, out string? error) => Parse(args, out error, out _);

    /// <summary>As <see cref="Parse(IReadOnlyList{string}, out string?)"/>; <paramref name="isHelp"/> tells the help text in <paramref name="error"/> from a mistake.</summary>
    public static GuiOptions? Parse(IReadOnlyList<string> args, out string? error, out bool isHelp)
    {
        isHelp = false;
        var snapshot = new Option<string?>("--snapshot") { Description = "A snapshot file to show instead of the live node" };
        var socket = new Option<string?>("--node-socket") { Description = "The node's socket; else PILOT_NODE_SOCKET, else this user's default" };
        var node = new Option<string?>("--node") { Description = "A node on the LAN: host:port, or its name (SE1) or host as pilot nodes lists them (pilot connect gets its grant); else PILOT_NODE" };
        var capture = new Option<string?>("--capture") { Description = "Render one frame on the CPU to this PNG (and a .rows.txt beside it), then exit" };
        var size = new Option<string?>("--size") { Description = "The capture's size, <W>x<H>; 1280x800 by default" };
        var page = new Option<string?>("--page") { Description = "The page a capture shows: home, sessions, flights, repos, prs, jobs or chat" };
        page.AcceptOnlyFromAmong("home", "sessions", "flights", "repos", "prs", "jobs", "chat");
        var flight = new Option<string?>("--flight") { Description = "With --page flights: open this flight's page (its legs, checklists and artifacts) in the capture, by code" };
        var hover = new Option<string?>("--hover") { Description = "With --capture: show this region's hover text as due; a button's id (home:flight:PIL258) or a list row (flights:3)" };
        var view = new Option<string?>("--view") { Description = "With --page flights: the view the capture draws" };
        view.AcceptOnlyFromAmong("canvas", "grid", "list");
        var select = new Option<string?>("--select") { Description = "With --flight: drag over the page's text in the capture, from the run starting with the words before the | to the run starting with those after it" };
        var exitAfter = new Option<string?>("--exit-after") { Description = "Close the window by itself after this long: 5s, 500ms, 2m" };
        var say = new Option<string[]>("--say")
        {
            Description = "Say <text…> to <session> through the node, print what became of it and exit",
            AllowMultipleArgumentsPerToken = true,
            Arity = new ArgumentArity(2, 100_000),
        };

        var root = new RootCommand("pilot v2's GUI: every Claude session on the machine, live from the node")
        {
            Options = { snapshot, socket, node, capture, size, page, flight, hover, view, select, exitAfter, say },
        };
        var parsed = root.Parse([.. args]);

        if (parsed.Action is System.CommandLine.Help.HelpAction)
        {
            var help = new StringWriter();
            parsed.Invoke(new InvocationConfiguration { Output = help });
            error = help.ToString();
            isHelp = true;
            return null;
        }
        if (parsed.Errors.Count > 0)
        {
            error = string.Join(Environment.NewLine, parsed.Errors.Select(e => e.Message)) + Environment.NewLine + "pilot-gui --help lists the options";
            return null;
        }

        var options = new GuiOptions
        {
            Snapshot = parsed.GetValue(snapshot),
            NodeSocket = parsed.GetValue(socket),
            Node = parsed.GetValue(node),
            Capture = parsed.GetValue(capture),
            Flight = parsed.GetValue(flight),
            Hover = parsed.GetValue(hover),
            View = parsed.GetValue(view) switch { "canvas" => FlightsViewMode.Canvas, "grid" => FlightsViewMode.Grid, "list" => FlightsViewMode.List, _ => null },
            Select = parsed.GetValue(select),
            Page = parsed.GetValue(page) switch { "flights" => PilotPage.Flights, "repos" => PilotPage.Repos, "prs" => PilotPage.PullRequests, "jobs" => PilotPage.Jobs, "sessions" => PilotPage.Sessions, "chat" => PilotPage.Chat, _ => PilotPage.Home },
        };
        if (parsed.GetValue(size) is { } sizeText)
        {
            if (!TryParseSize(sizeText, out var width, out var height))
            {
                error = $"--size takes <W>x<H>, as in 1280x800, not '{sizeText}'.";
                return null;
            }
            options = options with { Width = width, Height = height };
        }
        if (parsed.GetValue(exitAfter) is { } afterText)
        {
            if (!TryParseDuration(afterText, out var after))
            {
                error = $"--exit-after takes a duration such as 5s, 500ms or 2m, not '{afterText}'.";
                return null;
            }
            options = options with { ExitAfter = after };
        }
        if (parsed.GetValue(say) is { Length: >= 2 } words)
        {
            // Everything after the session is the text, so it needs no quoting.
            options = options with { SayTo = words[0], SayText = string.Join(' ', words.Skip(1)) };
        }
        if (options.Snapshot is not null && (options.NodeSocket is not null || options.Node is not null || options.SayTo is not null))
        {
            error = "--snapshot shows a file, so it takes no node: drop --node-socket, --node and --say.";
            return null;
        }

        error = null;
        return options;
    }

    private static bool TryParseSize(string value, out int width, out int height)
    {
        width = height = 0;
        var parts = value.Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width is > 0 and <= 16384 && height is > 0 and <= 16384;
    }

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
