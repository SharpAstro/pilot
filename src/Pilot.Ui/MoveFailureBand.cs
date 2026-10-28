using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A flight whose move to another node failed (#376), as a red band: what happened in words that wrap (never cut: the reason is
/// what the person came to read), and Retry move beside it. One component for the Flights page, the flight's own page and Home,
/// so they cannot disagree about it. Its colours come from the palette of the page it is on; the heading carries a ⚠ and the words
/// "move failed", so the red is never the only thing that says it.
/// </summary>
internal static class MoveFailureBand
{
    /// <summary>The most bands a page stacks before it says how many more there are.</summary>
    public const int MaxBands = 3;

    // The red stripe at the band's left edge, and the gap between it, the words and the button.
    private const float StripeWidth = 4f;
    private const float Gap = 10f;
    private const float ButtonGap = 12f;
    private const float ButtonHeight = 26f;

    /// <summary>The band's id for a flight's Retry move button, as the page's other action buttons are named.</summary>
    public static string ActionId(string code) => $"flight:{code}:{Verbs.MoveFlight}";

    /// <summary>
    /// <paramref name="line"/>'s band, or null when its move did not fail. <paramref name="retry"/> is the page's way of running
    /// the action (null draws no button: a snapshot has no node to ask, or the flight is on a node this window only watches).
    /// </summary>
    public static Layout.Node? Build<TSurface>(FlightLine line, FlightsPalette p, Action<FlightAction>? retry, string idPrefix = "")
    {
        if (line.MoveFailure is not { } reason)
        {
            return null;
        }
        var heading = Layout.Builder.Text($"⚠ {line.Code} · {line.MoveMark}", 13f, p.FailedText).RowH(22f);
        var cells = new List<Layout.Node>
        {
            Layout.Builder.Spacer().Bg(p.Failed).HStar(),
            Layout.Builder.Spacer(),
            Layout.Builder.VStack(heading, WrappedProse.Of(reason, 13f, p.Bright)).Pad(0f, 8f).WStar().HAuto(),
        };
        var columns = new List<Layout.Sizing> { Layout.Sizing.Fixed(StripeWidth), Layout.Sizing.Fixed(Gap), Layout.Sizing.Star() };
        if (retry is not null && FlightAction.For(line).FirstOrDefault(a => a.Verb == Verbs.MoveFlight) is { } action)
        {
            var button = CardsView<TSurface>.Pill(action.Label, idPrefix + ActionId(line.Code), () => retry(action), (p.RetryBg, p.RetryHover), p.RetryText)
                .RowH(ButtonHeight);
            cells.Add(Layout.Builder.Spacer());
            cells.Add(Layout.Builder.VStack(button).Pad(0f, 8f).WAuto());
            columns.Add(Layout.Sizing.Fixed(ButtonGap));
            columns.Add(Layout.Sizing.Auto);
        }
        cells.Add(Layout.Builder.Spacer());
        columns.Add(Layout.Sizing.Fixed(Gap));
        // A grid, not a stack: it measures the reason at the width its column is given, with the button's and the gaps' taken off,
        // where a stack measured it as wide as the band and the last line ran under the button (as the cards' bands were fixed).
        return Layout.Builder.Grid(columns.Count, [.. cells])
            .WithColumns([.. columns])
            .WithAutoRows()
            .Bg(p.FailedBg)
            .WStar()
            .HAuto();
    }

    /// <summary>
    /// The bands of the flights in <paramref name="lines"/> whose move failed, at most <see cref="MaxBands"/>, then a line saying how
    /// many more there are and where to read them; nothing when none failed.
    /// </summary>
    public static IEnumerable<Layout.Node> All<TSurface>(IEnumerable<FlightLine> lines, FlightsPalette p, Func<FlightLine, Action<FlightAction>?> retryOf, string idPrefix = "")
    {
        var failed = lines.Where(l => l.MoveMark is not null).ToList();
        foreach (var line in failed.Take(MaxBands))
        {
            if (Build<TSurface>(line, p, retryOf(line), idPrefix) is { } band)
            {
                yield return band;
            }
        }
        if (failed.Count > MaxBands)
        {
            yield return Layout.Builder.Text($"and {failed.Count - MaxBands} more flights whose move failed: their rows are red", 12f, p.FailedText).RowH(22f);
        }
    }
}
