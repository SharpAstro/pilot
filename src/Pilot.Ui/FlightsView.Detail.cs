using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// A flight's own page: the Flights table gives a leg one line, this gives each leg its goal, Done-when, checklist,
// report and artifacts, where the table's selected-flight panel has room for five legs and no words.
internal sealed partial class FlightsView<TSurface>
{
    private readonly ListScrollController _detailScroll = new() { Mode = ScrollBarMode.Interactive };
    private string? _openCode;

    /// <summary>Whether a flight's page is open in place of the table.</summary>
    public bool IsDetailOpen => _openCode is not null;

    /// <summary>The open page's flight code, or null.</summary>
    public string? OpenCode => _openCode;

    /// <summary>The flight the page is about: the one open, else the one selected; the cards panel lights its cards.</summary>
    public string? FocusCode => _openCode ?? (Selected >= 0 && Selected < _screen.Lines.Count ? _screen.Lines[Selected].Code : null);

    /// <summary>The cards panel as this frame shows it: the open page finds its flight's card that offers the merge.</summary>
    public CardsScreen? Cards { get; set; }

    /// <summary>Runs a card's choice, as the panel's button does, so the card closes once it is done.</summary>
    public Action<CardRow, int>? OnAnswerCard { get; set; }

    /// <summary>Called as a flight's page is about to open, before what the window shows changes: the chrome records the place being left.</summary>
    public Action? Opening { get; set; }

    /// <summary>Opens <paramref name="code"/>'s page, from the top.</summary>
    public void OpenDetail(string code)
    {
        Opening?.Invoke();
        _openCode = code;
        // Its row is shown whatever In play says while its page is open, and selected once the page is shut.
        _pinned = code;
        _detailScroll.AtomOffset = 0;
    }

    public void CloseDetail()
    {
        _selectPinned = _pinned is not null && _pinned == _openCode;
        _openCode = null;
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
        var color = flight.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };
        var head = new List<Layout.Node>
        {
            Pill("← Back", "flight:back", GoBack),
            Layout.Builder.Text(line.Code, 16f, Bright).WAuto(),
            Whole(Layout.Builder.Text(line.Title, 14f, Body).WStar(), line.Title, TitleFits),
            Layout.Builder.Text(line.StateDetail is null ? line.State : $"{line.State} ({line.StateDetail})", 13f, color, TextAlign.Far).WAuto(),
        };
        if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                head.Add(Pill(action.Label, $"flight:{line.Code}:{action.Verb}", () => Choose(action, line.Code)));
            }
        }

        var facts = new List<Layout.Node>
        {
            Layout.Builder.Text($"{line.Where} · {line.Branch} · {line.Legs}", 12f, Dim).WAuto(),
        };
        if (flight.PullRequestUrl is { } url)
        {
            facts.Add(Link($"PR #{flight.PullRequest}", url, $"pr:{line.Code}").WAuto());
        }
        if (flight.Review is { } review)
        {
            facts.Add(Layout.Builder.Text($"review {review.State.ToString().ToLowerInvariant()}", 12f,
                review.State == ReviewState.Passed ? Flying : review.State == ReviewState.Changes ? Held : Dim).WAuto());
        }
        var marks = FlightMarks.From(flight);
        if (marks.Line is { } markLine)
        {
            facts.Add(Layout.Builder.Text(markLine, 12f, MarksColor(flight)).WAuto());
        }
        if (marks.ReviewUrl is { } reviewUrl)
        {
            facts.Add(Link("review comment", reviewUrl, $"review:{line.Code}").WAuto());
        }
        if (marks.Warning is { } warning)
        {
            facts.Add(Layout.Builder.Text($"⚠ GitHub: {warning}", 12f, Held).WAuto());
        }
        if (marks.Pending is { } pending)
        {
            facts.Add(Layout.Builder.Text(pending, 12f, Held).WAuto());
        }
        facts.Add(Layout.Builder.Spacer().WStar());

        var legs = detail.Legs.Count == 0
            ? [Layout.Builder.Text(line.Node is null or { Here: true } ? "no legs yet: Plan a leg" : "no legs yet", 13f, Dim).RowH(24f)]
            : detail.Legs.Select(l => LegBlock(line.Code, l)).ToList();

        List<Layout.Node> page =
        [
            Layout.Builder.HStack([.. head]).WithGap(10f).RowH(32f),
        ];
        // A why asked for sits under the buttons it was asked by.
        page.AddRange(WhyRow(line));
        if (ReadyToMerge(line) is { } ready)
        {
            page.Add(ready);
        }
        return Layout.Builder.VStack(
                [.. page,
                Layout.Builder.HStack([.. facts]).WithGap(14f).RowH(22f),
                Layout.Builder.HStack([.. WhoWorks(line)]).WithGap(10f).RowH(26f),
                Layout.Builder.VStack([.. legs]).WithGap(10f).WithScroll(_detailScroll).Stretch()])
            .Pad(Gutter, 8f)
            .Stretch();
    }

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
            cells.Add(CardsView<TSurface>.ChoiceControl(choice, card.Detail, $"flight:{line.Code}:merge-card", () => answer(card, number)));
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
                yield return Layout.Builder.Text($"{doing} · {session.Name} · {session.State.ToString().ToLowerInvariant()}", 12f, color).WAuto();
                if (OnSession is { } open)
                {
                    // Named for whose it is: beside two sessions, two "Its transcript" said nothing of which was which.
                    var row = session;
                    yield return Pill($"{who}'s transcript", $"flight:{line.Code}:session:{row.Id}", () => open(row, line.Node));
                }
            }
        }
        else if (line.Row.State == FlightState.Flying && line.LegRows.Count == 0)
        {
            yield return Layout.Builder.Text("not planned yet: no planner is running", 12f, Held).WAuto();
            if (CanCommand && OnAction is { } act)
            {
                yield return Pill("Plan it", $"flight:{line.Code}:plan", () =>
                    act(new FlightAction($"plan {line.Code}", Verbs.PlanFlight, Verbs.FlightTarget(line.Code), []) { Node = line.Node }));
            }
        }
        else if (line.Row.State == FlightState.Flying)
        {
            yield return Layout.Builder.Text("nothing is running on it now", 12f, Dim).WAuto();
        }
        yield return Layout.Builder.Spacer().WStar();
    }

    private const string ValidatorSuffix = "-validator";

    private Layout.Node LegBlock(string code, LegDetail leg)
    {
        var color = leg.Row.Hold is not null ? Held : leg.Row.State switch { LegState.Done => Landed, LegState.Flying => Flying, LegState.Failed => Failed, _ => Dim };
        var head = new List<Layout.Node>
        {
            Layout.Builder.Text($"{leg.Order}. {leg.Name}", 15f, Bright).WAuto(),
            Layout.Builder.Text(leg.State, 13f, color).WAuto(),
        };
        if (leg.StepsText is { } steps)
        {
            head.Add(Layout.Builder.Text(steps, 12f, Dim).WAuto());
        }
        if (leg.GoArounds is { } goArounds)
        {
            head.Add(Layout.Builder.Text(goArounds, 12f, Held).WAuto());
        }
        head.Add(Layout.Builder.Spacer().WStar());

        var rows = new List<Layout.Node> { Layout.Builder.HStack([.. head]).WithGap(12f).RowH(24f) };
        rows.Add(Field("Goal", leg.Goal));
        rows.Add(Field("Done when", leg.DoneWhen));
        if (leg.Check is { } check)
        {
            rows.Add(Field("Check", check, Dim));
        }
        foreach (var step in leg.Steps)
        {
            rows.Add(Labelled(Layout.Builder.Text(step.Done ? "[x]" : "[ ]", 12f, step.Done ? Flying : Dim), 28f, 8f, Prose(step.Text, 12f, step.Done ? Dim : Body)));
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
            links.Add(Layout.Builder.Text($"{artifact.When} · {artifact.Location}", 11f, Faint, trim: TextTrim.Start).WStar());
            rows.Add(Layout.Builder.HStack([.. links]).WithGap(10f).RowH(20f));
        }
        return Layout.Builder.VStack([.. rows])
            .WithGap(4f)
            .Pad(12f, 8f)
            .Bg(PanelBg)
            .WStar()
            .HAuto();
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

    private static Layout.Node Prose(string text, float size, RGBAColor32 color) => WrappedProse.Of(text, size, color);
}
