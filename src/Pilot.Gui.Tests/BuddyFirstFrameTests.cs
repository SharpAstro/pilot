using System.Text;
using DIR.Lib;
using Pilot.Buddy;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1381 section 4 on fakes (NO BUSY SPINNING: no window, no GPU, no thread, a fake clock): the bubble's text appears whole in the first presented frame
/// after a wake. <see cref="LazyAtlas"/> behaves as SdlVulkan.Renderer's font atlas does: a glyph is rasterised on first use, but it is drawn only once the
/// next frame's flush has uploaded it (a glyph met during a draw is skipped), and the atlas reports itself dirty while uploads are pending. The loop below
/// is the host's: ask the policy, lay the scene out on the CPU (naming its texts), warm, present, tell the policy whether the atlas settled.
/// <para>
/// Frame counts from a wake to the whole bubble, on this model: <b>before</b> the fix the buddy presented 1 frame holding the bubble's box and none of its
/// text and the policy asked for no other, so the text came in with whatever frame a later change drew (first wake and every wake with a new letter); with
/// only the dirty backstop it was 2 frames (the empty one, then the whole text); <b>after</b> it is 1 frame, from the first wake on.
/// </para>
/// </summary>
public sealed class BuddyFirstFrameTests
{
    private static string Font => SessionsCapture.FontPath;

    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>The renderer's lazy atlas: rasterised glyphs wait for the next flush; drawing an unknown glyph rasterises it and skips it.</summary>
    private sealed class LazyAtlas : IBuddyAtlas
    {
        private readonly HashSet<Rune> _awaitingUpload = [];
        private readonly HashSet<Rune> _uploaded = [];

        public bool Dirty => _awaitingUpload.Count > 0;

        public void PreWarm(Rune glyph)
        {
            if (!_uploaded.Contains(glyph))
            {
                _awaitingUpload.Add(glyph);
            }
        }

        /// <summary>One presented frame: the flush uploads what was rasterised, then the draw shows the lines whose glyphs are all there.</summary>
        public string Present(SpeechBubble? bubble)
        {
            _uploaded.UnionWith(_awaitingUpload);
            _awaitingUpload.Clear();
            var shown = new StringBuilder();
            foreach (var line in bubble?.Lines ?? [])
            {
                var all = true;
                foreach (var rune in line.Text.EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r)))
                {
                    if (!_uploaded.Contains(rune))
                    {
                        all = false;
                        PreWarm(rune);
                    }
                }
                if (all)
                {
                    shown.Append(line.Text).Append('\n');
                }
            }
            return shown.ToString();
        }
    }

    private static (BuddyModel Model, BuddyFramePolicy Policy, LazyAtlas Atlas, BuddyGlyphWarmer Warmer, List<string> Presented) Setup()
    {
        var clock = new Clock();
        var model = new BuddyModel(clock);
        var policy = new BuddyFramePolicy(model, () => 0, () => string.Empty, () => null, clock);
        var atlas = new LazyAtlas();
        return (model, policy, atlas, new BuddyGlyphWarmer(atlas), []);
    }

    private static string Whole(BuddyNotice notice)
    {
        using var cpu = new RgbaImageRenderer(400, 300);
        var layout = BuddyScene.Draw(cpu, Font, BuddyPalette.For(false), new BuddyView(GrumpFace.Alert, 0, notice, null), 1f);
        return string.Concat(layout.Bubble!.Lines.Select(l => l.Text + "\n"));
    }

    /// <summary>The host's frame loop until the policy is satisfied (bounded, so a bug fails and never loops). Returns the text each presented frame showed.</summary>
    private static List<string> FramesUntilQuiet(BuddyModel model, BuddyFramePolicy policy, LazyAtlas atlas, BuddyGlyphWarmer warmer, bool warm, bool settle)
    {
        var shown = new List<string>();
        while (policy.NeedsFrame() && shown.Count < 10)
        {
            SpeechBubble? bubble = null;
            policy.Time(() =>
            {
                using var cpu = new RgbaImageRenderer(400, 300);
                Pen<RgbaImage>.TextSeen = warmer.Collect;
                try
                {
                    bubble = BuddyScene.Draw(cpu, Font, BuddyPalette.For(false), BuddyScene.ViewOf(model), 1f).Bubble;
                }
                finally
                {
                    Pen<RgbaImage>.TextSeen = null;
                }
                if (warm)
                {
                    warmer.Warm();
                }
                shown.Add(atlas.Present(bubble));
            });
            if (settle)
            {
                policy.AtlasSettled(atlas.Dirty);
            }
        }
        return shown;
    }

    private static BuddyNotice Ask(string title = "DE1 · DSN-2 designer asks you", string body = "Publish v3, or change the bubble first?") => new("card:ask-1", NoticeKind.Ask, title, body);

    [Fact]
    public void AWakePresentsOneFrameHoldingTheWholeBubbleText()
    {
        var (model, policy, atlas, warmer, _) = Setup();
        FramesUntilQuiet(model, policy, atlas, warmer, true, true).ShouldBe([string.Empty], "the sleeping cat's first frame has no bubble");

        model.Tell(Ask());
        var shown = FramesUntilQuiet(model, policy, atlas, warmer, true, true);

        shown.Count.ShouldBe(1, "one presented frame from the wake");
        shown[0].ShouldBe(Whole(Ask()), "and it holds the title, the body and the buttons' labels whole");
    }

    [Fact]
    public void ALaterWakeWithNewLettersIsStillOneFrame()
    {
        var (model, policy, atlas, warmer, _) = Setup();
        model.Tell(Ask());
        FramesUntilQuiet(model, policy, atlas, warmer, true, true);
        model.Clear("card:ask-1");
        FramesUntilQuiet(model, policy, atlas, warmer, true, true);

        var other = Ask("Zorro: QUIZ", "Jumpy vexing waltz, fjord bank; 0123456789 ?!");
        model.Tell(other);
        var shown = FramesUntilQuiet(model, policy, atlas, warmer, true, true);

        shown.Count.ShouldBe(1);
        shown[0].ShouldBe(Whole(other));
    }

    [Fact]
    public void BeforeTheFixTheFirstFrameHeldNoTextAndNothingAskedForAnother()
    {
        // The buddy as it was: no warm-up, and the policy ignored the atlas. Measured on this model: 1 frame, the bubble's box with none of its text.
        var (model, policy, atlas, warmer, _) = Setup();
        FramesUntilQuiet(model, policy, atlas, warmer, false, false);
        model.Tell(Ask());

        var shown = FramesUntilQuiet(model, policy, atlas, warmer, false, false);

        shown.Count.ShouldBe(1);
        shown[0].ShouldBe(string.Empty, "every glyph was new, so every one was skipped");
    }

    [Fact]
    public void TheDirtyBackstopAloneNeedsASecondFrame()
    {
        var (model, policy, atlas, warmer, _) = Setup();
        FramesUntilQuiet(model, policy, atlas, warmer, false, true);
        model.Tell(Ask());

        var shown = FramesUntilQuiet(model, policy, atlas, warmer, false, true);

        shown.Count.ShouldBe(2, "the empty frame, then the whole text");
        shown[0].ShouldBe(string.Empty);
        shown[1].ShouldBe(Whole(Ask()));
    }

    [Fact]
    public void TheBackstopNeverRunsAwayOnAnAtlasThatStaysDirty()
    {
        var model = new BuddyModel(new Clock());
        var policy = new BuddyFramePolicy(model, () => 0, () => string.Empty, () => null, new Clock());
        var frames = 0;
        while (policy.NeedsFrame() && frames < 10)
        {
            policy.Time(() => frames++);
            policy.AtlasSettled(true);
        }

        frames.ShouldBe(1 + BuddyFramePolicy.MostAtlasRedraws, "the first frame and the bounded extra ones, then quiet");
    }
}
