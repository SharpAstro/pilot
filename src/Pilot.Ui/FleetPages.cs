using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The node a row came from, as a merged page marks it: where it is reached, and its name in a word (<c>SE1</c>, its
/// machine prefix, else its machine's name). One node heard at two addresses is already one by then
/// (<see cref="FleetWatch.Feeds"/> drops the second by its stream).
/// </summary>
/// <param name="Key">
/// What the node selection keeps and a row's key is prefixed with: where it is reached, which a feed keeps for its life.
/// Not its stream, which a feed knows only once its snapshot is in, so a key taken a moment earlier would not hold.
/// </param>
/// <param name="Where">This window's socket, or <c>host:port</c> on the LAN: where a command for its rows goes.</param>
/// <param name="Name">What the Node column and the header's pills say.</param>
/// <param name="Here">Whether it is the node this window flies.</param>
internal sealed record NodeMark(string Key, string Where, string Name, bool Here)
{
    /// <summary>The mark for a node as its feed knows it: its prefix, else its machine, else the name it announced, else where it is.</summary>
    public static NodeMark For(string where, bool here, NodeSummary summary, string? announcedAs = null) =>
        new(where, where,
            summary.Node is { } node ? (node.MachinePrefix.Length > 0 ? node.MachinePrefix : node.MachineName) : announcedAs ?? where, here);
}

/// <summary>
/// Which nodes the pages show: all (none picked, the default), one, or several. One per window, shared by every page and
/// the cards panel, so it holds across a page switch; kept in memory, as a window's selection is.
/// </summary>
internal sealed class NodeFilter
{
    internal static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Lit = (new RGBAColor32(0x2f, 0x5a, 0x8c, 0xff), new RGBAColor32(0x3a, 0x6c, 0xa6, 0xff));

    // Swapped whole, never changed in place: the render thread picks, and the pages are merged off it (pilot-gui builds
    // them on a background task), so a reader always sees one whole set.
    private System.Collections.Immutable.ImmutableHashSet<string> _picked = System.Collections.Immutable.ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>The nodes there are to pick from, this window's first; set by each merge.</summary>
    public IReadOnlyList<NodeMark> Choices { get; set; } = [];

    /// <summary>The keys picked; empty is All nodes.</summary>
    public IReadOnlySet<string> Picked => Volatile.Read(ref _picked);

    /// <summary>Goes up on every pick, so whoever builds the pages knows to build them again now.</summary>
    public int Revision => Volatile.Read(ref _revision);

    private int _revision;

    /// <summary>Whether every node is shown: none picked, or none picked is among the nodes there are now.</summary>
    public bool All => Volatile.Read(ref _picked) is var picked && !Choices.Any(c => picked.Contains(c.Key));

    /// <summary>Whether rows of <paramref name="node"/> are shown; an unmarked row is this window's node's.</summary>
    public bool Shows(NodeMark? node) => All || (Volatile.Read(ref _picked) is var picked && (node is null ? Choices.FirstOrDefault(c => c.Here) is { } here && picked.Contains(here.Key) : picked.Contains(node.Key)));

    /// <summary>Picks exactly <paramref name="keys"/>: what a place the window goes back to had picked.</summary>
    public void Restore(IEnumerable<string> keys)
    {
        Volatile.Write(ref _picked, _picked.Clear().Union(keys));
    }

    /// <summary>Shows every node again.</summary>
    public void ShowAll()
    {
        Volatile.Write(ref _picked, _picked.Clear());
        Interlocked.Increment(ref _revision);
    }

    /// <summary>Adds <paramref name="key"/> to the nodes shown, or takes it out again: one pick is one node, two are several.</summary>
    public void Toggle(string key)
    {
        Volatile.Write(ref _picked, _picked.Contains(key) ? _picked.Remove(key) : _picked.Add(key));
        Interlocked.Increment(ref _revision);
    }

    /// <summary>
    /// A click on a node's pill: that node alone, as a pick of one; with Shift held, it is added to the pick or taken out of
    /// it, so several are shown (SE1, 2026-10-25: a plain click added to the pick, so narrowing to one took two clicks).
    /// </summary>
    public void Pick(string key, InputModifier modifiers)
    {
        if ((modifiers & InputModifier.Shift) != 0)
        {
            Toggle(key);
            return;
        }
        Volatile.Write(ref _picked, _picked.Clear().Add(key));
        Interlocked.Increment(ref _revision);
    }

    /// <summary>The header's pills: All nodes, then each node, the shown ones lit; nothing while there is one node.</summary>
    public IEnumerable<Layout.Node> Pills(FlightsPalette? night = null)
    {
        if (Choices.Count < 2)
        {
            yield break;
        }
        var all = All;
        var lit = night is null ? Lit : (night.RowFocusBg, night.ButtonHover);
        (RGBAColor32, RGBAColor32)? plain = night is null ? null : (night.Button, night.ButtonHover);
        yield return CardsView<RgbaImage>.HeaderPill("All nodes", "nodes:all", ShowAll, all ? lit : plain, night?.Bright);
        foreach (var choice in Choices)
        {
            var key = choice.Key;
            var down = Down.ContainsKey(key);
            yield return CardsView<RgbaImage>.HeaderPill(Label(choice, down ? Down[key] : null, down), $"nodes:{key}", modifiers => Pick(key, modifiers), !all && _picked.Contains(key) ? lit : plain, down ? night?.Dim ?? DimText : night?.Bright);
        }
    }

    private static readonly RGBAColor32 DimText = new(0x86, 0x89, 0x98, 0xff);

    /// <summary>The choices whose node was heard but cannot be reached now, by key, with when it went quiet (none when that is not known); set by each merge.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset?> Down { get; set; } = new Dictionary<string, DateTimeOffset?>();

    /// <summary>A pill for a node that cannot be reached: its name and when it was last heard, so it reads as last known.</summary>
    internal static string Label(NodeMark node, DateTimeOffset? downSince, bool down) =>
        !down ? Label(node) : $"{Label(node)} · {(downSince is { } since ? $"last heard {since.ToLocalTime():HH:mm}" : "last heard earlier")}";

    /// <summary>
    /// A node's pill: its name, and "(this)" for the node this window runs against, which the page's lines call "this node"
    /// (in maintenance, its usage). Without it nothing on screen said which pill that was (SE1, 2026-10-27).
    /// </summary>
    internal static string Label(NodeMark node) => node.Here ? $"{node.Name} (this)" : node.Name;

    /// <summary>The pills in a line, for a dump or a test: <c>[All nodes] SE1 LE1</c>, the lit ones bracketed.</summary>
    public string Line() =>
        Choices.Count < 2 ? "" : string.Join(" ", new[] { All ? "[All nodes]" : "All nodes" }.Concat(Choices.Select(c => !All && _picked.Contains(c.Key) ? $"[{PillName(c)}]" : PillName(c))));

    private string PillName(NodeMark c) => Down.TryGetValue(c.Key, out var since) ? Label(c, since, true).Replace(" (this)", "") : c.Name;
}

/// <summary>
/// What the pages show of what is done: <b>In play</b> (the default) keeps what still moves and lets the rest get out of the
/// way, <b>Everything</b> shows all. One per window, shared by the Sessions, Flights and Jobs pages and the cards panel, so it
/// holds across a page switch. Each view filters its own tree with it, so the screens stay whole for Home and the jumps
/// between pages, which need the rows that are done. The choice is kept in memory, as the node selection is, and also in a
/// small per-user file when one is given (<see cref="DefaultFile"/>): read when the window starts, written when it flips.
/// </summary>
internal sealed class PlayFilter
{
    private static readonly RGBAColor32 ClauseColor = new(0x8a, 0xb4, 0xf8, 0xff);

    /// <summary>How long a finished job stays in play, counted back from the screen's moment.</summary>
    public static readonly TimeSpan JobsFinished = TimeSpan.FromMinutes(10);

    /// <summary>The GUI's per-user file beside the node's data, <c>%LOCALAPPDATA%\pilot2\gui.json</c>.</summary>
    public static string DefaultFile => Path.Combine(NodeSocket.DefaultDataFolder, "gui.json");

    private readonly string? _file;

    /// <param name="file">
    /// The file the choice is kept in, or null to keep it in memory only. A missing, unreadable or malformed file is In play,
    /// the default; a file that cannot be written leaves the choice standing for this window.
    /// </param>
    public PlayFilter(string? file = null)
    {
        _file = file;
        InPlay = Load(file);
    }

    /// <summary>Whether only what is in play is shown; true until a click on Everything or on a page's hidden clause.</summary>
    public bool InPlay { get; private set; }

    /// <summary>Shows everything, as the pill and the hidden clause do.</summary>
    public void ShowEverything() => Set(false);

    /// <summary>Shows only what is in play again.</summary>
    public void ShowInPlay() => Set(true);

    private void Set(bool inPlay)
    {
        if (InPlay == inPlay)
        {
            return;
        }
        InPlay = inPlay;
        Save(_file, inPlay);
    }

    private static bool Load(string? file)
    {
        if (file is null)
        {
            return true;
        }
        try
        {
            using var stream = File.OpenRead(file);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            return document.RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root
                && root.TryGetProperty("inPlay", out var value) && value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? value.GetBoolean()
                : true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    private static void Save(string? file, bool inPlay)
    {
        if (file is null)
        {
            return;
        }
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } folder)
            {
                Directory.CreateDirectory(folder);
            }
            File.WriteAllText(file, $"{{ \"inPlay\": {(inPlay ? "true" : "false")} }}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A choice that cannot be kept still holds for this window.
        }
    }

    /// <summary>The header's pills, beside the node pills: In play, Everything, the shown one lit.</summary>
    public IEnumerable<Layout.Node> Pills(FlightsPalette? night = null)
    {
        var lit = night is null ? NodeFilter.Lit : (night.RowFocusBg, night.ButtonHover);
        (RGBAColor32, RGBAColor32)? plain = night is null ? null : (night.Button, night.ButtonHover);
        yield return CardsView<RgbaImage>.HeaderPill("In play", "play:in-play", ShowInPlay, InPlay ? lit : plain, night?.Bright);
        yield return CardsView<RgbaImage>.HeaderPill("Everything", "play:everything", ShowEverything, InPlay ? plain : lit, night?.Bright);
    }

    /// <summary>The pills in a line, for a dump or a test: <c>[In play] Everything</c>, the shown one bracketed.</summary>
    public string Line() => InPlay ? "[In play] Everything" : "In play [Everything]";

    /// <summary>
    /// A page's count in the header: <paramref name="counts"/>, then <paramref name="hidden"/> (<c>29 landed hidden</c>) as a
    /// click that switches to Everything, so what is out of view is named and one click away.
    /// </summary>
    public static IEnumerable<Layout.Node> Summary(PlayFilter? play, string counts, string? hidden, string action, RGBAColor32 body, RGBAColor32 hover, FlightsPalette? night = null)
    {
        // Bottom-aligned as the page's title is: its 20 and these 15 design units then share a baseline, where centred
        // boxes of two sizes do not.
        yield return Layout.Builder.Text(hidden is null ? counts : counts + " · ", 15f, body, TextAlign.Far, TextAlign.Far).WAuto();
        if (hidden is not null)
        {
            yield return Layout.Builder.Text(hidden, 15f, night?.Bright ?? ClauseColor, TextAlign.Far, TextAlign.Far)
                .WAuto()
                .BgHover(hover)
                .Follows(new HitResult.ButtonHit(action), _ => play?.ShowEverything());
        }
    }
}

/// <summary>
/// Every page's screen for the nodes a window shows: each node's rows merged, marked with their node when there is more
/// than one, and filtered by the window's <see cref="NodeFilter"/>.
/// </summary>
internal sealed record FleetPages(SessionsScreen Sessions, FlightsScreen Flights, JobsScreen Jobs, IReadOnlyList<JobRow> JobRows, CardsScreen Cards, ReposScreen? Repos = null, SummaryShelf? Summaries = null, IReadOnlyList<MachineLine>? Machines = null, PullRequestsScreen? PullRequests = null)
{
    /// <summary>Whether a feed's <see cref="NodeFeed.Reach"/> says its node cannot be reached: down since a first failed try or a drop. The one test the panel's held lines and the flights' stale marks share.</summary>
    private static bool Unreachable((bool Connected, DateTimeOffset? DownSince) reach) => !reach.Connected && reach.DownSince is not null;

    /// <summary>
    /// Merges <paramref name="feeds"/> (this window's node first, then the others, each node once). This window's node's
    /// rows stay unmarked while it is the only node to choose, as they were before there was a fleet; another node's rows are
    /// marked always. The header's source is this window's node's.
    /// </summary>
    public static FleetPages Merge(IReadOnlyList<(NodeMark Mark, NodeFeed Feed)> feeds, NodeFilter filter, DateTimeOffset now, bool reposWanted = false)
    {
        // A node that was heard and cannot be reached keeps its pill (dimmed, labelled last heard), as its row keeps its last
        // known state; one never heard stays out. Its rows keep their stale marks, and its commands stay refused.
        var offered = feeds.Select(f => (f.Mark, f.Feed, Reach: f.Feed.Reach)).Where(f => f.Mark.Here || f.Reach.Connected || (Unreachable(f.Reach) && f.Feed.Node is not null)).ToList();
        filter.Choices = [.. offered.Select(f => f.Mark)];
        filter.Down = offered.Where(f => !f.Mark.Here && Unreachable(f.Reach)).ToDictionary(f => f.Mark.Key, f => f.Reach.DownSince);
        var several = filter.Choices.Count > 1;
        var shown = feeds.Where(f => !several || filter.Shows(f.Mark)).ToList();
        // Another node's rows keep their mark while its stream is down: its last rows are still shown, and unmarked they
        // were taken for this node's, so a card's Its transcript asked this node for DE1's worker ("There is no session
        // b87bef5d", SE1 2026-10-25) and any command on such a row went to the wrong node.
        NodeMark? Mark(NodeMark mark) => several || !mark.Here ? mark : null;

        var here = feeds[0].Feed;
        var hereSessions = here.Screen(now);
        var sessions = SessionLine.Order(shown.SelectMany(f => f.Feed.Screen(now).Lines.Select(l => l with { Node = Mark(f.Mark) })));
        // Read once per node, so a node that answers between two reads is not both reachable and not: the panel's held lines and
        // the flights' stale marks below read this one answer, which is the state the Fleet page shows too.
        var reach = shown.Select(f => (f.Mark, f.Feed, f.Feed.Reach)).ToList();
        // A flight on a node the window cannot reach is its last known row, marked stale with that node's name: it may be long
        // out of date and its commands cannot go anywhere (pilot #325).
        var flights = FlightLine.Order(reach.SelectMany(f => f.Feed.Flights(now).Lines.Select(l => l with { Node = Mark(f.Mark), Stale = Unreachable(f.Reach) ? f.Mark.Name : null })));
        var jobs = JobLine.Order(shown.SelectMany(f => f.Feed.JobsPage(now).Lines.Select(l => l with { Node = Mark(f.Mark) })));
        // A node the window cannot reach gives the panel none of its cards, which may be long answered and cannot be commanded
        // now (DE1, 2026-10-26: its stale copy stood on the panel and in its counts for hours). One line each says how many it
        // held, and the node's own answer fills the panel again when it is back.
        var cards = CardsScreen.Merge(
            reach.Where(f => f.Reach.Connected).Select(f => (Mark(f.Mark), f.Feed.Cards())),
            feeds[0].Feed.Cards().Watchtower,
            reach.Where(f => Unreachable(f.Reach)).Select(f => HeldNode.Of(f.Mark, f.Reach.DownSince!.Value, f.Feed.Cards(), now)));
        var repos = RepoLine.Order(shown.SelectMany(f => f.Feed.Repos(now, reposWanted).Lines.Select(l => l.Marked(Mark(f.Mark)))));
        // A node's machine as it last sampled it; none from a node that sends no row (an older one), and none from one the window
        // cannot reach, whose last figures may be long out of date.
        var machines = reach.Where(f => !Unreachable(f.Reach)).Select(f => (f.Mark, Row: f.Feed.Machine)).Where(f => f.Row is not null).Select(f => new MachineLine(Mark(f.Mark), f.Row!)).ToList();
        var source = several ? $"{hereSessions.Source} · {filter.Choices.Count} nodes{(filter.All ? "" : $", showing {string.Join(", ", filter.Choices.Where(c => filter.Shows(c)).Select(c => c.Name))}")}" : hereSessions.Source;
        return new FleetPages(
            hereSessions with { Lines = sessions, Source = source },
            new FlightsScreen(flights, several ? source : here.Flights(now).Source, now),
            new JobsScreen(jobs, several ? source : here.JobsPage(now).Source, now),
            [.. shown.SelectMany(f => f.Feed.Jobs())],
            cards,
            new ReposScreen(repos, several ? source : hereSessions.Source, now),
            // Every node's summaries, shown or not: the card picks the node it asks about, from the window's node selection.
            new SummaryShelf(filter.Choices, feeds.SelectMany(f => f.Feed.Summaries().Select(s => (Key: (f.Mark.Key, s.Scope), Row: s)))
                .ToDictionary(x => x.Key, x => x.Row)),
            machines,
            // Every shown node reads the same GitHub: a PR two of them read is one row, and the page is as old as the oldest read.
            new PullRequestsScreen(PullRequestsRow.Merge(shown.Select(f => f.Feed.PullRequests())), several ? source : hereSessions.Source, now));
    }
}

/// <summary>
/// The client a row's command goes through: this window's own for its node's rows, and for another node's a client of
/// its own, with the grant saved for it. A node without a grant is read (a transcript, a log) but never commanded.
/// </summary>
internal sealed class FleetClients(NodeClient here, Func<string, bool> granted, Func<string, NodeClient>? remote = null, Func<string, string?>? tokenOf = null) : IDisposable
{
    // Swapped whole, never changed in place, as is _revoked: For runs on the render thread (a click, a transcript opened)
    // and on the pool at once, and nothing here makes either wait.
    private System.Collections.Immutable.ImmutableDictionary<string, (NodeClient Client, string? Token)> _remote = System.Collections.Immutable.ImmutableDictionary.Create<string, (NodeClient Client, string? Token)>(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, NodeClient>? _connect = remote;
    // Nodes that refused the grant as unauthorized, with the token that was refused.
    private System.Collections.Immutable.ImmutableDictionary<string, string?> _revoked = System.Collections.Immutable.ImmutableDictionary.Create<string, string?>(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?>? _tokenOf = tokenOf;

    // The tokens held, as the token file last read: For runs on the render thread (a transcript or a log opened, a click),
    // and it read the file on every call. Read again by Refresh, off the render thread (pilot-gui's page builder, every 5 s).
    private IReadOnlyDictionary<string, string> _held = ReadHeld();

    private string? TokenOf(string endpoint) => _tokenOf is { } given ? given(endpoint) : Volatile.Read(ref _held).GetValueOrDefault(endpoint);

    private static IReadOnlyDictionary<string, string> ReadHeld()
    {
        try
        {
            var held = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in Pilot.Commands.NodeTargets.Granted())
            {
                if (Pilot.Commands.NodeTargets.TokenFor(endpoint) is { } token)
                {
                    held[endpoint] = token;
                }
            }
            return held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Whether this user may command <paramref name="node"/>: its own node always, another by a grant held and not since
    /// refused as unauthorized (a revoked token), until a new grant is saved.
    /// </summary>
    public bool CanSteer(NodeMark? node)
    {
        if (node is null or { Here: true })
        {
            return true;
        }
        return granted(node.Where) && !Volatile.Read(ref _revoked).ContainsKey(node.Where);
    }

    /// <summary>Whether <paramref name="envelope"/> is a node refusing the grant; notes it so the node reads as watch-only.</summary>
    public bool NoteRefusal<T>(NodeMark? node, ResponseEnvelope<T> envelope)
    {
        if (node is not { Here: false } || envelope is not { Success: false, StatusCode: 401 or 403 })
        {
            return false;
        }
        var token = TokenOf(node.Where);
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _revoked, revoked => revoked.SetItem(node.Where, token));
        return true;
    }

    /// <summary>A new grant (a token other than the one refused) lifts a refusal; called with the fleet tick.</summary>
    public void Refresh()
    {
        if (_tokenOf is null)
        {
            Volatile.Write(ref _held, ReadHeld());
        }
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _revoked, revoked => revoked.RemoveRange(revoked.Where(r => TokenOf(r.Key) != r.Value).Select(r => r.Key)));
    }

    /// <summary>
    /// The client for <paramref name="node"/>'s rows (null or this window's: its own); null when it is to be commanded
    /// and this user holds no grant for it.
    /// </summary>
    public NodeClient? For(NodeMark? node, bool command = true)
    {
        if (node is null or { Here: true })
        {
            return here;
        }
        if (command && !CanSteer(node))
        {
            return null;
        }
        // A remote client fixes its token for life, so one built under another token (none before Connect, the old one
        // after Forget or over a refusal) is replaced by one with the token held now. Two callers racing to make one: the
        // swap that lands keeps its client, the other disposes its own and takes the kept one.
        var token = TokenOf(node.Where);
        while (true)
        {
            var remote = Volatile.Read(ref _remote);
            var had = remote.TryGetValue(node.Where, out var held);
            if (had && held.Token == token)
            {
                return held.Client;
            }
            var made = (Client: _connect is { } connect ? connect(node.Where) : NodeClient.Remote(node.Where, token), Token: token);
            if (Interlocked.CompareExchange(ref _remote, remote.SetItem(node.Where, made), remote) == remote)
            {
                if (had)
                {
                    held.Client.Dispose();
                }
                return made.Client;
            }
            made.Client.Dispose();
        }
    }

    /// <summary>What a command on a node without a grant says instead.</summary>
    public static string WatchOnly(NodeMark? node) =>
        $"{node?.Name} ({node?.Where}) is watched, not commanded: Connect to {node?.Name} to steer";

    public void Dispose()
    {
        foreach (var (client, _) in Interlocked.Exchange(ref _remote, _remote.Clear()).Values)
        {
            client.Dispose();
        }
    }
}
