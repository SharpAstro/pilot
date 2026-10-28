using System.Text;
using System.Text.Json;
using DIR.Lib;
using Pilot.Contracts;
using SharpAstro.Png;

namespace Pilot.Gui;

/// <summary>Where the rows come from, and the one frame the CPU renders of them.</summary>
internal static class SessionsCapture
{
    /// <summary>The UI face, shipped beside the binary (Fonts\DejaVuSans.ttf).</summary>
    public static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    /// <summary>The contract's sample snapshot, shipped beside the binary: what the GUI shows until it reads a node.</summary>
    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot.sample.json");

    /// <summary>The contract's sample repo status, shipped beside the binary: what a snapshot capture of the Repos page shows.</summary>
    public static string ReposSamplePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "repos.sample.json");

    /// <summary>Reads a repo status file: the array <c>GET /api/v1/repos/status</c> answers.</summary>
    public static RepoStatus[] LoadRepos(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), PilotJsonContext.Default.RepoStatusArray) ?? [];

    /// <summary>
    /// The Repos page's screen for a snapshot file: its flights, sessions, legs and cards joined with <paramref name="repos"/>,
    /// as of the moment the node made it.
    /// </summary>
    public static ReposScreen ReposScreenFor(SnapshotEvent snapshot, RepoStatus[] repos, string source) =>
        new(RepoLine.Build(repos, snapshot.Flights, snapshot.Sessions, snapshot.Cards, snapshot.Legs, snapshot.At), source, snapshot.At);

    /// <summary>Reads a snapshot file: a node's <see cref="SnapshotEvent"/> as the event stream sends it.</summary>
    public static SnapshotEvent LoadSnapshot(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), PilotJsonContext.Default.NodeEvent) switch
        {
            SnapshotEvent snapshot => snapshot,
            var other => throw new InvalidDataException($"{path} holds a {other?.GetType().Name ?? "null"}, not a snapshot."),
        };

    /// <summary>
    /// The screen for a snapshot file. A file is a moment, so "3m ago" counts from the moment the node made it, and
    /// the same file always reads the same.
    /// </summary>
    public static SessionsScreen ScreenFor(SnapshotEvent snapshot, string source) =>
        new(SessionLine.Build(snapshot.Sessions, snapshot.At), source, snapshot.At);

    /// <summary>Paints <paramref name="screen"/> once on the CPU and returns the view, its regions current.</summary>
    /// <param name="canSay">Whether to draw the say bar, as the window does on a live node.</param>
    /// <param name="flights">The Flights page's screen, drawn when <paramref name="page"/> is Flights.</param>
    /// <param name="jobs">The Background tasks page's screen, drawn when <paramref name="page"/> is Jobs.</param>
    /// <param name="openFlight">The code of the flight whose page the Flights page shows in place of its table.</param>
    public static SessionsView<RgbaImage> Render(RgbaImageRenderer renderer, SessionsScreen screen, float dpiScale = 1f, bool canSay = false,
        FlightsScreen? flights = null, PilotPage page = PilotPage.Sessions, CardsScreen? cards = null, JobsScreen? jobs = null, string? openFlight = null, ReposScreen? repos = null, bool inPlay = false, IReadOnlyList<MachineLine>? machines = null,
        string? hover = null, FlightsViewMode? flightsView = null, string? select = null, PullRequestsScreen? pullRequests = null)
    {
        var view = new SessionsView<RgbaImage>(renderer) { CanSay = canSay };
        var chrome = new PilotChrome<RgbaImage>(renderer, view) { FontPath = FontPath, DpiScale = dpiScale };
        // A capture is a moment of a file, whole: the window's In play default leaves the done out unless asked for.
        if (!inPlay)
        {
            chrome.Play.ShowEverything();
        }
        // Live, the window commands the node from the Flights page and tells the watchtower from its panel: so does a capture.
        chrome.Flights.CanCommand = canSay;
        chrome.Jobs.CanCommand = canSay;
        chrome.Cards.CanTell = canSay;
        chrome.Show(page);
        if (repos is not null)
        {
            chrome.Repos.ExpandAll(repos);
        }
        if (openFlight is not null)
        {
            chrome.Flights.OpenDetail(openFlight);
        }
        if (flightsView is { } mode)
        {
            chrome.Flights.Mode = mode;
        }
        chrome.PullRequests.OnRefresh = _ => Task.CompletedTask;
        var window = new RectF32(0, 0, renderer.Width, renderer.Height);
        chrome.Render(screen, flights, cards, jobs, null, repos, pullRequests, window, machines);
        if (hover is not null)
        {
            // The hover text the window shows once the pointer has rested on the region, painted last, over everything.
            chrome.Tooltips.Render(TooltipOf(chrome.PageWidget, hover), window);
        }
        if (select is not null && openFlight is not null && page == PilotPage.Flights)
        {
            // A drag as the window gets one: a press at the start of the first run, a move to the end of the last, a release.
            // The selection is only offered where a copy has somewhere to go, so a capture's goes nowhere.
            chrome.Flights.CopyText = _ => { };
            chrome.Render(screen, flights, cards, jobs, null, repos, pullRequests, window, machines);
            var (from, to) = select.Split('|') is [var a, var b] ? (a.Trim(), b.Trim()) : (select.Trim(), select.Trim());
            var runs = chrome.Flights.SelectableTextRegions.ToArray();
            var first = Array.FindIndex(runs, r => r.Text.StartsWith(from, StringComparison.Ordinal));
            var last = Array.FindLastIndex(runs, r => r.Text.StartsWith(to, StringComparison.Ordinal));
            if (first >= 0 && last >= first)
            {
                var (start, end) = (runs[first], runs[last]);
                chrome.HandleInput(new InputEvent.MouseDown(start.X + 0.5f, start.Y + start.Height / 2f));
                chrome.HandleInput(new InputEvent.MouseMove(end.X + end.Width - 0.5f, end.Y + end.Height / 2f, MouseButton.Left));
                chrome.HandleInput(new InputEvent.MouseUp(end.X + end.Width - 0.5f, end.Y + end.Height / 2f));
                chrome.Render(screen, flights, cards, jobs, null, repos, pullRequests, window, machines);
            }
        }
        return view;
    }

    /// <summary>
    /// The tooltip the page's region <paramref name="hover"/> declares: a button's id, or a list row as <c>list:index</c>. Throws
    /// when no region has that id or the region states no hover text, so a capture never silently shows no tooltip.
    /// </summary>
    internal static TooltipRequest TooltipOf(IPixelWidget page, string hover)
    {
        var regions = new List<ClickableRegion>();
        page.CollectPaintedRegions(regions);
        var at = regions.FindLastIndex(r => r is
            { Result: HitResult.ButtonHit { Action: var action } } && action == hover
            || r is { Result: HitResult.ListItemHit { ListId: var list, Index: var index } } && $"{list}:{index}" == hover);
        if (at < 0)
        {
            throw new InvalidOperationException($"--hover {hover}: the page has no such region");
        }
        var region = regions[at];
        return region.Tooltip is { } text
            ? new TooltipRequest(text, new RectF32(region.X, region.Y, region.Width, region.Height)) { Side = region.TooltipSide ?? Layout.DockSide.Bottom }
            : throw new InvalidOperationException($"--hover {hover}: that region states no hover text");
    }

    /// <summary>Renders one frame to <paramref name="pngPath"/> and its rows to <c>&lt;pngPath&gt;.rows.txt</c>; returns the dump's path.</summary>
    public static string Write(SessionsScreen screen, string pngPath, int width, int height, bool canSay = false,
        FlightsScreen? flights = null, PilotPage page = PilotPage.Sessions, CardsScreen? cards = null, JobsScreen? jobs = null, string? openFlight = null, ReposScreen? repos = null,
        IReadOnlyList<MachineLine>? machines = null, string? hover = null, FlightsViewMode? flightsView = null, string? select = null, PullRequestsScreen? pullRequests = null)
    {
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        Render(renderer, screen, canSay: canSay, flights: flights, page: page, cards: cards, jobs: jobs, openFlight: openFlight, repos: repos, machines: machines, hover: hover, flightsView: flightsView, select: select, pullRequests: pullRequests);
        if (Path.GetDirectoryName(Path.GetFullPath(pngPath)) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(pngPath, PngWriter.Encode(renderer.Surface.Pixels, width, height));
        var rowsPath = pngPath + ".rows.txt";
        var open = page == PilotPage.Flights ? flights?.Lines.FirstOrDefault(l => l.Code == openFlight) : null;
        File.WriteAllText(rowsPath, open is not null
            ? FlightDetail.From(open, flights!.AsOf).ToDump() + "\n"
            : page == PilotPage.Flights && flights is not null
            ? string.Concat(flights.Lines.Select(l => l.ToDumpLine() + "\n"))
            : page == PilotPage.Repos && repos is not null
                ? string.Concat(repos.Lines.Where(l => l.Status.Tracked).SelectMany(l => l.Worktrees.Select(w => w.ToDumpLine()).Prepend(l.ToDumpLine())).Select(t => t + "\n"))
            : page == PilotPage.PullRequests
                ? PullRequestsDump(pullRequests) + "\n"
            : page == PilotPage.Jobs && jobs is not null
                ? string.Concat(jobs.Lines.Select(l => l.ToDumpLine() + "\n"))
                : page == PilotPage.Home
                    ? new HomeScreen(flights ?? new FlightsScreen([], screen.Source, screen.AsOf), screen, jobs ?? new JobsScreen([], screen.Source, screen.AsOf), cards ?? CardsScreen.Empty, machines).ToDump() + "\n"
                    : RowsText(screen), new UTF8Encoding(false));
        return rowsPath;
    }

    /// <summary>
    /// The Pull requests page's rows: a line an org (its counts, and its read error or that it has no open PR), then a line a repo,
    /// then a line a PR with its label, flight, author, review and age.
    /// </summary>
    public static string PullRequestsDump(PullRequestsScreen? screen)
    {
        if (screen?.Row is not { } row)
        {
            return "the node has not read the pull requests yet";
        }
        var text = new StringBuilder();
        foreach (var org in row.OrgGroups())
        {
            text.Append(org.Org).Append("  ").Append(org.Counts).Append('\n');
            if (org.Error is { } error)
            {
                text.Append("  could not be read: ").Append(error).Append('\n');
            }
            if (org.Repos.Length == 0)
            {
                text.Append("  no open pull requests\n");
            }
            foreach (var group in org.Repos)
            {
                text.Append("  ").Append(group.Repo).Append("  ").Append(group.PullRequests.Length).Append('\n');
                foreach (var pr in group.PullRequests)
                {
                    text.Append("    #").Append(pr.Number).Append(' ').Append(pr.Title).Append(" | ").Append(pr.Label).Append(" | ").Append(pr.Flight ?? "no flight")
                        .Append(" | ").Append(pr.Author).Append(" | review ").Append(pr.Review ?? "none").Append(" | updated ").Append(PullRequestsRow.Age(pr.UpdatedAt, screen.AsOf)).Append('\n');
                }
            }
        }
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>One line per row, in drawing order, the fields as shown.</summary>
    public static string RowsText(SessionsScreen screen)
    {
        var text = new StringBuilder();
        foreach (var line in screen.Lines)
        {
            text.Append(line.ToDumpLine()).Append('\n');
        }

        return text.ToString();
    }
}
