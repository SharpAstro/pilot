using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight is flown by one node (#375): a create-flight, a plan or a leg start on an issue or a branch a reachable peer node flies
/// is refused naming that node, and --anyway does not override a peer's live flight (it only overrides a label). A peer that cannot be
/// reached, or whose flight has landed, does not block.
/// </summary>
public sealed class OneNodePerFlightTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string GitHub = "SharpAstro/tianwen";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2o-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeFleet _fleet = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    public OneNodePerFlightTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        // The checkout's remote is GitHub's, as a peer's is told apart by it; the fetch and the plane's base come from a bare copy of its own.
        var bare = Path.Combine(_root, "origin.git");
        Git(_root, "clone", "-q", "--bare", Repo, bare);
        Git(Repo, "remote", "add", "origin", $"https://github.com/{GitHub}.git");
        Git(Repo, "config", $"url.{bare.Replace('\\', '/')}.insteadOf", $"https://github.com/{GitHub}.git");
        Git(Repo, "fetch", "-q", "origin");
        Git(Repo, "remote", "set-head", "origin", "main");
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System) { Machine = "SE1", Fleet = _fleet };
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "SE1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), _flights, options, TimeProvider.System);
    }

    private string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private Task<CommandResult> Create(params string[] args) =>
        _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = args, Who = "tests" }, Ct);

    private Task<CommandResult> Leg(string verb, string target) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Who = "tests" }, Ct);

    // What this node's own code for an issue is, as its create would give it.
    private string CodeFor(int issue) => FlightCodes.Next(FlightCodes.PrefixFor("tianwen", _state.Flights()), issue, _state.Flights())!;

    private static FlightRow PeerFlight(string code, int issue, string branch, FlightState state = FlightState.Flying, string repoPath = "D:/src/tianwen") => new()
    {
        Code = code, Repo = "tianwen", RepoPath = repoPath, Issue = issue, Branch = branch, Worktree = $"{repoPath}/.claude/worktrees/{code.ToLowerInvariant()}",
        Base = "origin/main", State = state, CreatedAt = DateTimeOffset.UtcNow,
    };

    private static FakePeer Peer(string name, params FlightRow[] flights) => new(name)
    {
        Flights = flights,
        Repos = [new RepoInfo { Name = "tianwen", Path = "D:/src/tianwen", GitHub = GitHub }],
    };

    // A flight of this node's own, made without any peer, then one a peer comes to fly too.
    private async Task<FlightRow> OwnFlight(int issue)
    {
        (await Create(issue.ToString())).Ok.ShouldBeTrue();
        return _state.Flights().Single(f => f.Issue == issue);
    }

    [Fact]
    public async Task ACreateOfAnIssueAPeerFliesIsRefusedNamingThePeerAndItsFlight()
    {
        _fleet.Peers.Add(Peer("DE1", PeerFlight("PIL294", 294, "flight/pil294")));

        var result = await Create("294");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("PIL294 flies #294 on DE1: fly it there");
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnywayDoesNotOverrideAPeersLiveFlight()
    {
        _fleet.Peers.Add(Peer("DE1", PeerFlight("PIL294", 294, "flight/pil294", FlightState.Held)));

        var result = await Create("294", "--anyway");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("PIL294 flies #294 on DE1: fly it there");
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task ACreateWhoseBranchAPeerFliesIsRefusedWhateverIssueItFlies()
    {
        // The peer's flight carries this node's code for #5, so its branch is the one a flight on #5 would get here.
        var code = CodeFor(5);
        _fleet.Peers.Add(Peer("DE1", PeerFlight(code, 99, $"flight/{code.ToLowerInvariant()}")));

        var result = await Create("5");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe($"{code} flies #5 on DE1: fly it there");
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APlanOfAFlightWhoseBranchAPeerFliesIsRefusedWithARingNamingThePeer()
    {
        var flight = await OwnFlight(7);
        _fleet.Peers.Add(Peer("DE1", PeerFlight("PEER7", 99, flight.Branch)));

        var result = await Leg(Verbs.PlanFlight, Verbs.FlightTarget(flight.Code));

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("on DE1");
        result.Error!.ShouldContain("PEER7");
        var card = _state.Cards().Single(c => c.Kind == LegCommander.FlownElsewhereKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.Ring, Verbs.FlightTarget(flight.Code)));
        card.Title.ShouldContain("DE1");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AStartOfALegOnAFlightWhoseBranchAPeerFliesIsRefusedWithARingNamingThePeer()
    {
        var flight = await OwnFlight(8);
        _state.PutLeg(new LegRow { Flight = flight.Code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned });
        _fleet.Peers.Add(Peer("DE1", PeerFlight("PEER8", 99, flight.Branch)));

        var result = await Leg(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"));

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("on DE1");
        _state.Cards().Single(c => c.Kind == LegCommander.FlownElsewhereKind).Bin.ShouldBe(CardBin.Ring);
        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Planned);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUnreachablePeerDoesNotBlockACreate()
    {
        var peer = Peer("DE1", PeerFlight("PIL294", 294, "flight/pil294"));
        peer.Unreachable = true;
        _fleet.Peers.Add(peer);

        var result = await Create("294");

        result.Ok.ShouldBeTrue(result.Error);
        _state.Flights().Single().Issue.ShouldBe(294);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AUnreachablePeerDoesNotBlockAPlan()
    {
        var flight = await OwnFlight(9);
        var peer = Peer("DE1", PeerFlight("PEER9", 99, flight.Branch));
        peer.Unreachable = true;
        _fleet.Peers.Add(peer);

        var result = await Leg(Verbs.PlanFlight, Verbs.FlightTarget(flight.Code));

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.FlownElsewhereKind);
        (result.Error ?? "").ShouldNotContain("DE1");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APeersLandedFlightDoesNotBlockACreate()
    {
        _fleet.Peers.Add(Peer("DE1", PeerFlight("PIL294", 294, "flight/pil294", FlightState.Landed)));

        var result = await Create("294");

        result.Ok.ShouldBeTrue(result.Error);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APeersFlightOnAnotherRepoDoesNotBlockACreate()
    {
        var peer = Peer("DE1", PeerFlight("PIL294", 294, "flight/pil294", repoPath: "D:/src/other"));
        peer.Repos = [new RepoInfo { Name = "other", Path = "D:/src/other", GitHub = "SharpAstro/other" }];
        _fleet.Peers.Add(peer);

        var result = await Create("294");

        result.Ok.ShouldBeTrue(result.Error);
    }

    [Fact]
    public async Task APeerThatRefusesTheGrantDoesNotBlockACreate()
    {
        var peer = Peer("DE1", PeerFlight("PIL294", 294, "flight/pil294"));
        peer.Granted = false;
        _fleet.Peers.Add(peer);

        var result = await Create("294");

        result.Ok.ShouldBeTrue(result.Error);
    }

    private sealed class FakeFleet : IFleet
    {
        public List<IFleetPeer> Peers { get; } = [];

        IReadOnlyList<IFleetPeer> IFleet.Peers() => [.. Peers];
    }

    private sealed class FakePeer(string name) : IFleetPeer
    {
        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasGrant => true;

        public bool Granted { get; set; } = true;

        public bool Unreachable { get; set; }

        public FlightRow[] Flights { get; init; } = [];

        public RepoInfo[] Repos { get; set; } = [];

        private void Reach()
        {
            if (Unreachable)
            {
                throw new HttpRequestException("connection refused");
            }
        }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
                DataFolder = "/data", SocketPath = "/sock", Stream = "s",
            });
        }

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(Granted ? Repos : null);
        }

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new FleetLoad(null, Flights));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a peer is only read here, never sent to");
    }
}
