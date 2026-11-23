using System.Collections.Immutable;
using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// A staying card's toast draws no line over another, at DPI 1 and 1.5 (the owner, 2026-11-23: "the toast had a problem that the text was
/// overlapping", on SE1's "PIL1293 is stuck" toast, which the live text check never looked at). The toast layer is arranged as the window
/// draws it, through the chrome, and checked with the same <see cref="TextOverlap"/> the live check uses.
/// </summary>
public sealed class ToastOverlapTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    // The card of the owner's screenshot: a long title, a summary past the toast's three lines, a detail, and three choices (two plain).
    private static CardRow Stuck() => new()
    {
        Id = "c28040", Bin = CardBin.Ring, Kind = "worker-stopped", Subject = "flight/PIL1293/gh-call-counter",
        Title = "PIL1293 is stuck: its worker stopped and cannot be nudged",
        Summary = "The worker of PIL1293/gh-call-counter stopped with its leg unfinished. A nudge may get it going again; if it does not, a fresh worker takes the leg over from the branch.",
        Detail = "no turn for 11 min, 0 of 5 steps ticked; its last turn ended on end_turn; idle with room in its context (41% of its window used) and no job of its own running",
        At = T0.AddSeconds(0.5), State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = "Nudge the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" },
            new CardChoice { Label = "Restart the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" },
        ],
    };

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void AStayingCardsToastDrawsNoLineOverAnother(float dpi)
    {
        var (width, height) = ((int)(1920 * dpi), (int)(1080 * dpi));
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        var seen = ImmutableArray<Layout.ArrangedNode<float>>.Empty;
        chrome.NotificationsLayer.Arranged = nodes => seen = nodes;
        chrome.Notifications.Open = false;
        chrome.Show(PilotPage.Jobs);
        var data = PageData.For("sample");
        void Frame(CardsScreen cards, int seconds) =>
            chrome.Render(data.Sessions with { AsOf = T0.AddSeconds(seconds) }, data.Flights, cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines);
        Frame(CardsScreen.Empty, 0);
        Frame(CardsScreen.From([Stuck()]), 1);
        Frame(CardsScreen.From([Stuck()]), 2);

        ArrangedText.Concat(seen).ShouldContain("PIL1293 is stuck");
        TextOverlap.Faults(seen).Select(f => f.Line).ShouldBeEmpty();

        // The card's title is longer than a toast line, its summary is past ToastSummaryFits (cut to 3 lines) and the cut-short line is drawn,
        // and that line's rect overlaps no other text line.
        Stuck().Title.Length.ShouldBeGreaterThan(40);
        Stuck().Summary!.Length.ShouldBeGreaterThan(PilotChrome<RgbaImage>.ToastSummaryFits);
        var cutShort = seen.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && PilotChrome<RgbaImage>.ToastCutLabel.Contains(v.Trim()) && v.Trim().Length > 0).ToArray();
        cutShort.ShouldNotBeEmpty("the cut-short line is drawn");
        ArrangedText.Concat(seen).Replace(" ", "").ShouldContain(PilotChrome<RgbaImage>.ToastCutLabel.Replace(" ", ""));
        var others = seen.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && !cutShort.Contains(a)).ToArray();
        foreach (var c in cutShort)
        {
            others.Where(o => o.Bounds.X < c.Bounds.X + c.Bounds.Width && c.Bounds.X < o.Bounds.X + o.Bounds.Width
                    && o.Bounds.Y < c.Bounds.Y + c.Bounds.Height - 0.5f && c.Bounds.Y < o.Bounds.Y + o.Bounds.Height - 0.5f
                    && !ReferenceEquals(o.Node, c.Node)
                    && !(o.Bounds.Y == c.Bounds.Y && o.Bounds.X >= c.Bounds.X + c.Bounds.Width))
                .Select(o => ((Layout.Content.Text)((Layout.Node.Leaf)o.Node).Content).Value).ShouldBeEmpty();
        }
    }
}
