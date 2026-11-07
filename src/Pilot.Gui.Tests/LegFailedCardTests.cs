using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The leg-failed card of c78 (PIL834/read-stamps), before and after pilot #844: the old card's title and both buttons began with the first
/// words of the worker's report ("All 7 steps done and committed; ..."), the new one names what failed and what each button leads to.
/// Both are drawn on the CPU at a phone-narrow 404 px and at a wide size; the new card's title and labels are drawn whole (or ellipsised with the
/// whole text on hover), and no text is drawn over other text. The captures are written into the folder PILOT_TEST_CAPTURE names, for the PR.
/// </summary>
public sealed class LegFailedCardTests
{
    private const string Report = "All 7 steps done and committed; filtered tests pass (115, run on the plane)";

    private static readonly CardChoice[] OldChoices =
    [
        new() { Label = "Send read-stamps round again to All 7 steps done and committed;", Verb = Verbs.GoAround, Target = "flight/PIL834/read-stamps", Args = [$"the last worker stopped: {Report}"] },
        new() { Label = "Accept read-stamps as it stands, the failure kept in its report", Verb = Verbs.FinishLeg, Target = "flight/PIL834/read-stamps", Args = ["accepted"] },
    ];

    // What the node raises now (LegCommander.Finish.cs, case failed) for a check that fails building Pilot.Ui in files the leg never touched.
    private static readonly CardChoice[] NewChoices =
    [
        new() { Label = "Send it round: a fresh worker fixes it and finishes", Verb = Verbs.GoAround, Target = "flight/PIL834/read-stamps", Args = [$"the last worker stopped: {Report}"] },
        new() { Label = "Accept as it is: the flight goes on with the check failing", Verb = Verbs.FinishLeg, Target = "flight/PIL834/read-stamps", Args = ["accepted"] },
    ];

    internal static CardRow Before() => new()
    {
        Id = "c78", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/PIL834/read-stamps",
        Title = "PIL834/read-stamps stopped: All 7 steps done and committed; filtered tests pass (115, run on",
        Summary = "The worker of PIL834/read-stamps stopped. Send read-stamps round again starts a fresh worker told why; Accept read-stamps as it stands keeps the failure in its report and the flight goes on.",
        Detail = $"the last worker stopped: {Report}",
        Choices = [.. OldChoices], At = Live.At, State = CardState.Open,
    };

    internal static CardRow After() => new()
    {
        Id = "c78", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/PIL834/read-stamps",
        Title = "PIL834/read-stamps: its check fails building Pilot.Ui, outside this leg's code",
        Summary = "The worker of PIL834/read-stamps stopped and its check fails building Pilot.Ui. The flight changed none of the failing files or their projects, so the failure is outside this leg's code. "
            + "main has changed those files since the flight took it (2 commits), so taking main may fix it. "
            + "Send it round starts a fresh worker, told the whole report, that fixes it and finishes the leg; Accept as it is keeps the failure in the leg's report and the flight goes on with the check still failing.",
        Detail = $"{Report}\n\nmain has changed the failing files since the flight took it; taking main may fix it:\nfaaacbee merge-queue skill: mq.sh enqueue queues a PR (#866)\ndc230bdc A leg whose ticking pass ends unfinished is finished by the node (#859)",
        Choices = [.. NewChoices], At = Live.At, State = CardState.Open,
    };

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardRow card, float width, out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, width, 1600), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    /// <summary>The text is drawn whole (its leaves run together read as it), or a tooltip carries all of it.</summary>
    private static bool WholeOrOnHover(ImmutableArray<Layout.ArrangedNode<float>> arranged, string text)
    {
        var drawn = ArrangedText.Concat(arranged);
        var squashed = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        return string.Concat(drawn.Where(c => !char.IsWhiteSpace(c))).Contains(squashed, StringComparison.Ordinal)
            || arranged.Select(a => a.Node.Tooltip).OfType<string>().Any(t => t.Contains(text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(404f)]
    [InlineData(900f)]
    [InlineData(1800f)]
    public void TheNewC78CardDrawsItsTitleAndBothButtonLabelsWholeAndNoTextOverlaps(float width)
    {
        var card = After();
        var arranged = Arrange(card, width, out var renderer);
        using var _ = renderer;

        WholeOrOnHover(arranged, card.Title).ShouldBeTrue($"title at {width}");
        foreach (var choice in card.Choices)
        {
            WholeOrOnHover(arranged, choice.Label).ShouldBeTrue($"'{choice.Label}' at {width}");
        }
        // The buttons are there to press, and none carries a word of the worker's report.
        var actions = arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        actions.ShouldContain("card:c78:1");
        actions.ShouldContain("card:c78:2");
        card.Choices.ShouldAllBe(c => !c.Label.Contains("All 7 steps", StringComparison.Ordinal));
        card.Title.ShouldNotContain("All 7 steps");
        // Nothing past the panel's edge, no text over text.
        arranged.Where(a => a.Bounds.X + a.Bounds.Width > width + 1).ShouldBeEmpty();
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(404f)]
    [InlineData(1800f)]
    public void TheOldC78CardIsTheOneThatNamedTheWorkersReportInItsTitleAndButtons(float width)
    {
        // The before-picture is really the old wording: the report's first words are in the title and in the Send round button.
        var arranged = Arrange(Before(), width, out var renderer);
        using var _ = renderer;
        var drawn = ArrangedText.Concat(arranged);

        drawn.ShouldContain("All 7 steps");
        Before().Choices[0].Label.ShouldContain("All 7 steps");
        Before().Title.ShouldContain("filtered tests pass");
    }

    [Fact]
    public void CapturesOfTheOldAndTheNewC78CardAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        // PILOT_TEST_CAPTURE names the folder the PNGs go into.
        var dir = capture;
        Directory.CreateDirectory(dir);
        foreach (var (width, height) in new[] { (404, 1000), (1600, 800) })
        {
            foreach (var (name, card) in new[] { ("before", Before()), ("after", After()) })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                void Draw() => chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([card]), new RectF32(0, 0, width, height));
                Draw();
                // A window too narrow for the panel beside the page cues the cards on the rail; open them in the page's place, as a click on the cue does.
                if (chrome.CardsDropped)
                {
                    chrome.ToggleCards();
                    Draw();
                }
                chrome.ShowsCards.ShouldBeTrue($"the cards panel is drawn in the {name} capture at {width}");
                File.WriteAllBytes(Path.Combine(dir, $"leg-failed-{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }
}
