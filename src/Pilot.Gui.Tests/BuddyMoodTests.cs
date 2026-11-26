using Pilot.Buddy;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The desktop cat's mood model (DSN-2 v5): pure, on a fake clock, no thread. Asleep, one eye open on hover, attention for an ask, good news that
/// ends by itself after 10 s, trouble; one bubble at a time with the rest queued; "Later" for that notice only; the grumpy face held while a failure is open.
/// </summary>
public sealed class BuddyMoodTests
{
    /// <summary>A clock the test moves by hand: the model never sleeps or polls, so nothing here waits.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static BuddyNotice Ask(string id = "ask-1") => new(id, NoticeKind.Ask, "DE1 · DSN-2 designer asks you", "Publish v3 of Desktop cat buddy, or change the bubble first?");

    private static BuddyNotice Trouble(string id = "ci-1351") => new(id, NoticeKind.Trouble, "CI failed on PR #1351", "3 tests failed in Pilot.Tests on SE1.");

    private static BuddyNotice Good(string id = "pr-1346") => new(id, NoticeKind.GoodNews, "PR #1346 merged in pilot", "Flight 42 landed.");

    [Fact]
    public void TheCatStartsAsleepAndOpensOneEyeWhileHovered()
    {
        var model = new BuddyModel(new Clock());
        model.Mood.ShouldBe(BuddyMood.Asleep);
        model.Current.ShouldBeNull();

        model.Hover(true);
        model.Mood.ShouldBe(BuddyMood.OneEyeOpen);

        model.Hover(false);
        model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public void AnAskWakesTheCatAndHoverDoesNotReplaceItsBubble()
    {
        var model = new BuddyModel(new Clock());
        model.Tell(Ask());
        model.Mood.ShouldBe(BuddyMood.Attention);
        model.Current.ShouldNotBeNull().Kind.ShouldBe(NoticeKind.Ask);

        model.Hover(true);
        model.Mood.ShouldBe(BuddyMood.Attention, "a showing bubble is not replaced by the hover summary");
    }

    [Fact]
    public void GoodNewsShowsForTenSecondsThenTheCatSleepsByItself()
    {
        var clock = new Clock();
        var model = new BuddyModel(clock);
        model.Tell(Good());
        model.Mood.ShouldBe(BuddyMood.GoodNews);

        clock.Advance(BuddyModel.GoodNewsFor - TimeSpan.FromMilliseconds(1));
        model.Mood.ShouldBe(BuddyMood.GoodNews, "just under 10 s");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        model.Mood.ShouldBe(BuddyMood.Asleep);
        model.Current.ShouldBeNull();
        model.GrumpyFace.ShouldBeFalse("good news opens no failure");
    }

    [Fact]
    public void AnAskDoesNotEndByItselfHoweverLongItWaits()
    {
        var clock = new Clock();
        var model = new BuddyModel(clock);
        model.Tell(Ask());
        clock.Advance(TimeSpan.FromHours(3));
        model.Mood.ShouldBe(BuddyMood.Attention);
    }

    [Fact]
    public void GoodNewsQueuedBehindAnAskGetsItsTenSecondsWhenItShows()
    {
        var clock = new Clock();
        var model = new BuddyModel(clock);
        model.Tell(Ask());
        model.Tell(Good());
        clock.Advance(TimeSpan.FromMinutes(5));

        model.Dismiss();
        model.Mood.ShouldBe(BuddyMood.GoodNews);
        clock.Advance(TimeSpan.FromSeconds(9));
        model.Mood.ShouldBe(BuddyMood.GoodNews, "its 10 s began when it showed, not when it came");
        clock.Advance(TimeSpan.FromSeconds(1));
        model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public void OneBubbleShowsAtATimeAndTroubleQueuesAheadOfAsksAheadOfGoodNews()
    {
        var model = new BuddyModel(new Clock());
        model.Tell(Ask("a1"));
        model.Tell(Good("g1"));
        model.Tell(Ask("a2"));
        model.Tell(Trouble("t1"));

        model.Current.ShouldNotBeNull().Id.ShouldBe("a1");
        model.Waiting.Select(n => n.Id).ShouldBe(["t1", "a2", "g1"]);
    }

    [Fact]
    public void LaterPutsTheCatToSleepForThatAskOnlyAndTheNextOneShows()
    {
        var model = new BuddyModel(new Clock());
        model.Tell(Ask("a1"));
        model.Tell(Ask("a2"));

        model.Dismiss().ShouldNotBeNull().Id.ShouldBe("a1");
        model.Current.ShouldNotBeNull().Id.ShouldBe("a2");
        model.Mood.ShouldBe(BuddyMood.Attention);

        model.Dismiss();
        model.Mood.ShouldBe(BuddyMood.Asleep);

        // Later is for that ask only: another ask wakes the cat again.
        model.Tell(Ask("a3"));
        model.Mood.ShouldBe(BuddyMood.Attention);
    }

    [Fact]
    public void ANoticeWithTheIdOfOneShowingOrWaitingReplacesItInsteadOfQueueingTwice()
    {
        var model = new BuddyModel(new Clock());
        model.Tell(Ask("a1"));
        model.Tell(Ask("a2"));
        model.Tell(Ask("a1") with { Title = "again" });
        model.Tell(Ask("a2") with { Title = "again too" });

        model.Current.ShouldNotBeNull().Title.ShouldBe("again");
        model.Waiting.Single().Title.ShouldBe("again too");
    }

    [Fact]
    public void TroubleWakesTheCatFuriousAndLaterLeavesItGrumpyWhileItSleeps()
    {
        var clock = new Clock();
        var model = new BuddyModel(clock);
        model.Tell(Trouble());
        model.Mood.ShouldBe(BuddyMood.Trouble);
        model.GrumpyFace.ShouldBeTrue();
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.Furious);

        model.Dismiss();
        model.Mood.ShouldBe(BuddyMood.Asleep);
        model.GrumpyFace.ShouldBeTrue("Later puts it off for this failure only; the failure is still open");
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.GrumpyAsleep);

        clock.Advance(TimeSpan.FromHours(6));
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.GrumpyAsleep, "held until the failure clears, however long it takes");
    }

    [Fact]
    public void TheGrumpyFaceGoesWhenTheFailureClearsAndNotBefore()
    {
        var model = new BuddyModel(new Clock());
        model.Tell(Trouble("t1"));
        model.Tell(Trouble("t2"));
        model.Dismiss();
        model.Dismiss();
        model.OpenFailures.Count.ShouldBe(2);

        model.Clear("t1");
        model.GrumpyFace.ShouldBeTrue("t2 is still open");
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.GrumpyAsleep);

        model.Clear("t2");
        model.GrumpyFace.ShouldBeFalse();
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.Asleep);
    }

    [Fact]
    public void ClearingAShowingFailureWakesTheNextNoticeOrSleeps()
    {
        var model = new BuddyModel(new Clock());
        model.Tell(Trouble("t1"));
        model.Tell(Ask("a1"));
        model.Clear("t1");
        model.Mood.ShouldBe(BuddyMood.Attention);
        model.Clear("a1");
        model.Mood.ShouldBe(BuddyMood.Asleep);
    }

    [Fact]
    public void TheFaceFollowsTheMoodAndHoverKeepsOneEyeEvenWithAFailureOpen()
    {
        var model = new BuddyModel(new Clock());
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.Asleep);
        model.Hover(true);
        BuddyScene.ViewOf(model, "4 sessions working on 3 nodes.").Summary.ShouldBe("4 sessions working on 3 nodes.");
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.OneEyeOpen);
        model.Hover(false);
        model.Tell(Ask());
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.Alert);
        model.Dismiss();
        model.Tell(Good());
        BuddyScene.ViewOf(model).Face.ShouldBe(GrumpFace.GrudgingHappy);
        BuddyScene.ViewOf(model, "summary").Summary.ShouldBeNull("the summary shows only while hovered and asleep");
    }

    [Fact]
    public void BreathingIsAPoseStepOnATimerNotAFrameLoop()
    {
        var clock = new Clock();
        var model = new BuddyModel(clock);
        model.BreathPose.ShouldBe(0);
        model.NextWake.ShouldBe(clock.GetUtcNow() + BuddyModel.BreathStep, "the host sleeps until the next step, it does not tick");

        clock.Advance(BuddyModel.BreathStep - TimeSpan.FromMilliseconds(1));
        model.BreathPose.ShouldBe(0);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        model.BreathPose.ShouldBe(1);
        clock.Advance(BuddyModel.BreathStep);
        model.BreathPose.ShouldBe(0);

        BuddyModel.BreathStep.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6), "a few seconds");
    }

    [Fact]
    public void TheNextWakeIsTheEndOfGoodNewsWhenThatComesBeforeTheNextBreath()
    {
        var clock = new Clock();
        var model = new BuddyModel(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        model.Tell(Good());
        // Breaths turn at 2.5 s, 5 s, 7.5 s, 10 s...; good news told at 1 s ends at 11 s.
        model.NextWake.ShouldBe(new DateTimeOffset(2026, 11, 26, 9, 0, 2, 500, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(9));
        model.NextWake.ShouldBe(new DateTimeOffset(2026, 11, 26, 9, 0, 11, TimeSpan.Zero));
    }
}
