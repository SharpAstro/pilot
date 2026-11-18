using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page's archive (PIL1083): the quiet "M archived" count on Everything and the list it opens (read from each node's archive,
// paged), Bring back on an archived flight, and "Archive landed" for the landed rows the page shows, each flight sent to its own node.
internal sealed partial class FlightsView<TSurface>
{
    /// <summary>How many archived flights a page of the archived list shows.</summary>
    internal const int ArchivedPageSize = 20;

    private const string ArchiveLandedKey = "archive-landed";
    private const string ArchiveLandedHit = "flights:archive-landed";
    internal const string ArchivedHit = "flights:archived";
    internal const string ArchivedBackHit = "flights:archived-back";

    /// <summary>
    /// Reads one page of a node's archive index (node, offset, count): the page, or the node's refusal in its words. The host sends
    /// <see cref="Verbs.ArchivedFlights"/> to that node. Null where the host has no node to ask.
    /// </summary>
    public Func<NodeMark?, int, int, Task<(ArchivedFlightsPage? Page, string? Refused)>>? OnReadArchived { get; set; }

    private sealed record ArchivedPageState(ArchivedFlightsPage? Page, int Number, string? Error, bool Loading);

    private bool _archivedOpen;
    private IReadOnlyList<ArchivedNode> _archivedNodes = [];
    private ImmutableDictionary<string, ArchivedPageState> _archivedPages = ImmutableDictionary<string, ArchivedPageState>.Empty;

    /// <summary>Whether the archived list is what the page shows.</summary>
    public bool IsArchivedOpen => _archivedOpen;

    private static string NodeKey(NodeMark? node) => node?.Key ?? "";

    /// <summary>Opens the archived list, reading the first page of every node's archive.</summary>
    public void OpenArchived()
    {
        _archivedOpen = true;
        foreach (var node in _archivedNodes)
        {
            LoadArchived(node, 1);
        }
        Presses.Changed?.Invoke();
    }

    /// <summary>Goes back to the flights from the archived list.</summary>
    public void CloseArchived()
    {
        _archivedOpen = false;
        Presses.Changed?.Invoke();
    }

    private void LoadArchived(ArchivedNode node, int number)
    {
        if (OnReadArchived is not { } read)
        {
            return;
        }
        var key = NodeKey(node.Node);
        var held = _archivedPages.GetValueOrDefault(key);
        var loading = new ArchivedPageState(held?.Page, number, null, true);
        ImmutableInterlocked.AddOrUpdate(ref _archivedPages, key, loading, (_, _) => loading);
        _ = Task.Run(async () =>
        {
            ArchivedPageState next;
            try
            {
                var (page, refused) = await read(node.Node, (number - 1) * ArchivedPageSize, ArchivedPageSize);
                next = new ArchivedPageState(page, number, refused ?? (page is null ? "the node sent no page" : null), false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
            {
                next = new ArchivedPageState(held?.Page, number, ex.Message, false);
            }
            ImmutableInterlocked.AddOrUpdate(ref _archivedPages, key, next, (_, _) => next);
            Presses.Changed?.Invoke();
        });
    }

    // After an archive or a bring back, the archived list shown is read again: what it lists has changed.
    private void ReloadArchived()
    {
        if (!_archivedOpen)
        {
            return;
        }
        foreach (var node in _archivedNodes)
        {
            LoadArchived(node, _archivedPages.GetValueOrDefault(NodeKey(node.Node))?.Number ?? 1);
        }
    }

    /// <summary>The landed flights of <paramref name="screen"/> that Archive landed would send: landed, on a node that can be reached and commanded.</summary>
    internal IReadOnlyList<FlightLine> ArchivableLanded(FlightsScreen screen) =>
        [.. screen.Lines.Where(l => l.Row.State == FlightState.Landed && l.Stale is null && WatchOnly(l) is null)];

    /// <summary>The header items of the archive: Archive landed and the quiet count, both on Everything only.</summary>
    private IEnumerable<Layout.Node> ArchiveHeader(FlightsScreen screen)
    {
        _archivedNodes = screen.Archived;
        if (Play is { InPlay: true })
        {
            yield break;
        }
        if (CanCommand && OnAction is not null && ArchivableLanded(screen) is { Count: > 0 } landed)
        {
            yield return Presses.Button(ArchiveLandedKey, ArchiveLandedHit,
                () => CardsView<TSurface>.HeaderPill($"Archive landed ({landed.Count})", ArchiveLandedHit, () => _ = ArchiveLanded(landed), NightOrNull is { } n ? (n.Button, n.ButtonHover) : null, NightOrNull?.Bright)
                    .WithTooltip("Archive the landed flights shown, each on its own node"),
                pressed => CardsView<TSurface>.DeadHeaderPill(pressed ? $"Archive landed ({landed.Count}) …" : $"Archive landed ({landed.Count})", NightOrNull is { } d ? (d.Button, d.ButtonHover) : null));
        }
        if (screen.ArchivedCount > 0)
        {
            // Quiet: the dim text colour, no pill; a click opens the list.
            yield return Layout.Builder.Text($"{screen.ArchivedCount} archived", 15f, QuietColor(P), TextAlign.Far, TextAlign.Far)
                .WAuto()
                .BgHover(P.RowHoverBg)
                .WithTooltip("The landed flights the nodes have archived: open the list")
                .Follows(new HitResult.ButtonHit(ArchivedHit), _ => OpenArchived());
        }
    }

    /// <summary>
    /// The colour of the quiet count on the header: the dim text colour, which reads on the header in the standard palette; Night's
    /// dim red is too dark on its header (as its other dim text is), so Night uses its bright red, as the header's other clauses do.
    /// </summary>
    internal static RGBAColor32 QuietColor(Palette palette) => ReferenceEquals(palette, Palette.Night) ? palette.Bright : palette.Dim;

    /// <summary>Sends archive-flight for each of <paramref name="lines"/> to its own node, one after the other; every refusal is said.</summary>
    internal Task ArchiveLanded(IReadOnlyList<FlightLine> lines)
    {
        if (OnAction is not { } send || Presses.PendingOf(ArchiveLandedKey) is not null)
        {
            return Task.CompletedTask;
        }
        var label = $"archive {lines.Count} landed";
        Say($"{label}…");
        return PressedAsync();

        async Task PressedAsync()
        {
            var refused = await Presses.PressAsync(ArchiveLandedKey, ArchiveLandedHit, async _ =>
            {
                var refusals = new List<string>();
                // Grouped per node, each flight to its own node; a refusal names the flight, and the rest still go.
                foreach (var group in lines.GroupBy(l => l.Node?.Key))
                {
                    foreach (var line in group)
                    {
                        var action = new FlightAction($"archive {line.Code}", Verbs.ArchiveFlight, Verbs.FlightTarget(line.Code), []) { Node = line.Node };
                        if (await send(action) is { } why)
                        {
                            refusals.Add($"{line.Code}{(line.Node is { } node ? $" on {node.Name}" : "")}: {why}");
                        }
                    }
                }
                return refusals.Count == 0 ? null : string.Join("; ", refusals);
            }, say: why => $"refused: {label}: {why}");
            Say(refused ?? $"ok: {label}");
            Presses.Changed?.Invoke();
        }
    }

    // The archived list in the flights' place: a section per node, each a page of its archive, newest landed first.
    private Layout.Node ArchivedBody(FlightsScreen screen)
    {
        var rows = new List<Layout.Node>
        {
            BackRow.Of("Flights", ArchivedBackHit, CloseArchived, Layout.Builder.Text($"Archived flights · {screen.ArchivedCount}", 16f, Bright).WAuto()).RowH(32f),
        };
        if (screen.Archived.Count == 0)
        {
            rows.Add(Layout.Builder.Text("No archived flights", 13f, Dim).RowH(24f));
        }
        var several = screen.Archived.Count > 1 || screen.Archived.Any(a => a.Node is not null);
        foreach (var node in screen.Archived)
        {
            var state = _archivedPages.GetValueOrDefault(NodeKey(node.Node));
            var total = state?.Page?.Total ?? node.Count;
            var from = state is null ? 0 : (state.Number - 1) * ArchivedPageSize;
            var shownTo = Math.Min(total, from + (state?.Page?.Rows.Length ?? 0));
            var range = state is null || state.Loading ? "reading…" : total == 0 ? "none" : $"{from + 1} to {shownTo}";
            rows.Add(Layout.Builder.Text($"{(several ? $"{node.Node?.Name ?? "this node"} · " : "")}{total} archived · {range}", 13f, Dim).RowH(26f));
            if (state?.Error is { } error)
            {
                rows.Add(Prose($"refused: {error}", 12f, Held).Pad(0f, 2f).WStar().HAuto());
            }
            foreach (var row in state?.Page?.Rows ?? [])
            {
                rows.Add(ArchivedRow(row, node.Node));
                if (Presses.RefusalLine(row.Code, 12f, Held, 3) is { } refusal)
                {
                    rows.Add(refusal);
                }
            }
            var paging = new List<Layout.Node>();
            if (state is { Loading: false } && state.Number > 1)
            {
                paging.Add(Pill("← Newer", $"flights:archived-prev:{NodeKey(node.Node)}", () => LoadArchived(node, state.Number - 1)));
            }
            if (state is { Loading: false } && state.Number * ArchivedPageSize < total)
            {
                paging.Add(Pill("Older →", $"flights:archived-next:{NodeKey(node.Node)}", () => LoadArchived(node, state.Number + 1)));
            }
            if (paging.Count > 0)
            {
                rows.Add(Layout.Builder.HStack([.. paging]).WithGap(10f).RowH(32f));
            }
        }
        return Layout.Builder.VStack([.. rows]).Pad(Gutter, 8f).WithScroll(_scroll).Stretch();
    }

    private Layout.Node ArchivedRow(ArchivedFlightRow row, NodeMark? node)
    {
        var title = row.Title ?? "";
        var landed = row.LandedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "landed earlier";
        var hit = $"flight:{row.Code}:bring-back";
        var button = CanCommand && OnAction is not null && (node is null || CanSteer?.Invoke(node) != false)
            ? Presses.Button(row.Code, hit,
                () => Pill("Bring back", hit, () => _ = Run(FlightAction.BringBack(row.Code, node), row.Code, hit)),
                _ => CardsView<TSurface>.Dead("Bring back …")).WAuto()
            : Layout.Builder.Spacer().WAuto();
        return Layout.Builder.HStack(
                Layout.Builder.Text(row.Code, 13f, Bright).WFixed(100f),
                Whole(Layout.Builder.Text(title, 13f, Body).WStar(1f, 0f, 120f), title, 0),
                Layout.Builder.Text(row.PullRequest is { } pr ? $"PR #{pr}" : "", 12f, Dim).WFixed(80f),
                Layout.Builder.Text(landed, 12f, Dim).WFixed(130f),
                button)
            .WithGap(10f).RowH(30f);
    }
}
