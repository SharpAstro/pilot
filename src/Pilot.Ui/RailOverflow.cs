using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// Which of the rail's cells fit. The rail's cells are squares, so a short window holds a whole number of them; rather than crop
/// the last, the rail draws one fewer and a "..." cell that opens a menu of the rest. The page being shown (and the Cards cue, when
/// it shows) always keep a cell: they are swapped in for the last page that would have been visible.
/// </summary>
internal static class RailOverflow
{
    /// <summary>The value of the "..." cell: no page has it, so it is never the active one.</summary>
    internal const PilotPage MoreValue = (PilotPage)(-1);

    /// <summary>The cells a rail of <paramref name="height"/> holds when each is <paramref name="cell"/> tall, rounding off sub-pixel noise.</summary>
    public static int Capacity(float height, float cell) => cell <= 0f ? int.MaxValue : Math.Max(0, (int)MathF.Floor((height / cell) + 0.001f));

    /// <summary>The indexes drawn and the indexes left for the menu, each in rail order.</summary>
    public readonly record struct Plan(ImmutableArray<int> Visible, ImmutableArray<int> Hidden)
    {
        /// <summary>Whether some cell does not fit, so a "..." cell stands in for them.</summary>
        public bool Overflows => Hidden.Length > 0;
    }

    /// <summary>
    /// Splits <paramref name="count"/> cells over <paramref name="capacity"/> places. All fit: every one is visible. Otherwise the "..."
    /// takes a place and the pinned cells keep theirs; the others fill what is left in rail order.
    /// </summary>
    public static Plan Of(int count, int capacity, Func<int, bool> pinned)
    {
        if (count <= capacity)
        {
            return new Plan([.. Enumerable.Range(0, count)], []);
        }

        var slots = Math.Max(0, capacity - 1);
        var keep = new HashSet<int>();
        foreach (var i in Enumerable.Range(0, count).Where(pinned).Take(slots))
        {
            keep.Add(i);
        }
        foreach (var i in Enumerable.Range(0, count))
        {
            if (keep.Count >= slots)
            {
                break;
            }
            keep.Add(i);
        }

        var visible = Enumerable.Range(0, count).Where(keep.Contains).ToImmutableArray();
        var hidden = Enumerable.Range(0, count).Where(i => !keep.Contains(i)).ToImmutableArray();
        return new Plan(visible, hidden);
    }
}

/// <summary>
/// The "..." cell's menu, a popup beside the rail listing the pages that did not fit, each with its glyph and name and chord. It
/// also carries the chords of those pages as invisible nodes, so Ctrl+n still reaches a page whose cell is in the menu.
/// </summary>
internal sealed class RailMoreMenu<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private const float FontSize = 13f;

    /// <summary>The menu's state: its entries are the hidden pages, set by the chrome each frame.</summary>
    public DropdownMenuState<PilotPage> Menu { get; } = new();

    /// <summary>The entries now in the menu, for a host or a test to read.</summary>
    public ImmutableArray<DropdownItem<PilotPage>> Entries => Menu.Items;

    /// <summary>The hidden pages' chords, each doing what its cell would.</summary>
    public IReadOnlyList<(KeyChord Chord, Action Act)> Chords { get; set; } = [];

    /// <summary>Where the "..." cell is: the menu hangs to its right.</summary>
    public RectF32 Anchor { get; set; }

    public void Render(RectF32 window)
    {
        BeginFrame();
        var nodes = new List<Layout.Node>();
        foreach (var (chord, act) in Chords)
        {
            var hit = new HitResult.ButtonHit("rail-more:" + chord);
            nodes.Add(Layout.Builder.Spacer().Clickable(hit, _ => act()).WithShortcut(chord));
        }

        var tree = Layout.Builder.HStack(nodes.ToArray());
        if (Menu.IsOpen)
        {
            tree = Layout.Builder.Overlay(tree, Layout.Builder.Dropdown(Anchor, Menu, FontSize, Palette.Current.Bright,
                Palette.Current.LinkMenuMenuBg, Palette.Current.Highlight, side: Layout.DockSide.Right));
        }
        else if (nodes.Count == 0)
        {
            return;
        }

        RenderLayout(tree, window);
    }
}
