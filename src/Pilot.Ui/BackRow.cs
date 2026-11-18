using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The back row (PIL1183): the one declaration of a page's first row. A "← Page" pill at its left, its own parts after it at one height and one middle. The
/// Sessions transcript, a flight's page, the archived flights and a design's page all head their first row with it, so the arrow, the size, the colours, the
/// tooltip and the hit are written once, on the pixel page and the terminal's alike. Escape runs the same action as the pill: each page's key handler
/// calls the action it gave here.
/// </summary>
internal static class BackRow
{
    /// <summary>The pill's label: an arrow and the page it goes up to.</summary>
    public static string Label(string page) => $"← {page}";

    /// <summary>The pill's tooltip, naming the key that does the same.</summary>
    public static string Tooltip(string page) => $"Up to the {page} page (Escape)";

    /// <summary>The "← Page" pill alone: <paramref name="hit"/> is its button id and <paramref name="up"/> what it and Escape do.</summary>
    public static Layout.Node Pill(string page, string hit, Action up) =>
        Layout.Builder.Text(Label(page), 13f, Palette.Current.Bright, TextAlign.Center).Pad(12f, 5f).WAuto()
            .Bg(Palette.Current.Button).BgHover(Palette.Current.ButtonHover)
            .Follows(new HitResult.ButtonHit(hit), _ => up())
            .WithTooltip(Tooltip(page));

    /// <summary>The row: the pill, then <paramref name="parts"/>, each centred on the pill's middle.</summary>
    public static Layout.Node Of(string page, string hit, Action up, params Layout.Node[] parts) =>
        Layout.Builder.HStack([Pill(page, hit, up), .. parts]).WithGap(12f);
}
