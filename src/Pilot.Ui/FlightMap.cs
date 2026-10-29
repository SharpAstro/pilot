using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>Where a leg is on the map: its airport's state.</summary>
internal enum AirportState { Done, Flying, Planned, Failed }

/// <summary>Where a flight's PR is, as its destination airport says it.</summary>
internal enum DestinationStatus
{
    /// <summary>No PR yet: the legs are not all in.</summary>
    NoPr,
    Open,
    Draft,
    Reviewing,
    Passed,
    Changes,
    /// <summary>A merge asked for waits for the PR's checks (<see cref="FlightRow.MergeWhenGreen"/>).</summary>
    Armed,
    Queued,
    Conflicts,
    /// <summary>Merged: the flight has landed.</summary>
    Merged,
}

/// <summary>What a map point is: the plane, an airport, a destination or a gate.</summary>
internal enum MapHitKind { Plane, Airport, Destination, Gate }

/// <summary>A node's continent: a box in design units holding its flights' routes. Greyed while the node is unreachable.</summary>
internal sealed record MapContinent(string Key, string Name, bool Here, bool Unreachable, float X, float Y, float Width, float Height, IReadOnlyList<string> Flights);

/// <summary>A leg as an airport on its flight's route.</summary>
internal sealed record MapAirport(string Flight, string Leg, int Order, AirportState State, float X, float Y);

/// <summary>The PR as the route's last airport.</summary>
internal sealed record MapDestination(string Flight, int? PullRequest, DestinationStatus Status, float X, float Y);

/// <summary>Where a held flight is parked, and what it waits for.</summary>
internal sealed record MapGate(string Flight, string WaitsFor, float X, float Y);

/// <summary>
/// The plane: at the flying leg's airport, at the gate of a held flight, at the destination once every leg is done and the PR is
/// not merged yet. <see cref="Leg"/> names the leg when at an airport; null elsewhere. A landed flight has none.
/// </summary>
internal sealed record MapPlane(string Flight, string? Leg, float X, float Y);

/// <summary>A flight's route: its gate (held only), airports in leg order, then its destination.</summary>
internal sealed record MapRoute(string Flight, string Node, MapGate? Gate, IReadOnlyList<MapAirport> Airports, MapDestination Destination, MapPlane? Plane)
{
    /// <summary>
    /// Whether the plane is parked on the destination (every leg done, the PR not merged): there the destination answers a point,
    /// its click opening the PR, and the plane is only drawn.
    /// </summary>
    public bool PlaneAtDestination => Plane is { Leg: null } && Gate is null;
}

/// <summary>
/// A connection from the flight waited for (<paramref name="From"/> on <paramref name="FromNode"/>, its destination) to the one it
/// holds (<paramref name="To"/> on <paramref name="ToNode"/>, its gate or first airport). Domestic when both are on one node, else an
/// intercontinental arc; <paramref name="Held"/> while it still holds, which draws it dashed. A code is unique on its node only, so
/// each end carries its node.
/// </summary>
internal sealed record MapConnection(string FromNode, string From, string ToNode, string To, bool Domestic, bool Held, float X0, float Y0, float X1, float Y1)
{
    /// <summary>
    /// A domestic connection's route as axis-aligned stops, out of the destination's right side, down to the corridor between two
    /// rows, along it to the continent's left margin, then to the entry's row and in from the left: it never crosses a label.
    /// Empty for an intercontinental arc, which is drawn as a curve.
    /// </summary>
    public IReadOnlyList<(float X, float Y)> Path { get; init; } = [];
}

/// <summary>What a point on the map is: the flight by its code and its node's key.</summary>
internal readonly record struct MapHit(MapHitKind Kind, string Flight, string? Leg = null, int? PullRequest = null, string Node = "");

/// <summary>
/// The Flights page as a map in pure geometry (design units, no renderer): a continent per node, a route per flight, arcs between
/// continents. Laid out by node and flight identity (not by state or refresh order), so a refresh moves nothing but what changed.
/// </summary>
internal sealed class FlightMap
{
    public const float Slot = 72f;
    public const float RowHeight = 72f;
    /// <summary>
    /// A row's label band under its airport: two text lines in boxes starting 12 and 23 down, 12.6 high. The next row's flight code
    /// starts 45 down: the corridor connections run in lies between, 40 down.
    /// </summary>
    public const float LabelBandTop = 12f;
    /// <summary>The second line's box under a stop: its state, or what a gate waits for.</summary>
    public const float LabelLineTwo = 23f;
    public const float LabelBandBottom = 36f;
    public const float CorridorOffset = 40f;
    /// <summary>Half the width of an airport's label: a leg name cut to ten characters at 9 units.</summary>
    public const float LabelHalfWidth = 26f;
    /// <summary>The flight code's box above a route's first stop: left aligned at the slot's left edge.</summary>
    public const float CodeWidth = 44f;
    public const float CodeTop = -27f;
    public const float CodeBottom = -11f;
    public const float Header = 32f;
    public const float Pad = 24f;
    public const float ContinentGap = 96f;
    public const float AirportRadius = 10f;
    /// <summary>
    /// The plane's ring, its stroke inside it: no further out than the label band under its stop and the flight code over it, so it
    /// never cuts their words (at 14 it ran into both).
    /// </summary>
    public const float PlaneRadius = 11f;

    public IReadOnlyList<MapContinent> Continents { get; private init; } = [];

    public IReadOnlyList<MapRoute> Routes { get; private init; } = [];

    public IReadOnlyList<MapConnection> Connections { get; private init; } = [];

    /// <summary>Connections within one node.</summary>
    public IEnumerable<MapConnection> Domestic => Connections.Where(c => c.Domestic);

    /// <summary>Connections between nodes.</summary>
    public IEnumerable<MapConnection> Arcs => Connections.Where(c => !c.Domestic);

    public float Width { get; private init; }

    public float Height { get; private init; }

    /// <summary>
    /// Every flight <paramref name="code"/> on <paramref name="node"/> waits on or is waited on by, through any number of
    /// connections, itself among them when it has any: what the map lights when a flight is picked. Keyed by (node, code), as one
    /// code on two nodes is two flights.
    /// </summary>
    public IReadOnlySet<(string Node, string Code)> Chain(string node, string code)
    {
        var chain = new HashSet<(string Node, string Code)> { (node, code) };
        var next = new Queue<(string Node, string Code)>([(node, code)]);
        while (next.TryDequeue(out var at))
        {
            foreach (var c in Connections)
            {
                var (from, to) = ((c.FromNode, c.From), (c.ToNode, c.To));
                if ((from == at ? to : to == at ? from : ((string, string)?)null) is { } other && chain.Add(other))
                {
                    next.Enqueue(other);
                }
            }
        }
        return chain.Count > 1 ? chain : new HashSet<(string Node, string Code)>();
    }

    /// <summary>The route of <paramref name="flight"/>, on <paramref name="node"/> when given, else the first of that code.</summary>
    public MapRoute? Route(string flight, string? node = null) => Routes.FirstOrDefault(r => r.Flight == flight && (node is null || r.Node == node));

    /// <summary>
    /// The destination's status from what the row carries. Not told by the row: a PR's failing checks (only a merge waiting for
    /// them is), and conflicts before the node's look has set the label; those read as the state before them.
    /// </summary>
    public static DestinationStatus StatusOf(FlightRow flight)
    {
        var pr = FlightMarks.PrState(flight);
        if (pr == "merged")
        {
            return DestinationStatus.Merged;
        }
        if (pr == "queued")
        {
            return DestinationStatus.Queued;
        }
        if (pr is null)
        {
            return DestinationStatus.NoPr;
        }
        if (flight.Review?.LabelOverride?.Contains("conflicts", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DestinationStatus.Conflicts;
        }
        if (flight.MergeWhenGreen is { Length: > 0 })
        {
            return DestinationStatus.Armed;
        }
        return flight.Review?.State switch
        {
            ReviewState.Reviewing => DestinationStatus.Reviewing,
            ReviewState.Passed => DestinationStatus.Passed,
            ReviewState.Changes => DestinationStatus.Changes,
            _ => pr == "draft" ? DestinationStatus.Draft : DestinationStatus.Open,
        };
    }

    /// <summary>
    /// The map of <paramref name="lines"/> as <paramref name="layers"/> leave it: a layer turned off drops its flights before the
    /// layout, so its routes, airports, gates and hit regions are gone, not hidden. Null shows everything.
    /// </summary>
    public static FlightMap Build(IReadOnlyList<FlightLine> lines, FlightMapLayerState? layers = null)
    {
        layers ??= new FlightMapLayerState();
        var shown = lines
            .Where(l => layers.ShowLanded || l.Row.State != FlightState.Landed)
            .Where(l => layers.ShowHeld || l.Row.State != FlightState.Held)
            .Where(l => layers.OnlyNode is null || (l.Node?.Key ?? "") == layers.OnlyNode)
            .ToList();
        var map = LayMap(shown);
        if (!layers.ArcsOnly)
        {
            return map;
        }
        // Only the flights an arc joins stay, each by its node and code, and the arcs alone are drawn between them.
        var ends = map.Arcs.SelectMany(c => new[] { (c.FromNode, c.From), (c.ToNode, c.To) }).ToHashSet();
        var arcs = LayMap([.. shown.Where(l => ends.Contains((l.Node?.Key ?? "", l.Code)))]);
        return new FlightMap
        {
            Continents = arcs.Continents,
            Routes = arcs.Routes,
            Connections = [.. arcs.Connections.Where(c => !c.Domestic)],
            Width = arcs.Width,
            Height = arcs.Height,
        };
    }

    private static FlightMap LayMap(IReadOnlyList<FlightLine> lines)
    {
        static string KeyOf(FlightLine l) => l.Node?.Key ?? "";

        var nodes = lines.GroupBy(KeyOf, StringComparer.Ordinal)
            .OrderByDescending(g => g.First().Node?.Here ?? true)
            .ThenBy(g => g.First().Node?.Name ?? "", StringComparer.Ordinal)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var continents = new List<MapContinent>();
        var routes = new List<MapRoute>();
        var x = 0f;
        var height = 0f;
        foreach (var node in nodes)
        {
            var flights = node.OrderBy(l => l.Row.CreatedAt).ThenBy(l => l.Code, StringComparer.Ordinal).ToList();
            var slots = flights.Max(l => SlotsOf(l));
            var width = Pad * 2 + slots * Slot;
            var h = Header + Pad + flights.Count * RowHeight;
            var mark = node.First().Node;
            for (var r = 0; r < flights.Count; r++)
            {
                routes.Add(Lay(flights[r], KeyOf(flights[r]), x + Pad, Header + Pad / 2 + r * RowHeight + RowHeight / 2));
            }
            continents.Add(new MapContinent(node.Key, mark?.Name ?? "this node", mark?.Here ?? true, node.Any(l => l.Stale is not null), x, 0f, width, h, [.. flights.Select(l => l.Code)]));
            x += width + ContinentGap;
            height = Math.Max(height, h);
        }

        return new FlightMap
        {
            Continents = continents,
            Routes = routes,
            Connections = Connect(lines, routes),
            Width = continents.Count == 0 ? 0f : x - ContinentGap,
            Height = height,
        };
    }

    private static int SlotsOf(FlightLine l) => (l.Row.State == FlightState.Held ? 1 : 0) + l.LegRows.Count + 1;

    private static MapRoute Lay(FlightLine line, string node, float x0, float y)
    {
        var held = line.Row.State == FlightState.Held;
        var at = x0 + Slot / 2;
        MapGate? gate = null;
        if (held)
        {
            gate = new MapGate(line.Code, FlightWaiting.For(line.Row), at, y);
            at += Slot;
        }
        var airports = new List<MapAirport>();
        foreach (var leg in line.LegRows.OrderBy(l => l.Order))
        {
            var state = leg.State switch { LegState.Done => AirportState.Done, LegState.Flying => AirportState.Flying, LegState.Failed => AirportState.Failed, _ => AirportState.Planned };
            airports.Add(new MapAirport(line.Code, leg.Name, leg.Order, state, at, y));
            at += Slot;
        }
        var status = StatusOf(line.Row);
        var destination = new MapDestination(line.Code, line.Row.PullRequest, status, at, y);

        MapPlane? plane = null;
        if (status != DestinationStatus.Merged && line.Row.State != FlightState.Landed)
        {
            if (gate is not null)
            {
                plane = new MapPlane(line.Code, null, gate.X, gate.Y);
            }
            else if (airports.FirstOrDefault(a => a.State == AirportState.Flying) is { } flying)
            {
                plane = new MapPlane(line.Code, flying.Leg, flying.X, flying.Y);
            }
            else if (airports.All(a => a.State == AirportState.Done))
            {
                plane = new MapPlane(line.Code, null, destination.X, destination.Y);
            }
            else if (airports.FirstOrDefault(a => a.State is AirportState.Planned or AirportState.Failed) is { } next)
            {
                plane = new MapPlane(line.Code, next.Leg, next.X, next.Y);
            }
        }
        return new MapRoute(line.Code, node, gate, airports, destination, plane);
    }

    private static List<MapConnection> Connect(IReadOnlyList<FlightLine> lines, List<MapRoute> routes)
    {
        var byCode = lines.Select((l, i) => (l, i)).ToLookup(t => t.l.Code, StringComparer.Ordinal);
        var routeOf = routes.ToDictionary(r => (r.Flight, r.Node));
        var seen = new HashSet<(string, string, string)>();
        var result = new List<MapConnection>();

        void Add(FlightLine holder, string code, bool holds)
        {
            // The flight waited for on the holder's own node, else the first of that code.
            var node = holder.Node?.Key ?? "";
            var from = byCode[code].Select(t => t.l).FirstOrDefault(l => (l.Node?.Key ?? "") == node) ?? byCode[code].Select(t => t.l).FirstOrDefault();
            if (from is null || ReferenceEquals(from, holder) || !seen.Add((from.Node?.Key ?? "", from.Code, holder.Code + "@" + node)))
            {
                return;
            }
            var a = routeOf[(from.Code, from.Node?.Key ?? "")];
            var b = routeOf[(holder.Code, node)];
            var entry = b.Gate is { } g ? (g.X, g.Y) : b.Airports.Count > 0 ? (b.Airports[0].X, b.Airports[0].Y) : (b.Destination.X, b.Destination.Y);
            var domestic = a.Node == b.Node;
            IReadOnlyList<(float X, float Y)> path = [];
            if (domestic)
            {
                var (dx, dy) = (a.Destination.X, a.Destination.Y);
                var corridor = dy + CorridorOffset;
                var margin = entry.Item1 - Slot / 2f - 6f - 4f * (result.Count % 3);
                path =
                [
                    (dx + AirportRadius, dy), (dx + 30f, dy), (dx + 30f, corridor), (margin, corridor),
                    (margin, entry.Item2), (entry.Item1 - AirportRadius, entry.Item2),
                ];
            }
            result.Add(new MapConnection(a.Node, from.Code, b.Node, holder.Code, domestic, holds && holder.Row.State == FlightState.Held, a.Destination.X, a.Destination.Y, entry.Item1, entry.Item2) { Path = path });
        }

        foreach (var line in lines.OrderBy(l => l.Node?.Key ?? "", StringComparer.Ordinal).ThenBy(l => l.Code, StringComparer.Ordinal))
        {
            foreach (var code in line.Row.Connections.Distinct())
            {
                var waited = byCode[code].Select(t => t.l).FirstOrDefault(l => (l.Node?.Key ?? "") == (line.Node?.Key ?? "")) ?? byCode[code].Select(t => t.l).FirstOrDefault();
                var holds = waited is not null && (waited.Row.State != FlightState.Landed || line.Row.PackageWaits.Any(w => w.Producer == code));
                Add(line, code, holds);
            }
            // A flight folded into this one is a connection that has landed with it, never held.
            foreach (var folded in line.Row.FoldedIn)
            {
                Add(line, folded.From, false);
            }
        }
        return result;
    }

    /// <summary>
    /// What is at a design-unit point: the plane first (it is drawn over its airport or gate), then a gate, an airport, a
    /// destination; null on empty sea. A plane parked on its destination is the destination's, out to the plane's ring, so the
    /// point opens the PR as the sign does.
    /// </summary>
    public MapHit? At(float x, float y)
    {
        static bool Near(float px, float py, float cx, float cy, float r) => (px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r;

        foreach (var route in Routes)
        {
            if (route.Plane is { } p && !route.PlaneAtDestination && Near(x, y, p.X, p.Y, PlaneRadius))
            {
                return new MapHit(MapHitKind.Plane, route.Flight, p.Leg, Node: route.Node);
            }
        }
        foreach (var route in Routes)
        {
            if (route.Gate is { } g && Near(x, y, g.X, g.Y, AirportRadius))
            {
                return new MapHit(MapHitKind.Gate, route.Flight, Node: route.Node);
            }
            foreach (var a in route.Airports)
            {
                if (Near(x, y, a.X, a.Y, AirportRadius))
                {
                    return new MapHit(MapHitKind.Airport, route.Flight, a.Leg, Node: route.Node);
                }
            }
            if (Near(x, y, route.Destination.X, route.Destination.Y, route.PlaneAtDestination ? PlaneRadius : AirportRadius))
            {
                return new MapHit(MapHitKind.Destination, route.Flight, PullRequest: route.Destination.PullRequest, Node: route.Node);
            }
        }
        return null;
    }
}
