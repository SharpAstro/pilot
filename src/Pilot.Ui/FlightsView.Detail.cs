using System.Collections.Immutable;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// A flight's own page: the Flights table gives a leg one line, this gives each leg its goal, Done-when, checklist,
// report and artifacts, where the table's selected-flight panel has room for five legs and no words.
internal sealed partial class FlightsView<TSurface>
{
    private readonly ListScrollController _detailScroll = new() { Mode = ScrollBarMode.Interactive };
    private string? _openCode;

    /// <summary>The attachments the open page shows and the viewer that opens one; the chrome shares one with the Sessions page.</summary>
    public AttachmentGallery Gallery { get; set; } = new();

    /// <summary>Whether a flight's page is open in place of the table.</summary>
    public bool IsDetailOpen => _openCode is not null;

    /// <summary>The open page's flight code, or null.</summary>
    public string? OpenCode => _openCode;

    /// <summary>The flight the page is about: the one open, else the one selected; the cards panel lights its cards.</summary>
    public string? FocusCode => _openCode ?? (Selected >= 0 && Selected < _screen.Lines.Count ? _screen.Lines[Selected].Code : null);

    /// <summary>The cards panel as this frame shows it: the open page finds its flight's card that offers the merge.</summary>
    public CardsScreen? Cards { get; set; }

    /// <summary>The window's summaries: the flight's page shows its own and asks for a new one. Null shows none.</summary>
    public SummaryBoard? Summary { get; set; }

    private readonly SummaryCard _summaryCard = new();

    /// <summary>Runs a card's choice, as the panel's button does, so the card closes once it is done.</summary>
    public Action<CardRow, int>? OnAnswerCard { get; set; }

    /// <summary>Opens the Chat page about the flight's card: its <b>Chat about this</b>. Null leaves the button off.</summary>
    public Action<CardRow>? OnChatAboutCard { get; set; }

    /// <summary>The button of a card whose press is with the node, as the cards panel marks it: the page's Merge is the same card's.</summary>
    public Func<string, string?>? CardWorking { get; set; }

    /// <summary>Called as a flight's page is about to open, before what the window shows changes: the chrome records the place being left.</summary>
    public Action? Opening { get; set; }

    /// <summary>Opens <paramref name="code"/>'s page, from the top.</summary>
    public void OpenDetail(string code)
    {
        Opening?.Invoke();
        ClearSelection();
        _openCode = code;
        // Its row is shown whatever In play says while its page is open, and selected once the page is shut.
        _pinned = code;
        _detailScroll.AtomOffset = 0;
        // A leg link that never resolved must not scroll the next page opened.
        _revealLeg = null;
    }

    public void CloseDetail()
    {
        _revealLeg = null;
        _selectPinned = _pinned is not null && _pinned == _openCode;
        _openCode = null;
        ClearSelection();
    }

    // The flight a jump opened: its row stays on the page whatever In play says, until the selection moves off it.
    private string? _pinned;
    private bool _selectPinned;

    /// <summary>What the Back pill and Escape do: the chrome's Back, which returns to wherever the page was opened from. Null (a host with no history) closes it.</summary>
    public Action? OnBack { get; set; }

    /// <summary>Leaves the page the way Back does: through <see cref="OnBack"/>, or by closing it where there is none.</summary>
    public void GoBack()
    {
        if (OnBack is { } back)
        {
            back();
        }
        else
        {
            CloseDetail();
        }
    }

    /// <summary>The open page's scroll position, in the layout's own units (a pixel in the window, a cell in the terminal).</summary>
    internal int DetailOffset => _detailScroll.AtomOffset;

    // The page's keys, so its lower legs are reachable without a mouse (pilot-tui has none): a line for the arrows, the
    // viewport less a line for the page keys, the ends for Home and End. The controller clamps at both ends.
    private bool ScrollDetail(InputKey key)
    {
        var line = MathF.Max(1f, _detailScroll.LineExtentPx);
        var page = MathF.Max(line, _detailScroll.Viewport.Height - line);
        var target = key switch
        {
            InputKey.Up => _detailScroll.Offset - line,
            InputKey.Down => _detailScroll.Offset + line,
            InputKey.PageUp => _detailScroll.Offset - page,
            InputKey.PageDown => _detailScroll.Offset + page,
            InputKey.Home => 0f,
            InputKey.End => _detailScroll.MaxOffset,
            _ => float.NaN,
        };
        if (float.IsNaN(target))
        {
            return false;
        }
        _detailScroll.AtomOffset = (int)MathF.Round(target);
        return true;
    }

    // The open flight's line, or null: a flight the node has forgotten closes its page.
    private FlightLine? OpenLine(FlightsScreen screen)
    {
        if (_openCode is null)
        {
            return null;
        }
        var line = screen.Lines.FirstOrDefault(l => l.Code == _openCode);
        if (line is null)
        {
            _openCode = null;
        }
        return line;
    }

    private Layout.Node DetailPage(FlightDetail detail)
    {
        var line = detail.Line;
        var flight = line.Row;
        var color = StateColor(line);
        var head = new List<Layout.Node>
        {
            Pill("← Back", "flight:back", GoBack),
            Layout.Builder.Text(line.Code, 16f, Bright).WAuto().Selectable(),
            WrappedProse.Of(line.Title, 14f, Body, selectable: true).WAuto(),
        };
        // The PR is in the header, beside the title, with where it stands: it sat in the facts line below, past a long review chip.
        if (flight.PullRequestUrl is { } url)
        {
            head.Add(PullRequestLink(line, url, $"pr:{line.Code}"));
        }
        // Where the Pull requests page has it standing (Ready, Waiting, ...): beside the PR link, not in a second PR line in the facts.
        // It says what the flight's own word (open, queued) would, and more, so the word is for a PR the page does not list (merged).
        if (flight.PullRequestUrl is not null && PullRequestChip(line, PullRequestChipWidth, PullRequestChipHeight) is { } chip)
        {
            head.Add(chip);
        }
        else if (FlightMarks.PrState(flight) is { } prState)
        {
            // Its cell is as tall as the link's, so the two share one middle.
            head.Add(Layout.Builder.Text(prState, 12f, PrStateColor(prState)).WAuto().Selectable());
        }
        // The stale mark is a line of its own below: here it overlapped the title and cut the buttons at a narrow width.
        head.Add(WrappedProse.Of(line.StateDetail is null ? line.State : $"{line.State} ({line.StateDetail})", 13f, color, selectable: true).WAuto());
        // The buttons wrap onto lines of their own under the title, as Home's chips do: in the head row they took the title's room and
        // the last (a stale flight's dead "Remove render") ran past the page's edge at a narrow width.
        // Retry move is in the red band under the head, not said twice.
        var buttons = CanCommand
            ? FlightAction.For(line).Where(a => a.Verb != Verbs.MoveFlight).Select(action => ActionButton(line, action, $"flight:{line.Code}:{action.Verb}")).ToList()
            : [];

        var facts = new List<Layout.Node>
        {
            WrappedProse.Of($"{line.Where} · {line.Branch} · {line.Legs}", 12f, Dim, selectable: true).WAuto(),
        };
        if (flight.FromIdea is { Length: > 0 } fromIdea)
        {
            // The idea it was promoted from: the same issue, so its id is the flight's issue number.
            facts.Add(Layout.Builder.Text($"from {fromIdea}", 12f, Dim).WAuto());
        }
        if (flight.Review is { } review)
        {
            facts.Add(Layout.Builder.Text($"review {review.State.ToString().ToLowerInvariant()}", 12f,
                review.State == ReviewState.Passed ? Flying : review.State == ReviewState.Changes ? Held : Dim).WAuto().Selectable());
        }
        var marks = FlightMarks.From(flight);
        if (marks.Line is { } markLine)
        {
            facts.Add(WrappedProse.Of(markLine, 12f, MarksColor(flight), selectable: true).WAuto());
        }
        if (marks.ReviewUrl is { } reviewUrl)
        {
            facts.Add(Link("review comment", reviewUrl, $"review:{line.Code}").WAuto());
        }
        if (marks.Warning is { } warning)
        {
            facts.Add(WrappedProse.Of($"⚠ GitHub: {warning}", 12f, Held, selectable: true).WAuto());
        }
        if (marks.Pending is { } pending)
        {
            facts.Add(WrappedProse.Of(pending, 12f, Held, selectable: true).WAuto());
        }

        _legBlocks.Clear();
        var legs = detail.Legs.Count == 0
            ? [Layout.Builder.Text(line.Node is null or { Here: true } ? "no legs yet: Plan a leg" : "no legs yet", 13f, Dim).RowH(24f)]
            : detail.Legs.Select(l => LegBlock(line.Code, l)).ToList();
        // The flight's own summary leads what scrolls: the legs are the detail under it.
        if (Summary is { } board && _summaryCard.Build(board, board.Shelf.Flight(line.Code, line.Node), P, _screen.AsOf) is { } summary)
        {
            legs.Insert(0, summary);
        }

        List<Layout.Node> page =
        [
            FlowRow.Of(32f, 10f, head),
        ];
        if (MoveFailureBand.Build<TSurface>(line, P, RetryOf(line)) is { } band)
        {
            page.Add(band);
        }
        if (line.Provenance is { } provenance)
        {
            page.Add(Prose(provenance, 12f, Dim));
        }
        if (buttons.Count > 0)
        {
            page.Add(Layout.Builder.WrapH([.. buttons]).WithGap(10f).WithLineGap(6f).WStar());
        }
        // A why asked for sits under the buttons it was asked by.
        page.AddRange(WhyRow(line));
        if (line.StaleMark is { } stale)
        {
            // Wrapped, never cut: it says why the buttons above are dead.
            page.Add(Prose($"⚠ {stale}: this is the last row the node told, and its commands wait until it answers", 12f, Held));
        }
        if (ReadyToMerge(line) is { } ready)
        {
            page.Add(ready);
        }
        page.AddRange(ReviewAsks(detail));
        // The node's copies of the images the legs recorded, above the legs: the links in each leg stay as they were.
        // The facts and who is working lead what scrolls, as the summary does: they wrap onto as many lines as the page needs, and
        // above the scroll they pushed the page's foot past the window's.
        List<Layout.Node> scrolled = [FlowRow.Of(22f, 14f, facts), FlowRow.Of(26f, 10f, WhoWorks(line))];
        if (Gallery.Section($"flight:{line.Code}", detail.Attachments) is { } attachments)
        {
            scrolled.Add(attachments);
        }
        scrolled.AddRange(legs);
        return Layout.Builder.VStack(
                [.. page,
                Layout.Builder.VStack([.. scrolled]).WithGap(10f).WithScroll(_detailScroll).Stretch()])
            .Pad(Gutter, 8f)
            .Stretch();
    }

    // The PR's state in the palette's own colours, the word always beside it: green while it is open for review, amber in the queue, blue once merged, dim as a draft.
    private RGBAColor32 PrStateColor(string state) => state switch { "open" => Flying, "queued" => Held, "merged" => Landed, _ => Dim };

    /// <summary>
    /// <paramref name="action"/>'s button, or for a stale flight (its node unreachable) the same words as a dead one: a verb goes
    /// to the node, which cannot be asked, and a live button there read as a command that would be lost (pilot #325).
    /// </summary>
    private Layout.Node ActionButton(FlightLine line, FlightAction action, string id)
    {
        // A label long past a narrow page (a leg's name in "Redo leg ...") ends in an ellipsis, with the whole of it on hover.
        var label = CardsView<TSurface>.Ellipsize(action.Label, ActionFits);
        var whole = label == action.Label ? null : action.Label;
        return line.Stale is null
            ? whole is null ? Pill(label, id, () => Choose(action, line.Code)) : Pill(label, id, () => Choose(action, line.Code)).WithTooltip(whole)
            : CardsView<TSurface>.Dead(label).WithTooltip(whole is null ? StaleWhy(line) : $"{whole}: {StaleWhy(line)}");
    }

    private const int ActionFits = 24;

    /// <summary>Why a stale flight's commands are off, for a dead button's or a disabled entry's hover text.</summary>
    internal static string StaleWhy(FlightLine line) => $"{line.Stale} is unreachable: its commands wait until it answers";

    /// <summary>
    /// A flight whose PR waits on the person, said at the top of its page and not only in the panel: the card that offers
    /// the merge, its title, and its Merge, which runs the card's own choice, so the card closes as it would from the panel
    /// (SE1, 2026-10-25). Null when no such card is open.
    /// </summary>
    private Layout.Node? ReadyToMerge(FlightLine line)
    {
        if (Cards is not { } cards
            || cards.Open.FirstOrDefault(c => c.Subject == Verbs.FlightTarget(line.Code) && c.Choices.Any(ch => ch.Verb == Verbs.Merge)
                && (line.Node is null || cards.NodeOf(c) is not { } node || node.Key == line.Node.Key)) is not { } card)
        {
            return null;
        }
        var number = Array.FindIndex(card.Choices, ch => ch.Verb == Verbs.Merge) + 1;
        var choice = card.Choices[number - 1];
        var cells = new List<Layout.Node>
        {
            Layout.Builder.Spacer().Bg(Flying).WFixed(4f).HStar(),
            Layout.Builder.Text(card.Title, 14f, Bright).WStar(),
        };
        if (CanCommand && OnAnswerCard is { } answer)
        {
            // While the card's Merge is with the node (pressed here or on the panel) it reads "…" and takes no second press:
            // a second Merge went out, and its end cleared the first's working state while that was still pending.
            // A stale flight's (its node unreachable) is dead: the choice goes to the node, which cannot be asked.
            cells.Add(line.Stale is not null
                ? CardsView<TSurface>.Dead(choice.Label).WithTooltip(StaleWhy(line))
                : CardWorking?.Invoke(card.Id) is not null
                ? CardsView<TSurface>.Dead($"{choice.Label} …")
                : CardsView<TSurface>.ChoiceControl(choice, card.Detail, $"flight:{line.Code}:merge-card", () => answer(card, number)));
        }
        if (OnChatAboutCard is { } about && WatchOnly(line) is null)
        {
            cells.Add(line.Stale is not null
                ? CardsView<TSurface>.Dead(CardsView<TSurface>.ChatAboutLabel).WithTooltip(StaleWhy(line))
                : CardWorking?.Invoke(card.Id) is not null
                ? CardsView<TSurface>.Dead(CardsView<TSurface>.ChatAboutLabel)
                : Pill(CardsView<TSurface>.ChatAboutLabel, $"flight:{line.Code}:chat-card", () => about(card)));
        }
        return Layout.Builder.HStack([.. cells]).WithGap(12f).Pad(0f, 6f).Bg(RowFocusBg).RowH(40f);
    }

    /// <summary>
    /// Who is working on the flight now, each with its transcript a click away, or why nothing is: "flying" said only that
    /// the flight was open, so a flight with no planner and no legs looked busy while nothing ran (PIL208, 2026-10-24).
    /// </summary>
    private IEnumerable<Layout.Node> WhoWorks(FlightLine line)
    {
        if (line.Working.Count > 0)
        {
            foreach (var session in line.Working.Take(3))
            {
                // A leg's validator is a worker session whose leg is the leg's name and "-validator".
                var validated = session.Leg is { } named && named.EndsWith(ValidatorSuffix, StringComparison.Ordinal) ? named[..^ValidatorSuffix.Length] : null;
                var (doing, who) = session.Role switch
                {
                    SessionRole.Planner => ("planning", "Planner"),
                    SessionRole.Reviewer => ("reviewing", "Reviewer"),
                    _ when validated is not null => ($"validating {validated}", "Validator"),
                    _ => (session.Leg is { } leg ? $"flying {leg}" : "working", "Worker"),
                };
                var color = session.State == SessionState.Waiting ? Held : Flying;
                yield return WrappedProse.Of($"{doing} · {session.Name} · {session.State.ToString().ToLowerInvariant()}", 12f, color, selectable: true).WAuto();
                if (OnSession is { } open)
                {
                    // Named for whose it is: beside two sessions, two "Its transcript" said nothing of which was which.
                    var row = session;
                    // The transcript is read from the node: dead while it cannot be reached.
                    yield return line.Stale is null
                        ? Pill($"{who}'s transcript", $"flight:{line.Code}:session:{row.Id}", () => open(row, line.Node))
                        : CardsView<TSurface>.Dead($"{who}'s transcript").WithTooltip(StaleWhy(line));
                }
            }
        }
        else if (line.Row.State == FlightState.Flying && line.LegRows.Count == 0)
        {
            yield return WrappedProse.Of("not planned yet: no planner is running", 12f, Held).WAuto();
            if (CanCommand && OnAction is { } act)
            {
                yield return line.Stale is null
                    ? Pill("Plan it", $"flight:{line.Code}:plan", () =>
                        act(new FlightAction($"plan {line.Code}", Verbs.PlanFlight, Verbs.FlightTarget(line.Code), []) { Node = line.Node }))
                    : CardsView<TSurface>.Dead("Plan it").WithTooltip(StaleWhy(line));
            }
        }
        else if (line.Row.State == FlightState.Flying)
        {
            yield return WrappedProse.Of("nothing is running on it now", 12f, Dim).WAuto();
        }
    }

    private const string ValidatorSuffix = "-validator";

    private Layout.Node LegBlock(string code, LegDetail leg)
    {
        var color = leg.Row.Hold is not null || (leg.Row.Wait is not null && leg.Row.State == LegState.Flying) ? Held : leg.Row.State switch { LegState.Done => Landed, LegState.Flying => Flying, LegState.Failed => Failed, _ => Dim };
        var head = new List<Layout.Node>
        {
            WrappedProse.Of($"{leg.Order}. {leg.Name}", 15f, Bright, selectable: true).WAuto(),
            Layout.Builder.Text(leg.State, 13f, color).WAuto().Selectable(),
        };
        if (leg.StepsText is { } steps)
        {
            head.Add(Layout.Builder.Text(steps, 12f, Dim).WAuto().Selectable());
        }
        if (leg.GoArounds is { } goArounds)
        {
            head.Add(Layout.Builder.Text(goArounds, 12f, Held).WAuto().Selectable());
        }
        if (leg.Attempt is { } attempt)
        {
            head.Add(Layout.Builder.Text(attempt, 12f, Dim).WAuto().Selectable());
        }

        var rows = new List<Layout.Node> { FlowRow.Of(24f, 12f, head) };
        rows.Add(Field("Goal", leg.Goal));
        rows.Add(Field("Done when", leg.DoneWhen));
        if (leg.Check is { } check)
        {
            rows.Add(Field("Check", check, Dim));
        }
        foreach (var step in leg.Steps)
        {
            rows.Add(Labelled(Layout.Builder.Text(step.Done ? "[x]" : "[ ]", 12f, step.Done ? Flying : Dim).Selectable(), 28f, 8f, Prose(step.Text, 12f, step.Done ? Dim : Body)));
        }
        if (leg.Report is { } report)
        {
            rows.Add(Field(leg.Row.State == LegState.Failed ? "Why it failed" : leg.Row.Hold is not null ? "Holding" : "Report", report, leg.Row.State == LegState.Failed ? Failed : leg.Row.Hold is not null ? Held : Body));
        }
        foreach (var artifact in leg.Artifacts)
        {
            var links = new List<Layout.Node>
            {
                Layout.Builder.Text("artifact", 11f, Faint).WFixed(DetailLabelWidth),
                Link(artifact.Title, artifact.Location, $"artifact:{code}:{leg.Name}:{artifact.Title}").WAuto(),
            };
            if (artifact.Folder is { } folder)
            {
                links.Add(Link("folder", folder, $"artifact-folder:{code}:{leg.Name}:{artifact.Title}").WAuto());
            }
            rows.Add(FlowRow.Of(20f, 10f, links));
            var where = $"{artifact.When} · {artifact.Location}";
            rows.Add(Layout.Builder.Text(where, 11f, Faint, trim: TextTrim.Start).WithTooltip(where).RowH(20f).Selectable());
        }
        var block = Layout.Builder.VStack([.. rows])
            .WithGap(4f)
            .Pad(12f, 8f)
            .Bg(PanelBg)
            .WStar()
            .HAuto();
        _legBlocks[leg.Name] = block;
        return block;
    }

    // The node of each leg's block in the page built last, so layout can say where one sits (see RevealLeg).
    private readonly Dictionary<string, Layout.Node> _legBlocks = [];

    // The leg a card's link asked the page to scroll to, until the layout that knows where its block sits has run.
    private string? _revealLeg;

    /// <summary>Opens <paramref name="code"/>'s page with leg <paramref name="leg"/>'s block scrolled into view, once layout knows where it sits.</summary>
    public void OpenDetail(string code, string leg)
    {
        OpenDetail(code);
        _revealLeg = leg;
    }

    // The arrangement of the page painted last.
    private ImmutableArray<Layout.ArrangedNode<float>> _arranged;

    /// <summary>Whether the top of <paramref name="leg"/>'s block lies inside the detail viewport as last painted; false when the leg is not on the page.</summary>
    internal bool LegBlockInView(string leg)
    {
        if (!_legBlocks.TryGetValue(leg, out var block) || _arranged.IsDefault)
        {
            return false;
        }
        foreach (var node in _arranged)
        {
            if (ReferenceEquals(node.Node, block))
            {
                var (start, _) = _detailScroll.ContentExtentOf(new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height));
                return start >= _detailScroll.Offset - 1f && start < _detailScroll.Offset + _detailScroll.Viewport.Height;
            }
        }
        return false;
    }

    // Scrolls the asked-for leg to the top of the page's scroll, from the arrangement just made; true when the offset moved and the page wants painting again.
    private bool RevealLeg(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        if (_revealLeg is not { } leg || _openCode is null)
        {
            return false;
        }
        if (!_legBlocks.TryGetValue(leg, out var block))
        {
            // Its page is not on show yet (or the leg is not on it): asked again on the next frame, until the page is closed.
            return false;
        }
        foreach (var node in arranged)
        {
            if (ReferenceEquals(node.Node, block))
            {
                _revealLeg = null;
                var before = _detailScroll.AtomOffset;
                var (start, _) = _detailScroll.ContentExtentOf(new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height));
                // A span as tall as the view aligns by its start: the leg's block at the top.
                _detailScroll.RevealExtent(start, start + MathF.Max(node.Bounds.Height, _detailScroll.Viewport.Height + 1f));
                return _detailScroll.AtomOffset != before;
            }
        }
        return false;
    }

    private const float DetailLabelWidth = 76f;

    private Layout.Node Field(string label, string text, RGBAColor32? color = null) =>
        Labelled(Layout.Builder.Text(label, 11f, Faint), DetailLabelWidth, 10f, Prose(text, 13f, color ?? Body));

    /// <summary>
    /// A label beside wrapped text, as a two-column grid with rows as tall as their content: a grid measures each cell at
    /// its own column's width, where a stack measured the text as if it had the label's room too, so it came out a line
    /// short and ran into the row under it (Goal over Done when, and the last row cut off at the end of the scroll).
    /// </summary>
    private static Layout.Node Labelled(Layout.Node label, float labelWidth, float gap, Layout.Node text) =>
        Layout.Builder.Grid(2, label, text)
            .WithColumns(Layout.Sizing.Fixed(labelWidth), Layout.Sizing.Star())
            .WithGaps(0f, gap)
            .WithAutoRows()
            .WStar()
            .HAuto();

    // The node's own cap (LegRow.MaxReviewGoArounds, which LegCommander reads): past it, redoing the leg on the review's findings is the person's call.
    private const int ReviewRedoCap = LegRow.MaxReviewGoArounds;

    /// <summary>
    /// What the review asks, in full, where the flight is read: the page showed only a "review changes" chip while the findings,
    /// the red CI and the reached cap sat on a card cut to three lines (PIL294's #357, 2026-10-27). Nothing unless it asks.
    /// </summary>
    internal IEnumerable<Layout.Node> ReviewAsks(FlightDetail detail)
    {
        if (detail.Line.Row.Review is not { State: ReviewState.Changes, Report: { Length: > 0 } report } review)
        {
            yield break;
        }
        // Which head and round the report is from, and when the PR has moved on since: that report no longer speaks for the PR, so it is dimmed.
        var current = detail.Line.Row.GitHubHead;
        var older = review.ReviewedHead is { Length: > 0 } reviewed && current is { Length: > 0 } now
            && !string.Equals(reviewed, now, StringComparison.OrdinalIgnoreCase);
        var from = review.ReviewedHead is { Length: > 0 } read ? $"review of {FlightMarks.Short(read)}, round {review.GoAround + 1}" : $"review round {review.GoAround + 1}";
        yield return Layout.Builder.Text("The review asks for changes:", 13f, Held).RowH(22f).Selectable();
        yield return Prose(older ? $"{from}; the head is now {FlightMarks.Short(current!)}" : from, 12f, older ? Held : Dim);
        // The report is Markdown, as the transcript's words are (a finding a list item, a name in backticks as code), folded to
        // each finding's first lines until Show all opens it; a report that fits shows no pill.
        var code = detail.Line.Row.Code;
        var open = _reportOpen.Contains(code);
        var (cut, folded) = ReviewReport.Fold(report);
        yield return ReportMarkdown(open ? report : cut, open, older);
        if (folded)
        {
            // Clear of the last finding's line: the pill sat on its marker.
            yield return Layout.Builder.Spacer().HFixed(4f);
            yield return Layout.Builder.HStack(
                    Pill(open ? "Show less" : "Show all", $"flight:{code}:review-fold", () =>
                    {
                        if (!_reportOpen.Remove(code))
                        {
                            _reportOpen.Add(code);
                        }
                    }),
                    Layout.Builder.Spacer().WStar())
                .RowH(28f);
        }
        // Only the flight's last leg, as the node (LegCommander) decides it: an earlier capped leg with a later one planned is not the person's yet.
        if (detail.Legs.LastOrDefault() is { } capped && capped.Row.ReviewGoArounds >= ReviewRedoCap)
        {
            // The card's own words (LegCommander.RaiseReviewChanges): its button fixes the findings on the work as it stands, never redoes it.
            yield return Prose($"{capped.Name} has been sent back {capped.Row.ReviewGoArounds} times on the review's findings, so the next round is yours: "
                + "Fix the review's findings on its card once more, or Ready anyway if the findings should not hold it up.", 12f, Held);
        }
    }

    // The flights whose review report is opened past each finding's first lines, by code: per flight, as the transcript holds its opened entries.
    private readonly HashSet<string> _reportOpen = [];

    // Each report's tree, parsed once per text, whether it is opened, older than the PR's head and in the night palette: the page builds its rows every frame, and a node is immutable.
    private readonly Dictionary<(string Text, bool Open, bool Older, bool Night), Layout.Node> _reports = [];
    private const int ReportsKept = 16;

    private Layout.Node ReportMarkdown(string text, bool open, bool older)
    {
        var key = (text, open, older, NightOrNull is not null);
        if (!_reports.TryGetValue(key, out var document))
        {
            if (_reports.Count >= ReportsKept)
            {
                _reports.Clear();
            }
            _reports[key] = document = SelectableTree.Of(Layout.Builder.Markdown(text, ReportStyle(older) with { OnLink = Follow }), breakLongWords: true);
        }
        return document;
    }

    // Links open through the page's own opener, and only web pages, as the transcript's do.
    private void Follow(string url)
    {
        if (TranscriptEntryView.WebLink(url) is { } web)
        {
            OnOpen?.Invoke(web);
        }
    }

    /// <summary>
    /// The transcript's style for the report (<see cref="TranscriptEntryView.Prose"/> itself on the standard palette, its colours
    /// from the palette in the night view, which has no blue or white to spend), and a dimmed one for a report of an older head:
    /// the same renderer with the text a step down, code and strong text kept readable.
    /// </summary>
    private Layout.MarkdownStyle ReportStyle(bool older)
    {
        var style = NightOrNull is { } n
            ? new Layout.MarkdownStyle { Text = n.Bright, Strong = n.Held, Emphasis = n.Body, Code = n.Held, CodeBackground = n.GhostBg, Link = n.Held, Muted = n.Dim }
            : TranscriptEntryView.Prose;
        return older ? style with { Text = Dim, Strong = Body, Emphasis = Dim, Code = Body, Link = Dim, Muted = Dim } : style;
    }

    private static Layout.Node Prose(string text, float size, RGBAColor32 color) => WrappedProse.Of(text, size, color, selectable: true);
}
