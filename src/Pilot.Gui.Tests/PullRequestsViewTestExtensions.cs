using DIR.Lib;
using Pilot.Ui;

namespace Pilot.Gui.Tests;

/// <summary>The Pull requests page's cursor in PR terms: the n-th PR listed, whatever headers sit between.</summary>
internal static class PullRequestsViewTestExtensions
{
    /// <summary>Puts the cursor on the <paramref name="ordinal"/>-th PR listed.</summary>
    public static void SelectPr(this PullRequestsView<RgbaImage> page, int ordinal) =>
        page.Select(page.IndexOf(page.VisiblePullRequests.ElementAt(ordinal).Url));

    /// <summary>How many PRs are listed before the cursor's, or -1 when the cursor is not on a PR.</summary>
    public static int SelectedPr(this PullRequestsView<RgbaImage> page) =>
        page.SelectedRow is { } selected ? page.VisiblePullRequests.ToList().FindIndex(p => p.Url == selected.Url) : -1;
}
