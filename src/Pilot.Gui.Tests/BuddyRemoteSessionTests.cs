using Pilot.Buddy;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1381 section 7, on a fake session kind (no Windows, no SDL, no window): in a remote desktop session, with "Show in remote desktop sessions" off, the cat
/// is absent: its window is hidden, the buddy says <c>ShowsCards</c> false (so the node's toasts carry on, the (c) case of the toast rule) and no frame is
/// asked for; a move to the console shows it again; the setting on keeps it shown. The kind is told by an event, never read in a loop.
/// </summary>
public sealed class BuddyRemoteSessionTests
{
    private sealed class FakeSession : ISessionKind
    {
        public bool IsRemote { get; private set; }

        public event Action? Changed;

        public void MoveTo(bool remote)
        {
            IsRemote = remote;
            Changed?.Invoke();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class Rig
    {
        public FakeSession Session { get; } = new();

        public BuddySettings Settings { get; set; } = new();

        public List<bool> WindowShown { get; } = [];

        public BuddyFeed Feed { get; }

        public BuddyPresence Presence { get; }

        public BuddyFramePolicy Policy { get; }

        public BuddyModel Model { get; }

        public Rig()
        {
            var clock = new Clock();
            Model = new BuddyModel(clock);
            Feed = new BuddyFeed(Model, clock, () => Settings);
            Presence = new BuddyPresence(Session, Feed, () => Settings, WindowShown.Add);
            Policy = new BuddyFramePolicy(Model, () => 0, () => string.Empty, () => null, clock, () => Presence.Hidden);
            // What the host does on a session change: say it again.
            Session.Changed += Presence.Update;
            Presence.Update();
        }
    }

    [Fact]
    public void ARemoteSessionHidesTheCatAndTheBuddyDoesNotShowTheCards()
    {
        var rig = new Rig();
        rig.Feed.ShowsCards.ShouldBeTrue("on the console it shows the cards");
        rig.WindowShown.ShouldBe([true]);

        rig.Session.MoveTo(remote: true);
        rig.Presence.Hidden.ShouldBeTrue();
        rig.WindowShown.Last().ShouldBeFalse("the window is hidden");
        rig.Feed.WindowVisible.ShouldBeFalse();
        rig.Feed.ShowsCards.ShouldBeFalse("so the node's toasts are as they were: case (c) of the toast rule");
    }

    [Fact]
    public void AMoveToTheConsoleShowsTheCatAgainWithItsFrame()
    {
        var rig = new Rig();
        rig.Policy.Drawn(TimeSpan.Zero);
        rig.Session.MoveTo(remote: true);
        rig.Session.MoveTo(remote: false);

        rig.WindowShown.ShouldBe([true, false, true]);
        rig.Feed.ShowsCards.ShouldBeTrue();
        rig.Presence.Hidden.ShouldBeFalse();
    }

    [Fact]
    public void TheSettingOnKeepsTheCatShownInARemoteSession()
    {
        var rig = new Rig { Settings = new BuddySettings { ShowInRemoteSessions = true } };
        rig.Session.MoveTo(remote: true);
        rig.Presence.Hidden.ShouldBeFalse();
        rig.WindowShown.ShouldAllBe(shown => shown);
        rig.Feed.ShowsCards.ShouldBeTrue();

        // Turned off while remote: the next Update (the host calls it on a settings change) hides it.
        rig.Settings = new BuddySettings { ShowInRemoteSessions = false };
        rig.Presence.Update();
        rig.Presence.Hidden.ShouldBeTrue();
        rig.Feed.ShowsCards.ShouldBeFalse();

        new BuddySettings().ShowInRemoteSessions.ShouldBeNull("a stored record member is nullable and defaults to off");
    }

    [Fact]
    public void AHiddenCatAsksNoFrameNoMatterWhatChanges()
    {
        var rig = new Rig();
        rig.Policy.Drawn(TimeSpan.Zero);
        rig.Policy.NeedsFrame().ShouldBeFalse();

        rig.Session.MoveTo(remote: true);
        rig.Model.Tell(new BuddyNotice("card:x", NoticeKind.Ask, "t", "b"));
        rig.Policy.NeedsFrame().ShouldBeFalse("hidden: no frame, no animation");
        var frames = rig.Policy.Frames;

        rig.Session.MoveTo(remote: false);
        rig.Policy.NeedsFrame().ShouldBeTrue("shown again, the change since the last frame is drawn");
        rig.Policy.Frames.ShouldBe(frames);
    }

    [Fact]
    public void TheLogonRowOfTheDesktopCatSectionSaysTheCatStaysHiddenInRemoteSessions()
    {
        // The words come from one place: off says it stays hidden, on does not.
        var hidden = SettingsLogonWording.Hint(new BuddySettings());
        hidden.ShouldContain("hidden in remote desktop sessions");
        SettingsLogonWording.Hint(new BuddySettings { ShowInRemoteSessions = true }).ShouldNotContain("hidden");
    }
}
