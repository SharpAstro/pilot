using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page's layout: rendering, the keys, the page tree and a flight's details, with the PR chip and link.
internal sealed partial class FlightsView<TSurface>
{
    // The PR chip of a flight, the one the Pull requests page draws, the width given; a click opens that row there. Null for a flight with no PR
    // the node lists (none opened, not read yet, or closed), or where the host cannot open the page.
    private Layout.Node? PullRequestChip(FlightLine line, float width, float height)
    {
        if (OnPullRequest is not { } open || PullRequestLinks.RowOf(PullRequests, line.Row) is not { } pr)
        {
            return null;
        }
        var chip = PullRequestsView<TSurface>.Chip(P, pr)
            .Follows(new HitResult.ButtonHit($"flight:{line.Code}:pr-chip"), _ => open(pr.Repo, pr.Number))
            .WithTooltip($"{pr.Label}: show #{pr.Number} on the Pull requests page")
            .HFixed(height);
        // In the middle of the row it sits in, whatever that row's height.
        return Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), chip, Layout.Builder.Spacer().HStar()).WFixed(width);
    }

    // A flight's PR as a link: "PR #n" opens its row on the Pull requests page, a small ↗ beside it the browser. Where the host has no
    // such page, or the PR has no row, "PR #n ↗" is the browser link it always was.
    private Layout.Node PullRequestLink(FlightLine line, string url, string action)
    {
        var flight = line.Row;
        var pr = PullRequestLinks.Of(flight);
        // A PR the node does not list (landed or closed, or not read yet) has no row: the browser link it always was.
        var route = OnPullRequest is { } open && pr is not null && PullRequestLinks.Lists(PullRequests, pr)
            ? (Action?)(() => open(pr.Repo, pr.Number))
            : null;
        // Qualified: this method is named PullRequestLink too.
        return Pilot.Ui.PullRequestLink.Of(_links, action, $"PR #{flight.PullRequest}", pr?.ToString() ?? url, url, OnOpen, route, "the Pull requests page",
                Landed, RowHoverBg, mayBeCut: false, fill: false)
            // HFixed, not RowH: RowH makes the width a star, which a flow's cell measures as nothing.
            .HFixed(20f);
    }

    public void Render(FlightsScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        _mapBodySeen = null;
        PageWidth = Table.PageWidth = Scale.ToDesignX(bounds.Width);
        NoteHeight(bounds);
        var arranged = RenderLayout(Tree(screen), bounds, drawFill: (fill, rect) =>
        {
            DrawCanvas(fill, rect);
            Gallery.DrawFill(fill, rect);
        });
        _arranged = arranged;
        NoteLayersArranged(arranged);
        // A leg a card's link asked for: the layout just made knows where its block sits, so scroll there and paint again at the new offset.
        // The map's body was not the size its tree was built for (the first frame, or a resized window): draw again at the right fit.
        if (RevealLeg(arranged) || NoteMapBody())
        {
            Render(screen, bounds);
        }
    }

    /// <summary>
    /// The page as a tree of <paramref name="screen"/>, in design units: what <see cref="Render"/> paints on pixels, and
    /// what pilot-tui arranges in terminal cells (the selection, the why field and their callbacks the same).
    /// </summary>
    internal Layout.Node Tree(FlightsScreen screen)
    {
        Palette.Use(Night);
        // In play, the landed are left out of the table; an open flight's page still finds its flight among them.
        var whole = screen;
        // A flight a jump opened is shown too, until its page is shut and the selection has moved off it.
        if (_pinned is not null && _openCode != _pinned && !_selectPinned
            && !(Selected >= 0 && Selected < _screen.Lines.Count && _screen.Lines[Selected].Code == _pinned))
        {
            _pinned = null;
        }
        if (Play is { InPlay: true })
        {
            screen = screen.InPlay(_pinned);
        }
        // A clicked title orders the list only, never the grid or the canvas; the flight selected before it is found again by its code.
        if (!GridShown && !CanvasShown && Table.Sort is not null)
        {
            var kept = Selected >= 0 && Selected < _screen.Lines.Count ? _screen.Lines[Selected].Code : null;
            screen = screen with { Lines = Table.Order(screen.Lines, l => l.Node?.Name ?? "", SortKeys) };
            if (kept is not null && screen.Lines.ToList().FindIndex(l => l.Code == kept) is >= 0 and var moved)
            {
                ListCursor.Open(ListId, moved, screen.Lines.Count);
            }
        }
        // A refresh that reorders the flights keeps the selection on the same flight, found by node and code, not on the same index.
        // (A clicked title's sort above has already moved the cursor by code, against the order it replaced.)
        if (!(!GridShown && !CanvasShown && Table.Sort is not null) && !_selectPinned && screen.Lines.Count > 0 && Selected >= 0 && Selected < _screen.Lines.Count)
        {
            var (keptNode, keptCode) = (_screen.Lines[Selected].Node?.Key ?? "", _screen.Lines[Selected].Code);
            var at = -1;
            for (var i = 0; i < screen.Lines.Count; i++)
            {
                if (screen.Lines[i].Code == keptCode && (screen.Lines[i].Node?.Key ?? "") == keptNode)
                {
                    at = i;
                    break;
                }
            }
            if (at >= 0 && at != Selected)
            {
                ListCursor.Open(ListId, at, screen.Lines.Count);
            }
        }
        if (screen.Lines.Count == 0)
        {
            ListCursor.Close();
        }
        else if (_selectPinned && screen.Lines.ToList().FindIndex(l => l.Code == _pinned) is >= 0 and var pinnedAt)
        {
            // Back from its page, the list has it selected.
            _selectPinned = false;
            ListCursor.Open(ListId, pinnedAt, screen.Lines.Count);
        }
        else if (!ListCursor.IsOpen || _count != screen.Lines.Count)
        {
            ListCursor.Open(ListId, Math.Clamp(ListCursor.Index, 0, screen.Lines.Count - 1), screen.Lines.Count);
        }
        _count = screen.Lines.Count;
        _screen = screen;
        WhyInput.OnCommit = text => SendWhy(text);
        WhyInput.OnCancel = CloseWhy;
        return Build(screen, whole);
    }

    public override bool HandleInput(InputEvent evt)
    {
        // The wizard, open, takes Escape (a step back, or out) and Enter on its review (Create); the flights' list keys wait.
        if (Wizard.IsOpen)
        {
            switch (evt)
            {
                case InputEvent.KeyDown { Key: InputKey.Escape }:
                    Wizard.Back();
                    return true;
                case InputEvent.KeyDown { Key: InputKey.Enter } when Wizard.Current == NewFlightWizard.Step.Review:
                    Wizard.GoOn();
                    return true;
                default:
                    return false;
            }
        }
        // A link's menu, and the keys that reach it, ahead of the page's.
        if (evt is InputEvent.KeyDown linkKey && _links.HandleKey(linkKey, this))
        {
            return true;
        }
        if (evt is InputEvent.KeyDown { Key: var viewerKey } && Gallery.HandleKey(viewerKey))
        {
            return true;
        }
        // An open why field takes Escape ahead of the page's own (the field has the keyboard while it is focused; this is for after).
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && _asking is not null)
        {
            CloseWhy();
            return true;
        }
        // Enter opens the selected flight's page; Escape goes back to the table.
        if (evt is InputEvent.KeyDown { Key: InputKey.Enter } && !IsDetailOpen && Selected >= 0 && Selected < _screen.Lines.Count)
        {
            OpenDetail(_screen.Lines[Selected].Code);
            return true;
        }
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && IsDetailOpen)
        {
            GoUp();
            return true;
        }
        // The wheel over the map pans it (Ctrl+wheel is left to zoom); the layer menu's own list took it first where it was over it.
        if (!IsDetailOpen && evt is InputEvent.Scroll wheel && HandleMapScroll(wheel))
        {
            return true;
        }
        if (IsDetailOpen)
        {
            return evt is InputEvent.KeyDown { Key: var scrollKey } && ScrollDetail(scrollKey);
        }
        // On the map the arrows and the zoom chords move the view; in List and Grid the arrows move the selection.
        if (evt is InputEvent.KeyDown mapKey && HandleMapKey(mapKey))
        {
            return true;
        }
        return evt is InputEvent.KeyDown key && HandleListKey(key.Key, key.Modifiers);
    }

    internal Layout.Node Build(FlightsScreen screen, FlightsScreen? whole = null)
    {
        var selected = Selected >= 0 && Selected < screen.Lines.Count ? screen.Lines[Selected] : null;
        var open = OpenLine(whole ?? screen);
        // The wizard, or an open flight's page, takes the flights' place; the header stays.
        var body = Wizard.IsOpen ? Wizard.Tree() : open is not null ? DetailPage(FlightDetail.From(open, screen.AsOf, Spend.FirstOrDefault(s => s.Flight == open.Code), Decisions.GetValueOrDefault(open.Code))) : Main(screen);
        var parts = new List<Layout.Node> { Header(screen) };
        if (!Wizard.IsOpen && open is null)
        {
            // Flights whose move failed, in red above the table: what happened, and Retry move. The open flight's page has its own.
            parts.AddRange(MoveFailureBand.All<TSurface>(screen.Lines, P, RetryOf, presses: Presses).Select(band => Layout.Builder.VStack(band).Pad(Gutter, 4f).WStar().HAuto()));
        }
        parts.Add(body.HStar(1f, BodyMinHeight));
        if (selected is not null && !Wizard.IsOpen && open is null)
        {
            // Its height is its content's, and no less than it needs: a window with less to give it (a header that wrapped onto many
            // lines) shows the table alone, which is what the page is for, rather than a panel past the window's foot.
            parts.Add(Details(selected).CollapseBelow(DetailHeight));
        }
        if (CanCommand && Said is { Length: > 0 } said)
        {
            // Wrapped, never cut: a refusal's reason is what the person came to read.
            parts.Add(Prose(said, 11f, Dim).Pad(Gutter, 4f).Bg(HeaderBg).WStar().HAuto());
        }
        var page = WhenShort(Layout.Builder.VStack([.. parts]).Bg(Ground).Stretch());
        // After the page's text and before the menus over it: where the selection is drawn.
        page = WithPageSelection(page);
        page = About?.Over(page, NightOrNull) ?? page;
        page = Gallery.Over(page);
        page = _menu.IsOpen ? Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_menuAt, _menu, 13f, Bright, MenuBg, RowFocusBg)) : page;
        return _links.Over(page);
    }

    /// <summary>The selected flight: its legs, its review, its artifacts, and what can be done with it.</summary>
    private Layout.Node Details(FlightLine line)
    {
        var flight = line.Row;
        var legs = new List<Layout.Node> { Label($"{line.Code} · legs").RowH(20f) };
        foreach (var leg in line.LegRows.Take(5))
        {
            var steps = leg.Checklist.Length == 0 ? "" : $" · {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
            var color = leg.Hold is not null || (leg.Wait is not null && leg.State == LegState.Flying) ? Held : leg.State switch { LegState.Done => Landed, LegState.Flying => Flying, LegState.Failed => Failed, _ => Dim };
            var legState = $"{FlightWaiting.LegState(leg)}{steps}{(leg.GoArounds > 0 ? $" · go-around {leg.GoArounds}" : "")}{(LegDetail.AttemptOf(leg) is { } attempt ? $" · {attempt}" : "")}";
            legs.Add(Layout.Builder.HStack(
                    Whole(Layout.Builder.Text($"{leg.Order}. {leg.Name}", 13f, Body).WStar(1f, 0f, 150f), $"{leg.Order}. {leg.Name}", 0),
                    Whole(Layout.Builder.Text(legState, 12f, color).WStar(), legState, 0))
                .RowH(20f));
        }
        if (line.LegRows.Count == 0)
        {
            legs.Add(Layout.Builder.Text(line.Node is null or { Here: true } ? "no legs yet: Plan a leg" : "no legs yet", 12f, Dim).RowH(20f));
        }
        if (flight.Review is { } review)
        {
            legs.Add(DetailLine($"review {review.State.ToString().ToLowerInvariant()}{(review.Report is { Length: > 0 } r ? $": {r.Split('\n')[0]}" : "")}",
                review.State == ReviewState.Passed ? Flying : review.State == ReviewState.Changes ? Held : Dim));
        }

        var marks = FlightMarks.From(flight);
        if (marks.Line is { } markLine)
        {
            legs.Add(DetailLine(markLine, MarksColor(flight)));
        }
        if (marks.Warning is { } warning)
        {
            legs.Add(DetailLine($"⚠ GitHub: {warning}", Held));
        }
        if (marks.Pending is { } pending)
        {
            legs.Add(DetailLine(pending, Held));
        }

        // Its jobs: the long runs its workers handed to the node, running first.
        foreach (var job in Jobs.Where(j => j.Flight == flight.Code).OrderBy(j => j.State switch { JobState.Running => 0, JobState.Queued => 1, _ => 2 })
            .ThenByDescending(j => j.StartedAt).Take(2))
        {
            var isPaused = job.State == JobState.Running && job.PausedAt is not null;
            var color = isPaused ? Held : job.State switch { JobState.Running => Flying, JobState.Queued => Held, JobState.Succeeded => Landed, _ => Failed };
            var waiting = job.State == JobState.Queued && job.Step is { } step ? $" ({step})" : "";
            var failing = job.State == JobState.Running && job.FailuresSoFar() is { } so ? $" · {so}" : "";
            legs.Add(DetailLine($"job {job.Id} {(isPaused ? "paused" : job.State.ToString().ToLowerInvariant())}{waiting}{failing}: {job.Command}", failing.Length > 0 ? Failed : color));
        }

        var artifacts = new List<Layout.Node> { Label("artifacts").RowH(20f) };
        var all = line.LegRows.SelectMany(l => l.Artifacts.Select(a => (Leg: l.Name, Artifact: a))).OrderByDescending(x => x.Artifact.At).Take(5).ToList();
        if (flight.PullRequestUrl is { } url)
        {
            artifacts.Add(PullRequestLink(line, url, $"pr:{line.Code}"));
        }
        if (marks.ReviewUrl is { } reviewUrl)
        {
            artifacts.Add(Link("review comment", reviewUrl, $"review:{line.Code}"));
        }
        foreach (var (leg, artifact) in all)
        {
            artifacts.Add(Link($"{artifact.Title} ({leg})", artifact.Location, $"artifact:{line.Code}:{artifact.Title}"));
        }
        if (all.Count == 0 && flight.PullRequestUrl is null && marks.ReviewUrl is null)
        {
            artifacts.Add(Layout.Builder.Text("nothing to look at yet", 12f, Dim).RowH(20f));
        }

        var buttons = new List<Layout.Node> { Pill("Open flight", $"flight:{line.Code}:open", () => OpenDetail(line.Code)) };
        if (CanCommand && WatchOnly(line) is { } watchedNode)
        {
            buttons.Add(Layout.Builder.Text(SessionsView<TSurface>.SteerHint(watchedNode), 12f, Dim).WAuto());
            // Connect stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(ConnectButton(watchedNode, $"flight:{line.Code}:connect"));
        }
        else if (CanCommand)
        {
            // Retry move is in the flight's red band, not said twice.
            foreach (var action in FlightAction.For(line).Where(a => a.Verb is not (Verbs.MoveFlight or Verbs.FlightPriority)))
            {
                buttons.Add(ActionButton(line, action, $"flight:{line.Code}:{action.Key}"));
            }
        }

        // A why asked for sits under the buttons, the panel a row taller while it is there.
        var why = WhyRow(line).ToList();
        // The columns are at least what the panel was (less its buttons' row), and more where they have more lines than that; they scroll
        // in what the window leaves them, the buttons flow onto lines of their own where the page is narrower than they are, and the
        // panel takes the height of both.
        var columns = Layout.Builder.HStack(
                Layout.Builder.VStack([.. legs]).WStar(1f, 0f, DetailColumn),
                Layout.Builder.VStack([.. artifacts]).WStar(1f, 0f, DetailColumn))
            .WithGap(24f)
            .WStar().HAuto().HClamp(DetailColumnsHeight);
        return Layout.Builder.VStack([
                Layout.Builder.VStack(columns).WithScroll(_detailColumnsScroll).WStar().HStarUpToContent(1f, DetailColumnsMinHeight),
                Layout.Builder.WrapH([.. buttons.Select(b => Layout.Builder.HStack(b).WAuto().HFixed(28f))]).WithGap(8f).WithLineGap(6f).WStar().HAuto(),
                .. RefusalOf(line),
                .. why])
            .Pad(Gutter, 8f)
            .Bg(PanelBg)
            .WStar().HStarUpToContent();
    }

    // The marks' colour: amber for changes, a disagreement or a write still to do, green for a pass, dim otherwise.
    private RGBAColor32 MarksColor(FlightRow flight) => FlightMarks.From(flight) switch
    {
        { Warning: not null } or { Pending: not null } => Held,
        _ => flight.Review?.StatusState switch { "success" => Flying, "failure" => Held, _ => Dim },
    };

    // A line of the panel: one row, ending in an ellipsis where the column is narrower than it, with the whole of it on hover.
    private static Layout.Node DetailLine(string text, RGBAColor32 color) => Layout.Builder.Text(text, 12f, color).Selectable().WithTooltip(text).RowH(20f);

    // A link that leaves the app: a click opens it, a right-click offers Copy URL (over the row's own menu, which stays on the rest of the row).
    private Layout.Node Link(string text, string location, string action) =>
        _links.OnLink(Layout.Builder.Text(text + Followable.Away, 12f, Landed)
            .BgHover(RowHoverBg)
            .Follows(new HitResult.ButtonHit(action), _ => OnOpen?.Invoke(location))
            .RowH(20f), location);

    private Layout.Node Pill(string label, string action, Action onClick) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());
}
