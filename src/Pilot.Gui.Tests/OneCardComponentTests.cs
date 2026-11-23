using System.Collections.Immutable;
using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// One card component draws a card wherever it shows (pilot #1292, ui-changes.md: one source for every component): the toast's body, the For you
/// panel's card and the Notifications page's row lay out the same parts in the same order, with the toast's frame (radius and padding from
/// NotificationsHost.Colors and Style). Only the width and the fit differ.
/// </summary>
public sealed class OneCardComponentTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    private static CardRow Long() => new()
    {
        Id = "c1292", Bin = CardBin.Ring, Kind = "worker-stopped", Subject = "flight/PIL1293/gh-call-counter",
        Title = "PIL1293 is stuck: its worker stopped",
        Summary = "The worker of the flight stopped with its leg unfinished. A nudge may get it going again; if it does not, a fresh worker takes the leg over from the branch. Nothing else of the flight is waiting on it.",
        Detail = "no turn for 11 min, 0 of 5 steps ticked; its last turn ended on end_turn; idle with room in its context and no job of its own running",
        At = T0, State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = "Nudge the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" },
            new CardChoice { Label = "Restart the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" },
        ],
    };

    public enum Fit { Toast, Panel, Page }

    private static (Layout.Node Tree, ImmutableArray<Layout.ArrangedNode<float>> Arranged) Build(Fit fit, float width, float dpi)
    {
        var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var card = Long();
        var tree = fit switch
        {
            Fit.Toast => view.ToastCard(card, null, cutShort: true, () => { }),
            Fit.Panel => view.PanelCard(card, null),
            _ => view.FullCard(card, null, closed: false),
        };
        var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, 3000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi));
        return (tree, arranged);
    }

    private static float TopOf(ImmutableArray<Layout.ArrangedNode<float>> arranged, string startsWith) =>
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.Trim().StartsWith(startsWith, StringComparison.Ordinal))
            .Select(a => a.Bounds.Y).DefaultIfEmpty(float.NaN).Min();

    private static readonly string[] Parts = ["RING", "PIL1293", "The", "▸ Detail"];
    private static readonly string[] Buttons = ["Nudge", "Restart"];

    [Theory]
    [InlineData(Fit.Toast, 280f)]
    [InlineData(Fit.Panel, 528f)]
    [InlineData(Fit.Page, 900f)]
    public void TheSamePartsComeInTheSameOrderAndTheDetailIsCollapsed(Fit fit, float width)
    {
        var (_, arranged) = Build(fit, width, 1f);
        var tops = Parts.Select(p => TopOf(arranged, p)).ToArray();
        tops.ShouldAllBe(t => !float.IsNaN(t), $"{fit}: every part is drawn");
        tops.ShouldBe([.. tops.Order()], $"{fit}: kind and node line, title, summary, Detail fold, in that order");
        ArrangedText.Concat(arranged).ShouldNotContain("no turn for 11 min", Case.Sensitive);
        if (fit != Fit.Toast)
        {
            var fold = tops[^1];
            Buttons.Select(b => TopOf(arranged, b)).All(t => t > fold).ShouldBeTrue($"{fit}: the card's own buttons follow the fold");
        }
        else
        {
            Buttons.Select(b => TopOf(arranged, b)).All(float.IsNaN).ShouldBeTrue("the toast's buttons are the library's row, not the card's");
        }
    }

    [Fact]
    public void PanelAndPageDrawTheToastsFrame()
    {
        var colors = NotificationsHost.Colors();
        var style = NotificationsHost.Style;
        float? left = null;
        foreach (var (fit, width) in new[] { (Fit.Panel, 528f), (Fit.Page, 900f) })
        {
            var (tree, arranged) = Build(fit, width, 1f);
            tree.BorderStyle.ShouldNotBeNull();
            tree.BorderStyle!.Radius.ShouldBe(colors.CornerRadius);
            tree.BorderStyle.Color.ShouldBe(colors.TintedEdge(NotificationSeverity.Warn));
            tree.Background.ShouldBe(colors.TintedFill(NotificationSeverity.Warn));
            var inset = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.Trim().StartsWith("RING", StringComparison.Ordinal)).Bounds.X;
            inset.ShouldBeGreaterThanOrEqualTo(style.Padding, $"{fit}: the content sits inside the toast's padding");
            left ??= inset;
            inset.ShouldBe(left.Value, 0.5f, "panel and page have the same padding");
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void NoFitDrawsALineOverAnother(float dpi)
    {
        foreach (var (fit, width) in new[] { (Fit.Toast, 280f), (Fit.Panel, 528f), (Fit.Page, 900f) })
        {
            var (_, arranged) = Build(fit, width, dpi);
            TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"{fit} at DPI {dpi}");
        }
    }
}
