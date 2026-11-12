using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page's band of PR review and review-fix rounds the node runs outside a flight (pilot #1007).
internal sealed partial class FlightsView<TSurface>
{
    private const float RoundRowHeight = 28f;
    private const float RoundPrWidth = 170f;
    private const float RoundRoundWidth = 64f;
    private const float RoundSessionWidth = 170f;
    private const float RoundStateWidth = 150f;
    private const float RoundNodeWidth = 64f;

    /// <summary>One row for each round, under a label naming them; null when there are none.</summary>
    private Layout.Node? RoundsBand(FlightsScreen screen)
    {
        if (screen.Rounds.Count == 0)
        {
            return null;
        }
        var rows = new List<Layout.Node>
        {
            Layout.Builder.Text($"PR rounds the node runs · {screen.Rounds.Count}", 12f, Faint).RowH(22f),
        };
        for (var i = 0; i < screen.Rounds.Count; i++)
        {
            rows.Add(RoundRow(screen.Rounds[i], i, screen.Rounds.Any(r => r.Node is not null)));
        }
        return Layout.Builder.VStack([.. rows]).Pad(Gutter, 6f).WStar().HAuto();
    }

    private Layout.Node RoundRow(PrRoundLine round, int index, bool marked)
    {
        var cells = new List<Layout.Node>();
        if (marked)
        {
            cells.Add(Layout.Builder.Text(round.Node?.Name ?? "", 13f, Body).WFixed(RoundNodeWidth).RowH(RoundRowHeight));
        }
        cells.Add(Layout.Builder.Text(round.Pr, 13f, Bright).WFixed(RoundPrWidth).RowH(RoundRowHeight));
        cells.Add(Whole(Layout.Builder.Text(round.Title, 13f, Body).RowH(RoundRowHeight), round.Title, 0).WStar());
        cells.Add(Layout.Builder.Text(round.Round, 13f, Body).WFixed(RoundRoundWidth).RowH(RoundRowHeight));
        cells.Add(Whole(Layout.Builder.Text(round.Session, 13f, Body).WFixed(RoundSessionWidth).RowH(RoundRowHeight), round.Session, 0));
        cells.Add(Layout.Builder.Text(round.State, 13f, round.Fixing ? Held : Flying).WFixed(RoundStateWidth).RowH(RoundRowHeight));
        var row = Layout.Builder.HStack([Layout.Builder.Spacer().Bg(round.Fixing ? Held : Flying).WFixed(ListTable.StripeWidth).HStar(), .. cells])
            .WithGap(8f)
            .Bg((index & 1) == 1 ? RowAltBg : PanelBg)
            .BgHover(RowHoverBg)
            .WithTooltip(round.Whole)
            .RowH(RoundRowHeight);
        // Its PR's row on the Pull requests page, where the host has that page and the node lists the PR.
        var pr = PrRef.Parse(round.Pr);
        if (OnPullRequest is { } open && pr is not null && PullRequestLinks.Lists(PullRequests, pr))
        {
            row = row.Follows(new HitResult.ButtonHit($"round:{round.Node?.Key}:{round.Pr}"), _ => open(pr.Repo, pr.Number));
        }
        return row;
    }
}
