using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The cards panel at 420 px with SE1's real cards of 2026-11-06 (pilot#797): c3646, c3623, c3530, c3483, c3479, c3472, c3528, c3637 and a
/// watchtower for-you card. <see cref="RealCards.Before"/> holds them as main wrote them (titles, first lines and buttons as the issue
/// quotes them, the cut ones cut where the issue shows them cut); <see cref="RealCards.After"/> holds the same eight situations as the
/// raisers word them now, the strings the node's golden tests pin (CardGoldenTests in Pilot.Node.Tests). Both are drawn with no display:
/// the check is that nothing is drawn over text and no word is cut at 420; the capture (docs/captures, for the PR) is written when
/// PILOT_CARDS_CAPTURES names a folder.
/// </summary>
public sealed class CardsPanelCaptureTests
{
    private const int Width = 420;

    private static CardRow Card(string id, CardBin bin, string kind, string subject, string title, string? summary, string? detail, params CardChoice[] choices) => new()
    {
        Id = id, Bin = bin, Kind = kind, Subject = subject, Title = title, Summary = summary, Detail = detail, Choices = choices,
        At = Live.At, State = CardState.Open,
    };

    private static CardChoice Choice(string label, string verb, string target, string? hint = null, string? asksFor = null) =>
        new() { Label = label, Verb = verb, Target = target, Hint = hint, AsksFor = asksFor };

    /// <summary>The two sets of cards the panel is drawn with.</summary>
    internal static class RealCards
    {
        public static readonly CardRow[] Before =
        [
            Card("c3646", CardBin.Ring, "worker-idle", "flight/PIL716/failure-investigation",
                "PIL716/failure-investigation's worker has been idle 3 min with 0 of 6 steps ticked, though it answers its nudges",
                "The worker of PIL716/failure-investigation has been idle with its leg unfinished and the node's nudges did not help. Nudge it to finish carries it on; Start a fresh worker lets it go.",
                "redo 2, on Opus at high; stale bound 45 m; 0 of 6 steps ticked; last nudge 3 min ago, answered at once.",
                Choice("Nudge it to finish", Verbs.Say, "session/a1b2c3d4"),
                Choice("Start a fresh worker on PIL716/failure-investigation", Verbs.GoAround, "flight/PIL716/failure-investigation")),
            Card("c3623", CardBin.Ring, "worker-stopped", "flight/PDF446/encoder",
                "PDF446/encoder stopped: Retired: PDF446 split, encoder went upstream to DIR188; worker stop...",
                "The worker of PDF446/encoder was stopped by the API and the node cannot resume it by itself. Tell it to carry on, or start a fresh worker on the leg.",
                null,
                Choice("Send encoder round again to Retired: PDF446 split, encoder went upstream to DIR188; worker stop...", Verbs.GoAround, "flight/PDF446/encoder"),
                Choice("Tell the worker to carry on", Verbs.Say, "session/e5f6a7b8")),
            Card("c3530", CardBin.ForYou, "usage-week", "node",
                "The 7-day window is 99 % spent",
                "The 7-day usage window is largely spent. Pause flights by hand if the week should last; nothing is stopped by itself.",
                "Pause flights by hand if the week should last.", []),
            Card("c3483", CardBin.Ring, "pr-review-design", "pr/SharpAstro/tianwen#2051",
                "SharpAstro/tianwen#2051: the review asks a question of design: which share of clear frames counts as a good night?",
                "The review asks which share of clear frames counts as a good night. No option reaches the criteria.",
                "A: measure the share of clear frames of the archive nebula-fields over the whole session, B: measure it per field and keep the worst.",
                Choice("measure the share of clear frames of the archive nebula-f...", Verbs.FixPr, "pr/SharpAstro/tianwen#2051", "measure the share of clear frames of the archive nebula-fields over the whole session"),
                Choice("measure it per field and keep the worst of the nebula-f...", Verbs.FixPr, "pr/SharpAstro/tianwen#2051", "measure it per field and keep the worst of the nebula-fields")),
            Card("c3479", CardBin.Ring, "pr-review-design", "pr/SharpAstro/pilot#888",
                "SharpAstro/pilot#888: the review asks a question of design: restart, remind or mark done?",
                "Two questions: should a stalled worker be restarted or reminded first, and is a leg done when its check passes? Options: restart / remind / mark done.",
                null,
                Choice("Answer in your own words", Verbs.FixPr, "pr/SharpAstro/pilot#888", asksFor: "Your answer")),
            Card("c3472", CardBin.ForYou, "disk-low", "node",
                "Drive C: has 12.1 GB free",
                "Free space on the repos' drive is below the disk-alert-gb setting. Biggest reclaimable items: job scratch 82c573f3 721 B.",
                "Biggest reclaimable items: job scratch 82c573f3 721 B",
                []),
            Card("c3528", CardBin.ForYou, "removal-held", "node",
                "1 folder Windows still holds open",
                "Folders of landed flights or closed reviews are still held open by Windows. The node removes them once they are let go; close whatever holds them.",
                "C:\\Users\\SebastianGodelet\\source\\repos\\sharpastro\\tianwen\\.claude\\worktrees\\tia2001: held by holder unknown",
                []),
            Card("c3637", CardBin.ForYou, "planned", "flight/PIL795",
                "PIL795: 5 legs planned",
                "PIL795 has a plan of legs. Start it to fly them, or change the plan first.",
                null,
                Choice("Start", Verbs.StartLeg, "flight/PIL795")),
            Card("c3651", CardBin.ForYou, "watchtower", "node",
                "SE1 has 3 flights ringing and 2 held",
                "A card raised for the person by the watchtower or a session. Read its detail and dismiss it once dealt with.",
                "3 ringing, 2 held by the owner, 1 waiting on a PR.", []),
        ];

        public static readonly CardRow[] After = [];
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(IEnumerable<CardRow> cards, int height)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From([.. cards])), new Rect<float>(0, 0, Width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRealCardsAtFourHundredTwentyDrawNothingOverTextAndNoWordPastThePanel(bool after)
    {
        var cards = after ? RealCards.After : RealCards.Before;
        if (cards.Length == 0)
        {
            return;
        }
        var arranged = Arrange(cards, 6000);

        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ShouldAllBe(a => a.Bounds.X + a.Bounds.Width <= Width + 0.5f);
    }

    [Fact]
    public void CapturesOfTheCardsPanelAt420BeforeAndAfterThePlainWordsRules()
    {
        // The captures go to docs/captures, for the PR, when a person asks for them: PILOT_CARDS_CAPTURES names the folder.
        if (Environment.GetEnvironmentVariable("PILOT_CARDS_CAPTURES") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        foreach (var (tag, cards) in new[] { ("before", RealCards.Before), ("after", RealCards.After) })
        {
            if (cards.Length == 0)
            {
                continue;
            }
            const int height = 3400;
            using var renderer = new RgbaImageRenderer(Width, height);
            var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
            view.Render(CardsScreen.From([.. cards]), new RectF32(0, 0, Width, height));
            File.WriteAllBytes(Path.Combine(folder, $"cards-plain-{tag}-{Width}.png"), PngWriter.Encode(renderer.Surface.Pixels, Width, height));
        }
    }
}
