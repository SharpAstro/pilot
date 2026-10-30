namespace Pilot.Ui;

/// <summary>The size of a flight's card, as the Grid view lays it out (the Canvas view is the <see cref="FlightMap"/>).</summary>
internal static class FlightCanvas
{
    public const float CardWidth = 232f;
    public const float CardHeight = 112f;
    /// <summary>The grid's card: one line taller, for what a held flight waits for.</summary>
    public const float GridCardHeight = 130f;
    public const float GridGap = 36f;
    public const float GapY = 20f;

    /// <summary>A card's width on a page <paramref name="room"/> design units wide with a gutter of <paramref name="gutter"/> either side: its own, or the page's less its gutters where the page is narrower.</summary>
    public static float CardWidthIn(float room, float gutter) => Math.Min(CardWidth, Math.Max(0f, room - 2 * gutter));
}
