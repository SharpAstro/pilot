using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>How the spend pages and a flight's spend block say a number and a timeframe, so every figure names what it is over.</summary>
internal static class SpendText
{
    /// <summary>Tokens with thousands separators: <c>1,234,567</c>.</summary>
    public static string Tokens(long tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What every share on the page is of, said once above the figures: the rows beside the shares are raw tokens.</summary>
    public const string SharesAre = "Every share is of the window's weighted tokens; the rows show raw tokens.";

    /// <summary>A share to a tenth, <c>12.5%</c>.</summary>
    public static string Share(double share) => share.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The window as a phrase: <c>the last 5 hours</c>, <c>today</c>, <c>this 7-day window</c>.</summary>
    public static string Window(string window) => window switch
    {
        "5h" => "the last 5 hours",
        "today" => "today",
        "week" => "this 7-day window",
        var other => other,
    };

    /// <summary>The span, in UTC so a file reads the same anywhere: <c>2026-10-28 00:00 to 03:00 UTC</c> (the end's date too when it is another day).</summary>
    public static string Span(DateTimeOffset from, DateTimeOffset to)
    {
        var (a, b) = (from.UtcDateTime, to.UtcDateTime);
        return $"{a:yyyy-MM-dd HH:mm} to {(a.Date == b.Date ? b.ToString("HH:mm") : b.ToString("yyyy-MM-dd HH:mm"))} UTC";
    }

    /// <summary>What a window's heading says after its name: <c>today, 2026-10-28 00:00 to 03:00 UTC</c>.</summary>
    public static string Timeframe(string window, DateTimeOffset from, DateTimeOffset to, bool fromAccount) =>
        $"{Window(window)}, {Span(from, to)}{(fromAccount ? ", the account's own window" : "")}";

    /// <summary>A round as the page says it: round 0 is the leg's first flight.</summary>
    public static string Round(int round) => round == 0 ? "first flight" : $"go-around {round}";

    /// <summary>A flight's spend as lines: the total with its timeframe, then each leg, then each of its rounds, indented.</summary>
    public static IReadOnlyList<(int Indent, string Text)> FlightLines(SpendFlightReport report)
    {
        var lines = new List<(int, string)>
        {
            (0, $"{Tokens(report.Tokens)} tokens so far, since the flight started ({Span(report.From, report.To)})"),
        };
        foreach (var leg in report.Legs)
        {
            lines.Add((1, $"{(leg.Leg == "-" ? "no leg (planner, watchtower)" : leg.Leg)}: {Tokens(leg.Tokens)} tokens"));
            lines.AddRange(leg.Rounds.Select(r => (2, $"round {r.Round} ({Round(r.Round)}): {Tokens(r.Tokens)} tokens")));
        }
        return lines;
    }
}

/// <summary>What the Spend page shows: the token ledger over one window, by flight and by role, and what bought nothing kept.</summary>
/// <param name="Window">The window shown: <c>5h</c>, <c>today</c> or <c>week</c>.</param>
/// <param name="Report">The spend by flight and role; null while the node has not answered.</param>
/// <param name="Waste">The waste list, worst first; null while the node has not answered.</param>
internal sealed record SpendScreen(string Window, SpendReport? Report, SpendWasteReport? Waste, string Source, DateTimeOffset AsOf)
{
    public static readonly string[] Windows = ["5h", "today", "week"];

    /// <summary>The screen a file's spend gives for <paramref name="window"/>: that window's report and waste; null reports while the file has none for it.</summary>
    public static SpendScreen From(SpendSnapshot spend, string window, string source, DateTimeOffset at) =>
        new(window, spend.Reports.FirstOrDefault(r => r.Window == window), spend.Waste.FirstOrDefault(w => w.Window == window), source, at);

    /// <summary>The first line's counts: <c>1,200 tokens in 3 messages, today, ...</c>; null before the node answered.</summary>
    public string? Counts => Report is { } r ? $"{SpendText.Tokens(r.Tokens)} tokens in {r.Messages:N0} messages, {SpendText.Timeframe(r.Window, r.From, r.To, r.FromAccount)}" : null;

    /// <summary>The page as a dump writes it, a line a row.</summary>
    public string ToDump()
    {
        var lines = new List<string>();
        if (Report is not { } report)
        {
            return "the node has not told its spend yet";
        }
        var timeframe = SpendText.Timeframe(report.Window, report.From, report.To, report.FromAccount);
        lines.Add(SpendText.SharesAre);
        lines.Add($"By flight, {timeframe}");
        lines.AddRange(report.Flights.Select(f => $"  {f.Key} | {SpendText.Tokens(f.Tokens)} tokens | {SpendText.Share(f.Share)}"));
        lines.Add($"By role, {timeframe}");
        lines.AddRange(report.Roles.Select(r => $"  {r.Key} | {SpendText.Tokens(r.Tokens)} tokens | {SpendText.Share(r.Share)}"));
        if (Waste is { } waste)
        {
            lines.Add($"Waste, {SpendText.Timeframe(waste.Window, waste.From, waste.To, waste.FromAccount)}: {SpendText.Tokens(waste.WasteTokens)} tokens, {SpendText.Share(waste.WasteShare)} of the window");
            lines.AddRange(waste.Rows.Select(w => $"  {w.Kind}: {w.Reason} | {Where(w)} | {SpendText.Tokens(w.Tokens)} tokens | {SpendText.Share(w.Share)}"));
        }
        return string.Join('\n', lines);
    }

    /// <summary>Whose waste a row is: <c>PIL515/ledger worker</c>.</summary>
    public static string Where(SpendWasteRow row) =>
        $"{(row.Flight is { } flight ? row.Leg is { } leg ? $"{flight}/{leg}" : flight : "no flight")} {row.Role}";
}

/// <summary>
/// The Spend page: the node's token ledger over a window you pick (the last 5 hours, today, the 7-day window), by flight and by role
/// each with its share, then what bought nothing kept, worst first. Every heading says the timeframe its figures are over.
/// </summary>
internal sealed class SpendView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    private const float Gutter = 16f;
    private const float RowH = 22f;
    private const float TokensWidth = 130f;
    private const float ShareWidth = 64f;

    private static RGBAColor32 Ground => Palette.Current.Ground;
    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Body => Palette.Current.Body;
    private static RGBAColor32 Dim => Palette.Current.Dim;
    private static RGBAColor32 Held => Palette.Current.Held;
    private static RGBAColor32 RowAltBg => Palette.Current.RowAltBg;
    private static RGBAColor32 Button => Palette.Current.Button;
    private static RGBAColor32 ButtonHover => Palette.Current.ButtonHover;
    private static RGBAColor32 Active => Palette.Current.RowFocusBg;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>The window picked.</summary>
    public string Window { get; private set; } = "today";

    /// <summary>Asked for a window's reports: the host reads the node's spend routes and hands a new screen.</summary>
    public Action<string>? OnWindow { get; set; }

    /// <summary>The header's ? card.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The header's Night switch.</summary>
    public NightSwitch? Night { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>Picks <paramref name="window"/>: the page shows it and the host is asked for its figures.</summary>
    public void Pick(string window)
    {
        if (Window == window)
        {
            return;
        }
        Window = window;
        OnWindow?.Invoke(window);
    }

    public void Render(SpendScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    internal Layout.Node Tree(SpendScreen screen)
    {
        Palette.Use(Night);
        var header = TopBar.Build(new PageHeader("spend")
        {
            Counts = screen.Counts,
            CountsFit = true,
            Buttons = [.. SpendScreen.Windows.Select(Pill)],
            Source = PageHeader.SourceLine(screen.Source, screen.AsOf),
        }, new TopBarParts(Nodes, null, Autopilot, About, Night));
        var page = Layout.Builder.VStack(header, Body_(screen).Stretch()).Bg(Ground).Stretch();
        return About?.Over(page) ?? page;
    }

    private Layout.Node Pill(string window) =>
        Layout.Builder.Text(window, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(window == Window ? Active : Button)
            .BgHover(ButtonHover)
            .WithTooltip($"Spend over {SpendText.Window(window)}")
            .Follows(new HitResult.ButtonHit($"spend:window:{window}"), _ => Pick(window));

    private Layout.Node Body_(SpendScreen screen)
    {
        if (screen.Report is not { } report)
        {
            return Layout.Builder.Text("Reading the node's spend…", 16f, Dim, TextAlign.Center).Stretch();
        }
        var timeframe = SpendText.Timeframe(report.Window, report.From, report.To, report.FromAccount);
        List<Layout.Node> parts =
        [
            Layout.Builder.Text(SpendText.SharesAre, 12f, Dim).RowH(RowH).Whole(SpendText.SharesAre),
            Heading($"By flight, {timeframe}"),
            .. Rows(report.Flights, "no spend in this window"),
            Heading($"By role, {timeframe}"),
            .. Rows(report.Roles, "no spend in this window"),
        ];
        if (screen.Waste is { } waste)
        {
            parts.Add(Heading($"Waste, worst first, {SpendText.Timeframe(waste.Window, waste.From, waste.To, waste.FromAccount)}: {SpendText.Tokens(waste.WasteTokens)} tokens, {SpendText.Share(waste.WasteShare)} of the window"));
            if (waste.Rows.Length == 0)
            {
                parts.Add(Layout.Builder.Text("nothing wasted in this window", 12f, Dim).RowH(RowH));
            }
            for (var i = 0; i < waste.Rows.Length; i++)
            {
                var row = waste.Rows[i];
                parts.Add(Line($"{row.Kind}: {row.Reason}", SpendScreen.Where(row), SpendText.Tokens(row.Tokens) + " tokens", SpendText.Share(row.Share), i, Held));
            }
        }
        return Layout.Builder.VStack([.. parts]).WithGap(2f).WithScroll(_scroll).Pad(Gutter, 8f).Stretch();
    }

    private static Layout.Node Heading(string text) =>
        Layout.Builder.Text(text, 14f, Bright).RowH(30f).Whole(text);

    private IEnumerable<Layout.Node> Rows(SpendRow[] rows, string none)
    {
        if (rows.Length == 0)
        {
            yield return Layout.Builder.Text(none, 12f, Dim).RowH(RowH);
        }
        for (var i = 0; i < rows.Length; i++)
        {
            yield return Line(rows[i].Key == "-" ? "no flight" : rows[i].Key, null, SpendText.Tokens(rows[i].Tokens) + " tokens", SpendText.Share(rows[i].Share), i, Body);
        }
    }

    private static Layout.Node Line(string label, string? where, string tokens, string share, int index, RGBAColor32 color)
    {
        List<Layout.Node> cells = [Layout.Builder.Text(label, 13f, color).WStar(where is null ? 1f : 2f).Whole(label)];
        if (where is not null)
        {
            cells.Add(Layout.Builder.Text(where, 12f, Dim).WStar(2f).Whole(where));
        }
        cells.Add(Layout.Builder.Text(tokens, 13f, Body, TextAlign.Far).WFixed(TokensWidth));
        cells.Add(Layout.Builder.Text(share, 13f, Bright, TextAlign.Far).WFixed(ShareWidth));
        var line = Layout.Builder.HStack([.. cells]).WithGap(10f).RowH(RowH);
        return index % 2 == 1 ? line.Bg(RowAltBg) : line;
    }
}
