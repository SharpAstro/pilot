using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using DIR.Lib.Layout;
using DIR.Lib.Prototyping;

namespace Pilot.Cells;

/// <summary>
/// A prototype's screen arranged and painted in terminal cells (PIL1098), the one piece the node's check and render, the GUI's grid and pilot-tui share.
/// It builds the screen's tree with DIR.Lib's <see cref="PrototypeTreeBuilder"/>, arranges it in <c>cols x rows</c> cells as pilot-tui arranges its pages
/// (Console.Lib's <see cref="CellMeasureContext.PixelAuthored"/>: an authored pixel is an eighth of a cell wide and a sixteenth tall, so a cell is 1:2),
/// and paints it into a <see cref="CellBuffer"/> through an in-memory viewport. This file names nothing of Pilot.Ui, so projects that cannot reference it
/// (the node) link the file itself.
/// </summary>
public sealed class PrototypeCells
{
    /// <summary>A cell's width in the grid <see cref="Draw"/> paints, in pixels; its height is twice that.</summary>
    public const int CellWidth = 8;

    /// <summary>A cell's height in the grid <see cref="Draw"/> paints, in pixels.</summary>
    public const int CellHeight = 16;

    private PrototypeCells(ImmutableArray<DIR.Lib.Layout.ArrangedNode<int>> arranged, CellBuffer buffer, CellWireframeFrame frame)
    {
        Arranged = arranged;
        Buffer = buffer;
        Frame = frame;
    }

    /// <summary>The arranged nodes, in cells: what <see cref="CellLayout.HitTest"/> and <see cref="CellLayout.TextInputs"/> read.</summary>
    public ImmutableArray<DIR.Lib.Layout.ArrangedNode<int>> Arranged { get; }

    /// <summary>The painted cells; flushed, so <see cref="CellBuffer.FrontAt"/> and <see cref="CellBuffer.FrontRowText"/> read what is on the screen.</summary>
    public CellBuffer Buffer { get; }

    /// <summary>What the painter recorded, which <see cref="Check"/> judges.</summary>
    public CellWireframeFrame Frame { get; }

    /// <summary>The columns of the grid.</summary>
    public int Columns => Buffer.Width;

    /// <summary>The rows of the grid.</summary>
    public int Rows => Buffer.Height;

    /// <summary>
    /// Builds <paramref name="screen"/> for <paramref name="state"/> and paints it in <paramref name="columns"/> by <paramref name="rows"/> cells.
    /// <paramref name="theme"/> is DIR.Lib's default prototype theme when null.
    /// </summary>
    public static PrototypeCells Build(PrototypeScreen screen, PrototypeState state, int columns, int rows, UiTheme? theme = null)
    {
        var builder = new PrototypeTreeBuilder(theme ?? Prototype.DefaultTheme, state)
        {
            // The read-only 'target' state follows the width the screen is authored at, as it does in the player.
            DesignSize = (columns * CellWidth, rows * CellHeight),
        };
        return Paint(builder.Build(screen), columns, rows);
    }

    /// <summary>Arranges <paramref name="tree"/> in the grid and paints it. Public so a host with a tree of its own (pilot-tui's frame) can use the same paint.</summary>
    public static PrototypeCells Paint(DIR.Lib.Layout.Node tree, int columns, int rows)
    {
        var window = new Rect<int>(0, 0, columns, rows);
        var arranged = DIR.Lib.Layout.Engine.Arrange(tree.Stretch(), window, CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        var frame = CellWireframe.RecordCells(new BufferedViewport(buffer, columns, rows), arranged, null, false);
        // Writes land in the back buffer; a flush into a sink that keeps nothing is what puts them on the screen.
        buffer.Flush(new NullSink());
        return new PrototypeCells(arranged, buffer, frame);
    }

    /// <summary>The buffer's rows as plain text, one string a row, each as wide as the grid.</summary>
    public string[] FrontRowText()
    {
        var rows = new string[Buffer.Height];
        for (var r = 0; r < rows.Length; r++)
        {
            rows[r] = Buffer.FrontRowText(r);
        }
        return rows;
    }

    /// <summary>The screen as text: the rows with trailing spaces trimmed and the blank rows at the end dropped, each ended by a line feed.</summary>
    public string Text()
    {
        var rows = FrontRowText().Select(r => r.TrimEnd()).ToList();
        while (rows.Count > 0 && rows[^1].Length == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return rows.Count == 0 ? "" : string.Join('\n', rows) + "\n";
    }

    /// <summary>The findings of Console.Lib's pixel rules and cell rules over this paint.</summary>
    public CellWireframeResult Check() => CellWireframe.CheckCells(Frame, new CellWireframeOptions());

    /// <summary>
    /// Each finding of <see cref="Check"/> as <c>&lt;rule&gt;: &lt;message&gt;</c>, once each, in the order they were found.
    /// </summary>
    public IEnumerable<string> Findings()
    {
        var result = Check();
        return result.Findings.Select(f => $"{f.Rule}: {f.Message}")
            .Concat(result.CellFindings.Select(f => $"{f.Rule}: {f.Message}"))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// The state variants of a screen, as the pixel check draws them: the default, then one for each distinct <c>name=value</c> a <c>when</c> on the screen names
    /// that the default state does not already show. <c>target</c> comes from the width, never from a variant.
    /// </summary>
    public static IReadOnlyList<(string Label, string? Name, string Value)> Variants(PrototypeScreen screen)
    {
        var list = new List<(string, string?, string)> { ("default", null, "") };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var empty = new PrototypeState();
        void Walk(PrototypeNode n)
        {
            if (n.When is { } when && !empty.WhenHolds(when) && seen.Add(when))
            {
                var eq = when.IndexOf('=');
                if (eq > 0 && when[..eq] != "target")
                {
                    list.Add((when, when[..eq], when[(eq + 1)..]));
                }
            }
            foreach (var c in n.Children)
            {
                Walk(c);
            }
        }
        Walk(screen.Root);
        return list;
    }

    /// <summary>The pixel size of the grid <see cref="Draw"/> paints for <paramref name="columns"/> by <paramref name="rows"/> cells.</summary>
    public static (uint Width, uint Height) PixelSize(int columns, int rows) => ((uint)(columns * CellWidth), (uint)(rows * CellHeight));

    /// <summary>
    /// Paints <paramref name="buffer"/> on <paramref name="renderer"/> as a grid with its upper left at (<paramref name="x"/>, <paramref name="y"/>), every cell
    /// <see cref="CellWidth"/> by <see cref="CellHeight"/> pixels: the cell's background, then its glyph (box-drawing borders included) in its foreground, in the
    /// monospace face at <paramref name="fontPath"/>. A colour a cell never stated is the terminal's own: a light glyph on a dark ground.
    /// </summary>
    public static void Draw<TSurface>(Renderer<TSurface> renderer, CellBuffer buffer, string fontPath, float x = 0f, float y = 0f)
    {
        var fontSize = CellHeight * 0.75f;
        for (var row = 0; row < buffer.Height; row++)
        {
            var top = (int)(y + row * CellHeight);
            // A run of cells with one background is one fill, so a wide screen is not a fill a cell.
            var start = 0;
            while (start < buffer.Width)
            {
                var color = BackgroundOf(buffer.FrontAt(start, row));
                var end = start + 1;
                while (end < buffer.Width && BackgroundOf(buffer.FrontAt(end, row)) == color)
                {
                    end++;
                }
                renderer.FillRectangle(Rect((int)(x + start * CellWidth), top, (end - start) * CellWidth, CellHeight), color);
                start = end;
            }
            for (var column = 0; column < buffer.Width; column++)
            {
                var cell = buffer.FrontAt(column, row);
                if (cell.Glyph is ' ' or '\0')
                {
                    continue;
                }
                var (foreground, _) = PenOf(cell);
                renderer.DrawText(cell.Glyph.ToString(), fontPath, fontSize, foreground, Rect((int)(x + column * CellWidth), top, CellWidth, CellHeight), TextAlign.Center, TextAlign.Center);
            }
        }
    }

    private static readonly RGBAColor32 DefaultForeground = new(204, 204, 204, 255);
    private static readonly RGBAColor32 DefaultBackground = new(18, 18, 20, 255);

    private static RGBAColor32 BackgroundOf(Cell cell) => PenOf(cell).Background;

    private static (RGBAColor32 Foreground, RGBAColor32 Background) PenOf(Cell cell)
    {
        var foreground = cell.Style.Foreground.Alpha == 0 ? DefaultForeground : cell.Style.Foreground;
        var background = cell.Style.Background.Alpha == 0 ? DefaultBackground : cell.Style.Background;
        return cell.Reverse ? (background, foreground) : (foreground, background);
    }

    private static RectInt Rect(int x, int y, int width, int height) => new(new PointInt(x + width, y + height), new PointInt(x, y));

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(CellWidth, CellHeight);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCaret(int column, int row, CaretStyle style) { }
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int column, int row) { }
        public void SetPen(VtStyle style, bool reverse) { }
        public void SetLink(string? url) { }
        public void Write(ReadOnlySpan<char> run) { }
    }
}
