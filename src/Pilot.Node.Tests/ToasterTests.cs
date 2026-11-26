using Pilot.Contracts;
using Pilot.Node.Toasts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node's desktop toasts of Ring cards (#1356): in memory, on a fake clock and a recording runner, with no process.</summary>
public sealed class ToasterTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRunner : IToastRunner
    {
        public List<(string Tag, string Xml)> Shown { get; } = [];
        public List<string> Removed { get; } = [];

        public Task Show(string tag, string xml, CancellationToken cancel = default)
        {
            Shown.Add((tag, xml));
            return Task.CompletedTask;
        }

        public Task Remove(string tag, CancellationToken cancel = default)
        {
            Removed.Add(tag);
            return Task.CompletedTask;
        }
    }

    private const string Normal = "worker-waiting";
    private const string Urgent = "push-blocked";

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private readonly Clock _time = new(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly NodeState _state;
    private readonly FakeRunner _runner = new();
    private readonly Toaster _toaster;
    private string _mode = "all";
    private bool _looking;

    public ToasterTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _toaster = new Toaster(_state, _runner, _time, () => _mode, () => _looking);
    }

    public ValueTask DisposeAsync() => _toaster.DisposeAsync();

    private CardRow Ring(string kind, string subject, string title = "needs you") => _state.RaiseCard(CardBin.Ring, kind, subject, title, "detail");

    [Fact]
    public async Task ARingCardShowsOneToastAndARaiseAgainReplacesIt()
    {
        var card = Ring(Normal, "flight/A");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Count.ShouldBe(1);
        _runner.Shown[0].Tag.ShouldBe("flight/A");
        _runner.Shown[0].Xml.ShouldContain("needs you");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Count.ShouldBe(1, "a card already toasted is not toasted again");

        _time.Advance(TimeSpan.FromMinutes(5));
        _state.RingAgain(card.Id, "waited").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Count.ShouldBe(2);
        _runner.Shown[1].Tag.ShouldBe("flight/A", "the same tag replaces the toast");
        _toaster.Tags.ShouldBe(["flight/A"]);
    }

    [Fact]
    public async Task AForYouOrHandledCardNeverToasts()
    {
        _state.RaiseCard(CardBin.ForYou, Normal, "flight/B", "for you", "detail");
        _state.RaiseCard(CardBin.Handled, Normal, "flight/C", "handled", "detail");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACardMovedIntoRingToastsThenAndClosingItRemovesTheToast()
    {
        var card = _state.RaiseCard(CardBin.ForYou, Normal, "flight/D", "judged", "detail");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.ShouldBeEmpty();

        _state.UpdateCard(card.Id, card.Title, card.Detail, card.Choices, card.Link, bin: CardBin.Ring).ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Select(s => s.Tag).ShouldBe(["flight/D"]);

        _state.CloseCard(card.Id, "done", "tests").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.ShouldBe(["flight/D"]);
        _toaster.Tags.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACardMovedOutOfRingWithdrawsItsToast()
    {
        var card = Ring(Normal, "flight/E");
        await _toaster.Sync(cancel: Ct);
        _state.UpdateCard(card.Id, card.Title, card.Detail, card.Choices, card.Link, bin: CardBin.ForYou).ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.ShouldBe(["flight/E"]);
    }

    [Fact]
    public async Task FourNormalRingsInAMinuteAreOneBurstToastAndUrgentOnesStillToastSingly()
    {
        for (var i = 1; i <= 3; i++)
        {
            Ring(Normal, $"flight/N{i}");
            await _toaster.Sync(cancel: Ct);
        }
        _runner.Shown.Select(s => s.Tag).ShouldBe(["flight/N1", "flight/N2", "flight/N3"]);

        Ring(Normal, "flight/N4");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown[^1].Tag.ShouldBe(ToastXml.BurstTag);
        _runner.Shown[^1].Xml.ShouldContain("4 cards need you");
        _runner.Removed.ShouldBe(["flight/N1", "flight/N2", "flight/N3"], "the singles the burst stands for are withdrawn");

        Ring(Normal, "flight/N5");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown[^1].Tag.ShouldBe(ToastXml.BurstTag, "the burst toast is replaced as more come");
        _runner.Shown[^1].Xml.ShouldContain("5 cards need you");

        Ring(Urgent, "flight/U1");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown[^1].Tag.ShouldBe("flight/U1");
        _runner.Shown[^1].Xml.ShouldContain("scenario=\"urgent\"");
    }

    [Fact]
    public async Task NormalRingsSpreadOverMoreThanAMinuteStaySingles()
    {
        for (var i = 1; i <= 5; i++)
        {
            Ring(Normal, $"flight/S{i}");
            await _toaster.Sync(cancel: Ct);
            _time.Advance(TimeSpan.FromSeconds(30));
        }
        _runner.Shown.Select(s => s.Tag).ShouldBe(["flight/S1", "flight/S2", "flight/S3", "flight/S4", "flight/S5"]);
    }

    [Fact]
    public async Task ANormalToastIsSkippedWhileAClientShowsTheCardsAndAnUrgentOneIsNot()
    {
        _looking = true;
        Ring(Normal, "flight/L1");
        Ring(Urgent, "flight/L2");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Select(s => s.Tag).ShouldBe(["flight/L2"]);

        _looking = false;
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Count.ShouldBe(1, "a card skipped while looking does not toast when the bar closes");
    }

    [Fact]
    public async Task ToastsUrgentHoldsBackNormalOnesAndToastsOffAllOfThem()
    {
        _mode = "urgent";
        Ring(Normal, "flight/M1");
        Ring(Urgent, "flight/M2");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Select(s => s.Tag).ShouldBe(["flight/M2"]);

        _mode = "off";
        Ring(Urgent, "flight/M3");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.Select(s => s.Tag).ShouldBe(["flight/M2"]);
    }

    [Fact]
    public async Task CardsOpenWhenTheNodeStartsAreNotToastedAgain()
    {
        Ring(Normal, "flight/R1");
        await _toaster.Sync(quiet: true, cancel: Ct);
        await _toaster.Sync(cancel: Ct);
        _runner.Shown.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheSubscriptionToastsACardWithoutACallToSync()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new SignallingRunner(shown);
        await using var toaster = new Toaster(_state, runner, _time, () => "all", () => false);
        toaster.Start();
        Ring(Normal, "flight/W1");
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        runner.Tags.ShouldContain("flight/W1");
    }

    private sealed class SignallingRunner(TaskCompletionSource shown) : IToastRunner
    {
        public List<string> Tags { get; } = [];

        public Task Show(string tag, string xml, CancellationToken cancel = default)
        {
            Tags.Add(tag);
            shown.TrySetResult();
            return Task.CompletedTask;
        }

        public Task Remove(string tag, CancellationToken cancel = default) => Task.CompletedTask;
    }

    [Fact]
    public void PageViewsKeepsWhoShowsTheCardsBesideThePage()
    {
        var views = new PageViews();
        var client = new object();
        views.Say(client, ClientPages.PullRequests);
        views.AnyShowsCards.ShouldBeFalse();
        views.SayCards(client, true);
        views.SayCards(client, true);
        views.AnyShowsCards.ShouldBeTrue();
        views.Count(ClientPages.PullRequests).ShouldBe(1, "saying the cards leaves the page alone");
        views.Forget(client);
        views.AnyShowsCards.ShouldBeFalse();
    }

    // #1357's rule, as the node's Toaster sees it: the buddy says it shows the cards only while visible and not snoozed, the GUI says it as before,
    // and the Toaster reads PageViews.AnyShowsCards, so it needs no rule of its own for the buddy.
    private async Task<List<string>> ToastsWith(Action<PageViews> clients)
    {
        var views = new PageViews();
        clients(views);
        var runner = new FakeRunner();
        await using var toaster = new Toaster(_state, runner, _time, () => "all", () => views.AnyShowsCards);
        Ring(Normal, "flight/BN");
        Ring(Urgent, "flight/BU");
        await toaster.Sync(cancel: Ct);
        return [.. runner.Shown.Select(s => s.Tag)];
    }

    [Fact]
    public async Task BuddyAloneVisibleMutesTheNormalCardAndStillToastsTheUrgentOne()
    {
        var buddy = new object();
        var shown = await ToastsWith(v => v.SayCards(buddy, true));
        shown.ShouldBe(["flight/BU"]);
    }

    [Fact]
    public async Task BuddyPlusGuiShowingCardsMutesTheNormalCardAndStillToastsTheUrgentOne()
    {
        var buddy = new object();
        var gui = new object();
        var shown = await ToastsWith(v =>
        {
            v.SayCards(buddy, true);
            v.SayCards(gui, true);
        });
        shown.ShouldBe(["flight/BU"]);
    }

    [Fact]
    public async Task BuddySnoozedOrHiddenLeavesTheToastsAsToday()
    {
        var buddy = new object();
        var shown = await ToastsWith(v =>
        {
            v.SayCards(buddy, true);
            v.SayCards(buddy, false);
        });
        shown.ShouldBe(["flight/BN", "flight/BU"]);
    }

    [Fact]
    public async Task ACardRaisedAgainAsTheFourthNormalRingLeavesNoSingleToastBehind()
    {
        var n1 = Ring(Normal, "flight/N1");
        await _toaster.Sync(cancel: Ct);
        _time.Advance(TimeSpan.FromMinutes(2));
        for (var i = 2; i <= 4; i++)
        {
            Ring(Normal, $"flight/N{i}");
            await _toaster.Sync(cancel: Ct);
        }
        _runner.Shown.Select(s => s.Tag).ShouldBe(["flight/N1", "flight/N2", "flight/N3", "flight/N4"]);

        _state.RingAgain(n1.Id, "again").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Shown[^1].Tag.ShouldBe(ToastXml.BurstTag);
        _runner.Removed.ShouldBe(["flight/N2", "flight/N3", "flight/N4", "flight/N1"], "the card's own earlier single toast goes when it joins the burst");

        _state.CloseCard(n1.Id, "done", "tests").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.Count(t => t == "flight/N1").ShouldBe(1);
    }

    [Fact]
    public async Task AToastUpFromBeforeARestartIsWithdrawnWhenItsCardCloses()
    {
        var card = Ring(Urgent, "flight/Q1");
        await _toaster.Sync(quiet: true, cancel: Ct);
        _runner.Shown.ShouldBeEmpty();

        _state.CloseCard(card.Id, "done", "tests").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        // The burst toast a run before may have left up goes too, once the cards found at start are all gone.
        _runner.Removed.ShouldBe(["flight/Q1", ToastXml.BurstTag]);
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.Count.ShouldBe(2, "withdrawn once");
    }

    [Fact]
    public async Task ABurstToastUpFromBeforeARestartIsNotWithdrawnByTheStartSyncButByTheNextWhenNoCardIsOpen()
    {
        await _toaster.Sync(quiet: true, cancel: Ct);
        _runner.Removed.ShouldBeEmpty("the start sync only notes: no process runs inside the host's start");
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.ShouldBe([ToastXml.BurstTag]);
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.Count.ShouldBe(1, "withdrawn once");
    }

    [Fact]
    public async Task TheConsumerWithdrawsTheOldBurstAfterStartReturns()
    {
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RemovedRunner(removed);
        await using var toaster = new Toaster(_state, runner, _time, () => "all", () => false);
        RemovedRunner.InsideStart = true;
        try { toaster.Start(); }
        finally { RemovedRunner.InsideStart = false; }
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        runner.Tags.ShouldBe([ToastXml.BurstTag]);
        runner.RemovedInsideStart.ShouldBeFalse("Start() returns before any withdraw runs; the consumer does it");
    }

    private sealed class RemovedRunner(TaskCompletionSource removed) : IToastRunner
    {
        [ThreadStatic] public static bool InsideStart;
        public List<string> Tags { get; } = [];
        public bool RemovedInsideStart { get; private set; }
        public Task Show(string tag, string xml, CancellationToken cancel = default) => Task.CompletedTask;
        public Task Remove(string tag, CancellationToken cancel = default)
        {
            if (InsideStart) RemovedInsideStart = true;
            Tags.Add(tag);
            removed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ABurstOfThisRunIsNotWithdrawnByTheRestartRule()
    {
        var old = Ring(Normal, "flight/O1");
        await _toaster.Sync(quiet: true, cancel: Ct);
        _runner.Removed.ShouldBeEmpty("a card found at start may still stand in the old burst");
        for (var i = 1; i <= 4; i++) Ring(Normal, $"flight/B{i}");
        await _toaster.Sync(cancel: Ct);
        _runner.Shown[^1].Tag.ShouldBe(ToastXml.BurstTag);

        _state.CloseCard(old.Id, "done", "tests").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.ShouldNotContain(ToastXml.BurstTag, "the burst up now is this run's, for open cards");
    }

    [Fact]
    public async Task ALongSubjectIsShownAndWithdrawnUnderOneShortTag()
    {
        var subject = @"repo/C:\Users\SebastianGodelet\source\repos\sharpastro\openphd2-multiscope-with-a-long-name-" + new string('x', 40);
        var card = Ring(Urgent, subject);
        await _toaster.Sync(cancel: Ct);
        var tag = _runner.Shown.Single().Tag;
        tag.Length.ShouldBeLessThanOrEqualTo(ToastXml.TagLimit);
        tag.ShouldBe(ToastXml.TagOf(subject));
        _toaster.Tags.ShouldBe([tag]);

        _state.CloseCard(card.Id, "done", "tests").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Removed.ShouldBe([tag], "the withdraw names the same tag as the show");

        var hundred = Ring(Urgent, new string('s', 100));
        await _toaster.Sync(cancel: Ct);
        _runner.Shown[^1].Tag.Length.ShouldBeLessThanOrEqualTo(ToastXml.TagLimit);
        _state.CloseCard(hundred.Id, "done", "tests").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        _runner.Removed[^1].ShouldBe(_runner.Shown[^1].Tag);
    }

    [Fact]
    public async Task AFailingRunnerIsLoggedAndTheNextCardStillToasts()
    {
        var runner = new ThrowingRunner();
        var log = new CaptureLog();
        await using var toaster = new Toaster(_state, runner, _time, () => "all", () => false, log);
        Ring(Urgent, "flight/F1");
        Ring(Urgent, "flight/F2");
        await toaster.Sync(cancel: Ct);
        // Both were tried: the first one failing did not stop the second.
        runner.Tags.Order().ToArray().ShouldBe(["flight/F1", "flight/F2"]);
        log.Lines.Order().ToArray().ShouldBe([
            "Warning toast flight/F1 was not shown: the toast script exited 1: boom",
            "Warning toast flight/F2 was not shown: the toast script exited 1: boom",
        ]);
    }

    private sealed class CaptureLog : Microsoft.Extensions.Logging.ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue($"{logLevel} {formatter(state, exception)}: {exception?.Message}");
    }

    private sealed class ThrowingRunner : IToastRunner
    {
        public List<string> Tags { get; } = [];

        public Task Show(string tag, string xml, CancellationToken cancel = default)
        {
            Tags.Add(tag);
            throw new InvalidOperationException("the toast script exited 1: boom");
        }

        public Task Remove(string tag, CancellationToken cancel = default) => Task.CompletedTask;
    }
}
