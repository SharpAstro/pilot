using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A PR as a link, decided in one place: where it goes is pilot's own page when there is one (a flight's page, or the PR's row on the
/// Pull requests page), else GitHub. An in-app link is a place of the app and carries no ↗, with a small ↗ beside it for GitHub; a link
/// that leaves the app ends in ↗. Every link keeps the Copy URL menu, and shows its whole text on hover when it may be cut.
/// </summary>
internal static class PullRequestLink
{
    /// <summary>
    /// <paramref name="text"/> as a link for <paramref name="pr"/> (<c>owner/name#n</c>, for the tooltips) whose GitHub page is <paramref name="url"/>.
    /// <paramref name="route"/> is the in-app way in, with <paramref name="routeName"/> saying where it goes ("the flight's page"); null (no such
    /// page, or a PR the node does not list) leaves the browser link with ↗. <paramref name="open"/> is the browser opener: null with a PR the
    /// node gave no URL for, which then has no GitHub link to offer. <paramref name="mayBeCut"/> says the text may not fit: its tooltip is then
    /// the whole text, else where the link goes. <paramref name="fill"/> makes the text take the room it is given, as a line's title does.
    /// </summary>
    public static Layout.Node Of(LinkMenu links, string action, string text, string pr, string? url, Action<string>? open, Action? route, string routeName,
        RGBAColor32 color, RGBAColor32 hover, bool mayBeCut, bool fill)
    {
        if (route is null)
        {
            var away = Layout.Builder.Text(text + Followable.Away, 12f, color).BgHover(hover)
                .Follows(new HitResult.ButtonHit(action), _ => Open(open, url))
                .WithTooltip(mayBeCut ? text : $"Open {pr} on GitHub");
            return Menu(links, fill ? away.WStar() : away.WAuto(), url);
        }
        var title = Layout.Builder.Text(text, 12f, color).BgHover(hover)
            .Follows(new HitResult.ButtonHit(action), _ => route())
            .WithTooltip(mayBeCut ? text : $"Show {pr} on {routeName}");
        // The browser's own, small: a mark, not a word, so it reads as the way out and the title as the way in.
        var browser = Layout.Builder.Text("↗", 11f, color, TextAlign.Center, TextAlign.Center)
            .Pad(6f, 0f)
            .WAuto()
            .BgHover(hover)
            .Follows(new HitResult.ButtonHit($"{action}:browser"), _ => Open(open, url))
            .WithTooltip($"Open {pr} on GitHub", Layout.DockSide.Left);
        var row = Layout.Builder.HStack(Menu(links, fill ? title.WStar() : title.WAuto(), url), Menu(links, browser, url));
        return fill ? row.WStar() : row.WAuto();
    }

    private static Layout.Node Menu(LinkMenu links, Layout.Node link, string? url) => url is null ? link : links.OnLink(link, url);

    private static void Open(Action<string>? open, string? url)
    {
        if (url is not null)
        {
            open?.Invoke(url);
        }
    }
}
