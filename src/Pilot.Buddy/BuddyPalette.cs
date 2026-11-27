using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>
/// The buddy's colours, one set for a bright desktop and one for a dark one, designed together (DSN-2 v5). The cats are characters and keep their own
/// colours in both; what changes is the bubble, the text drawn straight on the desktop (zZz, "!", "#@!", "...fine.") and the cards.
/// Every text colour here reads at 4.5:1 against what it is drawn on, and every border at 3:1 (<c>BuddyCatCaptureTests</c> checks both).
/// The accent comes from the desktop (its ramp's pick, passed in) where the design uses one: the good-news bubble's border and the picked cat's card.
/// </summary>
public sealed record BuddyPalette
{
    private static readonly RGBAColor32 DarkAccentFallback = new(0x4d, 0x9b, 0xff, 0xff);
    private static readonly RGBAColor32 LightAccentFallback = new(0x1f, 0x5f, 0xcf, 0xff);

    /// <summary>The desktop behind the capture (the real window is transparent; shown dark blue in the design).</summary>
    public required RGBAColor32 Desktop { get; init; }

    /// <summary>The bubble's and the cards' fill.</summary>
    public required RGBAColor32 PanelBg { get; init; }

    /// <summary>The bubble's title.</summary>
    public required RGBAColor32 HeaderText { get; init; }

    /// <summary>The bubble's body.</summary>
    public required RGBAColor32 BodyText { get; init; }

    /// <summary>Secondary body text (the good news' second line, a card's text).</summary>
    public required RGBAColor32 MutedText { get; init; }

    /// <summary>A button's fill.</summary>
    public required RGBAColor32 ButtonBg { get; init; }

    /// <summary>A button's text.</summary>
    public required RGBAColor32 ButtonText { get; init; }

    /// <summary>The amber of an ask: its border, its icon.</summary>
    public required RGBAColor32 Amber { get; init; }

    /// <summary>The red of trouble: its border, its icon, the "#@!".</summary>
    public required RGBAColor32 Red { get; init; }

    /// <summary>The green of "all fine": an idle session's check in the peek.</summary>
    public required RGBAColor32 Green { get; init; }

    /// <summary>The accent: good news' border and icon, the picked card.</summary>
    public required RGBAColor32 Accent { get; init; }

    /// <summary>The "zZz" and "...fine." drawn on the desktop.</summary>
    public required RGBAColor32 DesktopText { get; init; }

    /// <summary>The yellow "!" over an alert cat.</summary>
    public required RGBAColor32 Alert { get; init; }

    /// <summary>A card's outline.</summary>
    public required RGBAColor32 CardBorder { get; init; }

    /// <summary>Whether this is the dark set.</summary>
    public required bool Dark { get; init; }

    /// <summary>The set for a dark or a bright desktop. <paramref name="accent"/> is the desktop's pick for this chrome; one that does not read (3:1) on the panel gives way to the built-in blue.</summary>
    public static BuddyPalette For(bool dark, RGBAColor32? accent = null)
    {
        var panel = dark ? new RGBAColor32(0x1c, 0x1d, 0x21, 0xff) : new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        var fallback = dark ? DarkAccentFallback : LightAccentFallback;
        var chosen = accent is { } a && BuddyContrast.Ratio(a, panel) >= BuddyContrast.Edge ? a : fallback;
        return dark
            ? new BuddyPalette
            {
                Dark = true,
                Desktop = new RGBAColor32(0x1f, 0x2a, 0x36, 0xff),
                PanelBg = panel,
                HeaderText = new RGBAColor32(0xee, 0xee, 0xf0, 0xff),
                BodyText = new RGBAColor32(0xdd, 0xdd, 0xe0, 0xff),
                MutedText = new RGBAColor32(0xa6, 0xa8, 0xb0, 0xff),
                ButtonBg = new RGBAColor32(0x2b, 0x2c, 0x31, 0xff),
                ButtonText = new RGBAColor32(0xe4, 0xe4, 0xe8, 0xff),
                Amber = new RGBAColor32(0xf5, 0xb8, 0x2e, 0xff),
                Red = new RGBAColor32(0xff, 0x6b, 0x6b, 0xff),
                Green = new RGBAColor32(0x4c, 0xc2, 0x7a, 0xff),
                Accent = chosen,
                DesktopText = new RGBAColor32(0xcf, 0xe6, 0xff, 0xff),
                Alert = new RGBAColor32(0xff, 0xc2, 0x33, 0xff),
                CardBorder = new RGBAColor32(0x6b, 0x74, 0x80, 0xff),
            }
            : new BuddyPalette
            {
                Dark = false,
                Desktop = new RGBAColor32(0xe9, 0xed, 0xf3, 0xff),
                PanelBg = panel,
                HeaderText = new RGBAColor32(0x1b, 0x1b, 0x1f, 0xff),
                BodyText = new RGBAColor32(0x2c, 0x2d, 0x33, 0xff),
                MutedText = new RGBAColor32(0x55, 0x58, 0x63, 0xff),
                ButtonBg = new RGBAColor32(0xe8, 0xe9, 0xed, 0xff),
                ButtonText = new RGBAColor32(0x1b, 0x1b, 0x1f, 0xff),
                Amber = new RGBAColor32(0xa8, 0x62, 0x00, 0xff),
                Red = new RGBAColor32(0xc6, 0x28, 0x28, 0xff),
                Green = new RGBAColor32(0x1b, 0x7a, 0x3e, 0xff),
                Accent = chosen,
                DesktopText = new RGBAColor32(0x1f, 0x3a, 0x5f, 0xff),
                Alert = new RGBAColor32(0x9a, 0x5b, 0x00, 0xff),
                CardBorder = new RGBAColor32(0x7a, 0x80, 0x8c, 0xff),
            };
    }
}

/// <summary>WCAG contrast, for the palette and its tests.</summary>
public static class BuddyContrast
{
    /// <summary>Body text.</summary>
    public const double Text = 4.5;

    /// <summary>Large text, icons and control edges.</summary>
    public const double Edge = 3.0;

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>The relative luminance of a colour, 0 to 1.</summary>
    public static double Luminance(RGBAColor32 c) => 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);

    /// <summary>The contrast ratio of two colours, 1 to 21.</summary>
    public static double Ratio(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = Luminance(a) >= Luminance(b) ? (Luminance(a), Luminance(b)) : (Luminance(b), Luminance(a));
        return (hi + 0.05) / (lo + 0.05);
    }
}
