namespace Pilot.Ui;

/// <summary>
/// The sizes of the page header's buttons and the bar they stand in. In a window they are design units of a 28 high button; pilot-tui
/// lays the same trees out at 8 units a column and 16 a row, where 28 is two rows (#1044). The host (pilot-tui) turns
/// <see cref="Terminal"/> on, and a button is then one row high, padded one cell either side. The GUI never sets it, so every
/// window value stays as it was.
/// </summary>
public static class HeaderMetrics
{
    // Ambient per async flow, as Palette keeps its own: a test that sets it changes nothing for tests running beside it.
    private static readonly AsyncLocal<bool> _terminal = new();

    /// <summary>Whether the header is built for a grid of cells (one row a button).</summary>
    public static bool Terminal
    {
        get => _terminal.Value;
        set => _terminal.Value = value;
    }

    /// <summary>Until the returned scope is disposed, <see cref="Terminal"/> is <paramref name="on"/> in this async flow.</summary>
    public static IDisposable Use(bool on)
    {
        var was = _terminal.Value;
        _terminal.Value = on;
        return new Scope(was);
    }

    private sealed class Scope(bool was) : IDisposable
    {
        public void Dispose() => _terminal.Value = was;
    }

    /// <summary>The height of a header button and the side of the square ones (?, +): 28 in a window, one row (16) in the terminal.</summary>
    public static float Button => Terminal ? 16f : 28f;

    /// <summary>The padding either side of a header button's label: 10 in a window, one cell (8) in the terminal.</summary>
    public static float PadX => Terminal ? 8f : 10f;

    /// <summary>The row of the top bar: 32 in a window, one row in the terminal.</summary>
    public static float BarRow => Terminal ? 16f : 32f;

    /// <summary>The padding above and below the bar's rows: 6 in a window, none in the terminal.</summary>
    public static float BarPadY => Terminal ? 0f : 6f;

    /// <summary>The bottom of the bar's one row of buttons: where a card hung from it starts.</summary>
    public static float ButtonsBottom => BarPadY + BarRow;
}
