using System.Net.WebSockets;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A peer that has not answered for longer than the bound is away (PIL1312): every call on it is skipped with no network attempt, it is said once,
/// and the probe's first answer makes it a peer again. The peers are fakes that count their calls, and the clock is the test's own; nothing waits.
/// </summary>
public sealed class PeerPresenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Bound = TimeSpan.FromHours(1);

    private readonly TimerTime _clock = new(Noon);

    private readonly List<(string Kind, string Title)> _said = [];

    private sealed class FakePeer(string name) : IFleetPeer
    {
        public bool Down { get; set; } = true;

        public int Calls { get; private set; }

        public int Streams { get; private set; }

        public string Name => name;

        public string Endpoint => name.ToLowerInvariant() + ".local:47731";

        public bool HasGrant => true;

        /// <summary>Its node read never answers: it ends only when its token is cancelled, as a host that is off and drops the connect.</summary>
        public bool Hangs { get; set; }

        private TaskCompletionSource? _called;

        /// <summary>Completes at the next node read.</summary>
        public Task NextCall()
        {
            _called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _called.Task;
        }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            _called?.TrySetResult();
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<NodeInfo>(cancellationToken);
            }
            if (Hangs)
            {
                return HangAsync(cancellationToken);
            }
            if (Down)
            {
                return Task.FromException<NodeInfo>(new WebSocketException("Unable to connect to the remote server"));
            }
            return Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1,
                StartedAt = Noon, DataFolder = "/data", SocketPath = "/sock", Stream = "s",
            });
        }

        private static async Task<NodeInfo> HangAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("an infinite wait ended without its token");
        }

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new FleetLoad(null, []));

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<PullRequestsRow> PullRequestsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Streams++;
            await Task.CompletedTask;
            throw new WebSocketException("Unable to connect to the remote server");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class OneFleet(IFleetPeer peer) : IFleet
    {
        public IReadOnlyList<IFleetPeer> Peers() => [peer];
    }

    private (FakePeer Peer, PresenceFleet Fleet, PeerPresence Presence, FleetAdmission Admission) Setup()
    {
        var peer = new FakePeer("LE1");
        var presence = new PeerPresence(_clock, () => Bound, (kind, title, _) => _said.Add((kind, title)));
        var fleet = new PresenceFleet(new OneFleet(peer), presence);
        return (peer, fleet, presence, new FleetAdmission("AA1", _clock) { Fleet = fleet });
    }

    [Fact]
    public async Task A_peer_under_the_bound_is_still_asked()
    {
        var (peer, _, _, admission) = Setup();
        await admission.RefreshAsync(Ct);
        _clock.Advance(Bound - TimeSpan.FromMinutes(1));
        var view = await admission.RefreshAsync(Ct);

        peer.Calls.ShouldBe(2);
        view.NotCounted.Count.ShouldBe(1);
        view.Away.ShouldBeEmpty();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_peer_failing_past_the_bound_is_skipped_listed_as_away_and_said_once()
    {
        var (peer, _, _, admission) = Setup();
        await admission.RefreshAsync(Ct);
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));

        var view = await admission.RefreshAsync(Ct);
        await admission.RefreshAsync(Ct);
        await admission.RefreshAsync(Ct);

        peer.Calls.ShouldBe(1);
        view.NotCounted.ShouldBeEmpty();
        view.Away.ShouldHaveSingleItem().ShouldBe("LE1 away since 2026-11-24 12:00 UTC: skipped until it answers");
        view.Known.ShouldBe(1);
        FleetAdmission.Note(view).ShouldBeNull();
        _said.ShouldHaveSingleItem().ShouldBe(("peer-away", "LE1 away since 2026-11-24 12:00 UTC: skipped until it answers"));
    }

    [Fact]
    public async Task A_fleet_wide_setting_names_an_away_peer_as_unreachable()
    {
        var peer = new FakePeer("LE1");
        var presence = new PeerPresence(_clock, () => Bound, (kind, title, _) => _said.Add((kind, title)));
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var models = new RoleModels(NodeSettings.With(new NodeSettings(), "opus-cap", ["5"]).Settings!);
        var gate = new AdmissionGate(state, () => models, "AA1", _clock);
        gate.Fleet.Fleet = new PresenceFleet(new OneFleet(peer), presence);
        await gate.Fleet.RefreshAsync(Ct);
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));
        await gate.Fleet.RefreshAsync(Ct);

        var sent = gate.PushFleet("opus-cap", ["5"]);

        var one = sent.ShouldHaveSingleItem();
        one.Outcome.ShouldBe(AdmissionGate.FleetSendOutcome.Unreachable);
        one.Node.ShouldContain("LE1");
    }

    [Fact]
    public async Task No_pull_request_stream_is_opened_for_an_away_peer_and_no_failure_is_written()
    {
        var (peer, fleet, presence, admission) = Setup();
        await admission.RefreshAsync(Ct);
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));
        presence.IsAway("LE1").ShouldBeTrue();

        var error = new StringWriter();
        using var cache = new PeerPullRequests(() => fleet, _clock, () => TimeSpan.FromMinutes(1), presence.IsAway) { Error = error, Rescan = TimeSpan.FromHours(1) };
        await cache.StartAsync(Ct);
        await cache.StopAsync(Ct);

        peer.Streams.ShouldBe(0);
        error.ToString().ShouldNotContain("stream failed");
    }

    [Fact]
    public async Task The_probes_first_answer_makes_the_peer_a_peer_again_and_it_is_said_once()
    {
        var (peer, fleet, presence, admission) = Setup();
        await admission.RefreshAsync(Ct);
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));
        (await admission.RefreshAsync(Ct)).Away.Count.ShouldBe(1);
        var probe = new PeerProbe(fleet, _clock);

        // Still silent: tried once, still away, nothing said again.
        await probe.ProbeAsync(Ct);
        peer.Calls.ShouldBe(2);
        presence.IsAway("LE1").ShouldBeTrue();
        _said.Count.ShouldBe(1);

        peer.Down = false;
        await probe.ProbeAsync(Ct);
        presence.IsAway("LE1").ShouldBeFalse();

        var view = await admission.RefreshAsync(Ct);
        view.Peers.Select(p => p.Name).ShouldBe(["LE1"]);
        view.Away.ShouldBeEmpty();
        _said.Select(s => s.Kind).ShouldBe(["peer-away", "peer-back"]);
    }

    [Fact]
    public async Task A_call_on_an_away_peer_throws_an_IOException_at_once()
    {
        var (peer, fleet, presence, admission) = Setup();
        await admission.RefreshAsync(Ct);
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));

        var thrown = await Should.ThrowAsync<PeerAwayException>(() => fleet.Peers()[0].NodeAsync(Ct));

        thrown.ShouldBeAssignableTo<IOException>();
        thrown.Message.ShouldBe("LE1 away since 2026-11-24 12:00 UTC: skipped until it answers");
        peer.Calls.ShouldBe(1);
        presence.AwaySince("LE1").ShouldBe(Noon);
        presence.Away().Select(a => a.Name).ShouldBe(["LE1"]);
    }

    [Fact]
    public async Task The_probe_asks_an_away_peer_once_each_Every_and_not_before()
    {
        var (peer, fleet, presence, admission) = Setup();
        await admission.RefreshAsync(Ct);
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));
        presence.IsAway("LE1").ShouldBeTrue();
        var probe = new PeerProbe(fleet, _clock);
        probe.Every.ShouldBe(TimeSpan.FromMinutes(10));

        await probe.StartAsync(Ct);
        try
        {
            foreach (var round in new[] { 1, 2, 3 })
            {
                await _clock.WhenWaiting(1).WaitAsync(TimeSpan.FromSeconds(3), Ct);
                var asked = peer.NextCall();
                _clock.Advance(probe.Every - TimeSpan.FromMilliseconds(1));
                peer.Calls.ShouldBe(round, $"not asked before round {round}'s {probe.Every} are over");
                _clock.Advance(TimeSpan.FromMilliseconds(1));
                await asked.WaitAsync(TimeSpan.FromSeconds(3), Ct);
                peer.Calls.ShouldBe(round + 1);
            }
        }
        finally
        {
            await probe.StopAsync(Ct);
        }
        // Still silent each time: still away, and said only once.
        presence.IsAway("LE1").ShouldBeTrue();
        _said.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_call_its_caller_cancelled_starts_no_away_clock()
    {
        var (peer, fleet, presence, _) = Setup();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<TaskCanceledException>(() => fleet.Peers()[0].NodeAsync(cancelled.Token));
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));

        peer.Calls.ShouldBe(1);
        presence.IsAway("LE1").ShouldBeFalse();
        presence.Away().ShouldBeEmpty();
        _said.ShouldBeEmpty();

        // The same peer failing while its caller still waits does start it.
        await Should.ThrowAsync<WebSocketException>(() => fleet.Peers()[0].NodeAsync(Ct));
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));
        presence.IsAway("LE1").ShouldBeTrue();
    }

    [Fact]
    public async Task A_peer_that_hangs_past_the_admission_reads_budget_still_goes_away()
    {
        var (peer, _, presence, admission) = Setup();
        peer.Hangs = true;
        admission.Budget = TimeSpan.FromSeconds(5);

        // The budget runs on the injected clock: the read waits on the hanging peer until the test moves time past it, with no real wait.
        var read = admission.RefreshAsync(Ct);
        await _clock.WhenWaiting(1);
        _clock.Advance(admission.Budget);
        var view = await read;
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));

        view.NotCounted.ShouldHaveSingleItem().ShouldStartWith("LE1 (did not answer");
        presence.AwaySince("LE1").ShouldBe(Noon);
    }

    [Fact]
    public void The_presence_lines_land_as_Handled_cards_on_the_node()
    {
        var state = new NodeState(new InMemoryJournal(), _clock);
        var presence = new PeerPresence(_clock, () => Bound, PeerAway.Cards(() => state));
        presence.Failed("LE1");
        _clock.Advance(Bound + TimeSpan.FromMinutes(1));

        presence.IsAway("LE1").ShouldBeTrue();
        presence.Answered("LE1");

        var cards = state.Cards();
        cards.Select(c => (c.Kind, c.Bin, c.Subject, c.Title)).OrderBy(c => c.Kind, StringComparer.Ordinal).ShouldBe(
        [
            ("peer-away", CardBin.Handled, Verbs.NodeTarget, "LE1 away since 2026-11-24 12:00 UTC: skipped until it answers"),
            ("peer-back", CardBin.Handled, Verbs.NodeTarget, "LE1 answers again"),
        ]);
    }
}
