using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A create-flight at a node in maintenance goes to a willing node of the fleet (#177): one that takes fleet flights, is not in
/// maintenance, is reached with a grant, and has a checkout of the repo's GitHub remote; the one with the most 5-hour headroom
/// then the fewest flights in the air. With none, it is refused, each node named with why it did not qualify.
/// </summary>
public sealed class FleetRoutingTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string GitHub = "SharpAstro/tianwen";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2f-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly RoleModels _models = new(null);
    private readonly FakeFleet _fleet = new();
    private readonly FlightCommander _flights;

    public FleetRoutingTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        Git(Repo, "remote", "add", "origin", $"https://github.com/{GitHub}.git");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        _flights = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System) { Models = _models, Machine = "SE1", Fleet = _fleet };
        Maintenance(true);
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

    private void Maintenance(bool on) =>
        _models.Apply(NodeSettings.With(new NodeSettings(), NodeSettings.MaintenanceKey, [on ? "on" : "off"]).Settings!);

    private Task<CommandResult> Create(params string[] args) =>
        _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = args, Who = "tests" }, Ct);

    private static FakePeer Willing(string name, double used = 10, int flying = 0, string checkout = "/work/tianwen") => new(name)
    {
        AcceptsFleetFlights = true,
        Usage = new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = used, ResetsAt = DateTimeOffset.UtcNow.AddHours(2) } },
        Flights = [.. Enumerable.Range(0, flying).Select(i => Flight($"{name}{i}"))],
        Repos = [new RepoInfo { Name = "tianwen", Path = checkout, GitHub = GitHub }],
    };

    private static FakePeer NoCheckout(string name)
    {
        var peer = Willing(name);
        peer.Repos = [new RepoInfo { Name = "other", Path = "/work/other", GitHub = "SharpAstro/other" }];
        return peer;
    }

    private static FlightRow Flight(string code) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/work/tianwen", Issue = 1, Branch = $"flight/{code}", Worktree = $"/work/{code}", Base = "origin/main",
        State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ARoutedFlightIsMadeOnTheWillingPeerWithItsOwnCheckoutAndSaysWhere()
    {
        var peer = Willing("DE1", checkout: "D:/src/tianwen");
        _fleet.Peers.Add(peer);

        var result = await Create("1611", "Parser", "fix");

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("PIL180");
        result.Way.ShouldBe("PIL180 is flying on DE1: SE1 is in maintenance");
        var sent = peer.Sent.ShouldHaveSingleItem();
        (sent.Verb, sent.Target, sent.Who).ShouldBe((Verbs.CreateFlight, "repo/D:/src/tianwen", "tests"));
        sent.Args.ShouldBe(["#1611", "Parser", "fix"]);
        _state.Flights().ShouldBeEmpty();
        var card = _state.Cards().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Title).ShouldBe((CardBin.Handled, FlightCommander.RoutedKind, "PIL180 is flying on DE1: SE1 is in maintenance"));
    }

    [Fact]
    public async Task ARoutedBareNumberIsSentAsTheItemThisNodeResolvedItToSoThePeersSettingsCannotReadItDifferently()
    {
        var peer = Willing("DE1");
        _fleet.Peers.Add(peer);
        var withTracker = NodeSettings.With(new NodeSettings(), NodeSettings.MaintenanceKey, ["on"]).Settings!;
        _models.Apply(NodeSettings.With(withTracker, "repo-trackers", ["tianwen=shortcut"]).Settings!);

        (await Create("101645", "Story")).Ok.ShouldBeTrue();
        peer.Sent.ShouldHaveSingleItem().Args.ShouldBe(["sc-101645", "Story"]);

        peer.Sent.Clear();
        _models.Apply(NodeSettings.With(withTracker, "repo-trackers", ["tianwen=github"]).Settings!);
        (await Create("42")).Ok.ShouldBeTrue();
        peer.Sent.ShouldHaveSingleItem().Args.ShouldBe(["#42"]);
    }

    [Fact]
    public async Task TheMostHeadroomWinsThenTheFewestFlightsInTheAir()
    {
        var busy = Willing("AA1", used: 20, flying: 3);
        var idle = Willing("BB1", used: 20, flying: 1);
        var roomy = Willing("CC1", used: 5, flying: 9);
        var tight = Willing("DD1", used: 90, flying: 0);
        _fleet.Peers.AddRange([busy, idle, tight, roomy]);

        (await Create("7")).Way.ShouldBe("PIL180 is flying on CC1: SE1 is in maintenance");
        roomy.Sent.Count.ShouldBe(1);

        _fleet.Peers.Remove(roomy);
        (await Create("7")).Way.ShouldBe("PIL180 is flying on BB1: SE1 is in maintenance");

        _fleet.Peers.Remove(idle);
        (await Create("7")).Way.ShouldBe("PIL180 is flying on AA1: SE1 is in maintenance");

        _fleet.Peers.Remove(busy);
        (await Create("7")).Way.ShouldBe("PIL180 is flying on DD1: SE1 is in maintenance");
    }

    [Fact]
    public async Task WithNoWillingNodeItIsRefusedNamingEachNodeAndWhy()
    {
        var inMaintenance = Willing("AA1");
        inMaintenance.Maintenance = true;
        var unwilling = Willing("BB1");
        unwilling.AcceptsFleetFlights = false;
        var unreachable = Willing("CC1");
        unreachable.Unreachable = true;
        var ungranted = new FakePeer("DD1") { Granted = false };
        var noToken = new FakePeer("EE1") { HasToken = false };
        var noCheckout = NoCheckout("FF1");
        _fleet.Peers.AddRange([inMaintenance, unwilling, unreachable, ungranted, noToken, noCheckout]);

        var result = await Create("1611");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("SE1 is in maintenance: it takes no new flights, and no node of the fleet takes it either: "
            + $"AA1: in maintenance; BB1: not willing (accept-fleet-flights is off); {unreachable.Endpoint}: unreachable; DD1: no grant; "
            + $"{noToken.Endpoint}: no grant; FF1: no checkout of {GitHub}");
        foreach (var peer in _fleet.Peers.Cast<FakePeer>())
        {
            peer.Sent.ShouldBeEmpty();
        }
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusalNamesEveryNodeWithItsOwnReason()
    {
        var reasons = new (FakePeer Peer, string Reason)[]
        {
            (new FakePeer("AA1") { Maintenance = true, AcceptsFleetFlights = true }, "AA1: in maintenance"),
            (new FakePeer("BB1"), "BB1: not willing (accept-fleet-flights is off)"),
            (new FakePeer("CC1") { Unreachable = true }, "cc1.local:47731: unreachable"),
            (new FakePeer("DD1") { Granted = false }, "DD1: no grant"),
            (new FakePeer("EE1") { HasToken = false }, "ee1.local:47731: no grant"),
            (NoCheckout("FF1"), $"FF1: no checkout of {GitHub}"),
        };
        _fleet.Peers.AddRange(reasons.Select(r => r.Peer));

        var error = (await Create("1611")).Error.ShouldNotBeNull();

        foreach (var (_, reason) in reasons)
        {
            error.ShouldContain(reason);
        }
    }

    [Fact]
    public async Task APeerThatRefusesTheFlightLeavesItToTheNextOne()
    {
        var first = Willing("AA1", used: 1);
        first.Refuse = "no room in its plane folder";
        var second = Willing("BB1", used: 50);
        _fleet.Peers.AddRange([first, second]);

        (await Create("9")).Way.ShouldBe("PIL180 is flying on BB1: SE1 is in maintenance");
        first.Sent.Count.ShouldBe(1);

        _fleet.Peers.Remove(second);
        var refused = (await Create("9")).Error.ShouldNotBeNull();
        refused.ShouldContain("AA1: refused it (no room in its plane folder)");
    }

    [Fact]
    public async Task ANodeWithNoPeerStillRefusesWithTheMaintenanceReasonAlone()
    {
        (await Create("1611")).Error.ShouldBe("SE1 is in maintenance: it takes no new flights");
    }

    [Fact]
    public async Task ANodeNotInMaintenanceNeverRoutes()
    {
        Maintenance(false);
        // A plane is made from origin's main: without the GitHub remote the repo has no network to wait on.
        Git(Repo, "remote", "remove", "origin");
        var peer = Willing("DE1");
        _fleet.Peers.Add(peer);

        var result = await Create("1611", "Parser");

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBeNull();
        result.Made.ShouldBe("TIA1611");
        peer.Sent.ShouldBeEmpty();
        _state.Flights().ShouldHaveSingleItem().Code.ShouldBe("TIA1611");
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public void AWindowThatHasResetIsAllHeadroomAndNoFiguresIsNone()
    {
        var now = DateTimeOffset.UtcNow;
        UsageRow Row(double used, DateTimeOffset resets) => new() { ReadAt = now, FiveHour = new UsageWindow { UsedPercent = used, ResetsAt = resets } };

        FleetChoice.HeadroomOf(Row(30, now.AddHours(1)), now).ShouldBe(70);
        FleetChoice.HeadroomOf(Row(95, now.AddMinutes(-1)), now).ShouldBe(100);
        FleetChoice.HeadroomOf(null, now).ShouldBeNull();
        FleetChoice.HeadroomOf(new UsageRow { ReadAt = now }, now).ShouldBeNull();
    }

    [Fact]
    public void APeerWithNoFiguresRanksAfterOneWithSome()
    {
        var verdicts = FleetChoice.Judge(
        [
            new FleetPeerFacts("AA1", true, true, false, true, "/a", null, 0),
            new FleetPeerFacts("BB1", true, true, false, true, "/b", 3, 5),
        ], GitHub);

        verdicts.Select(v => v.Name).ShouldBe(["BB1", "AA1"]);
        verdicts.ShouldAllBe(v => v.Reason == null);
    }

    private sealed class FakeFleet : IFleet
    {
        public List<IFleetPeer> Peers { get; } = [];

        IReadOnlyList<IFleetPeer> IFleet.Peers() => [.. Peers];
    }

    private sealed class FakePeer(string name) : IFleetPeer
    {
        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasToken { get; init; } = true;

        public bool HasGrant => HasToken;

        public bool Granted { get; init; } = true;

        public bool Unreachable { get; set; }

        public bool Maintenance { get; set; }

        public bool AcceptsFleetFlights { get; set; }

        public UsageRow? Usage { get; init; }

        public FlightRow[] Flights { get; init; } = [];

        public PrReviewRow[] PrReviews { get; init; } = [];

        public RepoInfo[] Repos { get; set; } = [];

        public string? Refuse { get; set; }

        public List<CommandRequest> Sent { get; } = [];

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
                DataFolder = "/data", SocketPath = "/sock", Stream = "s", Maintenance = Maintenance, AcceptsFleetFlights = AcceptsFleetFlights,
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
            return Task.FromResult(new FleetLoad(Usage, Flights, PrReviews));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken)
        {
            Reach();
            Sent.Add(command);
            return Task.FromResult(new CommandResult
            {
                Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = Refuse is null, Error = Refuse, Made = Refuse is null ? "PIL180" : null, Seq = 1,
            });
        }
    }
}
