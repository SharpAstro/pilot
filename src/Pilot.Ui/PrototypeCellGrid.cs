using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using DIR.Lib.Prototyping;
using Pilot.Cells;

namespace Pilot.Ui;

/// <summary>
/// A design's current screen arranged by Console.Lib in <c>columns x rows</c> terminal cells and drawn as a grid in the monospace face (PIL1098), the Cells
/// half of the Designs page's Pixels or Cells switch. It plays the <see cref="PrototypePlayer{TSurface}"/> it is given: the same <see cref="PrototypeState"/>,
/// the same current screen and the same tree builder (so a typed value is still there after a switch), only arranged by <see cref="PrototypeCells"/> in cells
/// instead of by the pixel engine. The grid is fitted into its rectangle at 1:2 cells, letterboxed.
/// A press on the grid is mapped to its cell and hit-tested by <see cref="CellLayout.HitTest"/>, as pilot-tui does it, which runs the hit node's own press
/// (a goto, a set, a toggle); every text input is a region of its own over the grid, so the router focuses it and routes keys to it.
/// The arrangement is rebuilt only when the screen, the size, a field's text or a press changes it, never every frame.
/// </summary>
internal sealed class PrototypeCellGrid<TSurface>(Renderer<TSurface> renderer, PrototypePlayer<TSurface> player) : PixelWidgetBase<TSurface>(renderer)
{
    private const string GridKey = "design-cells";

    private PrototypeCells? _cells;
    private (string Screen, int Columns, int Rows, string Fields, int Presses, int Generation)? _builtFor;
    private int _presses;
    private int _generation;
    private RectF32 _grid;

    /// <summary>The player whose screen and state this grid shows.</summary>
    public PrototypePlayer<TSurface> Player { get; } = player;

    /// <summary>The columns of the terminal the screen is arranged for.</summary>
    public int Columns { get; set; } = 80;

    /// <summary>The rows of the terminal the screen is arranged for.</summary>
    public int Rows { get; set; } = 24;

    /// <summary>The cells as last arranged and painted; null before the first <see cref="Render"/>.</summary>
    public PrototypeCells? Cells => _cells;

    /// <summary>How many times the arrangement was rebuilt, which a test reads to see that a frame does not rebuild it.</summary>
    public int Builds { get; private set; }

    /// <summary>The rectangle the grid occupies in the last frame, in surface pixels.</summary>
    public RectF32 GridRect => _grid;

    /// <summary>Asks for the arrangement to be rebuilt in the next frame (the player was played in Pixels meanwhile).</summary>
    public void Invalidate() => _generation++;

    /// <summary>The rectangle of <paramref name="columns"/> by <paramref name="rows"/> cells fitted into <paramref name="rect"/> at 1:2 and centred.</summary>
    public static RectF32 Fit(RectF32 rect, int columns, int rows)
    {
        var scale = MathF.Min(rect.Width / (columns * PrototypeCells.CellWidth), rect.Height / (rows * PrototypeCells.CellHeight));
        if (!(scale > 0f))
        {
            return new RectF32(rect.X, rect.Y, 0f, 0f);
        }
        var w = columns * PrototypeCells.CellWidth * scale;
        var h = rows * PrototypeCells.CellHeight * scale;
        return new RectF32(rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f, w, h);
    }

    /// <summary>The centre, in surface pixels, of cell (<paramref name="column"/>, <paramref name="row"/>) in the last frame.</summary>
    public (float X, float Y) CellCentre(int column, int row)
        => (_grid.X + (column + 0.5f) * _grid.Width / Columns, _grid.Y + (row + 0.5f) * _grid.Height / Rows);

    /// <summary>Paints the screen's cells in <paramref name="rect"/> and registers the grid's press and its fields.</summary>
    public void Render(RectF32 rect)
    {
        BeginFrame();
        _grid = Fit(rect, Columns, Rows);
        if (_grid.Width <= 0f)
        {
            return;
        }
        Ensure();
        var cells = _cells!;
        var scale = _grid.Width / (Columns * PrototypeCells.CellWidth);
        PrototypeCells.Draw(Renderer, cells.Buffer, FontPath ?? "",_grid.X, _grid.Y, scale);

        // One press region over the whole grid: the press is mapped to its cell and hit-tested as pilot-tui does, which runs the hit node's own press.
        RegisterClickable(_grid.X, _grid.Y, _grid.Width, _grid.Height, new HitResult.ButtonHit(GridKey), null, null, press =>
        {
            if (press.Button == MouseButton.Left)
            {
                var column = (int)((press.X - _grid.X) / _grid.Width * Columns);
                var row = (int)((press.Y - _grid.Y) / _grid.Height * Rows);
                if (CellLayout.HitTest(cells.Arranged, Math.Clamp(column, 0, Columns - 1), Math.Clamp(row, 0, Rows - 1), press.Modifiers) is { })
                {
                    _presses++;
                }
            }
            return null;
        });
        // Over it, a region for each text input, which is what focuses the field; a key then goes to it as it goes to any field.
        foreach (var node in cells.Arranged)
        {
            if (node.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput }
                && CellLayout.HitTest(cells.Arranged, node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2, InputModifier.None) is HitResult.TextInputHit hit)
            {
                var r = node.Bounds;
                var cw = _grid.Width / Columns;
                var ch = _grid.Height / Rows;
                RegisterClickable(_grid.X + r.X * cw, _grid.Y + r.Y * ch, r.Width * cw, r.Height * ch, hit);
            }
        }
    }

    /// <summary>The text inputs of the screen as arranged, in paint order: what Tab cycles and what a typed key goes to.</summary>
    public override List<TextInputState> GetRegisteredTextInputs() => _cells is null ? [] : [.. CellLayout.TextInputs(_cells.Arranged)];

    private string FieldsOf() => _cells is null ? "" : string.Join('\u0001', CellLayout.TextInputs(_cells.Arranged).Select(f => f.Text));

    private void Ensure()
    {
        var key = (Player.CurrentScreen.Id, Columns, Rows, FieldsOf(), _presses, _generation);
        if (_builtFor == key && _cells is not null)
        {
            return;
        }
        Builds++;
        // The player's own builder builds the tree, so its persistent fields and its goto are the ones the pixel player has; only the design area is the grid's.
        var builder = Player.TreeBuilder;
        var size = builder.DesignSize;
        builder.DesignSize = (Columns * PrototypeCells.CellWidth, Rows * PrototypeCells.CellHeight);
        try
        {
            _cells = PrototypeCells.Paint(Player.BuildTree(), Columns, Rows);
        }
        finally
        {
            builder.DesignSize = size;
        }
        // The fields' text is read from the new arrangement, which is what the next frame compares against.
        _builtFor = (Player.CurrentScreen.Id, Columns, Rows, FieldsOf(), _presses, _generation);
    }
}
