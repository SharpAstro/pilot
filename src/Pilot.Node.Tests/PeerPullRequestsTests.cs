using System.Threading.Channels;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node keeps each peer's latest PR read from the peer's event stream (#834): a changed read reaches the cache with no poll, and a peer
/// whose stream stays down past one pace is forgotten, so the watch reads the repo itself. The peer is in memory; ageing and the backoff
/// before a stream is opened again run on the test's clock, which fires a timer when it is advanced past it. Nothing waits on a real timer.
/// </summary>
public sealed class PeerPullRequestsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Pace = TimeSpan.FromMinutes(1);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ppr-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly TimerTime _clock = new(Noon);

    public void Dispose() => TestDirs.DeleteTree(_root);

    /// <summary>A peer whose event stream the test drives: rows pushed arrive, a drop ends the stream, and while it is down no stream opens.</summary>
    private sealed class StreamPeer : IFleetPeer
    {
        private Channel<PullRequestsRow> _stream = Channel.CreateUnbounded<PullRequestsRow>();

        public bool Down { get; private set; }

        public int Opened { get; private set; }

        public int Loads { get; private set; }

        public string Name => "B1";

        public string Endpoint => "b1.local:47731";

        public bool HasGrant => true;

        public void Push(PullRequestsRow row) => _stream.Writer.TryWrite(row);

        public void Drop()
        {
            Down = true;
            _stream.Writer.TryComplete(new IOException("connection reset"));
        }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromException<FleetLoad>(new HttpRequestException("connection refused"));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<PullRequestsRow> PullRequestsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Opened++;
            if (Down)
            {
                throw new HttpRequestException("connection refused");
            }
            await foreach (var row in _stream.Reader.ReadAllAsync(cancellationToken))
            {
                yield return row;
            }
        }
    }

    private sealed class OneFleet(IFleetPeer peer) : IFleet
    {
        public IReadOnlyList<IFleetPeer> Peers() => [peer];
    }

    private static PullRequestRow Pr(int number) => new()
    {
        Repo = "SharpAstro/pilot", Number = number, Title = $"PR {number}", Url = $"https://github.com/SharpAstro/pilot/pull/{number}", Kind = PullRequestKind.Ready, Label = "Ready",
    };

    private static PullRequestsRow RowOf(DateTimeOffset readAt, params int[] numbers) => new()
    {
        ReadAt = readAt,
        PullRequests = [.. numbers.Select(Pr)],
        RepoReads = [new PullRequestRepoRead("SharpAstro/pilot", readAt, "B1", false)],
    };

    private PeerPullRequests Cache(StreamPeer peer) => new(() => new OneFleet(peer), _clock, () => Pace)
    {
        Rescan = TimeSpan.FromHours(1),
        Backoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(4),
    };

    // Completes when the cache says it changed and the condition holds; a test never waits on a poll.
    private static async Task Until(PeerPullRequests cache, Func<bool> condition)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On()
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }
        cache.Changed += On;
        try
        {
            On();
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        }
        finally
        {
            cache.Changed -= On;
        }
    }

    // The stream is down and the cache waits to open it again (the rescan's timer and the backoff's are set).
    private Task Backing(PeerPullRequests cache) => Until(cache, () => _clock.Waiting >= 2);

    [Fact]
    public async Task AChangedReadReachesTheCacheWithNoPoll()
    {
        var peer = new StreamPeer();
        using var cache = Cache(peer);
        await cache.StartAsync(Ct);

        cache.RowOf("B1").ShouldBeNull();
        peer.Push(RowOf(Noon, 7));
        await Until(cache, () => cache.RowOf("B1")?.PullRequests.Length == 1);
        cache.RowOf("B1")!.PullRequests.Single().Number.ShouldBe(7);

        peer.Push(RowOf(Noon.AddSeconds(30), 7, 8));
        await Until(cache, () => cache.RowOf("B1")?.PullRequests.Length == 2);
        cache.RowOf("B1")!.RepoReads.Single().ReadAt.ShouldBe(Noon.AddSeconds(30));

        peer.Loads.ShouldBe(0);
        peer.Opened.ShouldBe(1);
        await cache.StopAsync(Ct);
    }

    [Fact]
    public async Task ADroppedStreamIsOpenedAgainAndItsNextReadIsKept()
    {
        var peer = new StreamPeer();
        using var cache = Cache(peer);
        await cache.StartAsync(Ct);
        peer.Push(RowOf(Noon, 7));
        await Until(cache, () => cache.RowOf("B1") is not null);

        peer.Drop();
        await Backing(cache);
        _clock.Advance(cache.Backoff);
        await Until(cache, () => peer.Opened >= 2);
        // Down for no time yet: the read is still held.
        cache.RowOf("B1").ShouldNotBeNull();
        await cache.StopAsync(Ct);
    }

    [Fact]
    public async Task APeerDownPastOnePaceIsForgottenAndTheWatchReadsTheRepoItself()
    {
        var peer = new StreamPeer();
        using var cache = Cache(peer);
        await cache.StartAsync(Ct);

        // B has read pilot; A's watch takes B's read from the cache, asking nobody.
        var github = new FakeGitHub();
        github.PutPullRequest(Pr(7));
        var clone = Directory.CreateDirectory(Path.Combine(_root, "pilot", ".git")).Parent!.FullName;
        File.WriteAllText(Path.Combine(clone, RepoCatalog.MarkerFile), "rules");
        File.WriteAllText(Path.Combine(clone, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        var state = new NodeState(new InMemoryJournal(), _clock);
        var log = new StringWriter();
        var watch = new PullRequestWatch(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), github, _clock)
        {
            Machine = "A1", Interval = Pace, Fleet = new OneFleet(peer), Held = cache,
        };
        watch.Error = TextWriter.Synchronized(log);

        peer.Push(RowOf(Noon, 7));
        await Until(cache, () => cache.RowOf("B1") is not null);
        var taken = (await watch.RefreshAsync(Ct))!;
        github.Searches.Count.ShouldBe(0);
        peer.Loads.ShouldBe(0);
        taken.PullRequests.Select(p => p.Number).ShouldBe([7]);

        // B goes away and stays away longer than one pace: its read is gone, the ask at read time fails, and A searches GitHub.
        peer.Drop();
        await Backing(cache);
        _clock.Advance(cache.Backoff);
        await Until(cache, () => peer.Opened >= 2);
        _clock.Advance(Pace + TimeSpan.FromSeconds(1));
        cache.RowOf("B1").ShouldBeNull();
        var read = (await watch.RefreshAsync(Ct))!;
        github.Searches.Count.ShouldBe(1);
        read.PullRequests.Select(p => p.Number).ShouldBe([7]);
        read.RepoReads.Single().By.ShouldBe("A1");
        await cache.StopAsync(Ct);
    }
}
