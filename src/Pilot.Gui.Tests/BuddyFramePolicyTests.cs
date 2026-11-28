using Pilot.Ui;
using DIR.Lib;
using Pilot.Buddy;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// When the desktop cat draws and what a pointer does to it (NO BUSY SPINNING): on a fake clock and a fake window, no SDL, no thread, nothing waits.
/// An asleep cat asks no frame between its breathing steps, a mood change asks one, and hover, click, right-click and drag go to the handler of
/// what is under the pointer. Clicks pass through the sleeping cat when the setting says so.
/// </summary>
public sealed class BuddyFramePolicyTests
{
    private static string Font => SessionsCapture.FontPath;

    /// <summary>A clock the test moves by hand, whose timers the test fires by hand.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        public List<(FakeTimer Timer, TimeSpan Due)> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state);
            Timers.Add((timer, dueTime));
            return timer;
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWindow : IBuddyWindow
    {
        public List<(int X, int Y)> Moves { get; } = [];

        public int Drops { get; private set; }

        public void MoveBy(int dx, int dy) => Moves.Add((dx, dy));

        public void Dropped() => Drops++;

        public List<bool> TextInputs { get; } = [];

        public void TextInput(bool on) => TextInputs.Add(on);
    }

    private sealed class NullNode : IBuddyNode
    {
        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => Task.FromResult<ChatPage?>(null);
    }

    private sealed class RecordingGui : IGuiHandOff
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _links = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _launched = new();

        /// <summary>False makes <see cref="Open"/> refuse, as when no pilot-gui runs.</summary>
        public bool Takes { get; set; } = true;

        /// <summary>Whether the fallback start worked.</summary>
        public bool LaunchWorks { get; set; }

        /// <summary>Thrown by <see cref="Open"/> when set: a hand-off that fails in a way the desk does not list.</summary>
        public Exception? Throws { get; set; }

        /// <summary>Signalled by every hand-off; the desk runs them on the pool, so a test awaits this instead of the clock.</summary>
        public SemaphoreSlim Done { get; } = new(0);

        public IReadOnlyList<string> Links => [.. _links];

        public IReadOnlyList<string> Launched => [.. _launched];

        public bool Open(string link)
        {
            _links.Enqueue(link);
            if (Throws is { } thrown)
            {
                throw thrown;
            }
            if (Takes)
            {
                Done.Release();
            }
            return Takes;
        }

        public bool Launch(string link)
        {
            _launched.Enqueue(link);
            Done.Release();
            return LaunchWorks;
        }
    }

    /// <summary>Everything the window host wires together, on fakes, with the scene drawn on the CPU at 480 x 720 to give the desk its geometry.</summary>
    private sealed class Rig
    {
        public Clock Clock { get; } = new();

        public BuddyModel Model { get; }

        public BuddyFeed Feed { get; }

        public FakeWindow Window { get; } = new();

        public RecordingGui Gui { get; } = new();

        public BuddyDesk Desk { get; }

        public BuddyFramePolicy Policy { get; }

        public BuddySettings Settings { get; set; } = new();

        public int Wakes { get; private set; }

        /// <summary>What the desk logged; a chat verb or hand-off runs on the pool, so the queue is a concurrent one.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Logged { get; } = new();

        public Rig()
        {
            Model = new BuddyModel(Clock);
            Feed = new BuddyFeed(Model, Clock, () => Settings);
            var chat = new BuddyChat(new NullNode(), Gui, () => null);
            Desk = new BuddyDesk(Model, Feed, chat, Gui, Window, () => Settings, changed => Settings = changed, Clock, TimeZoneInfo.Utc, new object(), () =>
            {
                Wakes++;
                Woke.Release();
            }, Logged.Enqueue);
            Policy = new BuddyFramePolicy(Model, () => Desk.Version, Desk.ContentKey, () => Settings.SnoozedUntil, Clock);
            Draw();
        }

        /// <summary>The frame the host would draw: the scene, then the geometry the desk hit-tests with.</summary>
        public SceneLayout Draw()
        {
            using var renderer = new RgbaImageRenderer(480, 720);
            var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), BuddyScene.ViewOf(Model), 1f, false, Settings.CornerIndex, Desk.CardFactory<RgbaImage>());
            Desk.Geometry = new BuddyGeometry(layout);
            Policy.Drawn(TimeSpan.Zero);
            return layout;
        }

        public (float X, float Y) CatBody => (Desk.Geometry!.Layout.CatX + 100, Desk.Geometry.Layout.CatY + 80);

        public (float X, float Y) Cushion => (Desk.Geometry!.Layout.CatX + 100, Desk.Geometry.Layout.CatY + GrumpCat.CushionY + 5);

        /// <summary>Released after each hand-off or chat verb of the desk ends and has repainted (its wake): awaited on the event, never on the clock.</summary>
        public SemaphoreSlim Woke { get; } = new(0);

        // The deadline is a guard against a hang, not a wait: a wake that never came fails here instead of passing silently.
        public async Task AfterHandOff() =>
            (await Woke.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue("the desk woke the window when its work ended");

        public void Click(int button, (float X, float Y) at)
        {
            Desk.ButtonDown(button, at.X, at.Y);
            Desk.ButtonUp(button, at.X, at.Y);
        }
    }

    private static BuddyNotice Ask() => new("card:ask-1", NoticeKind.Ask, "DE1 · DSN-2 designer asks you", "Publish v3 of Desktop cat buddy, or change the bubble first?");

    [Fact]
    public void AnAsleepCatAsksNoFrameBetweenItsBreathingSteps()
    {
        var rig = new Rig();
        rig.Policy.NeedsFrame().ShouldBeFalse("the frame just drawn is what is shown");

        // The model's breathing step is 2.5 s: a second, then two, later nothing has changed.
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Policy.NeedsFrame().ShouldBeFalse();
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Policy.NeedsFrame().ShouldBeFalse();

        // At the step the body grows two pixels: one frame, and then none again until the next step.
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Policy.NeedsFrame().ShouldBeTrue();
        rig.Draw();
        rig.Policy.NeedsFrame().ShouldBeFalse();
        rig.Policy.Frames.ShouldBe(2);
    }

    [Fact]
    public void TheNextWakeIsTheNextBreathAndAnEarlierSnoozeEndWinsOverIt()
    {
        var rig = new Rig();
        rig.Policy.NextWake.ShouldBe(rig.Model.NextWake);
        rig.Policy.NextWake.ShouldBeGreaterThan(rig.Clock.GetUtcNow());

        var until = rig.Clock.GetUtcNow() + TimeSpan.FromSeconds(1);
        rig.Settings = new BuddySettings { SnoozedUntil = until };
        rig.Policy.NextWake.ShouldBe(until, "a snooze that ends before the breath is a change with no event");
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Policy.NextWake.ShouldBe(rig.Model.NextWake, "once it has ended it is no wake");
    }

    [Fact]
    public void AMoodChangeAsksExactlyOneFrame()
    {
        var rig = new Rig();
        rig.Model.Tell(Ask());
        rig.Policy.NeedsFrame().ShouldBeTrue();
        rig.Draw();
        rig.Policy.NeedsFrame().ShouldBeFalse();

        // The same notice told again (a card raised again) changes nothing shown; a new text does.
        rig.Model.Tell(Ask());
        rig.Policy.NeedsFrame().ShouldBeFalse();
        rig.Model.Tell(Ask() with { Body = "Something else." });
        rig.Policy.NeedsFrame().ShouldBeTrue();
    }

    [Fact]
    public void GoodNewsEndsByItselfAndTheWakeIsItsEndNotAPoll()
    {
        var rig = new Rig();
        rig.Model.Tell(new BuddyNotice("merged:1", NoticeKind.GoodNews, "PR #1 merged", "Flight 1 landed."));
        rig.Draw();
        rig.Policy.NextWake.ShouldBeLessThanOrEqualTo(rig.Clock.GetUtcNow() + BuddyModel.GoodNewsFor);

        rig.Clock.Advance(BuddyModel.GoodNewsFor);
        rig.Policy.NeedsFrame().ShouldBeTrue("the cat goes back to sleep by itself");
    }

    [Fact]
    public void HoverOpensOneEyeAndLeavingClosesIt()
    {
        var rig = new Rig();
        rig.Desk.PointerMoved(rig.CatBody.X, rig.CatBody.Y);
        rig.Model.Mood.ShouldBe(BuddyMood.OneEyeOpen);
        rig.Policy.NeedsFrame().ShouldBeTrue();
        rig.Draw();

        rig.Desk.PointerLeft();
        rig.Model.Mood.ShouldBe(BuddyMood.Asleep);
        rig.Policy.NeedsFrame().ShouldBeTrue();
    }

    [Fact]
    public void AMoveOffTheCatDoesNotWakeIt()
    {
        var rig = new Rig();
        rig.Desk.PointerMoved(5, 5);
        rig.Model.Mood.ShouldBe(BuddyMood.Asleep);
        rig.Policy.NeedsFrame().ShouldBeFalse();
    }

    [Fact]
    public void OpeningTheChatStartsTheWindowsTextInputAndClosingItStopsIt()
    {
        // Without SDL's text input started, no typed character reached the chat box (the owner, 2026-11-28: "ask the buddy does not accept key input").
        var rig = new Rig();
        rig.Click(1, rig.CatBody);
        rig.Desk.Card.ShouldBe(DeskCard.Chat);
        rig.Window.TextInputs.ShouldBe([true]);

        rig.Desk.Escape();
        rig.Window.TextInputs.ShouldBe([true, false]);
    }

    [Fact]
    public void ASwitchBetweenTheOtherCardsAsksNothingOfTheTextInput()
    {
        var rig = new Rig();
        rig.Desk.ShowSettings();
        rig.Desk.Escape();
        rig.Window.TextInputs.ShouldBeEmpty("only the chat opening or closing touches the text input");
    }

    [Fact]
    public async Task AClickOnTheSleepingCatOpensTheChatWithTheInputFocusedAndAnotherClosesIt()
    {
        var rig = new Rig();
        rig.Click(1, rig.CatBody);
        rig.Desk.Card.ShouldBe(DeskCard.Chat, "the chat is always reachable (#1381 section 5)");
        rig.Policy.NeedsFrame().ShouldBeTrue();
        // Opening the chat refreshes it on the pool, and that run wakes the window once.
        await rig.AfterHandOff();
        rig.Draw();
        var card = rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<ChatCard>();
        card.Lines.ShouldContain(l => l.Text == BuddyChat.Placeholder);
        BuddyChat.Placeholder.ShouldBe("Ask the watchtower…");

        // The input has the focus without a click on it: typing lands in it.
        rig.Desk.TextTyped("hi");
        rig.Draw();
        rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<ChatCard>().Lines.ShouldContain(l => l.Text == "hi");

        rig.Click(1, rig.CatBody);
        rig.Desk.Card.ShouldBe(DeskCard.None);
    }

    [Fact]
    public async Task TheTalkButtonOfThePeekCardOpensTheChatAndTheChatLinksBackToThePeek()
    {
        var rig = new Rig();
        rig.Click(1, rig.CatBody);
        await rig.AfterHandOff();
        rig.Draw();
        var chat = rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<ChatCard>();
        var sessions = chat.Hits.Single(h => h.Id == ChatCard.SessionsId);
        var layout = rig.Desk.Geometry.Layout;
        rig.Click(1, (layout.CardX + sessions.X + 4, layout.CardY + sessions.Y + 4));
        rig.Desk.Card.ShouldBe(DeskCard.Peek);
        rig.Draw();
        var peek = rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<PeekCard>();

        var talk = peek.Hits.Single(h => h.Id == PeekCard.TalkId);
        layout = rig.Desk.Geometry.Layout;
        rig.Click(1, (layout.CardX + talk.X + 4, layout.CardY + talk.Y + 4));
        rig.Desk.Card.ShouldBe(DeskCard.Chat);
        await rig.AfterHandOff();
        rig.Draw();
        rig.Desk.TextTyped("x");
        rig.Draw();
        rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<ChatCard>().Lines.ShouldContain(l => l.Text == "x", "the input is focused after Talk too");
    }

    [Fact]
    public async Task TheHoverListShowsTheBadgedCardsAndARowHandsOffThatCardsLink()
    {
        var rig = new Rig();
        rig.Feed.Apply(new SnapshotEvent { Seq = 0, At = rig.Clock.GetUtcNow(), Stream = "s", Sessions = [], Notes = [], Cards = [], Flights = [] });
        rig.Feed.Apply(new OthersShowCardsEvent { Seq = 1, At = rig.Clock.GetUtcNow(), Shown = true });
        rig.Feed.Apply(new SnapshotEvent
        {
            Seq = 2, At = rig.Clock.GetUtcNow(), Stream = "s", Sessions = [], Notes = [], Flights = [],
            Cards =
            [
                new CardRow { Id = "c19375", Bin = CardBin.Ring, Kind = "session-waiting", Subject = "flight/PIL1377/watchdog", Title = "PIL1377's watchdog worker asks you", At = rig.Clock.GetUtcNow() - TimeSpan.FromMinutes(5), State = CardState.Open },
                new CardRow { Id = "c19378", Bin = CardBin.Ring, Kind = "session-waiting", Subject = "flight/TIA2553", Title = "TIA2553 worker crashed", At = rig.Clock.GetUtcNow() - TimeSpan.FromMinutes(1), State = CardState.Open },
            ],
        });
        rig.Model.BadgeCount.ShouldBe(2);
        rig.Desk.PointerMoved(rig.CatBody.X, rig.CatBody.Y);
        var layout = rig.Draw();
        var hover = layout.Card.ShouldBeOfType<HoverCard>();
        hover.Lines.Select(l => l.Text).ShouldContain("2 need you");
        hover.Lines.Select(l => l.Text).ShouldContain("PIL1377's watchdog worker asks you");
        hover.Lines.ShouldContain(l => l.Text.StartsWith("flight PIL1377 leg watchdog", StringComparison.Ordinal));

        // The pointer travels from the cat to a row over the gap: the list stays, and a press hands off the card's link.
        var row = hover.Hits.Single(h => h.Id == "row:1");
        var at = (X: layout.CardX + row.X + 20, Y: layout.CardY + row.Y + 6);
        rig.Desk.PointerMoved(at.X, at.Y);
        rig.Model.Hovering.ShouldBeTrue();
        rig.Click(1, at);
        await rig.Gui.Done.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        rig.Gui.Links.ShouldBe([BuddyFeed.CardLink("c19375")], "the rows are newest first: row 1 is the older card");
        await rig.AfterHandOff();
    }

    [Fact]
    public void ARightClickOpensTheSettingsAndTheCushionToo()
    {
        var rig = new Rig();
        rig.Click(3, rig.CatBody);
        rig.Desk.Card.ShouldBe(DeskCard.Settings);
        rig.Draw();
        rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<SettingsCard>();

        rig.Click(3, rig.Cushion);
        rig.Desk.Card.ShouldBe(DeskCard.None, "a right-click on the cushion toggles the same card");
    }

    [Fact]
    public void LosingTheFocusClosesTheCardAndEscapeToo()
    {
        var rig = new Rig();
        rig.Click(1, rig.CatBody);
        rig.Desk.FocusLost();
        rig.Desk.Card.ShouldBe(DeskCard.None);

        rig.Click(3, rig.CatBody);
        rig.Desk.Escape();
        rig.Desk.Card.ShouldBe(DeskCard.None);
    }

    [Fact]
    public void ADragMovesTheWindowAndDropsItAndIsNotAClick()
    {
        var rig = new Rig();
        var (x, y) = rig.CatBody;
        rig.Desk.ButtonDown(1, x, y);
        // Under the threshold it is still a click.
        rig.Desk.PointerMoved(x + 1, y);
        rig.Window.Moves.ShouldBeEmpty();
        rig.Desk.PointerMoved(x + 20, y + 10);
        rig.Desk.PointerMoved(x + 30, y + 10);
        rig.Desk.ButtonUp(1, x + 30, y + 10);

        rig.Window.Moves.ShouldBe([(20, 10), (30, 10)]);
        rig.Window.Drops.ShouldBe(1);
        rig.Desk.Card.ShouldBe(DeskCard.None, "a drag does not peek");
        rig.Desk.Dragging.ShouldBeFalse();
    }

    [Fact]
    public async Task AClickWhileTheCatSpeaksOpensTheChatAndLaterPutsItBackToSleep()
    {
        var rig = new Rig();
        rig.Model.Tell(Ask());
        var layout = rig.Draw();
        var later = layout.Bubble!.Buttons.Single(b => b.Label == SpeechBubble.LaterLabel);
        var open = layout.Bubble.Buttons.Single(b => b.Label == SpeechBubble.PrimaryLabel(BubbleStyle.Ask));

        rig.Click(1, rig.CatBody);
        rig.Desk.Card.ShouldBe(DeskCard.Chat);
        // Opening the chat refreshes it on the pool, and that run wakes the window once: take that wake now, so the wait after the hand-off below
        // cannot return on this stale one before the hand-off ran.
        await rig.AfterHandOff();
        rig.Click(1, rig.CatBody);
        rig.Desk.Card.ShouldBe(DeskCard.None);

        rig.Click(1, (layout.BubbleX + later.X + later.Width / 2, layout.BubbleY + later.Y + later.Height / 2));
        rig.Model.Current.ShouldBeNull();
        rig.Gui.Links.ShouldBeEmpty();

        rig.Model.Tell(Ask());
        rig.Click(1, (layout.BubbleX + open.X + open.Width / 2, layout.BubbleY + open.Y + open.Height / 2));
        await rig.Gui.Done.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        rig.Gui.Links.ShouldBe([BuddyFeed.CardLink("ask-1")], "Open the chat hands the card to the GUI");
        await rig.AfterHandOff();
        rig.Model.Current.ShouldBeNull();
    }

    [Fact]
    public async Task WhenNoGuiRunsTheNoticeStaysUntilTheFallbackStartedOneAndTakesTheLink()
    {
        var rig = new Rig();
        rig.Gui.Takes = false;
        rig.Model.Tell(Ask());
        var layout = rig.Draw();
        var open = layout.Bubble!.Buttons.Single(b => b.Label == SpeechBubble.PrimaryLabel(BubbleStyle.Ask));

        rig.Click(1, (layout.BubbleX + open.X + open.Width / 2, layout.BubbleY + open.Y + open.Height / 2));
        await rig.Gui.Done.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        rig.Gui.Launched.ShouldBe([BuddyFeed.CardLink("ask-1")], "no GUI took it, so the fallback starts pilot-gui with the link");
        await rig.AfterHandOff();
        rig.Model.Current.ShouldNotBeNull("neither worked: the notice is kept, not lost");

        rig.Gui.LaunchWorks = true;
        rig.Click(1, (layout.BubbleX + open.X + open.Width / 2, layout.BubbleY + open.Y + open.Height / 2));
        await rig.Gui.Done.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await rig.AfterHandOff();
        rig.Model.Current.ShouldBeNull("the fallback started the GUI, which has the card now");
    }

    [Fact]
    public async Task AHandOffThatThrowsAnythingIsLoggedAndTheWindowStillRepaints()
    {
        var rig = new Rig();
        // Not one of the exceptions the desk lists for a node that does not answer: before, it skipped the repaint and the wake, unobserved.
        rig.Gui.Throws = new System.ComponentModel.Win32Exception(5, "Access is denied");
        rig.Model.Tell(Ask());
        var layout = rig.Draw();
        var open = layout.Bubble!.Buttons.Single(b => b.Label == SpeechBubble.PrimaryLabel(BubbleStyle.Ask));

        rig.Click(1, (layout.BubbleX + open.X + open.Width / 2, layout.BubbleY + open.Y + open.Height / 2));
        // The wake comes only after the repaint under the gate, so it proves both.
        await rig.AfterHandOff();

        rig.Logged.ShouldHaveSingleItem().ShouldContain("Win32Exception");
        rig.Logged.Single().ShouldContain("Access is denied");
        rig.Model.Current.ShouldNotBeNull("nothing took the link, so the notice is kept");
    }

    [Fact]
    public void TheHostRelayDeskChatAndLinkShareOneGate()
    {
        var gate = new object();
        var model = new BuddyModel(new Clock());
        var feed = new BuddyFeed(model, new Clock(), () => new BuddySettings());
        // The host hands one gate to the chat, the relay, the desk and the link; each must hold that very object, or the window draws under another lock.
        var chat = new BuddyChat(new NullNode(), new RecordingGui(), () => null, gate);
        chat.Gate.ShouldBeSameAs(gate);
        new BuddyRelay(model, feed, new Clock(), gate).Gate.ShouldBeSameAs(gate);
        new BuddyRelay(model, feed, new Clock()).Gate.ShouldNotBeNull();
        new BuddyDesk(model, feed, chat, new RecordingGui(), new FakeWindow(), () => new BuddySettings(), _ => { }, new Clock(), TimeZoneInfo.Utc, gate, () => { }).Gate.ShouldBeSameAs(gate);
        new BuddyNodeLink((_, _, _) => Task.FromException<IBuddyEvents>(new IOException()), _ => Task.FromException<NodeInfo>(new IOException()), feed, chat, gate, () => { }, _ => { }, TimeSpan.Zero)
            .Gate.ShouldBeSameAs(gate);
    }

    [Fact]
    public void ASettingsRowPressedChangesTheSettingsAndTheCornerMovesTheScene()
    {
        var rig = new Rig();
        rig.Click(3, rig.CatBody);
        var layout = rig.Draw();
        var card = layout.Card!;
        var hit = card.Hits.Single(h => h.Id == "corner:1");

        rig.Click(1, (layout.CardX + hit.X + hit.Width / 2, layout.CardY + hit.Y + hit.Height / 2));
        rig.Settings.CornerIndex.ShouldBe(1);
        var moved = rig.Draw();
        moved.CatX.ShouldBeLessThan(layout.CatX, "the cat sits in the bottom left corner now");

        var done = moved.Card!.Hits.Single(h => h.Id == "done");
        rig.Click(1, (moved.CardX + done.X + done.Width / 2, moved.CardY + done.Y + done.Height / 2));
        rig.Desk.Card.ShouldBe(DeskCard.None);
    }

    [Fact]
    public void ClicksPassThroughTheSleepingCatWhenTheSettingSaysSoAndTheCatStaysDrawn()
    {
        var rig = new Rig();
        using var renderer = new RgbaImageRenderer(480, 720);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), BuddyScene.ViewOf(rig.Model), 1f);
        var geometry = new BuddyGeometry(layout);

        // The shape is the drawn silhouette whatever the setting: a shape also cuts what is drawn outside it, so the sleeping cat would vanish.
        var mask = BuddySilhouette.Of(renderer.Surface);
        BuddySilhouette.Catches(mask, (int)rig.CatBody.X, (int)rig.CatBody.Y).ShouldBeTrue("the cat is drawn, so it is in the shape");

        // The pass is the window's own style, on only while nothing can be clicked.
        BuddySilhouette.PassesClicks(geometry, asleep: true, setting: true).ShouldBeTrue();
        BuddySilhouette.PassesClicks(geometry, asleep: true, setting: false).ShouldBeFalse();
        BuddySilhouette.PassesClicks(geometry, asleep: false, setting: true).ShouldBeFalse("a cat that speaks takes clicks on its buttons");

        rig.Click(1, rig.CatBody);
        var open = rig.Draw();
        BuddySilhouette.PassesClicks(new BuddyGeometry(open), asleep: true, setting: true).ShouldBeFalse("an open card takes clicks");
    }

    [Fact]
    public void TheSettingsCardOpensWithoutAClickForTheWayBackToTheSwitch()
    {
        var rig = new Rig();
        rig.Desk.ShowSettings();
        rig.Desk.Card.ShouldBe(DeskCard.Settings);
        rig.Policy.NeedsFrame().ShouldBeTrue();
    }

    [Fact]
    public void TheWakeTimerArmsOneTimerForTheNextChangeAndRearmsOnlyForAnother()
    {
        var clock = new Clock();
        var wakes = 0;
        using var timer = new BuddyWakeTimer(clock, () => wakes++);
        var at = clock.GetUtcNow() + TimeSpan.FromSeconds(2.5);

        timer.Arm(at);
        timer.Arm(at);
        clock.Timers.Count.ShouldBe(1);
        clock.Timers[0].Due.ShouldBe(TimeSpan.FromSeconds(2.5));
        wakes.ShouldBe(0, "armed, not fired: nothing runs until the moment comes");

        clock.Timers[0].Timer.Fire();
        wakes.ShouldBe(1);

        timer.Arm(at + TimeSpan.FromSeconds(2.5));
        clock.Timers.Count.ShouldBe(2);
        clock.Timers[0].Timer.Disposed.ShouldBeTrue("the earlier timer is replaced");
    }

    [Fact]
    public void TheSummaryLineSaysFramesAsksAndTheDrawTime()
    {
        var rig = new Rig();
        rig.Policy.NeedsFrame();
        rig.Policy.NeedsFrame();
        rig.Policy.Summary().ShouldContain("1 frames for 2 asks");
    }
}
