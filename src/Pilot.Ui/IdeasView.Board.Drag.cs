using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The board's drag: an item lifted by its handle, the section under the pointer, and the section a point is in.
internal sealed partial class IdeasView<TSurface>
{
    // ---- Drag ----

    // The handle's press: the item is lifted (its row dims), the section under the pointer lights as it passes, and letting go on a
    // milestone asks the node. Letting go anywhere else, or on the section it is in, asks nothing.
    private DragCapture? BeginDrag(BoardItem item, PointerPress press)
    {
        if (press.Button != MouseButton.Left || Send is null)
        {
            return null;
        }
        _dragging = item.Key;
        _dropOn = null;
        Changed?.Invoke();
        return new DragCapture(
            move =>
            {
                var over = SectionAt(move.X, move.Y)?.Key;
                if (over != _dropOn)
                {
                    _dropOn = over;
                    Changed?.Invoke();
                }
            },
            release =>
            {
                var target = SectionAt(release.X, release.Y);
                _dragging = null;
                _dropOn = null;
                Changed?.Invoke();
                if (target is not null)
                {
                    Run(() => MoveToAsync(item, target), $"move {Label(item)} to {target.Title}");
                }
            });
    }

    // The section a pointer is over, from what was painted. In the list: under the divider, the last section header above the pointer,
    // so the whole section answers and not only its header; above the divider (the pinned section) nothing takes a drop. In the tree
    // grid: the last section row above the pointer, none from the detail's handle down. On the canvas: the header of the column the
    // pointer is over (the nearest above it, where the columns wrap into lines). Null where nothing takes it.
    internal BoardSection? SectionAt(float x, float y)
    {
        var regions = new List<ClickableRegion>();
        CollectPaintedRegions(regions);
        var limit = float.MaxValue;
        switch (ViewMode)
        {
            case IdeasViewMode.List:
                var divider = regions.FindIndex(r => r is { Result: HitResult.ButtonHit { Action: SplitHit } });
                if (divider < 0 || y < regions[divider].Y + regions[divider].Height)
                {
                    return null;
                }
                break;
            case IdeasViewMode.Tree:
                if (regions.FirstOrDefault(r => r is { Result: HitResult.ButtonHit { Action: DetailSplitHit } }) is { } detail)
                {
                    limit = detail.Y;
                }
                break;
        }
        if (y >= limit)
        {
            return null;
        }
        BoardSection? found = null;
        var best = float.MinValue;
        foreach (var region in regions)
        {
            if (region is { Result: HitResult.ButtonHit hit } && hit.Action.StartsWith(SectionHit, StringComparison.Ordinal) && region.Y <= y && region.Y > best
                && (ViewMode != IdeasViewMode.Canvas || (region.X <= x && x < region.X + region.Width)))
            {
                best = region.Y;
                found = _board.Sections.FirstOrDefault(s => SectionHit + s.Key == hit.Action);
            }
        }
        return found is { Accepts: true } ? found : null;
    }
}
