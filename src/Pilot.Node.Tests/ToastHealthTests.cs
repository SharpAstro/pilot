using Pilot.Contracts;
using Pilot.Node.Toasts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A broken toaster is visible (#1376): one card in For you, the setup check's line, and its test toast. In memory, on fakes, no process.</summary>
public sealed class ToastHealthTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Runner : IToastRunner
    {
        public bool Throws { get; set; }
        public List<(string Tag, string Xml)> Shown { get; } = [];

        public Task Show(string tag, string xml, CancellationToken cancel = default)
        {
            if (Throws) throw new InvalidOperationException("0x80131047 no projection");
            Shown.Add((tag, xml));
            return Task.CompletedTask;
        }

        public Task Remove(string tag, CancellationToken cancel = default)
            => Throws ? throw new InvalidOperationException("0x80131047 no projection") : Task.CompletedTask;
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private readonly Clock _time = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly NodeState _state;
    private readonly Runner _runner = new();
    private readonly ToastHealth _health = new();
    private readonly Toaster _toaster;

    public ToastHealthTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _toaster = new Toaster(_state, _runner, _time, () => "all", () => false, health: _health);
    }

    public ValueTask DisposeAsync() => _toaster.DisposeAsync();

    private IReadOnlyList<CardRow> Broken() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == ToastHealth.BrokenKind)];

    private NodeSetup Setup() => new(new NodeOptions("/tmp/n.sock", "/tmp/pilot-toast-health"), "pilot-node", "/tmp/pilot-toast-health")
    {
        ToastHealth = _health,
        ToastRunner = _runner,
        ToastsApply = true,
    };

    [Fact]
    public async Task FailingToastsOverSeveralCardsRaiseOneForYouCardAndALaterSuccessClosesIt()
    {
        _runner.Throws = true;
        for (var i = 0; i < 4; i++)
        {
            _state.RaiseCard(CardBin.Ring, "worker-waiting", $"flight/{i}", "needs you", "detail");
        }
        await _toaster.Sync(cancel: Ct);

        var card = Broken().ShouldHaveSingleItem();
        card.Kind.ShouldBe(ToastHealth.BrokenKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe(Verbs.NodeTarget);
        card.Detail.ShouldNotBeNull().ShouldContain("0x80131047");

        _runner.Throws = false;
        _state.RaiseCard(CardBin.Ring, "worker-waiting", "flight/9", "needs you", "detail");
        await _toaster.Sync(cancel: Ct);
        Broken().ShouldBeEmpty();
        _state.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task AFailedRemoveRaisesTheCardToo()
    {
        var card = _state.RaiseCard(CardBin.Ring, "worker-waiting", "flight/A", "needs you", "detail");
        await _toaster.Sync(cancel: Ct);
        Broken().ShouldBeEmpty();

        _runner.Throws = true;
        _state.CloseCard(card.Id, "done", "tests").ShouldBeTrue();
        await _toaster.Sync(cancel: Ct);
        Broken().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheCheckSaysNoAttemptShownAndFailed()
    {
        Setup().ToastsCheck().Line.ShouldContain("no toast has been tried");

        _state.RaiseCard(CardBin.Ring, "worker-waiting", "flight/A", "needs you", "detail");
        await _toaster.Sync(cancel: Ct);
        var shown = Setup().ToastsCheck();
        shown.Ok.ShouldBeTrue();
        shown.Optional.ShouldBeTrue();
        shown.Key.ShouldBe(NodeSetup.ToastsKey);
        shown.Line.ShouldContain("last shown at 2026-11-27 09:00:00Z");

        _time.Advance(TimeSpan.FromMinutes(1));
        _runner.Throws = true;
        _state.RaiseCard(CardBin.Ring, "worker-waiting", "flight/B", "needs you", "detail");
        await _toaster.Sync(cancel: Ct);
        var failed = Setup().ToastsCheck();
        failed.Ok.ShouldBeFalse();
        failed.Line.ShouldContain("failed at 2026-11-27 09:01:00Z: 0x80131047");
        failed.Fix.ShouldBe("Send a test toast");
    }

    [Fact]
    public async Task TheFixShowsATestToastOrReportsTheError()
    {
        (await Setup().FixAsync(NodeSetup.ToastsKey, Ct)).ShouldBeNull();
        var (tag, xml) = _runner.Shown.ShouldHaveSingleItem();
        tag.ShouldBe(ToastXml.TestTag);
        xml.ShouldContain("pilot test toast");

        _runner.Throws = true;
        (await Setup().FixAsync(NodeSetup.ToastsKey, Ct)).ShouldNotBeNull().ShouldContain("0x80131047");
    }

    [Fact]
    public async Task TheRefusalNamesTheToastsKey()
        => (await Setup().FixAsync("nonsense", Ct)).ShouldNotBeNull().ShouldContain(NodeSetup.ToastsKey);
}
