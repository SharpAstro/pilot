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
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
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
        var changes = new List<bool>();
        views.CardsChanged += changes.Add;
        views.Say(client, ClientPages.PullRequests);
        views.AnyShowsCards.ShouldBeFalse();
        views.SayCards(client, true);
        views.SayCards(client, true);
        views.AnyShowsCards.ShouldBeTrue();
        views.Count(ClientPages.PullRequests).ShouldBe(1, "saying the cards leaves the page alone");
        views.Forget(client);
        views.AnyShowsCards.ShouldBeFalse();
        changes.ShouldBe([true, false]);
    }
}
