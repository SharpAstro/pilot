using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A PR as a link, decided in one place: where it goes is pilot's own page when there is one (a flight's page, or the PR's row on the
/// Pull requests page), else GitHub. An in-app link is a place of the app and carries no ↗, with a small ↗ beside it for GitHub; a link
/// that leaves the app ends in ↗. Every link keeps the Copy URL menu, and shows its whole text on hover when it may be cut.
/// The Repos page's PR lines, the Cards panel's PR title and the Flights page's PR link all build theirs here.
/// The Pull requests page does not: its rows are pilot's own page for the PR, so a row's title is the way out and keeps its ↗.
/// </summary>
internal static class PullRequestLink
{
    /// <summary>
    /// <paramref name="text"/> as a link for <paramref name="pr"/> (<c>owner/name#n</c>, for the tooltips) whose GitHub page is <paramref name="url"/>.
    /// <paramref name="route"/> is the in-app way in, with <paramref name="routeName"/> saying where it goes ("the flight's page"); null (no such
    /// page, or a PR the node does not list) leaves the browser link with ↗. <paramref name="open"/> is the browser opener: null with a PR the
    /// node gave no URL for, which then has no GitHub link to offer. <paramref name="mayBeCut"/> says the text may not fit: its tooltip is then
    /// the whole text, else where the link goes. <paramref name="fill"/> makes the text take the room it is given, as a line's title does.
    /// <paramref name="size"/> is the text's size, <paramref name="tipSide"/> the side of the text its tooltip opens on (null for the default),
    /// <paramref name="tall"/> says the link stands in a row of a height of its own, which the caller sets: its parts then take that height,
    /// and <paramref name="browserAction"/> names the ↗'s hit where it is not <c>&lt;action&gt;:browser</c>.
    /// </summary>
    public static Layout.Node Of(LinkMenu links, string action, string text, string pr, string? url, Action<string>? open, Action? route, string routeName,
        RGBAColor32 color, RGBAColor32 hover, bool mayBeCut, bool fill, float size = 12f, Layout.DockSide? tipSide = null, bool tall = false, string? browserAction = null)
    {
        if (route is null)
        {
            var away = Tip(Layout.Builder.Text(text + Followable.Away, size, color).BgHover(hover)
                .Follows(new HitResult.ButtonHit(action), _ => Open(open, url)),
                mayBeCut ? text : $"Open {pr} on GitHub", tipSide);
            return Menu(links, fill ? away.WStar() : away.WAuto(), url);
        }
        var title = Tip(Layout.Builder.Text(text, size, color).BgHover(hover)
            .Follows(new HitResult.ButtonHit(action), _ => route()),
            mayBeCut ? text : $"Show {pr} on {routeName}", tipSide);
        // The browser's own, small: a mark, not a word, so it reads as the way out and the title as the way in.
        var browser = Layout.Builder.Text("↗", 11f, color, TextAlign.Center, TextAlign.Center)
            .Pad(6f, 0f)
            .WAuto()
            .BgHover(hover)
            .Follows(new HitResult.ButtonHit(browserAction ?? $"{action}:browser"), _ => Open(open, url))
            .WithTooltip($"Open {pr} on GitHub", Layout.DockSide.Left);
        if (tall)
        {
            title = title.HStar();
            browser = browser.HStar();
        }
        var row = Layout.Builder.HStack(Menu(links, fill ? title.WStar() : title.WAuto(), url), Menu(links, browser, url));
        return fill ? row.WStar() : row.WAuto();
    }

    private static Layout.Node Tip(Layout.Node node, string text, Layout.DockSide? side) =>
        side is { } at ? node.WithTooltip(text, at) : node.WithTooltip(text);

    private static Layout.Node Menu(LinkMenu links, Layout.Node link, string? url) => url is null ? link : links.OnLink(link, url);

    private static void Open(Action<string>? open, string? url)
    {
        if (url is not null)
        {
            open?.Invoke(url);
        }
    }
}
