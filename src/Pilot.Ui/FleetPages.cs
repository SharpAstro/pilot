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
    private static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Lit = (new RGBAColor32(0x2f, 0x5a, 0x8c, 0xff), new RGBAColor32(0x3a, 0x6c, 0xa6, 0xff));

    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);

    /// <summary>The nodes there are to pick from, this window's first; set by each merge.</summary>
    public IReadOnlyList<NodeMark> Choices { get; set; } = [];

    /// <summary>The keys picked; empty is All nodes.</summary>
    public IReadOnlySet<string> Picked => _picked;

    /// <summary>Whether every node is shown: none picked, or none picked is among the nodes there are now.</summary>
    public bool All => !Choices.Any(c => _picked.Contains(c.Key));

    /// <summary>Whether rows of <paramref name="node"/> are shown; an unmarked row is this window's node's.</summary>
    public bool Shows(NodeMark? node) => All || (node is null ? Choices.FirstOrDefault(c => c.Here) is { } here && _picked.Contains(here.Key) : _picked.Contains(node.Key));

    /// <summary>Shows every node again.</summary>
    public void ShowAll() => _picked.Clear();

    /// <summary>Adds <paramref name="key"/> to the nodes shown, or takes it out again: one pick is one node, two are several.</summary>
    public void Toggle(string key)
    {
        if (!_picked.Remove(key))
        {
            _picked.Add(key);
        }
    }

    /// <summary>The header's pills: All nodes, then each node, the shown ones lit; nothing while there is one node.</summary>
    public IEnumerable<Layout.Node> Pills()
    {
        if (Choices.Count < 2)
        {
            yield break;
        }
        var all = All;
        yield return CardsView<RgbaImage>.Pill("All nodes", "nodes:all", ShowAll, all ? Lit : null);
        foreach (var choice in Choices)
        {
            var key = choice.Key;
            yield return CardsView<RgbaImage>.Pill(choice.Name, $"nodes:{key}", () => Toggle(key), !all && _picked.Contains(key) ? Lit : null);
        }
    }

    /// <summary>The pills in a line, for a dump or a test: <c>[All nodes] SE1 LE1</c>, the lit ones bracketed.</summary>
    public string Line() =>
        Choices.Count < 2 ? "" : string.Join(" ", new[] { All ? "[All nodes]" : "All nodes" }.Concat(Choices.Select(c => !All && _picked.Contains(c.Key) ? $"[{c.Name}]" : c.Name)));
}

/// <summary>
/// Every page's screen for the nodes a window shows: each node's rows merged, marked with their node when there is more
/// than one, and filtered by the window's <see cref="NodeFilter"/>.
/// </summary>
internal sealed record FleetPages(SessionsScreen Sessions, FlightsScreen Flights, JobsScreen Jobs, IReadOnlyList<JobRow> JobRows, CardsScreen Cards)
{
    /// <summary>
    /// Merges <paramref name="feeds"/> (this window's node first, then the others, each node once). A single node's rows
    /// stay unmarked, as they were before there was a fleet. The header's source is this window's node's.
    /// </summary>
    public static FleetPages Merge(IReadOnlyList<(NodeMark Mark, NodeFeed Feed)> feeds, NodeFilter filter, DateTimeOffset now)
    {
        filter.Choices = [.. feeds.Where(f => f.Mark.Here || f.Feed.Connected).Select(f => f.Mark)];
        var several = filter.Choices.Count > 1;
        var shown = feeds.Where(f => !several || filter.Shows(f.Mark)).ToList();
        NodeMark? Mark(NodeMark mark) => several ? mark : null;

        var here = feeds[0].Feed;
        var hereSessions = here.Screen(now);
        var sessions = SessionLine.Order(shown.SelectMany(f => f.Feed.Screen(now).Lines.Select(l => l with { Node = Mark(f.Mark) })));
        var flights = FlightLine.Order(shown.SelectMany(f => f.Feed.Flights(now).Lines.Select(l => l with { Node = Mark(f.Mark) })));
        var jobs = JobLine.Order(shown.SelectMany(f => f.Feed.JobsPage(now).Lines.Select(l => l with { Node = Mark(f.Mark) })));
        var cards = CardsScreen.Merge(shown.Select(f => (Mark(f.Mark), f.Feed.Cards())), feeds[0].Feed.Cards().Watchtower);
        var source = several ? $"{hereSessions.Source} · {filter.Choices.Count} nodes{(filter.All ? "" : $", showing {string.Join(", ", filter.Choices.Where(c => filter.Shows(c)).Select(c => c.Name))}")}" : hereSessions.Source;
        return new FleetPages(
            hereSessions with { Lines = sessions, Source = source },
            new FlightsScreen(flights, several ? source : here.Flights(now).Source, now),
            new JobsScreen(jobs, several ? source : here.JobsPage(now).Source, now),
            [.. shown.SelectMany(f => f.Feed.Jobs())],
            cards);
    }
}

/// <summary>
/// The client a row's command goes through: this window's own for its node's rows, and for another node's a client of
/// its own, with the grant saved for it. A node without a grant is read (a transcript, a log) but never commanded.
/// </summary>
internal sealed class FleetClients(NodeClient here, Func<string, bool> granted, Func<string, NodeClient>? remote = null, Func<string, string?>? tokenOf = null) : IDisposable
{
    private readonly Dictionary<string, (NodeClient Client, string? Token)> _remote = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, NodeClient> _connect = remote ?? (endpoint => NodeClient.Remote(endpoint, Pilot.Commands.NodeTargets.TokenFor(endpoint)));
    private readonly Lock _lock = new();
    // Nodes that refused the grant as unauthorized, with the token that was refused.
    private readonly Dictionary<string, string?> _revoked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _tokenOf = tokenOf ?? (endpoint => Pilot.Commands.NodeTargets.TokenFor(endpoint));

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
        lock (_lock)
        {
            return granted(node.Where) && !_revoked.ContainsKey(node.Where);
        }
    }

    /// <summary>Whether <paramref name="envelope"/> is a node refusing the grant; notes it so the node reads as watch-only.</summary>
    public bool NoteRefusal<T>(NodeMark? node, ResponseEnvelope<T> envelope)
    {
        if (node is not { Here: false } || envelope is not { Success: false, StatusCode: 401 or 403 })
        {
            return false;
        }
        lock (_lock)
        {
            _revoked[node.Where] = _tokenOf(node.Where);
        }
        return true;
    }

    /// <summary>A new grant (a token other than the one refused) lifts a refusal; called with the fleet tick.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            foreach (var (where, refused) in _revoked.ToArray())
            {
                if (_tokenOf(where) != refused)
                {
                    _revoked.Remove(where);
                }
            }
        }
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
        lock (_lock)
        {
            // A remote client fixes its token for life, so one built under another token (none before Connect, the old
            // one after Forget or over a refusal) is dropped and rebuilt with the token held now.
            var token = _tokenOf(node.Where);
            if (_remote.TryGetValue(node.Where, out var held) && held.Token != token)
            {
                held.Client.Dispose();
                _remote.Remove(node.Where);
            }
            if (!_remote.TryGetValue(node.Where, out held))
            {
                _remote[node.Where] = held = (_connect(node.Where), token);
            }
            return held.Client;
        }
    }

    /// <summary>What a command on a node without a grant says instead.</summary>
    public static string WatchOnly(NodeMark? node) =>
        $"{node?.Name} ({node?.Where}) is watched, not commanded: Connect to {node?.Name} to steer";

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (client, _) in _remote.Values)
            {
                client.Dispose();
            }
            _remote.Clear();
        }
    }
}
