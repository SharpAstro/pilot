using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights map's selection: what counts as empty canvas for a pan, and the keys that move the selection through the rows.
internal sealed partial class FlightsView<TSurface>
{
    // Whether a surface point is empty canvas: in no row band, no continent header, and not on the zoom pill or the layers menu.
    // A left press there pans; a press on a row is left to the router (a click selects, and a drag is kept for reordering).
    private bool IsEmptyCanvas(RectF32 body, float x, float y)
    {
        if (_layersOnShow && MapLayers.Palette.PanelRect.Contains(x, y))
        {
            return false;
        }
        var (vx, vy) = ToView(body, x, y);
        var pillX = _vp.ViewWidth - PillWidth - PillGap - (MapLayers.Palette.Side == Layout.DockSide.Right ? FlightMapLayerPalette.PanelWidth + PillGap : 0f);
        var pillY = _vp.ViewHeight - PillHeight - PillGap;
        if (vx >= pillX && vx <= pillX + PillWidth && vy >= pillY && vy <= pillY + PillHeight)
        {
            return false;
        }
        var (dx, dy) = (_vp.DesignX(vx), _vp.DesignY(vy));
        foreach (var c in _map.Continents)
        {
            if (dx >= c.X && dx <= c.X + c.Width && dy >= c.Y && dy <= c.Y + FlightMap.Header)
            {
                return false;
            }
        }
        foreach (var route in _map.Routes)
        {
            if (IndexOf(_screen, route) < 0)
            {
                continue;
            }
            var (bx, by, bw, bh) = _map.RowBand(route);
            if (dx >= bx && dx <= bx + bw && dy >= by && dy <= by + bh)
            {
                return false;
            }
        }
        return true;
    }

    // Moves the selection through the flights in the order they are drawn (continents as drawn, rows top to bottom), stopping at the
    // ends, then pans just far enough to bring its row into view. With none picked, Down and Home take the first, Up and End the last.
    private void MoveSelection(InputKey key)
    {
        var order = new List<(int Index, MapRoute Route)>();
        foreach (var route in _map.Routes)
        {
            if (IndexOf(_screen, route) is >= 0 and var i)
            {
                order.Add((i, route));
            }
        }
        if (order.Count == 0)
        {
            return;
        }
        var at = order.FindIndex(o => o.Index == Selected);
        var to = key switch
        {
            InputKey.Home => 0,
            InputKey.End => order.Count - 1,
            InputKey.Down => at < 0 ? 0 : Math.Min(at + 1, order.Count - 1),
            _ => at < 0 ? order.Count - 1 : Math.Max(at - 1, 0),
        };
        ListCursor.MoveTo(order[to].Index);
        var (bx, by, bw, bh) = _map.RowBand(order[to].Route);
        _vp.EnsureVisible(bx, by, bw, bh);
    }
}
