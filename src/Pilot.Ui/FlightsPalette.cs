using DIR.Lib;

namespace Pilot.Ui;

/// <summary>The views of the Flights page, in the order the header's switch offers them.</summary>
internal enum FlightsViewMode
{
    Canvas,
    Grid,
    List,
}

/// <summary>
/// Every colour the Flights page paints with, so a view is a palette handed to the same builders: the canvas and the night
/// view are one code path, and night is only another record.
/// </summary>
internal sealed record FlightsPalette(
    RGBAColor32 Ground,
    RGBAColor32 HeaderBg,
    RGBAColor32 RowAltBg,
    RGBAColor32 RowHoverBg,
    RGBAColor32 RowFocusBg,
    RGBAColor32 Bright,
    RGBAColor32 Body,
    RGBAColor32 Dim,
    RGBAColor32 Faint,
    RGBAColor32 Flying,
    RGBAColor32 Held,
    RGBAColor32 Landed,
    RGBAColor32 PanelBg,
    RGBAColor32 Button,
    RGBAColor32 ButtonHover,
    RGBAColor32 Failed,
    RGBAColor32 MenuBg,
    RGBAColor32 GhostBg,
    RGBAColor32 LinkedBg,
    RGBAColor32 SelectionBg,
    RGBAColor32 SelectionText)
{
    public static FlightsPalette Standard { get; } = new(
        Ground: new(0x12, 0x12, 0x18, 0xff),
        HeaderBg: new(0x1b, 0x1c, 0x24, 0xff),
        RowAltBg: new(0x16, 0x17, 0x1e, 0xff),
        RowHoverBg: new(0x22, 0x24, 0x2e, 0xff),
        RowFocusBg: new(0x26, 0x33, 0x4a, 0xff),
        Bright: new(0xe6, 0xe7, 0xee, 0xff),
        Body: new(0xc4, 0xc6, 0xd0, 0xff),
        Dim: new(0x86, 0x89, 0x98, 0xff),
        Faint: new(0x5c, 0x5f, 0x6c, 0xff),
        Flying: new(0x4c, 0xc3, 0x8a, 0xff),
        Held: new(0xf0, 0xb4, 0x3c, 0xff),
        Landed: new(0x6f, 0x9b, 0xd8, 0xff),
        PanelBg: new(0x18, 0x19, 0x21, 0xff),
        Button: new(0x2a, 0x3a, 0x58, 0xff),
        ButtonHover: new(0x35, 0x4a, 0x70, 0xff),
        Failed: new(0xe0, 0x5a, 0x5a, 0xff),
        MenuBg: new(0x1f, 0x21, 0x2b, 0xff),
        GhostBg: new(0x14, 0x15, 0x1c, 0xff),
        LinkedBg: new(0x20, 0x27, 0x36, 0xff),
        // Selected text is drawn again on its bar, in the page's brightest ink (6.6:1 on the bar).
        SelectionBg: new(0x2f, 0x4d, 0x8a, 0xff),
        SelectionText: new(0xe6, 0xe7, 0xee, 0xff));

    /// <summary>Dim reds on black and nothing else: no green or blue in any channel, so the eye stays dark-adapted.</summary>
    public static FlightsPalette Night { get; } = new(
        Ground: new(0x00, 0x00, 0x00, 0xff),
        HeaderBg: new(0x14, 0x00, 0x00, 0xff),
        RowAltBg: new(0x0c, 0x00, 0x00, 0xff),
        RowHoverBg: new(0x26, 0x00, 0x00, 0xff),
        RowFocusBg: new(0x3a, 0x00, 0x00, 0xff),
        Bright: new(0xc8, 0x00, 0x00, 0xff),
        Body: new(0xa0, 0x00, 0x00, 0xff),
        Dim: new(0x70, 0x00, 0x00, 0xff),
        Faint: new(0x50, 0x00, 0x00, 0xff),
        Flying: new(0xb0, 0x00, 0x00, 0xff),
        Held: new(0xe0, 0x00, 0x00, 0xff),
        Landed: new(0x60, 0x00, 0x00, 0xff),
        PanelBg: new(0x10, 0x00, 0x00, 0xff),
        Button: new(0x30, 0x00, 0x00, 0xff),
        ButtonHover: new(0x44, 0x00, 0x00, 0xff),
        Failed: new(0xf0, 0x00, 0x00, 0xff),
        MenuBg: new(0x18, 0x00, 0x00, 0xff),
        GhostBg: new(0x08, 0x00, 0x00, 0xff),
        LinkedBg: new(0x1e, 0x00, 0x00, 0xff),
        // A bar as bright as the palette goes with black on it (4.7:1): the night view has no red dark enough to read its own ink on.
        SelectionBg: new(0xf0, 0x00, 0x00, 0xff),
        SelectionText: new(0x00, 0x00, 0x00, 0xff));
}
