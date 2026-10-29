using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page's Canvas view as a map: FlightMap places continents, routes and arcs in design units. The layout engine
// holds the click targets (planes, airports, gates, destinations, continent labels), each at its map point; one Fill leaf under
// them paints land, routes, arcs and the words by hand, scaled as the leaf's rect says, so all of it scrolls together.
internal sealed partial class FlightsView<TSurface>
{
    private const float MinMapScale = 0.5f;
    // The scrollbar's lane and the page's gutters, taken off the width the map is fitted to.
    private const float MapSideRoom = 2f * Gutter + 16f;
    private const float LabelSize = 9f;
    // One line of a stop's label band: the second starts where the first ends.
    private const float StopLine = FlightMap.LabelLineTwo - FlightMap.LabelBandTop;
    private const int LabelChars = 10;

    // The window's width in design units as of the last frame, so a map wider than it is drawn smaller, down to half size.
    private float _viewWidth;

    // Whether the last tree drew the map: the Canvas view, with a map that fits the page at half size.
    private bool _mapFits;

    // Whether the last render arranged the layer menu, so the pointer is held against where it is.
    private bool _layersOnShow;

    // How much of its design size the map is drawn at.
    private float MapScale => _viewWidth <= 0f || _map.Width <= 0f ? 1f : Math.Clamp((_viewWidth - MapSideRoom) / _map.Width, MinMapScale, 1f);

    /// <summary>
    /// Whether the page shows the map now: the Canvas view, and a map that fits the page at half size. One that does not is shown
    /// as the grid, as the canvas of cards was: the map scrolls down only, so its far side would be cut off out of reach.
    /// </summary>
    public bool MapShown => CanvasShown && _mapFits;

    // Builds the map for the page and says whether it fits at half size.
    private bool MapFits(FlightsScreen screen)
    {
        MapLayers.Nodes = [.. screen.Lines.GroupBy(l => l.Node?.Key ?? "").OrderByDescending(g => g.First().Node?.Here ?? true).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.First().Node?.Name ?? "this node"))];
        // A node picked and not on the page (its feed has not arrived yet, or its flights landed) keeps the choice: the map shows
        // nothing for it until it appears, and only an explicit pick changes it.
        _map = FlightMap.Build(screen.Lines, MapLayers);
        _mapFits = _viewWidth <= 0f || _map.Width * MinMapScale <= _viewWidth - MapSideRoom;
        return _mapFits;
    }

    private Layout.Node CanvasBody(FlightsScreen screen)
    {
        _chain = Selected >= 0 && Selected < screen.Lines.Count ? _map.Chain(screen.Lines[Selected].Node?.Key ?? "", screen.Lines[Selected].Code) : new HashSet<(string Node, string Code)>();
        var layers = FlightMapLayerPalette.Build(MapLayers, P, 11f, Scale, MapLayers.NoteChanged);
        // The page has flights and the layers left none: one line says which layer hides them, beside the menu that turns it back.
        // It wraps between two spaces of the menu's width, one each side where the menu may be docked, so no word runs under it.
        if (FlightMapLayers.Hiding(screen.Lines, MapLayers, _map) is { } hidden)
        {
            var clear = Layout.Builder.Spacer().WFixed(FlightMapLayerPalette.PanelWidth);
            var line = Layout.Builder.HStack(clear, Prose(hidden, 14f, Dim).WStar().HAuto(), clear).WithGap(Gutter).WStar().HAuto();
            var note = Layout.Builder.VStack(line).Pad(Gutter, 8f).Stretch();
            return Layout.Builder.Overlay(note, layers).Stretch();
        }
        var k = MapScale;
        var width = MathF.Max(1f, _map.Width * k);
        var height = MathF.Max(1f, _map.Height * k);
        Layout.Node surface = Layout.Builder.Fill(key: CanvasKey).WFixed(width).HFixed(height);
        foreach (var hit in MapTargets(screen, k))
        {
            surface = Layout.Builder.Overlay(surface, hit);
        }
        surface = surface.WFixed(width).HFixed(height);
        var map = Layout.Builder.VStack(surface)
            .WithScroll(_scroll)
            .Pad(Gutter, 8f)
            .Stretch();
        // The layer menu floats over the map, outside its scroll, as the sky map's does over the sky.
        return Layout.Builder.Overlay(map, layers).Stretch();
    }

    // The panel is the node carrying the palette's own hit, which FloatingPalette puts on it; its parent, the Anchored, was given
    // the rect the palette floats in (the map's body, under the page's header), and its offset is measured from that rect's top.
    // Measured from the window's top instead, each frame moved it a header's height further down.
    private static readonly HitResult LayersPanelHit = new HitResult.ButtonHit(FlightMapLayerPalette.GripAction + ":Panel");

    // Where the palette landed, handed back so its stored offset follows the engine's clamp; and while it fades, a frame asked
    // for, or the fade stops wherever the last event left it (nothing else redraws while the pointer is still).
    private void NoteLayersArranged(IReadOnlyList<Layout.ArrangedNode<float>> arranged)
    {
        _layersOnShow = false;
        if (!MapShown)
        {
            return;
        }
        for (var i = 0; i < arranged.Count; i++)
        {
            if (!Equals(arranged[i].Node.Hit, LayersPanelHit))
            {
                continue;
            }
            var parent = i - 1;
            while (parent >= 0 && arranged[parent].Depth >= arranged[i].Depth)
            {
                parent--;
            }
            if (parent < 0)
            {
                return;
            }
            var (panel, inside) = (arranged[i].Bounds, arranged[parent].Bounds);
            MapLayers.Palette.NoteArranged(new RectF32(panel.X, panel.Y, panel.Width, panel.Height), new RectF32(inside.X, inside.Y, inside.Width, inside.Height), Scale);
            _layersOnShow = true;
            if (MapLayers.Palette.IsFading)
            {
                Changed?.Invoke();
            }
            return;
        }
    }

    /// <summary>
    /// Where the pointer is, for the map's layer menu: over it, the menu holds itself opaque, and off it, it fades again. A frame is
    /// asked for when that changes, so the fade starts without waiting for another event.
    /// </summary>
    public void NotePointer(float x, float y)
    {
        if (!_layersOnShow)
        {
            return;
        }
        var was = MapLayers.Palette.IsEngaged;
        MapLayers.Palette.NotePointer(x, y);
        if (MapLayers.Palette.IsEngaged != was)
        {
            Changed?.Invoke();
        }
    }

    private static string StateWord(AirportState state) => state switch
    {
        AirportState.Done => "done",
        AirportState.Flying => "flying",
        AirportState.Failed => "failed",
        _ => "planned",
    };

    private static string StatusWord(DestinationStatus status) => status switch
    {
        DestinationStatus.NoPr => "no PR yet",
        DestinationStatus.Open => "open",
        DestinationStatus.Draft => "draft",
        DestinationStatus.Reviewing => "reviewing",
        DestinationStatus.Passed => "passed",
        DestinationStatus.Changes => "changes",
        DestinationStatus.Armed => "armed",
        DestinationStatus.Queued => "queued",
        DestinationStatus.Conflicts => "conflicts",
        _ => "merged",
    };

    private static string Fit(string text, int chars) => text.Length <= chars ? text : text[..(chars - 1)] + "…";

    // The words on a destination's sign: its repo and PR number (tianwen #7), the repo cut where both do not fit (tia… #1896); as wide
    // as a leg's name, so a connection's drop out of the sign (FlightMap.LabelHalfWidth) passes clear of it. The repo alone
    // before there is a PR.
    private static string SignOf(string repo, int? pullRequest)
    {
        if (pullRequest is not { } pr)
        {
            return repo.Length > 0 ? Fit(repo, LabelChars) : "PR";
        }
        var number = $" #{pr}";
        return LabelChars - number.Length < 4 || repo.Length == 0 ? $"PR #{pr}" : Fit(repo, LabelChars - number.Length) + number;
    }

    // What a destination says on hover: its PR by repo and number and where it stands, then the flight's brief.
    private string DestinationTip(FlightLine line, MapDestination d)
    {
        var repo = line.Row.Repo;
        var sign = d.PullRequest is { } pr ? $"{repo} PR #{pr}" : $"{repo} PR";
        var first = !MapLayers.ShowPrStates ? sign : d.PullRequest is null ? $"{repo}: {StatusWord(d.Status)}" : $"{sign}: {StatusWord(d.Status)}";
        return first + "\n" + ChipBrief.Text(ChipBrief.Flight(line));
    }

    // A click target at a map point: a spacer at its own size, floated at its place in the map (scaled by k).
    private static Layout.Node Target(float cx, float cy, float r, float k, Func<Layout.Node, Layout.Node> make)
    {
        var side = 2f * r * k;
        var box = make(Layout.Builder.Spacer().WFixed(side).HFixed(side));
        return Layout.Builder.Anchored(box, null, (cx - r) * k, (cy - r) * k, 0f, false);
    }

    private int IndexOf(FlightsScreen screen, MapRoute route)
    {
        for (var i = 0; i < screen.Lines.Count; i++)
        {
            if (screen.Lines[i].Code == route.Flight && (screen.Lines[i].Node?.Key ?? "") == route.Node)
            {
                return i;
            }
        }
        return -1;
    }

    // Planes last, so they are over their airport as they are drawn. A plane parked on its destination has no target of its own:
    // the destination's, as wide as the plane's ring, is the one there, so a click on it opens the PR.
    private IEnumerable<Layout.Node> MapTargets(FlightsScreen screen, float k)
    {
        foreach (var c in _map.Continents)
        {
            // The continent's name: its node's flights, picked by a click on the first.
            var first = _map.Routes.FirstOrDefault(r => r.Node == c.Key);
            if (first is not null && IndexOf(screen, first) is >= 0 and var at)
            {
                var landWidth = c.Width * k;
                var landHeader = FlightMap.Header * k;
                yield return Layout.Builder.Anchored(
                    Layout.Builder.Spacer().WFixed(landWidth).HFixed(landHeader)
                        .WithTooltip(c.Unreachable ? $"{c.Name} is unreachable" : c.Here ? $"{c.Name}, this node" : c.Name)
                        .Clickable(new HitResult.ButtonHit($"map:{c.Key}"), _ => ListCursor.MoveTo(at)),
                    null, c.X * k, c.Y * k, 0f, false);
            }
        }
        foreach (var route in _map.Routes)
        {
            if (IndexOf(screen, route) is not (>= 0 and var index))
            {
                continue;
            }
            var line = screen.Lines[index];
            var brief = ChipBrief.Text(ChipBrief.Flight(line));
            Layout.Node Common(Layout.Node n, string id, Action? open = null) => n
                .Clickable(new HitResult.ButtonHit($"map:{line.Code}:{id}"), _ => ListCursor.MoveTo(index))
                .WithContextMenu(at => OpenMenu(line, index, at))
                .DoubleClickable(_ =>
                {
                    ListCursor.MoveTo(index);
                    (open ?? (() => OpenDetail(line.Code)))();
                })
                .WithCursor(CursorKind.Pointer);
            if (route.Gate is { } gate)
            {
                yield return Target(gate.X, gate.Y, FlightMap.AirportRadius, k, n => Common(n.WithTooltip($"{line.Code} is held at its gate: {gate.WaitsFor}"), "gate"));
            }
            foreach (var a in route.Airports)
            {
                var leg = a.Leg;
                yield return Target(a.X, a.Y, FlightMap.AirportRadius, k, n => Common(n.WithTooltip($"{line.Code}/{leg}: {StateWord(a.State)}\n{brief}"), "leg:" + leg, () => OpenDetail(line.Code, leg)));
            }
            var d = route.Destination;
            var url = line.Row.PullRequestUrl;
            var parked = route.PlaneAtDestination;
            yield return Target(d.X, d.Y, parked ? FlightMap.PlaneRadius : FlightMap.AirportRadius, k, n =>
            {
                var tip = DestinationTip(line, d);
                var node = n.WithTooltip(parked && line.StaleMark is { } stale ? stale + "\n" + tip : tip)
                    .WithContextMenu(at => OpenMenu(line, index, at))
                    .DoubleClickable(_ =>
                    {
                        ListCursor.MoveTo(index);
                        OpenDetail(line.Code);
                    })
                    .WithCursor(CursorKind.Pointer);
                // A click shows its PR on the Pull requests page where the node lists it, as the card's chip does; else opens its PR, else picks the flight.
                if (OnPullRequest is { } show && PullRequestLinks.RowOf(PullRequests, line.Row) is { } listed)
                {
                    return node.Clickable(new HitResult.ButtonHit($"flight:{line.Code}:pr-chip"), _ =>
                    {
                        ListCursor.MoveTo(index);
                        show(listed.Repo, listed.Number);
                    });
                }
                return url is not null && OnOpen is not null
                    ? node.Clickable(new HitResult.ButtonHit($"flight:{line.Code}:pr"), _ =>
                    {
                        ListCursor.MoveTo(index);
                        OnOpen?.Invoke(url);
                    })
                    : node.Clickable(new HitResult.ButtonHit($"map:{line.Code}:destination"), _ => ListCursor.MoveTo(index));
            });
        }
        foreach (var route in _map.Routes)
        {
            if (route.Plane is { } p && !route.PlaneAtDestination && IndexOf(screen, route) is >= 0 and var index)
            {
                var line = screen.Lines[index];
                var leg = p.Leg;
                yield return Target(p.X, p.Y, FlightMap.PlaneRadius, k, n => n
                    .WithTooltip(line.StaleMark is { } stale ? stale + "\n" + ChipBrief.Text(ChipBrief.Flight(line)) : ChipBrief.Text(ChipBrief.Flight(line)))
                    .Clickable(new HitResult.ButtonHit($"map:{line.Code}:plane"), _ => ListCursor.MoveTo(index))
                    .WithContextMenu(at => OpenMenu(line, index, at))
                    .DoubleClickable(_ =>
                    {
                        ListCursor.MoveTo(index);
                        if (leg is null)
                        {
                            OpenDetail(line.Code);
                        }
                        else
                        {
                            OpenDetail(line.Code, leg);
                        }
                    })
                    .WithCursor(CursorKind.Pointer));
            }
        }
    }

    private bool InChain(MapConnection c) => _chain.Contains((c.FromNode, c.From)) && _chain.Contains((c.ToNode, c.To));

    // The words the map painted last, each in the box it was drawn in: one Fill leaf paints them all, so the page's text check
    // (TextOverlap, which reads the arranged text leaves) never sees them, and a test holds them apart from these.
    private readonly List<(string Text, RectF32 Box)> _words = [];

    /// <summary>The words the last frame painted on the map and their boxes, surface units.</summary>
    internal IReadOnlyList<(string Text, RectF32 Box)> MapWords => _words;

    // A word in a box of its line's height: by default its size and the room around it; a line under a stop is given StopLine, so the
    // band's two lines meet and never share a pixel.
    private void Label(string text, float x, float y, float w, float s, float size, RGBAColor32 color, TextAlign align = TextAlign.Center, float line = 0f)
    {
        var h = (line > 0f ? line : size * 1.4f) * s;
        _words.Add((text, new RectF32(x, y, w, h)));
        DrawText(text, FontPath, x, y, w, h, size * s, color, align, TextAlign.Center);
    }

    // The map, in the leaf's rect: the leaf is as big as the map in design units times the fit, so its rect says the scale.
    private void DrawCanvas(Layout.Content.Fill fill, RectF32 rect)
    {
        if (fill.Key != CanvasKey || _map.Height <= 0f || _map.Width <= 0f)
        {
            return;
        }
        _words.Clear();
        var s = rect.Width / _map.Width;
        float X(float v) => rect.X + v * s;
        float Y(float v) => rect.Y + v * s;
        var thin = Math.Max(1, (int)MathF.Round(2f * s));

        foreach (var c in _map.Continents)
        {
            FillRect(X(c.X), Y(c.Y), c.Width * s, c.Height * s, c.Here ? P.LandHere : P.Land, 14f * s);
            // The node code alone: a node's key is where it is (an address on the LAN), which labels nothing.
            var name = c.Name + (c.Unreachable ? " (unreachable)" : "");
            Label(Fit(name, (int)(c.Width / 6.5f)), X(c.X + 12f), Y(c.Y + 6f), (c.Width - 24f) * s, s, 13f, c.Unreachable ? Faint : Bright, TextAlign.Near);
        }

        foreach (var route in _map.Routes)
        {
            DrawRoute(route, s, X, Y, thin);
        }

        // The picked flight's chain last, so its connections lie over the rest where they cross.
        var order = Enumerable.Range(0, _map.Connections.Count).OrderBy(i => InChain(_map.Connections[i]));
        foreach (var i in order)
        {
            DrawConnection(_map.Connections[i], i, s, X, Y, thin);
        }

        foreach (var route in _map.Routes)
        {
            if (route.Plane is { } p)
            {
                DrawPlane(route, p, s, X, Y);
            }
        }
    }

    private void DrawRoute(MapRoute route, float s, Func<float, float> X, Func<float, float> Y, int thin)
    {
        var line = _lineOf(route);
        var picked = line is not null && Selected >= 0 && Selected < _screen.Lines.Count && _screen.Lines[Selected].Code == route.Flight && (_screen.Lines[Selected].Node?.Key ?? "") == route.Node;
        var d = route.Destination;
        var c = _map.Continents.First(c => c.Key == route.Node);
        var rowTop = d.Y - FlightMap.RowHeight / 2f + 8f;
        if (picked || _chain.Contains((route.Node, route.Flight)))
        {
            FillRect(X(c.X + 6f), Y(rowTop), (c.Width - 12f) * s, (FlightMap.RowHeight - 6f) * s, picked ? RowFocusBg : P.LinkedBg, 8f * s);
        }
        var landed = line?.Row.State == FlightState.Landed || line?.Stale is not null;
        var startX = route.Gate?.X ?? route.Airports.FirstOrDefault()?.X ?? d.X;
        Label(route.Flight, X(startX - FlightMap.Slot / 2f), Y(d.Y + FlightMap.CodeTop), (FlightMap.Slot * 2f) * s, s, 11f, landed ? Dim : Bright, TextAlign.Near);

        // Segments: a leg reached (done or flying) makes the run into it passed; the rest is ahead, dashed.
        var stops = new List<(float X, float Y, bool Reached)>();
        if (route.Gate is { } gate)
        {
            stops.Add((gate.X, gate.Y, true));
        }
        stops.AddRange(route.Airports.Select(a => (a.X, a.Y, a.State is AirportState.Done or AirportState.Flying)));
        stops.Add((d.X, d.Y, d.Status == DestinationStatus.Merged));
        for (var i = 1; i < stops.Count; i++)
        {
            var (x0, y0, _) = stops[i - 1];
            var (x1, y1, reached) = stops[i];
            if (reached)
            {
                Renderer.DrawLine(X(x0), Y(y0), X(x1), Y(y1), landed ? Faint : P.RoutePassed, thin);
            }
            else
            {
                Renderer.DrawLineDashed(X(x0), Y(y0), X(x1), Y(y1), P.RouteAhead, DashLength * s, GapLength * s, thin);
            }
        }

        var r = FlightMap.AirportRadius * s;
        if (route.Gate is { } g)
        {
            FillRect(X(g.X) - r, Y(g.Y) - r, 2f * r, 2f * r, Held, 3f * s);
            Label("gate", X(g.X - FlightMap.Slot / 2f), Y(g.Y + FlightMap.LabelBandTop), FlightMap.Slot * s, s, LabelSize, Held, line: StopLine);
            Label(Fit(g.WaitsFor, LabelChars), X(g.X - FlightMap.Slot / 2f), Y(g.Y + FlightMap.LabelLineTwo), FlightMap.Slot * s, s, LabelSize, Dim, line: StopLine);
        }
        foreach (var a in route.Airports)
        {
            var (cx, cy) = (X(a.X), Y(a.Y));
            switch (a.State)
            {
                case AirportState.Done:
                    Renderer.FillEllipse(new RectInt(new PointInt((int)(cx + r), (int)(cy + r)), new PointInt((int)(cx - r), (int)(cy - r))), P.RoutePassed);
                    break;
                case AirportState.Flying:
                    Renderer.FillEllipse(new RectInt(new PointInt((int)(cx + r), (int)(cy + r)), new PointInt((int)(cx - r), (int)(cy - r))), Flying);
                    break;
                case AirportState.Failed:
                    Renderer.FillEllipse(new RectInt(new PointInt((int)(cx + r), (int)(cy + r)), new PointInt((int)(cx - r), (int)(cy - r))), Failed);
                    Renderer.DrawLine(cx - r * 0.5f, cy - r * 0.5f, cx + r * 0.5f, cy + r * 0.5f, Ground, thin);
                    Renderer.DrawLine(cx - r * 0.5f, cy + r * 0.5f, cx + r * 0.5f, cy - r * 0.5f, Ground, thin);
                    break;
                default:
                    Renderer.FillEllipse(new RectInt(new PointInt((int)(cx + r), (int)(cy + r)), new PointInt((int)(cx - r), (int)(cy - r))), P.RouteAhead);
                    var inner = r - 2f * s;
                    Renderer.FillEllipse(new RectInt(new PointInt((int)(cx + inner), (int)(cy + inner)), new PointInt((int)(cx - inner), (int)(cy - inner))), Ground);
                    break;
            }
            Label(Fit(a.Leg, LabelChars), X(a.X - FlightMap.Slot / 2f), Y(a.Y + FlightMap.LabelBandTop), FlightMap.Slot * s, s, LabelSize, Body, line: StopLine);
            Label(StateWord(a.State), X(a.X - FlightMap.Slot / 2f), Y(a.Y + FlightMap.LabelLineTwo), FlightMap.Slot * s, s, LabelSize, a.State == AirportState.Failed ? Failed : a.State == AirportState.Flying ? Flying : Dim, line: StopLine);
        }

        // The destination is a square sign, its PR and its status in words under it.
        // PR states off: the destination is a plain sign, its PR number kept and no status in colour or words.
        var color = !MapLayers.ShowPrStates ? P.RouteAhead : d.Status switch
        {
            DestinationStatus.Merged => Landed,
            DestinationStatus.Conflicts or DestinationStatus.Changes => Failed,
            DestinationStatus.NoPr => P.RouteAhead,
            DestinationStatus.Passed or DestinationStatus.Queued or DestinationStatus.Armed => Flying,
            _ => Held,
        };
        FillRect(X(d.X) - r, Y(d.Y) - r, 2f * r, 2f * r, color, 2f * s);
        if (d.Status == DestinationStatus.NoPr && MapLayers.ShowPrStates)
        {
            FillRect(X(d.X) - r + 2f * s, Y(d.Y) - r + 2f * s, 2f * r - 4f * s, 2f * r - 4f * s, Ground, 1f * s);
        }
        Label(SignOf(line?.Row.Repo ?? "", d.PullRequest), X(d.X - FlightMap.Slot / 2f), Y(d.Y + FlightMap.LabelBandTop), FlightMap.Slot * s, s, LabelSize, Body, line: StopLine);
        if (MapLayers.ShowPrStates)
        {
            Label(StatusWord(d.Status), X(d.X - FlightMap.Slot / 2f), Y(d.Y + FlightMap.LabelLineTwo), FlightMap.Slot * s, s, LabelSize, color, line: StopLine);
        }
    }

    private FlightLine? _lineOf(MapRoute route) =>
        _screen.Lines.FirstOrDefault(l => l.Code == route.Flight && (l.Node?.Key ?? "") == route.Node);

    private void DrawPlane(MapRoute route, MapPlane p, float s, Func<float, float> X, Func<float, float> Y)
    {
        var held = route.Gate is not null && p.Leg is null;
        var color = held ? Held : Flying;
        var (cx, cy, r) = (X(p.X), Y(p.Y), FlightMap.PlaneRadius * s);
        var thick = Math.Max(2, (int)MathF.Round(2f * s));
        // A ring around the stop, its stroke inside the plane's radius (the renderer centres a stroke on the radius it is given),
        // and a plane in it: fuselage, wing, tail.
        var ring = r - thick / 2;
        Renderer.DrawEllipse(new RectInt(new PointInt((int)(cx + ring), (int)(cy + ring)), new PointInt((int)(cx - ring), (int)(cy - ring))), color, thick);
        Renderer.DrawLine(cx - r * 0.55f, cy, cx + r * 0.6f, cy, Bright, thick);
        Renderer.DrawLine(cx - r * 0.1f, cy, cx - r * 0.45f, cy - r * 0.5f, Bright, thick);
        Renderer.DrawLine(cx - r * 0.1f, cy, cx - r * 0.45f, cy + r * 0.5f, Bright, thick);
    }

    private void DrawConnection(MapConnection c, int index, float s, Func<float, float> X, Func<float, float> Y, int thin)
    {
        var lit = InChain(c);
        var color = c.Held ? Held : lit ? Bright : Dim;
        var thick = lit ? Math.Max(2, (int)MathF.Round(3f * s)) : thin;
        var (x0, y0, x1, y1) = (X(c.X0), Y(c.Y0), X(c.X1), Y(c.Y1));
        float tx, ty;
        if (c.Domestic)
        {
            // Out of the destination's side, down the corridor between rows, along the margin and into the entry from the left:
            // dashed runs that stay off every label.
            for (var i = 1; i < c.Path.Count; i++)
            {
                DrawDashedRun(X(c.Path[i - 1].X), Y(c.Path[i - 1].Y), X(c.Path[i].X), Y(c.Path[i].Y), color, thick, ArrowDashes.Phase(index), DashLength * s, GapLength * s);
            }
            (x1, y1) = (X(c.Path[^1].X), Y(c.Path[^1].Y));
            (tx, ty) = (X(c.Path[^2].X), Y(c.Path[^2].Y));
        }
        else
        {
            // An intercontinental arc: a curve bowed up out of the land, drawn as short strokes, dashed while it holds.
            var mx = (c.X0 + c.X1) / 2f;
            var cy = MathF.Max(4f, MathF.Min(c.Y0, c.Y1) - 0.12f * MathF.Abs(c.X1 - c.X0) - 24f);
            var (px, py) = (x0, y0);
            (tx, ty) = (x0, y0);
            const int steps = 28;
            for (var i = 1; i <= steps; i++)
            {
                var t = i / (float)steps;
                var u = 1f - t;
                var nx = X(u * u * c.X0 + 2f * u * t * mx + t * t * c.X1);
                var ny = Y(u * u * c.Y0 + 2f * u * t * cy + t * t * c.Y1);
                if (!c.Held || i % 3 != 0)
                {
                    Renderer.DrawLine(px, py, nx, ny, color, thick);
                }
                (tx, ty) = (px, py);
                (px, py) = (nx, ny);
            }
        }
        var head = ArrowHead * s;
        var angle = MathF.Atan2(y1 - ty, x1 - tx);
        foreach (var spread in new[] { 0.45f, -0.45f })
        {
            Renderer.DrawLine(x1, y1, x1 - head * MathF.Cos(angle - spread), y1 - head * MathF.Sin(angle - spread), color, thick);
        }
    }
}
