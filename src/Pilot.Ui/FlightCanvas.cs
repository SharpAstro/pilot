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

/// <summary>A connection: <paramref name="From"/> is waited for, <paramref name="To"/> is held by it. Points are canvas design units; <paramref name="ToCard"/> is the held card's position in the canvas's cards.</summary>
internal readonly record struct CanvasEdge(string From, string To, float X0, float Y0, float X1, float Y1, int ToCard);

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

    /// <summary>
    /// Every flight <paramref name="code"/> waits on or is waited on by, through any number of arrows, itself among them
    /// when it has any: what the canvas lights when its card is picked.
    /// </summary>
    public IReadOnlySet<string> Chain(string code)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal) { code };
        var next = new Queue<string>([code]);
        while (next.TryDequeue(out var at))
        {
            foreach (var edge in Edges)
            {
                if ((edge.From == at ? edge.To : edge.To == at ? edge.From : null) is { } other && chain.Add(other))
                {
                    next.Enqueue(other);
                }
            }
        }
        return chain.Count > 1 ? chain : new HashSet<string>();
    }

    public CanvasCard? Find(string code) => Cards.Cast<CanvasCard?>().FirstOrDefault(c => c!.Value.Code == code);

    /// <summary>The card at a canvas point, or null.</summary>
    public CanvasCard? At(float x, float y) =>
        Cards.Cast<CanvasCard?>().FirstOrDefault(c => x >= c!.Value.X && x < c.Value.Right && y >= c.Value.Y && y < c.Value.Bottom);

    public static FlightCanvas Build(IReadOnlyList<FlightLine> lines)
    {
        // Cards are keyed by position: codes are unique per machine only, so a merged page can hold one code twice.
        var byCode = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!byCode.TryGetValue(lines[i].Code, out var same))
            {
                byCode[lines[i].Code] = same = [];
            }
            same.Add(i);
        }

        // Ghosts: connected codes the page does not have, in order of first mention, one per code.
        var ghostCodes = new List<string>();
        foreach (var line in lines)
        {
            foreach (var code in line.Row.Connections)
            {
                if (!byCode.ContainsKey(code) && !ghostCodes.Contains(code))
                {
                    ghostCodes.Add(code);
                }
            }
        }
        var ghostId = ghostCodes.Select((c, g) => (c, g)).ToDictionary(x => x.c, x => lines.Count + x.g, StringComparer.Ordinal);

        // A connection resolves to the flight of that code on the holder's own node, else the page's first one, else its ghost.
        int Resolve(int holder, string code)
        {
            if (!byCode.TryGetValue(code, out var candidates))
            {
                return ghostId[code];
            }
            var node = lines[holder].Node?.Key;
            foreach (var c in candidates)
            {
                if (lines[c].Node?.Key == node)
                {
                    return c;
                }
            }
            return candidates[0];
        }

        // Depth: one more than the deepest flight waited for. A connection met again while its flight is still being
        // resolved closes a cycle; it is dropped, so the walk ends and no arrow points back.
        var depth = new Dictionary<int, int>();
        var visiting = new HashSet<int>();
        var broken = new List<(string, string)>();
        var kept = new HashSet<(int, int)>();

        int Depth(int id)
        {
            if (depth.TryGetValue(id, out var known))
            {
                return known;
            }
            visiting.Add(id);
            var d = 0;
            if (id < lines.Count)
            {
                foreach (var code in lines[id].Row.Connections.Distinct())
                {
                    var waited = Resolve(id, code);
                    if (waited == id || visiting.Contains(waited))
                    {
                        broken.Add((code, lines[id].Code));
                        continue;
                    }
                    kept.Add((waited, id));
                    d = Math.Max(d, Depth(waited) + 1);
                }
            }
            visiting.Remove(id);
            depth[id] = d;
            return d;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            Depth(i);
        }

        // Stable within a column: the page's own order, then ghosts in the order they were met.
        var order = lines.Select((l, i) => (l.Code, Line: (FlightLine?)l, Index: i))
            .Concat(ghostCodes.Select(g => (Code: g, Line: (FlightLine?)null, Index: -1)))
            .Select((x, id) => (x.Code, x.Line, x.Index, Column: depth.GetValueOrDefault(id)))
            .ToList();
        var rows = new Dictionary<int, int>();
        var cards = new List<CanvasCard>();
        foreach (var (code, line, i, column) in order)
        {
            var row = rows.GetValueOrDefault(column);
            rows[column] = row + 1;
            cards.Add(new CanvasCard(code, line, i, column, row, Margin + column * (CardWidth + GapX), Margin + row * (CardHeight + GapY)));
        }

        var edges = new List<CanvasEdge>();
        foreach (var (from, to) in kept.OrderBy(k => k.Item2).ThenBy(k => k.Item1))
        {
            var a = cards[from];
            var b = cards[to];
            edges.Add(new CanvasEdge(a.Code, b.Code, a.Right, a.Y + CardHeight / 2f, b.X, b.Y + CardHeight / 2f, to));
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
