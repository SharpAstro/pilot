namespace Pilot.Ui;

/// <summary>One card on the canvas: a flight, or a ghost standing in for a connected flight that is not on the page.</summary>
/// <param name="Code">The flight's code.</param>
/// <param name="Line">The flight's line; null for a ghost.</param>
/// <param name="Index">The line's index in the page's lines, or -1 for a ghost.</param>
/// <param name="Column">Its depth: 0 waits for nothing on the page.</param>
/// <param name="Row">Its place in its column, top down.</param>
/// <param name="X">Left edge, in design units from the canvas's corner.</param>
/// <param name="Y">Top edge.</param>
internal readonly record struct CanvasCard(string Code, FlightLine? Line, int Index, int Column, int Row, float X, float Y)
{
    public bool IsGhost => Line is null;

    public float Right => X + FlightCanvas.CardWidth;

    public float Bottom => Y + FlightCanvas.CardHeight;
}

/// <summary>A connection: <paramref name="From"/> is waited for, <paramref name="To"/> is held by it. Points are canvas design units.</summary>
internal readonly record struct CanvasEdge(string From, string To, float X0, float Y0, float X1, float Y1);

/// <summary>
/// The Flights page's canvas as pure geometry: a card per flight in a column by connection depth, so every arrow points
/// rightwards, and an arrow per connection. No renderer is needed to build or test it.
/// </summary>
internal sealed class FlightCanvas
{
    public const float CardWidth = 232f;
    public const float CardHeight = 112f;
    /// <summary>The grid's card: one line taller, for what a held flight waits for.</summary>
    public const float GridCardHeight = 130f;
    public const float GridGap = 36f;
    public const float GapX = 72f;
    public const float GapY = 20f;
    public const float Margin = 16f;

    public IReadOnlyList<CanvasCard> Cards { get; private init; } = [];

    public IReadOnlyList<CanvasEdge> Edges { get; private init; } = [];

    /// <summary>Connections left out because they close a cycle: they would point leftwards.</summary>
    public IReadOnlyList<(string From, string To)> Broken { get; private init; } = [];

    public float Width { get; private init; }

    public float Height { get; private init; }

    public CanvasCard? Find(string code) => Cards.Cast<CanvasCard?>().FirstOrDefault(c => c!.Value.Code == code);

    /// <summary>The card at a canvas point, or null.</summary>
    public CanvasCard? At(float x, float y) =>
        Cards.Cast<CanvasCard?>().FirstOrDefault(c => x >= c!.Value.X && x < c.Value.Right && y >= c.Value.Y && y < c.Value.Bottom);

    public static FlightCanvas Build(IReadOnlyList<FlightLine> lines)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            index.TryAdd(lines[i].Code, i);
        }

        // Ghosts: connected codes the page does not have, in order of first mention.
        var ghosts = new List<string>();
        foreach (var line in lines)
        {
            foreach (var code in line.Row.Connections)
            {
                if (!index.ContainsKey(code) && !ghosts.Contains(code))
                {
                    ghosts.Add(code);
                }
            }
        }

        // Depth: one more than the deepest flight waited for. A connection met again while its flight is still being
        // resolved closes a cycle; it is dropped, so the walk ends and no arrow points back.
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var broken = new List<(string, string)>();
        var kept = new HashSet<(string, string)>();

        int Depth(string code)
        {
            if (depth.TryGetValue(code, out var known))
            {
                return known;
            }
            visiting.Add(code);
            var d = 0;
            if (index.TryGetValue(code, out var at))
            {
                foreach (var waited in lines[at].Row.Connections.Distinct())
                {
                    if (waited == code || visiting.Contains(waited))
                    {
                        broken.Add((waited, code));
                        continue;
                    }
                    kept.Add((waited, code));
                    d = Math.Max(d, Depth(waited) + 1);
                }
            }
            visiting.Remove(code);
            depth[code] = d;
            return d;
        }

        foreach (var line in lines)
        {
            Depth(line.Code);
        }

        // Stable within a column: the page's own order, then ghosts in the order they were met.
        var order = lines.Select((l, i) => (l.Code, Line: (FlightLine?)l, Index: i))
            .Concat(ghosts.Select(g => (Code: g, Line: (FlightLine?)null, Index: -1)))
            .Select(x => (x.Code, x.Line, x.Index, Column: depth.GetValueOrDefault(x.Code)))
            .ToList();
        var rows = new Dictionary<int, int>();
        var cards = new List<CanvasCard>();
        foreach (var (code, line, i, column) in order)
        {
            var row = rows.GetValueOrDefault(column);
            rows[column] = row + 1;
            cards.Add(new CanvasCard(code, line, i, column, row, Margin + column * (CardWidth + GapX), Margin + row * (CardHeight + GapY)));
        }

        var byCode = cards.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var edges = new List<CanvasEdge>();
        foreach (var (from, to) in kept.OrderBy(k => byCode[k.Item2].Index < 0 ? int.MaxValue : byCode[k.Item2].Index).ThenBy(k => k.Item1, StringComparer.Ordinal))
        {
            if (byCode.TryGetValue(from, out var a) && byCode.TryGetValue(to, out var b))
            {
                edges.Add(new CanvasEdge(from, to, a.Right, a.Y + CardHeight / 2f, b.X, b.Y + CardHeight / 2f));
            }
        }

        var columns = cards.Count == 0 ? 0 : cards.Max(c => c.Column) + 1;
        var tallest = rows.Count == 0 ? 0 : rows.Values.Max();
        return new FlightCanvas
        {
            Cards = cards,
            Edges = edges,
            Broken = broken,
            Width = columns == 0 ? 0f : Margin * 2 + columns * CardWidth + (columns - 1) * GapX,
            Height = tallest == 0 ? 0f : Margin * 2 + tallest * CardHeight + (tallest - 1) * GapY,
        };
    }
}
