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

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken)
        {
            Calls++;
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
        view.Away.ShouldHaveSingleItem().ShouldBe("LE1 away since 12:00 UTC: skipped until it answers");
        view.Known.ShouldBe(1);
        FleetAdmission.Note(view).ShouldBeNull();
        _said.ShouldHaveSingleItem().ShouldBe(("peer-away", "LE1 away since 12:00 UTC: skipped until it answers"));
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
        thrown.Message.ShouldBe("LE1 away since 12:00 UTC: skipped until it answers");
        peer.Calls.ShouldBe(1);
        presence.AwaySince("LE1").ShouldBe(Noon);
        presence.Away().Select(a => a.Name).ShouldBe(["LE1"]);
    }
}
