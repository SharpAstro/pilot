using System.Runtime.CompilerServices;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The one place pilot's <see cref="Palette"/> becomes DIR.Lib's <see cref="UiTheme"/> (PIL1232), so every library control
/// (icon toggle, icon action, segmented control) is drawn from pilot's own colours and a later control takes the theme from here.
/// </summary>
internal static class UiThemes
{
    // One theme per palette, built once: a UiPalette is a reference type and building one in a draw call is a per-frame allocation.
    private static readonly ConditionalWeakTable<Palette, UiTheme> _themes = new();

    /// <summary>The theme for <paramref name="palette"/>: the standard palette's, the night palette's or an accent-built standard one.</summary>
    public static UiTheme For(Palette palette) => _themes.GetValue(palette, Build);

    /// <summary>The theme for the palette the page being built paints with.</summary>
    public static UiTheme Current => For(Palette.Current);

    /// <summary>Ink on an accent fill must read at this ratio (WCAG AA body text).</summary>
    public const double InkRatio = 4.5;

    /// <summary>
    /// Which pilot colour feeds each slot: ContentBg=Ground, PanelBg=PanelBg, HeaderBg=HeaderBg, RaisedBg=MenuBg, Edge=CardBorder,
    /// Separator=RailSeparator, BodyText=Bright, DimText=Dim, HeaderText=Bright, Accent=RailAccent (the colour the pinned bar's button
    /// already used) unless the ink that reads on it misses <see cref="InkRatio"/>, then SelectionBg (night: RailAccent is a dim red no
    /// ink reads on, SelectionBg is the brightest red with black on it), Selection=SelectionBg, SelectionText=SelectionText,
    /// Info=Link, Warn=Held, Error=Failed, Success=Ok. Metrics: ButtonHeight is AboutCard.HeaderButton, the height of the header's own buttons.
    /// </summary>
    private static UiTheme Build(Palette p)
    {
        var accent = InkRatioOn(p, p.RailAccent) >= InkRatio ? p.RailAccent : p.SelectionBg;
        return new UiTheme
        {
            Palette = new UiPalette
            {
                ContentBg = p.Ground,
                PanelBg = p.PanelBg,
                HeaderBg = p.HeaderBg,
                RaisedBg = p.MenuBg,
                Edge = p.CardBorder,
                Separator = p.RailSeparator,
                BodyText = p.Bright,
                DimText = p.Dim,
                HeaderText = p.Bright,
                Accent = accent,
                Selection = p.SelectionBg,
                SelectionText = p.SelectionText,
                Info = p.Link,
                Warn = p.Held,
                Error = p.Failed,
                Success = p.Ok,
            },
            Metrics = new UiMetrics(12f, 6f, AboutCard.HeaderButton, 24f, AboutCard.HeaderButton),
        };
    }

    // The ratio of the ink DIR.Lib would pick (BodyText or ContentBg, the better) on a fill.
    private static double InkRatioOn(Palette p, RGBAColor32 fill) => Math.Max(Contrast.Ratio(p.Bright, fill), Contrast.Ratio(p.Ground, fill));
}
