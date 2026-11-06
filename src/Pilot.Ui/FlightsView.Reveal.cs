using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

// Opening a flight's page at one leg: a card's link asks for it, and the layout that knows where the leg's block sits scrolls it to the top.
internal sealed partial class FlightsView<TSurface>
{
    // The node of each leg's block in the page built last, so layout can say where one sits (see RevealLeg).
    private readonly Dictionary<string, Layout.Node> _legBlocks = [];

    // The leg a card's link asked the page to scroll to, until the layout that knows where its block sits has run.
    private string? _revealLeg;

    /// <summary>Opens <paramref name="code"/>'s page with leg <paramref name="leg"/>'s block scrolled into view, once layout knows where it sits.</summary>
    public void OpenDetail(string code, string leg)
    {
        OpenDetail(code);
        _revealLeg = leg;
    }

    // The arrangement of the page painted last.
    private ImmutableArray<Layout.ArrangedNode<float>> _arranged;

    /// <summary>Whether the top of <paramref name="leg"/>'s block lies inside the detail viewport as last painted; false when the leg is not on the page.</summary>
    internal bool LegBlockInView(string leg)
    {
        if (!_legBlocks.TryGetValue(leg, out var block) || _arranged.IsDefault)
        {
            return false;
        }
        foreach (var node in _arranged)
        {
            if (ReferenceEquals(node.Node, block))
            {
                var (start, _) = _detailScroll.ContentExtentOf(new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height));
                return start >= _detailScroll.Offset - 1f && start < _detailScroll.Offset + _detailScroll.Viewport.Height;
            }
        }
        return false;
    }

    // Scrolls the asked-for leg to the top of the page's scroll, from the arrangement just made; true when the offset moved and the page wants painting again.
    private bool RevealLeg(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        if (_revealLeg is not { } leg || _openCode is null)
        {
            return false;
        }
        if (!_legBlocks.TryGetValue(leg, out var block))
        {
            // Its page is not on show yet (or the leg is not on it): asked again on the next frame, until the page is closed.
            return false;
        }
        foreach (var node in arranged)
        {
            if (ReferenceEquals(node.Node, block))
            {
                _revealLeg = null;
                var before = _detailScroll.AtomOffset;
                var (start, _) = _detailScroll.ContentExtentOf(new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height));
                // A span as tall as the view aligns by its start: the leg's block at the top.
                _detailScroll.RevealExtent(start, start + MathF.Max(node.Bounds.Height, _detailScroll.Viewport.Height + 1f));
                return _detailScroll.AtomOffset != before;
            }
        }
        return StepTowardLeg(leg, arranged);
    }

    // The layout arranges only what lies within a viewport or two of the view (DIR.Lib 11.46 on), so a leg far down a long page has no
    // arranged block to measure. Step the page one view toward it, by where the legs that are arranged stand in the page's order, and
    // paint again: the next frame arranges it, or steps again. Stops where the page ends, so a leg that cannot be found never loops.
    private bool StepTowardLeg(string leg, ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        var order = _legBlocks.Keys.ToList();
        var wanted = order.IndexOf(leg);
        var seen = order.Select((name, index) => (name, index)).Where(l => arranged.Any(a => ReferenceEquals(a.Node, _legBlocks[l.name]))).Select(l => l.index).ToList();
        if (wanted < 0 || seen.Count == 0)
        {
            return false;
        }
        var before = _detailScroll.AtomOffset;
        var step = MathF.Max(1f, _detailScroll.Viewport.Height);
        _detailScroll.AtomOffset = (int)MathF.Round(_detailScroll.Offset + (seen.Min() > wanted ? -step : step));
        if (_detailScroll.AtomOffset == before)
        {
            _revealLeg = null;
            return false;
        }
        return true;
    }
}
