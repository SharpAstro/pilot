using DIR.Lib;
using Pilot.Buddy;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1381 section 2, on a fake clock and no window (NO BUSY SPINNING: nothing waits, nothing runs but the model): a long quiet sleep leads the cat to leave
/// (the leaving animation, once) and be gone (only a clickable paw badge at the edge); any waking card, a hover on the badge or a click brings it back
/// (the return animation, once); and the frame policy asks for frames only during those two animations, none at all while gone.
/// </summary>
public sealed class BuddyGoneAwayTests
{
    private static readonly TimeSpan LongSleep = TimeSpan.FromMinutes(30);

    private static string Font => SessionsCapture.FontPath;

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static (Clock Clock, BuddyModel Model) Sleeping(TimeSpan? sleep = null)
    {
        var clock = new Clock();
        return (clock, new BuddyModel(clock) { LongSleep = sleep ?? LongSleep });
    }

    private static (Clock Clock, BuddyModel Model) Gone()
    {
        var (clock, model) = Sleeping();
        clock.Advance(LongSleep + BuddyModel.LeaveFor + TimeSpan.FromSeconds(1));
        model.Phase.ShouldBe(AwayPhase.Gone);
        return (clock, model);
    }

    private static BuddyNotice Ask() => new("card:ask-1", NoticeKind.Ask, "DE1 · DSN-2 designer asks you", "Publish v3, or change the bubble first?");

    [Fact]
    public void ALongQuietSleepLeadsToLeavingThenGone()
    {
        var (clock, model) = Sleeping();
        model.Phase.ShouldBe(AwayPhase.Present);

        clock.Advance(LongSleep - TimeSpan.FromSeconds(1));
        model.Phase.ShouldBe(AwayPhase.Present, "a second short of the long sleep");

        clock.Advance(TimeSpan.FromSeconds(2));
        model.Phase.ShouldBe(AwayPhase.Leaving);
        model.AwayProgress.ShouldBeInRange(0f, 0.5f);

        clock.Advance(BuddyModel.LeaveFor);
        model.Phase.ShouldBe(AwayPhase.Gone);
        model.AwayProgress.ShouldBe(1f);
    }

    [Fact]
    public void ThePhaseIsReachedFromTheMomentTheSleepEndedWhenReadLate()
    {
        var (clock, model) = Sleeping();
        // Nobody looked for an hour: the leave began at 30 minutes and ended at 30 min 1.6 s, so it is gone, not leaving again.
        clock.Advance(TimeSpan.FromHours(1));
        model.Phase.ShouldBe(AwayPhase.Gone);
    }

    [Fact]
    public void NeverIsNoLongSleepAndTheDefaultIsThirtyMinutes()
    {
        var (clock, model) = Sleeping();
        model.LongSleep = null;
        clock.Advance(TimeSpan.FromDays(2));
        model.Phase.ShouldBe(AwayPhase.Present);

        new BuddySettings().LongSleep.ShouldBe(TimeSpan.FromMinutes(30));
        new BuddySettings().LongSleepMinutes.ShouldBeNull("a stored record member is nullable");
        new BuddySettings { LongSleepMinutes = 0 }.LongSleep.ShouldBeNull();
        BuddySettings.LongSleepChoices[new BuddySettings().LongSleepIndex].ShouldBe(30);
    }

    [Fact]
    public void ABusyCatDoesNotLeave()
    {
        var (clock, model) = Sleeping();
        model.Tell(Ask());
        clock.Advance(TimeSpan.FromHours(3));
        model.Phase.ShouldBe(AwayPhase.Present, "an ask waits for the person; the cat does not walk off from it");
        model.Dismiss();
        clock.Advance(LongSleep - TimeSpan.FromSeconds(1));
        model.Phase.ShouldBe(AwayPhase.Present, "the sleep counts from the moment it went quiet");
        clock.Advance(TimeSpan.FromSeconds(2));
        model.Phase.ShouldBe(AwayPhase.Leaving);

        var (c2, m2) = Sleeping();
        m2.Engaged(true);
        c2.Advance(TimeSpan.FromHours(3));
        m2.Phase.ShouldBe(AwayPhase.Present, "a card is open");

        var (c3, m3) = Sleeping();
        m3.Badge("card:one");
        c3.Advance(TimeSpan.FromHours(3));
        m3.Phase.ShouldBe(AwayPhase.Present, "a count badge keeps it awake");
    }

    [Fact]
    public void AWakingCardBringsAGoneCatBackAndItSpeaks()
    {
        var (clock, model) = Gone();
        model.Tell(Ask());
        model.Phase.ShouldBe(AwayPhase.Returning);
        model.Current.ShouldNotBeNull().Id.ShouldBe("card:ask-1");

        clock.Advance(BuddyModel.ReturnFor + TimeSpan.FromMilliseconds(1));
        model.Phase.ShouldBe(AwayPhase.Present);
        model.Mood.ShouldBe(BuddyMood.Attention);
    }

    [Fact]
    public void AWakingCardDuringTheLeaveReturnsTheCat()
    {
        var (clock, model) = Sleeping();
        clock.Advance(LongSleep + TimeSpan.FromSeconds(0.5));
        model.Phase.ShouldBe(AwayPhase.Leaving);

        model.Tell(Ask());
        model.Phase.ShouldBe(AwayPhase.Returning);
        clock.Advance(BuddyModel.ReturnFor);
        model.Phase.ShouldBe(AwayPhase.Present);
    }

    [Fact]
    public void AHoverOnTheBadgeAClickAndABadgeEachBringItBackThenAsleep()
    {
        foreach (var wake in new Action<BuddyModel>[] { m => m.Hover(true), m => m.Return(), m => m.Badge("card:other") })
        {
            var (clock, model) = Gone();
            wake(model);
            model.Phase.ShouldBe(AwayPhase.Returning);
            clock.Advance(BuddyModel.ReturnFor);
            model.Phase.ShouldBe(AwayPhase.Present);
        }

        // Back and quiet: the long sleep starts over from the return, so the cat is not gone again at once.
        var (c, m) = Gone();
        m.Return();
        c.Advance(BuddyModel.ReturnFor + TimeSpan.FromMinutes(5));
        m.Phase.ShouldBe(AwayPhase.Present);
        c.Advance(LongSleep);
        m.Phase.ShouldBe(AwayPhase.Gone);
    }

    [Fact]
    public void FramesAreAskedOnlyDuringTheLeaveAndTheReturnNeverWhileGone()
    {
        var (clock, model) = Sleeping();
        var policy = new BuddyFramePolicy(model, () => 0, () => string.Empty, () => null, clock);
        void Run(TimeSpan span)
        {
            // What the host does: draw when asked, then sleep until the next wake (an event, never a poll).
            var end = clock.GetUtcNow() + span;
            while (clock.GetUtcNow() < end)
            {
                if (policy.NeedsFrame())
                {
                    policy.Drawn(TimeSpan.Zero);
                }
                clock.Advance(policy.NextWake < end ? policy.NextWake - clock.GetUtcNow() : end - clock.GetUtcNow());
            }
        }

        // Asleep: the breathing step is its only frame (as ever); the leave then adds one frame per animation step, once.
        Run(LongSleep - TimeSpan.FromSeconds(1));
        var asleep = policy.Frames;
        Run(TimeSpan.FromSeconds(1) + BuddyModel.LeaveFor);
        var leaving = policy.Frames - asleep;
        leaving.ShouldBeInRange(8, 20, "a second or two of frames at one per step");
        model.Phase.ShouldBe(AwayPhase.Gone);

        // Gone: not one frame in ten hours, and the host's sleeps are a minute at most (a deadline, not a poll).
        var gone = policy.Frames;
        var asks = policy.Asks;
        Run(TimeSpan.FromHours(10));
        policy.Frames.ShouldBe(gone);
        (policy.Asks - asks).ShouldBeLessThan(700, "one ask per host wake, a minute apart at the most");
        model.BreathPose.ShouldBe(0);

        // The wake: the return plays once, then the cat sleeps with its breath again.
        model.Hover(true);
        model.Hover(false);
        var before = policy.Frames;
        Run(BuddyModel.ReturnFor + TimeSpan.FromMilliseconds(100));
        (policy.Frames - before).ShouldBeInRange(6, 20);
        model.Phase.ShouldBe(AwayPhase.Present);
    }

    [Fact]
    public void AGoneCatHoldsOnlyItsBadgeAndTheWindowsShapeShrinksToIt()
    {
        var (_, model) = Gone();
        var view = BuddyScene.ViewOf(model);
        view.Away.ShouldBe(AwayPhase.Gone);

        using var renderer = new RgbaImageRenderer(480, 360);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), view, 1f, false, 0, null, BuddySettings.SizeScales[1]);
        layout.GoneAway.ShouldBeTrue();
        layout.BadgeW.ShouldBeGreaterThan(0);
        (layout.BadgeX + layout.BadgeW).ShouldBe(480f, 0.01f, "at the right edge for the bottom right corner");

        var geometry = new BuddyGeometry(layout);
        geometry.At(layout.BadgeX + 5, layout.BadgeY + layout.BadgeH / 2, true).Kind.ShouldBe(TargetKind.Badge);
        geometry.At(layout.CatX + 50, layout.CatY + 50, true).Kind.ShouldBe(TargetKind.None, "the cat's old place is the desktop's");
        BuddySilhouette.PassesClicks(geometry, true, true).ShouldBeFalse("the badge must be hoverable even with click-through on");

        // The silhouette is the badge alone: a small share of the window, and the cat's old place lets clicks through.
        var mask = BuddySilhouette.Of(renderer.Surface);
        var caught = 0;
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                caught += BuddySilhouette.Catches(mask, x, y) ? 1 : 0;
            }
        }
        caught.ShouldBeGreaterThan(0);
        caught.ShouldBeLessThan(480 * 360 / 20);
        BuddySilhouette.Catches(mask, (int)layout.CatX + 50, (int)layout.CatY + 50).ShouldBeFalse();
    }

    [Fact]
    public void TheBadgeIsAtTheLeftEdgeForTheLeftCorner()
    {
        var (_, model) = Gone();
        using var renderer = new RgbaImageRenderer(480, 360);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(true), BuddyScene.ViewOf(model), 1f, false, 1, null, BuddySettings.SizeScales[1]);
        layout.BadgeX.ShouldBe(0f);
    }

    [Fact]
    public void TheSettingsCardCyclesTheLongSleepAndFlipsTheRemoteSwitch()
    {
        var settings = new BuddySettings();
        var next = SettingsCard.Press(settings, "longSleep", DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc).ShouldNotBeNull();
        next.LongSleepMinutes.ShouldBe(60);
        SettingsCard.Press(new BuddySettings { LongSleepMinutes = 0 }, "longSleep", DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc)!.LongSleepMinutes.ShouldBe(15);

        settings.ShowsInRemoteSessions.ShouldBeFalse();
        SettingsCard.Press(settings, "remote", DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc)!.ShowInRemoteSessions.ShouldBe(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheGoneAwayBadgeCapture(bool dark)
    {
        var outFolder = Path.Combine(Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts ? artifacts : Path.GetTempPath(), "pilot-buddy-captures");
        Directory.CreateDirectory(outFolder);
        var palette = BuddyPalette.For(dark);
        foreach (var (name, phase, progress) in new[] { ("gone", AwayPhase.Gone, 1f), ("leaving", AwayPhase.Leaving, 0.55f), ("returning", AwayPhase.Returning, 0.3f) })
        {
            using var renderer = new RgbaImageRenderer(1280, 720);
            var view = new BuddyView(GrumpFace.Asleep, 0, null, null, 0, phase, progress);
            var layout = BuddyScene.Draw(renderer, Font, palette, view, 1f, true, 0, null, 1f);
            File.WriteAllBytes(Path.Combine(outFolder, $"away-{name}-{(dark ? "dark" : "light")}.png"), PngWriter.Encode(renderer.Surface.Pixels, 1280, 720));
            if (phase == AwayPhase.Gone)
            {
                // The badge is on the screen, at the right edge, and its outline reads against the desktop it is drawn on (3:1 for a control edge).
                layout.BadgeW.ShouldBeGreaterThan(0);
                (layout.BadgeX + layout.BadgeW).ShouldBeLessThanOrEqualTo(1280.01f);
                (layout.BadgeY + layout.BadgeH).ShouldBeLessThanOrEqualTo(720.01f);
                // On a bright desktop the ink outline carries the edge, on a dark one the grey fill does.
                Math.Max(Contrast.Ratio(GrumpCat.Ink, palette.Desktop), Contrast.Ratio(GrumpCat.Fur, palette.Desktop)).ShouldBeGreaterThanOrEqualTo(3.0, "the paw's edge against the desktop it is on");
            }
        }
    }
}
