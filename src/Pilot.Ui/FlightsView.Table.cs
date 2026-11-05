using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page's table: its header, columns, sort keys and the cells of a flight's row.
internal sealed partial class FlightsView<TSurface>
{
    private Layout.Node Header(FlightsScreen screen)
    {
        var buttons = new List<Layout.Node>();
        if (CanvasAvailable && screen.Lines.Count > 0)
        {
            buttons.Add(ViewSwitch());
        }
        if (CanCommand && !Wizard.IsOpen && Wizard.LoadRepos is not null)
        {
            buttons.Add(CardsView<TSurface>.IconPill(Layout.IconKind.Plus, "New flight", "flights:new", () => Wizard.Open(), square: AboutCard.HeaderButton));
        }
        // Discovering, discovery down, or an ask still waiting: nothing the dashboard waits for is invisible. The nodes heard
        // are not listed here: the node pills above and the Fleet page (with its Connect) show them, and a chip per node here
        // said it twice (SE1, 2026-10-24).
        var lines = new List<Layout.Node>();
        if (Maintenance)
        {
            lines.Add(Layout.Builder.Text(MaintenanceNotice.LineFor(P), 12f, Held).RowH(20f));
        }
        if (Lan is { Quiet: false } lan)
        {
            lines.Add(Layout.Builder.Text(lan.Line(screen.AsOf), 12f, Held).RowH(LanRowHeight));
        }
        return TopBar.Build(new PageHeader("flights")
        {
            Counts = screen.Counts,
            Hidden = screen.Hidden,
            HiddenAction = "flights:play-all",
            Buttons = buttons,
            Source = PageHeader.SourceLine(screen.Source, screen.AsOf, screen.Outside),
            Lines = lines,
        }, new TopBarParts(Nodes, Play, Autopilot, About, Night));
    }

    private Layout.Node Main(FlightsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text(screen.Hidden is null ? "No flights yet: New flight, above, makes one" : "No flight in play: Everything, above, shows the landed", 16f, Dim, TextAlign.Center).Stretch();
        }
        if (CanvasShown && MapFits(screen))
        {
            return CanvasBody(screen);
        }
        if (GridShown || CanvasShown)
        {
            return GridBody(screen);
        }

        var marked = screen.Lines.Any(l => l.Node is not null);
        var columns = Table.Fit(Columns(marked, screen.Lines.Any(l => l.Stale is not null || l.StaleSummary is not null)));
        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i, marked, columns);
        }

        return Layout.Builder.VStack(
                Table.Header(columns, Scale, NightOrNull),
                Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    /// <summary>
    /// The list's columns, header and rows both over them: Node only while the page merges several nodes. A stale row's mark sits
    /// under its code, so the Flight column is wide enough for it while one shows. Repo is dropped on a narrow window, as
    /// Branch and Sessions are: as a fixed column it ran into What at 760 wide and overprinted it.
    /// </summary>
    private static IReadOnlyList<ListColumn> Columns(bool marked, bool stale)
    {
        var columns = new List<ListColumn>
        {
            Sorted(ListColumn.Fixed("Flight", stale ? StaleCodeWidth : CodeWidth, CodeMin)),
            Sorted(new ListColumn("What", Layout.Sizing.Star(1f, 0f), WhatDropBelow)),
            Sorted(ListColumn.Optional("Repo", WhereWidth)),
            Sorted(ListColumn.Fixed("State", StateWidth, StateMin)),
            Sorted(ListColumn.Fixed("Legs", LegsWidth), descendingFirst: true),
            Sorted(ListColumn.Optional("Branch", BranchWidth)),
            Sorted(ListColumn.Optional("Sessions", SessionsWidth), descendingFirst: true),
        };
        if (marked)
        {
            columns.Insert(0, Sorted(ListColumn.Fixed("Node", NodeWidth, NodeMin)));
        }
        return columns;
    }

    // A column that sorts by a click on its title, as its squeezed form does.
    private static ListColumn Sorted(ListColumn column, bool descendingFirst = false) =>
        column with
        {
            Sortable = true,
            DescendingFirst = descendingFirst,
            Squeezed = column.Squeezed is { } squeezed ? Sorted(squeezed, descendingFirst) : null,
        };

    // What each column sorts by: the flight by its code, the legs by how many are done, the state in the page's own order.
    private static readonly IReadOnlyDictionary<string, Func<FlightLine, object?>> SortKeys = new Dictionary<string, Func<FlightLine, object?>>(StringComparer.Ordinal)
    {
        ["Node"] = l => l.Node?.Name ?? "",
        ["Flight"] = l => l.Code,
        ["What"] = l => l.Title,
        ["Repo"] = l => l.Row.Repo,
        ["State"] = l => (l.Row.State switch { FlightState.Flying => 0, FlightState.Held => 1, _ => 2 }, l.State),
        ["Legs"] = l => (l.LegRows.Count(r => r.State == LegState.Done), l.LegRows.Count),
        ["Branch"] = l => l.Branch ?? "",
        ["Sessions"] = l => l.Sessions.Length > 0 && char.IsDigit(l.Sessions[0]) ? int.Parse(l.Sessions.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) : 0,
    };

    private Layout.Node Label(string text) => Layout.Builder.Text(text, 12f, Faint).Selectable();

    // Past these a text may well be cut by its column's ellipsis, so it carries its whole text as hover text: what a flight or leg
    // is held for runs past a column ("waiting for PNG243, SharpAstro.Png.Extensions.Experimental 3.23 on nuget.org").
    private const int TitleFits = 40;
    private const int StateDetailFits = 24;
    private const int LegsFits = 28;
    private const int DetailLegFits = 28;
    private const int CardTitleFits = 26;
    private const int CardLineFits = 30;

    private static Layout.Node Whole(Layout.Node node, string text, int fits) => text.Length > fits ? node.WithTooltip(text) : node;

    /// <summary>The colour of a flight's state: dim for a stale row, which no longer says what the flight is doing.</summary>
    private RGBAColor32 StateColor(FlightLine line) =>
        line.Stale is not null ? Dim : line.MoveMark is not null ? P.FailedText : line.Queued is not null ? Held : line.Row.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };

    /// <summary>The stripe at a row's or card's left edge: <see cref="StateColor"/>, except that a failed move's stripe is the red of a failed leg, its words being the lighter red that holds 4.5:1 on the failed ground.</summary>
    private RGBAColor32 StripeColor(FlightLine line) => line.MoveMark is not null ? Failed : StateColor(line);

    // The legs, and under them the flight's PR chip when it has one: the list's row (54 with its padding) holds the 22 line and the 20 chip exactly, and the chip is the same as everywhere.
    private Layout.Node LegsCell(FlightLine line, bool landed)
    {
        return PullRequestChip(line, PullRequestChipWidth, PullRequestChipHeight) is { } chip
            ? Layout.Builder.VStack(Layout.Builder.Text(line.Legs, 13f, landed ? Dim : Body).RowH(22f), chip.HFixed(PullRequestChipHeight))
            : ListTable.Top(Whole(Layout.Builder.Text(line.Legs, 13f, landed ? Dim : Body), line.Legs, LegsFits));
    }

    private Layout.Node Row(FlightLine line, int index, bool marked, IReadOnlyList<ListColumn> columns)
    {
        // A stale row is drawn as a landed one is, dim: its state is the last the node told, not what it is now.
        var landed = line.Row.State == FlightState.Landed || line.Stale is not null;
        var color = StateColor(line);
        var text = landed ? Dim : Bright;
        // A failed move's row is red, and its secondary words are Body rather than Dim: Dim is 4.1:1 on the red, Body is 8:1.
        var failed = line.MoveMark is not null;
        var dim = failed ? Body : Dim;
        var cells = new List<Layout.Node>
        {
            // A stale row's mark is under its code, in words and in the amber of a held thing: at a narrow width the State column is off the page.
            line.StaleMark is { } mark
                ? Layout.Builder.VStack(
                    Layout.Builder.Text(line.Code, 15f, text).RowH(22f),
                    Whole(Layout.Builder.Text(mark, 11f, Held).RowH(18f), mark, 0))
                : line.StaleWorkMark is { } workMark
                    ? Layout.Builder.VStack(
                        Layout.Builder.Text(line.Code, 15f, text).RowH(22f),
                        Whole(Layout.Builder.Text(workMark, 11f, Held).RowH(18f), workMark, 0))
                : line.ShownPriority is { } priority
                    ? Layout.Builder.VStack(
                        Layout.Builder.Text(line.Code, 15f, text).RowH(22f),
                        Whole(Layout.Builder.Text(priority, 11f, line.PriorityMark is null ? Dim : Held).RowH(18f), priority, 0))
                    : ListTable.Top(Layout.Builder.Text(line.Code, 15f, text)),
            line.StaleSummary is { } staleSummary
                // The stale summary and the Ask the watchtower button under the title (#736): the row's 22 line and its 20 line, as the legs cell has.
                ? Layout.Builder.VStack(
                    Whole(Layout.Builder.Text(line.Title, 14f, landed ? Dim : Body).RowH(22f), line.Title, TitleFits),
                    Layout.Builder.HStack(
                        [Whole(Layout.Builder.Text(staleSummary, 11f, Held), $"{staleSummary}{(line.StaleRaise is { } raise ? $" ({raise})" : "")}", 0).WStar(),
                         .. (AskButton(line) is { } ask ? new[] { ask } : [])]).WithGap(6f).RowH(20f))
                : ListTable.Top(Whole(Layout.Builder.Text(line.Title, 14f, landed ? Dim : Body), line.Title, TitleFits)),
            ListTable.Top(Layout.Builder.Text(line.Where, 13f, dim)),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.State, 14f, color).RowH(22f),
                Whole(Layout.Builder.Text(line.RowDetail ?? "", 11f, failed ? P.FailedText : (line.Row.State == FlightState.Held || line.Queued is not null) && line.Stale is null ? Body : Dim).RowH(18f), line.RowDetail ?? "", StateDetailFits)),
            LegsCell(line, landed),
            ListTable.Top(Layout.Builder.Text(line.Branch, 12f, dim)),
            line.TokensText is { } spent
                ? Layout.Builder.VStack(
                    Layout.Builder.Text(line.Sessions, 13f, landed ? Dim : Body).RowH(22f),
                    Whole(Layout.Builder.Text(spent, 11f, dim).RowH(18f), spent, 0))
                : ListTable.Top(Layout.Builder.Text(line.Sessions, 13f, landed ? Dim : Body)),
        };
        if (marked)
        {
            cells.Insert(0, ListTable.Top(Layout.Builder.Text(line.Node?.Name ?? "", 13f, landed ? Dim : Body).Whole(line.Node?.Name)));
        }
        var row = Table.Row(columns, Layout.Builder.Spacer().Bg(StripeColor(line)), cells)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(failed ? P.FailedFocusBg : RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at))
            // The same brief as the Home chip (#258). A cell cut by its column keeps its own whole-text tooltip, which is the
            // innermost and so wins over the cell; the rest of the row says the brief.
            .WithTooltip(ChipBrief.Text(ChipBrief.Flight(line)))
            .DoubleClickable(_ =>
            {
                ListCursor.MoveTo(index);
                OpenDetail(line.Code);
            });
        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(failed ? P.FailedFocusBg : RowFocusBg) : failed ? row.Bg(P.FailedBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }
}
