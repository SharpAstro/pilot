namespace Pilot.Ui;

/// <summary>
/// Where the captions and labels of a Settings range row (#770) go, worked out from the width the row's line of pairs is given so no two
/// text runs overlap: the pure part, with no nodes, so a test can drive it with any measure. Columns are the line's pairs; a caption
/// is placed over a run of columns that holds its handle and is wide enough for its text.
/// </summary>
internal static class RangeCaptions
{
    /// <summary>One text run over columns <see cref="From"/>..<see cref="To"/> (inclusive) on caption line <see cref="Line"/>.</summary>
    internal readonly record struct Caption(string Text, int From, int To, int Line);

    /// <summary>The width of one column when <paramref name="columns"/> of them and their gaps fill <paramref name="width"/>.</summary>
    internal static float Column(int columns, float width, float gap) => Math.Max(1f, (width - gap * (columns - 1)) / columns);

    /// <summary>The columns' left edge, in the line's units.</summary>
    internal static float LeftOf(int column, float cell, float gap) => column * (cell + gap);

    private static int Span(float needed, int columns, float cell, float gap)
    {
        var k = 1;
        while (k < columns && k * cell + (k - 1) * gap < needed)
        {
            k++;
        }
        return k;
    }

    private static int Centred(int handle, int span, int columns) => Math.Clamp(handle - (span - 1) / 2, 0, columns - span);

    /// <summary>
    /// The captions of a range with its handles on columns <paramref name="def"/> and <paramref name="max"/>: one 'default = max' when both
    /// are on one stop; else a caption per handle over the columns around it, the two shifted apart when they meet and stacked on a second
    /// caption line when they cannot sit side by side. <paramref name="textWidth"/> is a text's width in the line's units.
    /// </summary>
    internal static IReadOnlyList<Caption> Place(int columns, int def, int max, float width, float gap, float margin, Func<string, float> textWidth)
    {
        var cell = Column(columns, width, gap);
        if (def < 0 || max < 0 || columns <= 0)
        {
            return [];
        }
        if (def == max)
        {
            var span = Span(textWidth("default = max") + margin, columns, cell, gap);
            var at = Centred(def, span, columns);
            return [new("default = max", at, at + span - 1, 0)];
        }
        var kd = Span(textWidth("default") + margin, columns, cell, gap);
        var km = Span(textWidth("max") + margin, columns, cell, gap);
        var cd = Centred(def, kd, columns);
        var cm = Centred(max, km, columns);
        if (cd + kd - 1 < cm)
        {
            return [new("default", cd, cd + kd - 1, 0), new("max", cm, cm + km - 1, 0)];
        }
        // They meet: look for the placements, each still holding its handle, that clear each other with the least move from centred.
        (int D, int M)? best = null;
        var bestMove = int.MaxValue;
        for (var d = Math.Max(0, def - kd + 1); d <= Math.Min(def, columns - kd); d++)
        {
            for (var m = Math.Max(0, max - km + 1); m <= Math.Min(max, columns - km); m++)
            {
                var move = Math.Abs(d - cd) + Math.Abs(m - cm);
                if (d + kd - 1 < m && move < bestMove)
                {
                    best = (d, m);
                    bestMove = move;
                }
            }
        }
        return best is { } b
            ? [new("default", b.D, b.D + kd - 1, 0), new("max", b.M, b.M + km - 1, 0)]
            : [new("default", cd, cd + kd - 1, 0), new("max", cm, cm + km - 1, 1)];
    }

    /// <summary>The shortest form of the pair labels that fits a cell: 0 is 'S high', 1 the effort alone, 2 its first letter; the model then shows once per group.</summary>
    internal static int LabelForm(IReadOnlyList<string> pairs, float cell, float margin, Func<string, float> textWidth)
    {
        for (var form = 0; form < 2; form++)
        {
            if (pairs.All(p => textWidth(Label(p, form)) + margin <= cell))
            {
                return form;
            }
        }
        return 2;
    }

    /// <summary>One pair's label in <paramref name="form"/> (see <see cref="LabelForm"/>).</summary>
    internal static string Label(string pair, int form)
    {
        var cut = pair.IndexOf('-');
        var model = cut < 0 ? pair : pair[..cut];
        var effort = cut < 0 ? "" : pair[(cut + 1)..];
        var initial = model.Length > 0 ? char.ToUpperInvariant(model[0]).ToString() : "?";
        return form switch
        {
            0 => $"{initial} {(effort == "medium" ? "med" : effort)}".TrimEnd(),
            1 => effort.Length == 0 ? initial : effort == "medium" ? "med" : effort,
            _ => effort.Length == 0 ? initial : effort[..1],
        };
    }

    /// <summary>The runs of columns that share a model, in order, with the model's name.</summary>
    internal static IReadOnlyList<(string Model, int From, int To)> Groups(IReadOnlyList<string> pairs)
    {
        var groups = new List<(string, int, int)>();
        for (var i = 0; i < pairs.Count; i++)
        {
            var cut = pairs[i].IndexOf('-');
            var model = cut < 0 ? pairs[i] : pairs[i][..cut];
            if (groups.Count > 0 && groups[^1].Item1 == model)
            {
                groups[^1] = (model, groups[^1].Item2, i);
            }
            else
            {
                groups.Add((model, i, i));
            }
        }
        return groups;
    }

    /// <summary>The widths, in the line's units, of the spacers and runs along a caption line: left to right, a gap (with a null text) or a run.</summary>
    internal static IReadOnlyList<(string? Text, float Width)> Segments(IEnumerable<(string Text, int From, int To)> runs, int columns, float width, float gap)
    {
        var cell = Column(columns, width, gap);
        float EdgeLeft(int c) => c == 0 ? 0f : LeftOf(c, cell, gap) - gap / 2f;
        float EdgeRight(int c) => c >= columns - 1 ? width : LeftOf(c + 1, cell, gap) - gap / 2f;
        var segments = new List<(string?, float)>();
        var at = 0f;
        foreach (var (text, from, to) in runs.OrderBy(r => r.From))
        {
            var left = EdgeLeft(from);
            if (left - at > 0.5f)
            {
                segments.Add((null, left - at));
            }
            segments.Add((text, EdgeRight(to) - left));
            at = EdgeRight(to);
        }
        if (width - at > 0.5f)
        {
            segments.Add((null, width - at));
        }
        return segments;
    }
}
