using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL903: the fleet summary follows changes through the watchtower while a client is attached. Every test runs the follower one look at
/// a time (<see cref="SummaryFollower.StepAsync"/>) on a clock the test moves, against a fake brain and a fake one-off call, so nothing waits
/// and nothing runs <c>claude</c>.
/// </summary>
public sealed class SummaryFollowerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private const string BrainAnswer = """{"summary": "## Needs you\nNothing.\n\n## Where it is\nThe brain says all is quiet."}""";

    /// <summary>A brain that answers every summary, or says why it cannot be asked.</summary>
    private sealed class FakeBrain : IBrain
    {
        public List<string> Asked { get; } = [];

        public string? Unavailable { get; set; }

        public Task<BrainReply> AskAsync(string kind, string request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add(kind);
            if (Unavailable is { } down)
            {
                throw new BrainDownException(down);
            }
            return Task.FromResult(new BrainReply(JsonDocument.Parse(BrainAnswer).RootElement.Clone(), TimeSpan.FromMilliseconds(5), null));
        }
    }

    /// <summary>The paid one-off call: counted, so a test can say it never ran.</summary>
    private sealed class FakeOldCall : ISummaryModel
    {
        public int Calls { get; private set; }

        public Task<SummaryAnswer> AskAsync(string prompt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SummaryAnswer("## Needs you\nNothing.\n\n## Where it is\nThe old call says so.", 900, 0.002));
        }
    }

    private sealed class ListSink : INodeEventSink
    {
        public int Capacity => 1000;

        public bool TryWrite(byte[] message) => true;

        public void Overflowed()
        {
        }
    }

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeBrain _brain = new();
    private readonly FakeOldCall _old = new();
    private readonly Summaries _summaries;
    private readonly SummaryFollower _follower;

    public SummaryFollowerTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _summaries = new Summaries(_state, new BrainSummaryModel(_brain, _old), _time);
        _follower = new SummaryFollower(_state, _summaries, _time);
        _state.PutFlight(Flight("TIA1611"));
    }

    private static FlightRow Flight(string code) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = 1611, Title = "Plate-solve the guide frame", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/r/.claude/worktrees/{code}", Base = "origin/main", State = FlightState.Flying, CreatedAt = Start - TimeSpan.FromHours(3),
    };

    private IDisposable Attach() => _state.Subscribe(null, null, new ListSink());

    private void Change(string title = "A change") => _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1611", title);

    private async Task AfterAsync(TimeSpan by)
    {
        _time.Advance(by);
        await _follower.StepAsync(Ct);
    }

    private SummaryRow? Kept() => _state.Summaries().FirstOrDefault(s => s.Scope == SummaryRow.FleetScope);

    [Fact]
    public async Task AChangeWithAClientAttachedAsksTheWatchtowerOnceAfterTheQuietSpell()
    {
        using var client = Attach();
        await _follower.StepAsync(Ct);

        await AfterAsync(SummaryFollower.Quiet - TimeSpan.FromSeconds(1));
        _brain.Asked.ShouldBeEmpty();
        Kept().ShouldBeNull();

        await AfterAsync(TimeSpan.FromSeconds(2));
        _brain.Asked.ShouldBe(["summary"]);
        _old.Calls.ShouldBe(0);
        Kept().ShouldNotBeNull().Markdown.ShouldContain("The brain says all is quiet.");
        Kept()!.NotFollowing.ShouldBeNull();

        // Nothing changed since: nothing more is asked, however long it stays that way.
        await AfterAsync(SummaryFollower.MinGap * 2);
        await AfterAsync(SummaryFollower.MinGap * 2);
        _brain.Asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ABurstOfChangesAsksOnce()
    {
        using var client = Attach();
        await _follower.StepAsync(Ct);
        await AfterAsync(SummaryFollower.Quiet + TimeSpan.FromSeconds(1));
        _brain.Asked.Count.ShouldBe(1);

        // A burst every ten seconds keeps the quiet spell from ever ending, so nothing is asked in the middle of it...
        for (var i = 0; i < 20; i++)
        {
            Change($"Change {i}");
            await AfterAsync(TimeSpan.FromSeconds(10));
        }
        _brain.Asked.Count.ShouldBe(1);

        // ...and once it is over, the one ask covers all of it.
        await AfterAsync(SummaryFollower.MinGap);
        _brain.Asked.Count.ShouldBe(2);
        await AfterAsync(SummaryFollower.MinGap);
        _brain.Asked.Count.ShouldBe(2);
        _old.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ChangesCloseTogetherNeverAskMoreOftenThanTheMinimumGap()
    {
        using var client = Attach();
        await _follower.StepAsync(Ct);
        await AfterAsync(SummaryFollower.Quiet + TimeSpan.FromSeconds(1));
        _brain.Asked.Count.ShouldBe(1);

        Change();
        await _follower.StepAsync(Ct);
        await AfterAsync(SummaryFollower.Quiet + TimeSpan.FromSeconds(1));
        _brain.Asked.Count.ShouldBe(1);

        await AfterAsync(SummaryFollower.MinGap);
        _brain.Asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WithNoClientAttachedNothingIsAsked()
    {
        Change();
        for (var i = 0; i < 12; i++)
        {
            await AfterAsync(TimeSpan.FromMinutes(1));
            Change($"Change {i}");
        }

        _brain.Asked.ShouldBeEmpty();
        _old.Calls.ShouldBe(0);
        Kept().ShouldBeNull();
    }

    [Fact]
    public async Task AClientThatLeavesStopsTheAsks()
    {
        var client = Attach();
        await _follower.StepAsync(Ct);
        client.Dispose();

        await AfterAsync(SummaryFollower.Quiet + TimeSpan.FromSeconds(1));

        _brain.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADownWatchtowerAsksNothingMakesNoFallbackCallAndTheKeptRowSaysSo()
    {
        // A summary is kept first, as an explicit ask makes it.
        await _summaries.MakeAsync(SummaryRow.FleetScope, Ct);
        _brain.Asked.Count.ShouldBe(1);
        using var client = Attach();
        await _follower.StepAsync(Ct);
        _brain.Unavailable = "it has no running process";
        Change();
        await _follower.StepAsync(Ct);

        await AfterAsync(SummaryFollower.Quiet + TimeSpan.FromSeconds(1));

        _brain.Asked.Count.ShouldBe(1);
        _old.Calls.ShouldBe(0);
        Kept().ShouldNotBeNull().NotFollowing.ShouldNotBeNull().ShouldContain("watchtower is down");
        Kept()!.NotFollowing!.ShouldContain("no running process");
        Kept()!.Markdown.ShouldContain("The brain says all is quiet.");

        // Still down, much later: still nothing asked, and still no call of the old kind.
        await AfterAsync(SummaryFollower.MinGap * 3);
        _brain.Asked.Count.ShouldBe(1);
        _old.Calls.ShouldBe(0);

        // Back up: the next look asks, and the fresh row no longer says it is not following.
        _brain.Unavailable = null;
        await AfterAsync(TimeSpan.FromSeconds(1));
        _brain.Asked.Count.ShouldBe(2);
        Kept()!.NotFollowing.ShouldBeNull();
    }

    [Fact]
    public async Task AnExplicitSummariseAsksAtOnceAndTheFollowerHasNothingLeftToAsk()
    {
        using var client = Attach();
        await _follower.StepAsync(Ct);
        Change();

        var result = await _summaries.RunAsync(new CommandRequest { Verb = Verbs.Summarise, Target = Verbs.NodeTarget }, Ct);

        result.Ok.ShouldBeTrue();
        _brain.Asked.ShouldBe(["summary"]);
        _old.Calls.ShouldBe(0);

        await AfterAsync(SummaryFollower.MinGap * 2);
        _brain.Asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnExplicitSummariseWithTheWatchtowerDownRunsTheOldCall()
    {
        _brain.Unavailable = "it is starting";

        var result = await _summaries.RunAsync(new CommandRequest { Verb = Verbs.Summarise, Target = Verbs.NodeTarget }, Ct);

        result.Ok.ShouldBeTrue();
        _old.Calls.ShouldBe(1);
        Kept().ShouldNotBeNull().Markdown.ShouldContain("The old call says so.");
    }

    [Fact]
    public void TheFingerprintFollowsWhatTheSummaryReadsAndNeverTheClock()
    {
        var before = SummaryRecords.Fingerprint(_state);

        _time.Advance(TimeSpan.FromHours(5));
        SummaryRecords.Fingerprint(_state).ShouldBe(before);

        Change();
        SummaryRecords.Fingerprint(_state).ShouldNotBe(before);
    }

    [Fact]
    public void ClientsAttachedCountsTheFeedsSubscribersAndDropsThemOnDispose()
    {
        _state.ClientsAttached.ShouldBe(0);
        var one = Attach();
        var two = Attach();
        _state.ClientsAttached.ShouldBe(2);
        one.Dispose();
        _state.ClientsAttached.ShouldBe(1);
        two.Dispose();
        _state.ClientsAttached.ShouldBe(0);
    }
}
