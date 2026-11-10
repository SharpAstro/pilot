using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The fleet shares the PR read (#834): two nodes in memory, each with its own state, its own fake GitHub and its own watch, the peer path
/// between them handing one's row to the other as the LAN would. The chosen reader's GitHub is asked for a shared repo and the other node
/// takes its PRs with its stamps; a repo only one node tracks is read by it; a reader that stops answering is read around within one pace;
/// an older node (a row with no per-repo list) is read around too. The clock is the test's.
/// </summary>
public sealed class PullRequestShareTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private readonly Clock _clock = new(Noon);

    public void Dispose() => TestDirs.DeleteTree(_root);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Node(string name, NodeState state, FakeGitHub github, PullRequestWatch watch, StringWriter log, string root, ListFleet fleet)
    {
        public void Link(IFleetPeer peer) => fleet.Held.Add(peer);

        public string Name => name;

        public NodeState State => state;

        public FakeGitHub GitHub => github;

        public PullRequestWatch Watch => watch;

        public string Log => log.ToString();

        public string PathOf(string repo) => Path.Combine(root, repo);

        public int Searches => github.Searches.Count;
    }

    private sealed class Peer(Node node) : IFleetPeer
    {
        public bool Down { get; set; }

        public string Name => node.Name;

        public string Endpoint => $"{node.Name.ToLowerInvariant()}.local:47731";

        public bool HasGrant => true;

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) =>
            Down ? Task.FromException<FleetLoad>(new HttpRequestException("connection refused")) : Task.FromResult(new FleetLoad(null, node.State.Flights(), PullRequests: node.State.PullRequests()));

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ListFleet : IFleet
    {
        public List<IFleetPeer> Held { get; } = [];

        public IReadOnlyList<IFleetPeer> Peers() => Held;
    }

    // A node whose clones are the given repos (each with a GitHub origin and a PILOT.md, so the catalog tracks it), watching on the test's clock.
    private Node NewNode(string name, params string[] repos)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var repo in repos)
        {
            var clone = Directory.CreateDirectory(Path.Combine(root, repo, ".git")).Parent!.FullName;
            File.WriteAllText(Path.Combine(clone, RepoCatalog.MarkerFile), "rules");
            File.WriteAllText(Path.Combine(clone, ".git", "config"), $"[remote \"origin\"]\n\turl = https://github.com/SharpAstro/{repo}.git\n");
        }
        var state = new NodeState(new InMemoryJournal(), _clock);
        var github = new FakeGitHub();
        var log = new StringWriter();
        var fleet = new ListFleet();
        var watch = new PullRequestWatch(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [root])), github, _clock)
        {
            Machine = name,
            Interval = TimeSpan.FromMinutes(1),
            Fleet = fleet,
        };
        watch.Error = TextWriter.Synchronized(log);
        return new Node(name, state, github, watch, log, root, fleet);
    }

    private static PullRequestRow Pr(string repo, int number, string? head = null) => new()
    {
        Repo = $"SharpAstro/{repo}", Number = number, Title = $"PR {number}", Url = $"https://github.com/SharpAstro/{repo}/pull/{number}", Kind = PullRequestKind.Ready, Label = "Ready", Head = head,
    };

    // The flight of a node in a repo of it, flying, on the branch a PR comes from.
    private static void Fly(Node node, string repo, string code) => node.State.PutFlight(new FlightRow
    {
        Code = code, Repo = repo, RepoPath = node.PathOf(repo), Issue = 1, Branch = $"flight/{code}", Worktree = node.PathOf(code), Base = "origin/main",
        State = FlightState.Flying, CreatedAt = Noon,
    });

    [Fact]
    public async Task OnlyTheChosenReadersGitHubIsAskedForASharedRepoAndTheOtherShowsItsPrsNamingTheReader()
    {
        // B flies in pilot, so B reads it; A tracks it too and takes what B read.
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7, "flight/PIL1"));
        a.Link(new Peer(b));
        b.Link(new Peer(a));

        await b.Watch.RefreshAsync(Ct);
        var row = (await a.Watch.RefreshAsync(Ct))!;

        a.Searches.ShouldBe(0);
        b.Searches.ShouldBe(1);
        row.PullRequests.Select(p => p.Number).ShouldBe([7]);
        row.Cost.ShouldBe(0);
        row.RepoReads.ShouldHaveSingleItem().ShouldBe(new PullRequestRepoRead("SharpAstro/pilot", Noon, "B1", false));
        // A has no flight of its own: the flight is re-derived by A's own flights, never taken from B's row.
        row.PullRequests.Single().Flight.ShouldBeNull();
        a.Log.ShouldContain("1 repo from peers, 0 read here in 0 searches, 0 GraphQL points");

        // A minute on B reads again (its stamp is a pace old) and A takes that read once more, still asking GitHub nothing.
        _clock.Advance(TimeSpan.FromSeconds(61));
        b.GitHub.PutPullRequest(Pr("pilot", 8));
        await b.Watch.RefreshAsync(Ct);
        var next = (await a.Watch.RefreshAsync(Ct))!;

        a.Searches.ShouldBe(0);
        next.PullRequests.Select(p => p.Number).Order().ShouldBe([7, 8]);
        next.RepoReads.Single().ReadAt.ShouldBe(Noon.AddSeconds(61));
    }

    [Fact]
    public async Task TheNonReaderOnItsPaceLeavesTheRepoToTheReaderEvenBeforeItReadsAgain()
    {
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.Link(new Peer(b));

        await b.Watch.RefreshAsync(Ct);
        await a.Watch.ReadOnPaceAsync(Ct);
        a.Searches.ShouldBe(0);

        // 61 s on, B's read is a pace old and B has not read again; A (looking first) leaves the repo to B and keeps what it held.
        _clock.Advance(TimeSpan.FromSeconds(61));
        var row = (await a.Watch.ReadOnPaceAsync(Ct))!;

        a.Searches.ShouldBe(0);
        b.Searches.ShouldBe(1);
        row.PullRequests.Select(p => p.Number).ShouldBe([7]);
        row.RepoReads.Single().By.ShouldBe("B1");
    }

    [Fact]
    public async Task ARepoLeftToTheReaderIsReadHereOnceTheReaderIsTwoPacesQuiet()
    {
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.GitHub.PutPullRequest(Pr("pilot", 7));
        a.Link(new Peer(b));

        await b.Watch.RefreshAsync(Ct);
        await a.Watch.ReadOnPaceAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(121));
        var row = (await a.Watch.ReadOnPaceAsync(Ct))!;

        a.Searches.ShouldBe(1);
        row.RepoReads.Single().By.ShouldBe("A1");
    }

    [Fact]
    public async Task ARepoThisNodeHoldsNoPrsOfIsReadHereNotLeftToTheReader()
    {
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.GitHub.PutPullRequest(Pr("pilot", 7));
        a.Link(new Peer(b));

        await b.Watch.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(61));
        // B's read is a pace old and A holds nothing of the repo: waiting would leave A with no PRs of it.
        var row = (await a.Watch.ReadOnPaceAsync(Ct))!;

        a.Searches.ShouldBe(1);
        row.PullRequests.Select(p => p.Number).ShouldBe([7]);
    }

    [Fact]
    public async Task AReadOfTheSamePrsStillShowsAPeerAFreshStamp()
    {
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.Link(new Peer(b));

        await b.Watch.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(60));
        await b.Watch.ReadOnPaceAsync(Ct);
        b.Searches.ShouldBe(2);
        _clock.Advance(TimeSpan.FromSeconds(10));
        var row = (await a.Watch.ReadOnPaceAsync(Ct))!;

        // B's second read found the same PRs, yet the stamp A sees is the second read's, 10 s old: A takes it.
        a.Searches.ShouldBe(0);
        row.RepoReads.Single().ReadAt.ShouldBe(Noon.AddSeconds(60));
    }

    [Fact]
    public async Task ARefreshAPersonAsksForReadsGitHubWhateverAPeerHolds()
    {
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.GitHub.PutPullRequest(Pr("pilot", 7));
        a.Link(new Peer(b));

        await b.Watch.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var row = (await a.Watch.RefreshAsync(Ct))!;

        // B's read is 30 s old, well inside a pace, and a timed read would take it; a refresh asked for searches GitHub.
        a.Searches.ShouldBe(1);
        row.RepoReads.Single().By.ShouldBe("A1");
    }

    [Fact]
    public async Task ARepoOnlyOneNodeTracksIsReadByThatNode()
    {
        var a = NewNode("A1", "pilot", "tianwen");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        a.GitHub.PutPullRequest(Pr("tianwen", 3));
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.Link(new Peer(b));

        await b.Watch.RefreshAsync(Ct);
        var row = (await a.Watch.RefreshAsync(Ct))!;

        // pilot comes from B; tianwen, which B does not track, A searches for itself.
        a.GitHub.Searches.Single().ShouldBe(["SharpAstro/tianwen"]);
        row.PullRequests.Select(p => (p.Repo, p.Number)).Order().ShouldBe([("SharpAstro/pilot", 7), ("SharpAstro/tianwen", 3)]);
        row.RepoReads.Single(s => s.Repo == "SharpAstro/tianwen").By.ShouldBe("A1");
        row.RepoReads.Single(s => s.Repo == "SharpAstro/pilot").By.ShouldBe("B1");
    }

    [Fact]
    public async Task WhenTheReaderStopsAnsweringTheOtherReadsTheRepoItselfWithinOnePace()
    {
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.GitHub.PutPullRequest(Pr("pilot", 7));
        var toB = new Peer(b);
        a.Link(toB);

        await b.Watch.RefreshAsync(Ct);
        await a.Watch.RefreshAsync(Ct);
        a.Searches.ShouldBe(0);

        toB.Down = true;
        _clock.Advance(TimeSpan.FromSeconds(30));
        var row = (await a.Watch.RefreshAsync(Ct))!;

        a.Searches.ShouldBe(1);
        row.RepoReads.Single().By.ShouldBe("A1");
        a.Log.ShouldContain("peer B1 did not answer");
        a.Log.ShouldContain("1 read here in 1 search, 3 GraphQL points");
    }

    [Fact]
    public async Task AnOlderPeerWhoseRowHasNoPerRepoListIsReadAroundAndThisNodeReadsForItself()
    {
        var a = NewNode("B1", "pilot");
        var older = NewNode("A1", "pilot");
        // The older node's row has PRs of the repo but no stamps: it cannot be taken from, and it is not left to.
        older.State.PutPullRequests(new PullRequestsRow { ReadAt = Noon, PullRequests = [Pr("pilot", 5)] });
        a.GitHub.PutPullRequest(Pr("pilot", 7));
        a.Link(new Peer(older));

        var row = (await a.Watch.RefreshAsync(Ct))!;

        a.Searches.ShouldBe(1);
        row.PullRequests.Select(p => p.Number).ShouldBe([7]);
        row.RepoReads.Single().By.ShouldBe("B1");
    }

    [Fact]
    public async Task ARepoWithNoOpenPrsIsLeftToItsReaderLikeAnyOther()
    {
        var a = NewNode("A1", "pilot");
        var b = NewNode("B1", "pilot");
        // A read the empty repo itself before B came into the fleet: it holds a stamp and no PR of it.
        await a.Watch.RefreshAsync(Ct);
        a.Searches.ShouldBe(1);
        Fly(b, "pilot", "PIL1");
        await b.Watch.RefreshAsync(Ct);
        a.Link(new Peer(b));

        // B's read is a pace old, not fresh to take, yet within its grace: A leaves the repo to B.
        _clock.Advance(TimeSpan.FromSeconds(61));
        await a.Watch.ReadOnPaceAsync(Ct);

        a.Searches.ShouldBe(1);
    }

    [Fact]
    public async Task ARefreshAPersonAsksForDoesNotJoinAnOnPaceReadThatTookAPeersRead()
    {
        var a = NewNode("A1", "pilot", "tianwen");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        await b.Watch.RefreshAsync(Ct);
        a.Link(new Peer(b));
        _clock.Advance(TimeSpan.FromSeconds(10));
        var hold = a.GitHub.HoldSearches();

        // The pace's read takes pilot from B and waits in its search of tianwen.
        var onPace = a.Watch.ReadOnPaceAsync(Ct);
        var refresh = a.Watch.RefreshAsync(Ct);
        hold.SetResult();
        var paced = (await onPace)!;
        var asked = (await refresh)!;

        paced.RepoReads.Single(s => s.Repo.EndsWith("/pilot", StringComparison.Ordinal)).By.ShouldBe("B1");
        // The person's refresh read pilot from GitHub itself, a peer's read ten seconds old being too old for it.
        asked.RepoReads.Single(s => s.Repo.EndsWith("/pilot", StringComparison.Ordinal)).By.ShouldBe("A1");
        a.Searches.ShouldBe(2);
    }

    [Fact]
    public async Task ATakenRepoReachesTheLookedListenersLikeItsOwn()
    {
        var a = NewNode("A1", "pilot", "tianwen");
        var b = NewNode("B1", "pilot");
        Fly(b, "pilot", "PIL1");
        b.GitHub.PutPullRequest(Pr("pilot", 7));
        a.GitHub.PutPullRequest(Pr("tianwen", 3));
        a.Link(new Peer(b));
        var looked = new List<(string[] Repos, PullRequestRow[] Prs)>();
        a.Watch.Looked += (repos, prs) => looked.Add((repos, prs));

        await b.Watch.RefreshAsync(Ct);
        await a.Watch.RefreshAsync(Ct);

        var heard = looked.ShouldHaveSingleItem();
        heard.Repos.Order().ShouldBe(["SharpAstro/pilot", "SharpAstro/tianwen"]);
        heard.Prs.Select(p => p.Number).Order().ShouldBe([3, 7]);
    }

    // What a flight does when it acts (merge, queue, labels, landing, its own PR reads) goes to GitHub through IGitHub, never through the
    // watch's row, so a peer's read cannot stand in for it. Pinned by where the sharing lives: only the watch knows the fleet's reads.
    [Fact]
    [Trait("Category", InvariantTraitTests.Invariant)]
    public void OnlyThePullRequestWatchTakesAPeersRead()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull("no folder holding Pilot.slnx above the test's directory");
        var node = Path.Combine(dir.FullName, "Pilot.Node");
        var flights = Path.Combine(node, "Flights");

        // The scan is the guard because a future acting file is not known to a behavioural test: it reads every source of the node, so one
        // added anywhere that takes the fleet's reads is named here, not only one in Flights. The two wirings are the node's composition.
        var sources = Directory.EnumerateFiles(node, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToArray();
        var sharing = sources.Where(f => File.ReadAllText(f).Contains("PrReadShare")).Select(Path.GetFileName).Order().ToArray();
        sharing.ShouldBe(["PrReadShare.cs", "PullRequestWatch.cs"]);
        var holding = sources.Where(f => File.ReadAllText(f).Contains("PeerPullRequests")).Select(Path.GetFileName).Order().ToArray();
        holding.ShouldBe(["NodeApp.Services.cs", "PeerPullRequests.cs", "PullRequestWatch.cs"]);

        var acting = Directory.EnumerateFiles(flights, "*.cs")
            .Where(f => Path.GetFileName(f) is { } n && (n.StartsWith("LegCommander", StringComparison.Ordinal) || n.StartsWith("FlightCommander", StringComparison.Ordinal) || n.StartsWith("LegTools", StringComparison.Ordinal) || n.StartsWith("LabelHold", StringComparison.Ordinal)))
            .Where(f => File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).Any(l => l.Contains("state.PullRequests()") || l.Contains("_state.PullRequests()") || l.Contains("PullRequestWatch")))
            .Select(Path.GetFileName).ToArray();
        acting.ShouldBeEmpty();
    }
}
