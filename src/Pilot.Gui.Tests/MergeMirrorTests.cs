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
/// The card's Merge shows what GitHub says of the PR's merge, one label per state, and only a state that can be acted on is a
/// button; a merge that finished reads Merged on the panel for a few seconds before it goes (PIL244, pilot#244).
/// </summary>
public sealed class MergeMirrorTests
{
    private const string LongCheck = "build-and-test (windows-11-arm, net10.0, Release, ExtendedIntegrationTests+LongRunningSnapshotComparisons)";

    private static CardChoice Merge(string? status, string? note = null) =>
        new() { Label = "Merge", Verb = Verbs.Merge, Target = "flight/PIL244", Args = ["77018f1"], Status = status, Note = note };

    private static CardRow Card(CardChoice[] choices, string title = "PIL244's PR #251 is ready to merge", string? detail = null, CardState state = CardState.Open, DateTimeOffset? closedAt = null, string id = "c1") => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/PIL244", Title = title, Detail = detail, Choices = choices,
        Link = "https://github.com/SharpAstro/pilot/pull/251", At = Live.At, State = state, ClosedAt = closedAt, Outcome = closedAt is null ? null : "PIL244 landed",
    };

    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardsScreen screen, float width, out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, width, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static string[] Words(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];

    private static string[] Actions(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action)];

    [Theory]
    [InlineData(ChoiceStatus.Passing, null, "Merge")]
    [InlineData(ChoiceStatus.Pending, "2 of 7", "Merge · checks running · 2 of 7")]
    [InlineData(ChoiceStatus.Pending, null, "Merge · checks running")]
    [InlineData(ChoiceStatus.Armed, "6 of 7", "Merging when checks pass · 6 of 7")]
    [InlineData(ChoiceStatus.Queued, "position 2", "In the merge queue · position 2")]
    [InlineData(ChoiceStatus.Queued, null, "In the merge queue")]
    [InlineData(ChoiceStatus.Blocked, "build (linux)", "Blocked · build (linux)")]
    [InlineData(ChoiceStatus.Blocked, null, "Blocked")]
    [InlineData(ChoiceStatus.ChangesRequested, null, "Changes requested")]
    [InlineData(ChoiceStatus.Behind, null, "Behind main")]
    [InlineData(ChoiceStatus.Merged, null, "Merged")]
    public void EachStateOfGitHubsMergeHasOneLabel(string status, string? note, string label)
    {
        CardsView<RgbaImage>.StatusLabel(Merge(status, note)).ShouldBe(label);
    }

    [Fact]
    public void ALongFailedChecksNameEndsInAnEllipsisOnTheChipAndItsWholeNameIsOnHover()
    {
        var label = CardsView<RgbaImage>.StatusLabel(Merge(ChoiceStatus.Blocked, LongCheck));
        label.ShouldEndWith("…");
        label.Length.ShouldBeLessThan(CardsView<RgbaImage>.BlockedLabel.Length + 3 + CardsView<RgbaImage>.NoteFits + 1);

        var arranged = Arrange(CardsScreen.From([Card([Merge(ChoiceStatus.Blocked, LongCheck)])]), 300, out var renderer);
        using var _ = renderer;
        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(t => t.Contains(LongCheck, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ChoiceStatus.Armed, false)]
    [InlineData(ChoiceStatus.Queued, false)]
    [InlineData(ChoiceStatus.Blocked, false)]
    [InlineData(ChoiceStatus.ChangesRequested, false)]
    [InlineData(ChoiceStatus.Merged, false)]
    [InlineData(ChoiceStatus.Passing, true)]
    [InlineData(ChoiceStatus.Pending, true)]
    [InlineData(null, true)]
    public void OnlyAStateThatCanBeActedOnIsAButton(string? status, bool button)
    {
        var arranged = Arrange(CardsScreen.From([Card([Merge(status)])]), 340, out var renderer);
        using var _ = renderer;
        Actions(arranged).Contains("card:c1:1").ShouldBe(button);
        // Dismiss and the title link are the card's own, whatever its Merge is.
        Actions(arranged).ShouldContain("card:c1:dismiss");
        Actions(arranged).ShouldContain("card:c1:title");
    }

    [Theory]
    [InlineData(ChoiceStatus.Armed)]
    [InlineData(ChoiceStatus.Queued)]
    [InlineData(ChoiceStatus.Blocked)]
    [InlineData(ChoiceStatus.ChangesRequested)]
    [InlineData(ChoiceStatus.Merged)]
    [InlineData(ChoiceStatus.Passing)]
    [InlineData(ChoiceStatus.Pending)]
    public void EveryStatesWordsMeetTheContrastFloorOnItsFill(string status)
    {
        var tint = CardsView<RgbaImage>.StatusTint(status).ShouldNotBeNull();
        Contrast(CardsView<RgbaImage>.ArmedText, tint.Bg).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(CardsView<RgbaImage>.ArmedText, tint.Hover).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void StatesTheNodeAnswersDifferentlyAreToldApartByTheirWordsAndNotOnlyByColour()
    {
        // Queued shares the armed violet (the merge goes through by itself); blocked shares failing's red. Their words differ.
        var labels = new[] { ChoiceStatus.Armed, ChoiceStatus.Queued, ChoiceStatus.Blocked, ChoiceStatus.ChangesRequested, ChoiceStatus.Merged, ChoiceStatus.Passing, ChoiceStatus.Pending }
            .Select(s => CardsView<RgbaImage>.StatusLabel(Merge(s)));
        labels.Distinct().Count().ShouldBe(7);
    }

    [Fact]
    public void AConflictingOrBehindCardOffersTheGoAroundAndDismissAndNoMerge()
    {
        var card = Card([new CardChoice { Label = "Redo leg armed-in-gui", Verb = Verbs.GoAround, Target = "flight/PIL244/armed-in-gui", Args = ["rebase"] }],
            "PIL244's PR #251 conflicts with its base", "GitHub cannot merge it until the conflict is resolved.");
        var arranged = Arrange(CardsScreen.From([card]), 340, out var renderer);
        using var _ = renderer;

        Actions(arranged).ShouldBe(["card:c1:title", "card:c1:leg", "card:c1:1", "card:c1:dismiss"], ignoreOrder: true);
        Words(arranged).ShouldContain("Redo leg armed-in-gui");
        Words(arranged).ShouldNotContain("Merge");
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(520f)]
    public void ABlockedChipAndAGoAroundCardsDetailStayInsideTheCardAndAreNeverCutSilently(float width)
    {
        var conflict = "GitHub cannot merge it until the conflict is resolved. The node rebases it by itself, once a head; one that conflicts is a go-around: the last leg resolves it.";
        var cards = new[]
        {
            Card([Merge(ChoiceStatus.Blocked, LongCheck)], detail: $"GitHub blocks the merge: {LongCheck} failed."),
            Card([new CardChoice { Label = "Redo leg armed-in-gui", Verb = Verbs.GoAround, Target = "flight/PIL244/armed-in-gui", Args = ["rebase"] }], "conflicts", conflict, id: "c2"),
        };
        foreach (var card in cards)
        {
            var arranged = Arrange(CardsScreen.From([card]), width, out var renderer);
            using var _ = renderer;
            var drawn = string.Concat(arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value));
            foreach (var word in card.Detail!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                drawn.Contains(word, StringComparison.Ordinal).ShouldBeTrue($"'{word}' of {card.Id}'s detail at {width}");
            }
            arranged.Where(a => a.Bounds.X + a.Bounds.Width > width + 1).ShouldBeEmpty();
        }
    }

    [Fact]
    public void AMergedCardLingersOnThePanelForAFewSecondsAndIsNeverCountedAsWaiting()
    {
        var closedAt = Live.At;
        var merged = Card([Merge(ChoiceStatus.Merged)], "PIL244's PR #251 is ready to merge", state: CardState.Closed, closedAt: closedAt);

        var during = CardsScreen.From([merged], now: closedAt.AddSeconds(3));
        during.Open.ShouldBeEmpty();
        during.Merged.ShouldHaveSingleItem().Id.ShouldBe("c1");
        during.Summary.ShouldBe("Nothing waits on you");

        var after = CardsScreen.From([merged], now: closedAt + CardsScreen.MergedLinger + TimeSpan.FromSeconds(1));
        after.Merged.ShouldBeEmpty();
    }

    [Fact]
    public void ACardClosedForAnyOtherReasonDoesNotLinger()
    {
        var closedAt = Live.At;
        var landed = Card([Merge(ChoiceStatus.Passing)], state: CardState.Closed, closedAt: closedAt);
        CardsScreen.From([landed], now: closedAt.AddSeconds(1)).Merged.ShouldBeEmpty();
        // Nor does one that merged long ago, as a snapshot at connect holds them.
        var old = Card([Merge(ChoiceStatus.Merged)], state: CardState.Closed, closedAt: closedAt.AddHours(-2));
        CardsScreen.From([old], now: closedAt).Merged.ShouldBeEmpty();
    }

    [Fact]
    public void AMergedCardReadsMergedWithNoButtonButTheTitleLink()
    {
        var closedAt = Live.At;
        var screen = CardsScreen.From([Card([Merge(ChoiceStatus.Merged)], state: CardState.Closed, closedAt: closedAt)], now: closedAt.AddSeconds(1));
        var arranged = Arrange(screen, 340, out var renderer);
        using var _ = renderer;

        Words(arranged).ShouldContain("Merged");
        Words(arranged).ShouldContain("merged");
        Words(arranged).ShouldNotContain("Dismiss");
        Actions(arranged).ShouldBe(["card:c1:title"]);
    }

    [Fact]
    public void ThePanelWithOnlyAMergedCardIsStillDrawn()
    {
        var closedAt = Live.At;
        CardsScreen.From([Card([Merge(ChoiceStatus.Merged)], state: CardState.Closed, closedAt: closedAt)], now: closedAt).HasAny.ShouldBeTrue();
    }

    [Fact]
    public void ALingeringCardKeepsItsPlaceInAMergedPanel()
    {
        var closedAt = Live.At;
        var screen = CardsScreen.From([Card([Merge(ChoiceStatus.Merged)], state: CardState.Closed, closedAt: closedAt)], now: closedAt);
        var merged = CardsScreen.Merge([(null, screen), (null, CardsScreen.From([Card([Merge(ChoiceStatus.Passing)], id: "c2")]))], null);
        merged.Merged.Select(c => c.Id).ShouldBe(["c1"]);
        merged.Open.Select(c => c.Id).ShouldBe(["c2"]);
    }

    [Fact]
    public void CapturesOfEachStateAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var closedAt = Live.At;
        CardRow[] cards =
        [
            Card([Merge(ChoiceStatus.Pending, "2 of 7")], "PIL244's PR #251 is ready to merge", "Planets: 3 legs done, head 77018f1c. Reviewed deeply: checked the claims.", id: "c1"),
            Card([Merge(ChoiceStatus.Passing)], "PIL245's PR #252 is ready to merge", "Every check passed.", id: "c2"),
            Card([Merge(ChoiceStatus.Queued, "position 2")], "PIL246's PR #253 is in the merge queue", "in the merge queue, position 2: PIL246 lands once the queue merges it", id: "c3"),
            Card([Merge(ChoiceStatus.Armed, "6 of 7")], "PIL247's PR #254 merges when its checks pass", "merging when checks pass: 6 of 7 done. GitHub merges it, with the node up or not.", id: "c4"),
            Card([Merge(ChoiceStatus.Blocked, LongCheck)], $"PIL248's PR #255 is blocked: {LongCheck} failed", $"GitHub blocks the merge: {LongCheck} failed. Redo the leg to fix what it found, or re-run the check if it was a flake.", id: "c5"),
            Card([Merge(ChoiceStatus.ChangesRequested)], "PIL249's PR #256 has changes requested", "A review on GitHub asks for changes. They are read first, then the merge is offered again.", id: "c6"),
            Card([new CardChoice { Label = "Redo leg armed-in-gui", Verb = Verbs.GoAround, Target = "flight/PIL244/armed-in-gui", Args = ["rebase"] }],
                "PIL250's PR #257 conflicts with its base", "GitHub cannot merge it until the conflict is resolved. The node rebases it by itself, once a head; one that conflicts is a go-around: the last leg resolves it.", id: "c7"),
            Card([Merge(ChoiceStatus.Merged)], "PIL251's PR #258 is ready to merge", state: CardState.Closed, closedAt: closedAt, id: "c8"),
        ];
        foreach (var (width, height) in new[] { (1280, 1700), (2000, 1700) })
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From(cards, now: closedAt.AddSeconds(1)), new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(dir, $"mirror-states-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }
}
