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

    /// <summary>Opens <paramref name="code"/>'s page, from the top.</summary>
    public void OpenDetail(string code)
    {
        _openCode = code;
        _detailScroll.AtomOffset = 0;
    }

    public void CloseDetail() => _openCode = null;

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
            Pill("Back to the flights", "flight:back", CloseDetail),
            Layout.Builder.Text(line.Code, 16f, Bright).WAuto(),
            Layout.Builder.Text(line.Title, 14f, Body).WStar(),
            Layout.Builder.Text(line.StateDetail is null ? line.State : $"{line.State} ({line.StateDetail})", 13f, color, TextAlign.Far).WAuto(),
        };
        if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                head.Add(Pill(action.Label, $"flight:{line.Code}:{action.Verb}", () =>
                {
                    if (action.Prefill is { } prefill)
                    {
                        Prefill(prefill);
                    }
                    else
                    {
                        OnAction?.Invoke(action);
                    }
                }));
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
        facts.Add(Layout.Builder.Spacer().WStar());

        var legs = detail.Legs.Count == 0
            ? [Layout.Builder.Text("no legs yet: Plan a leg", 13f, Dim).RowH(24f)]
            : detail.Legs.Select(l => LegBlock(line.Code, l)).ToList();

        return Layout.Builder.VStack(
                Layout.Builder.HStack([.. head]).WithGap(10f).RowH(32f),
                Layout.Builder.HStack([.. facts]).WithGap(14f).RowH(22f),
                Layout.Builder.VStack([.. legs]).WithGap(10f).WithScroll(_detailScroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private Layout.Node LegBlock(string code, LegDetail leg)
    {
        var color = leg.Row.State switch { LegState.Done => Landed, LegState.Flying => Flying, LegState.Failed => Failed, _ => Dim };
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
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(step.Done ? "[x]" : "[ ]", 12f, step.Done ? Flying : Dim).WFixed(28f),
                    Prose(step.Text, 12f, step.Done ? Dim : Body))
                .WithGap(8f).HAuto().WStar());
        }
        if (leg.Report is { } report)
        {
            rows.Add(Field(leg.Row.State == LegState.Failed ? "Why it failed" : "Report", report, leg.Row.State == LegState.Failed ? Failed : Body));
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
        Layout.Builder.HStack(Layout.Builder.Text(label, 11f, Faint).WFixed(DetailLabelWidth), Prose(text, 13f, color ?? Body))
            .WithGap(10f)
            .HAuto()
            .WStar();

    /// <summary>
    /// A sentence that wraps at the width it is given, each word a text leaf ending in its space as Markdown's paragraphs
    /// are: a text leaf is one line, and wrapping the words is what lets it follow the window and the terminal alike.
    /// </summary>
    private static Layout.Node Prose(string text, float size, RGBAColor32 color)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Layout.Builder.Text(w + " ", size, color).WAuto())
            .ToArray();
        return Layout.Builder.WrapH(words).WithLineGap(size * 0.2f).WStar();
    }
}
