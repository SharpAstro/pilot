using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What a landing does with the follow-ups a flight recorded in other repos (PIL1340): each gets a flight with no one touching it, held on a
/// PackageWait when the landing releases a package its repo uses, made on a peer when only a peer holds the repo, and a failed create never
/// refuses the land and is retried without making a second flight. In memory: the model of git, a fake GitHub, a fake feed and a fake peer.
/// </summary>
public sealed class FollowUpLandTests : PackageWaitTestsBase
{
    private async Task<Rig> ProducerWithFollowUp(CrossRepoFollowUp followUp, string machine = "SE1")
    {
        var node = NewRig();
        node.Flights.Machine = machine;
        (await Drive(node, Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1", "Issue 1")).Ok.ShouldBeTrue();
        var producer = Flight(node, "TIA1");
        node.State.PutFlight(producer with { RepoFollowUps = [followUp] });
        return node;
    }

    private static CrossRepoFollowUp Record(string repo = "SharpAstro/sdlapp", int issue = 5, bool package = false) =>
        new() { Repo = repo, Issue = issue, Title = "Use the new thing", Package = package, At = Start };

    private static FakePeer Peer(string name, string repo, string checkout) => new(name)
    {
        Repos = [new RepoInfo { Name = repo[(repo.IndexOf('/') + 1)..], Path = checkout, GitHub = repo }],
    };

    [Fact]
    public async Task ALandedFlightsFollowUpGetsAFlightOnItsIssueNamedOnTheRowAndCommentedOnTheIssue()
    {
        var node = await ProducerWithFollowUp(Record());

        await LandProducer(node);

        var made = node.State.Flights().Single(f => f.Repo == "sdlapp");
        made.Issue.ShouldBe(5);
        made.Title.ShouldBe("Use the new thing");
        var row = Flight(node, "TIA1").RepoFollowUps.ShouldHaveSingleItem();
        (row.Flight, row.Node, row.Problem).ShouldBe((made.Code, "SE1", null));
        row.Line().ShouldContain($"flight {made.Code} on SE1");
        var comment = _github.IssueComments.ShouldHaveSingleItem();
        comment.Number.ShouldBe(5);
        comment.Body.ShouldContain(made.Code);
        // The landing released nothing the repo uses, so the flight is free to fly.
        made.State.ShouldBe(FlightState.Flying);
        made.Connections.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFollowUpOfARepoThatUsesTheReleasedPackageIsHeldOnAPackageWaitAndFliesOnceItIsOnTheFeed()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = await ProducerWithFollowUp(Record());

        await LandProducer(node);
        _github.Runs[MergeCommit] = [RunOf("success", Start.AddMinutes(1))];

        var made = node.State.Flights().Single(f => f.Repo == "sdlapp");
        made.State.ShouldBe(FlightState.Held);
        var wait = made.PackageWaits.ShouldHaveSingleItem();
        (wait.Id, wait.Line, wait.Above, wait.Producer).ShouldBe(("Drawboard.Core", "1.2", "1.2.5", "TIA1"));

        _feed.Publish("Drawboard.Core", "1.2.6");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);

        var flying = node.State.Flights().Single(f => f.Code == made.Code);
        flying.State.ShouldBe(FlightState.Flying);
        flying.PackageWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFollowUpRecordedAsNeededAsAPackageWaitsEvenWhenItsProjectsDoNotNameIt()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        _repos.Seed("plainapp");
        var node = await ProducerWithFollowUp(Record("SharpAstro/plainapp", package: true));

        await LandProducer(node);

        var made = node.State.Flights().Single(f => f.Repo == "plainapp");
        made.State.ShouldBe(FlightState.Held);
        made.PackageWaits.ShouldHaveSingleItem().Id.ShouldBe("Drawboard.Core");
    }

    [Fact]
    public async Task AFollowUpWhoseRepoOnlyAPeerHoldsIsMadeOnThatPeerThroughItsCreateFlight()
    {
        var peer = Peer("DE1", "SharpAstro/ghostapp", "D:/src/ghostapp");
        var node = await ProducerWithFollowUp(Record("SharpAstro/ghostapp", 9));
        node.Flights.Fleet = new FakeFleet(peer);
        _github.UnclonedRepos.Add("ghostapp");

        await LandProducer(node);

        var sent = peer.Sent.ShouldHaveSingleItem();
        (sent.Verb, sent.Target).ShouldBe((Verbs.CreateFlight, "repo/D:/src/ghostapp"));
        sent.Args.ShouldBe(["#9", "Use the new thing"]);
        node.State.Flights().ShouldNotContain(f => f.Repo == "ghostapp");
        var row = Flight(node, "TIA1").RepoFollowUps.ShouldHaveSingleItem();
        (row.Flight, row.Node).ShouldBe(("PIL180", "DE1"));
    }

    [Fact]
    public async Task APeerFollowUpNeededAsAPackageRaisesACardNamingThePackageInsteadOfDroppingTheWait()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var peer = Peer("DE1", "SharpAstro/ghostapp", "D:/src/ghostapp");
        var node = await ProducerWithFollowUp(Record("SharpAstro/ghostapp", 9, package: true));
        node.Flights.Fleet = new FakeFleet(peer);
        _github.UnclonedRepos.Add("ghostapp");

        await LandProducer(node);

        var card = node.State.Cards().Where(c => c.Kind == FlightCommander.FollowUpPackageKind).ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "flight/PIL180"));
        card.Title.ShouldContain("DE1");
        card.Detail.ShouldNotBeNull().ShouldContain("package");
        // The peer's flight is grounded until the package is out, as its wait cannot be set from here.
        peer.Sent.ShouldContain(c => c.Verb == Verbs.Ground && c.Target == "flight/PIL180");
        card.Detail.ShouldContain("grounded");
    }

    [Fact]
    public async Task ACreateThatFailsNeverRefusesTheLandAndIsRetriedWithoutMakingASecondFlight()
    {
        // The folder is no git checkout, so the repo counts as not cloned and no peer holds it: nothing can make the flight yet.
        Directory.CreateDirectory(Path.Combine(_root, "brokenapp"));
        var node = await ProducerWithFollowUp(Record("SharpAstro/brokenapp", 3));

        await LandProducer(node);

        Flight(node, "TIA1").State.ShouldBe(FlightState.Landed);
        node.State.Flights().ShouldNotContain(f => f.Repo == "brokenapp");
        var row = Flight(node, "TIA1").RepoFollowUps.ShouldHaveSingleItem();
        row.Flight.ShouldBeNull();
        row.Problem.ShouldNotBeNull().ShouldContain("not made here");
        row.Line().ShouldContain("last try:");

        _repos.Seed("brokenapp");
        await node.Flights.EnsureFollowUpFlightsAsync("TIA1", Ct);
        await node.Flights.EnsureFollowUpFlightsAsync("TIA1", Ct);

        var made = node.State.Flights().Single(f => f.Repo == "brokenapp");
        var done = Flight(node, "TIA1").RepoFollowUps.ShouldHaveSingleItem();
        (done.Flight, done.Problem).ShouldBe((made.Code, null));
    }

    [Fact]
    public async Task AFollowUpIssueThatAlreadyHasAFlightRecordsThatFlightAndMakesNoSecond()
    {
        var node = await ProducerWithFollowUp(Record());
        (await Drive(node, Verbs.CreateFlight, Verbs.RepoTarget(Consumer), "5", "Already flying")).Ok.ShouldBeTrue();
        var existing = node.State.Flights().Single(f => f.Repo == "sdlapp");

        await LandProducer(node);

        node.State.Flights().Count(f => f.Repo == "sdlapp").ShouldBe(1);
        Flight(node, "TIA1").RepoFollowUps.ShouldHaveSingleItem().Flight.ShouldBe(existing.Code);
        Flight(node, existing.Code).Connections.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASecondEnsureWhileAPeerCreateIsStillAnsweringStartsNoSecondRunAndMakesOneFlight()
    {
        var peer = Peer("DE1", "SharpAstro/ghostapp", "D:/src/ghostapp");
        peer.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = await ProducerWithFollowUp(Record("SharpAstro/ghostapp", 9));
        node.Flights.Fleet = new FakeFleet(peer);
        _github.UnclonedRepos.Add("ghostapp");
        // Landing with the peer's answer held back: the run for the flight is in flight, off the lock.
        var landing = LandProducer(node);
        await peer.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await node.Flights.EnsureFollowUpFlightsAsync("TIA1", Ct);

        peer.Sent.Count(c => c.Verb == Verbs.CreateFlight).ShouldBe(1);
        peer.Gate.SetResult();
        await landing;
        peer.Sent.Count(c => c.Verb == Verbs.CreateFlight).ShouldBe(1);
        Flight(node, "TIA1").RepoFollowUps.ShouldHaveSingleItem().Flight.ShouldBe("PIL180");
    }

    [Fact]
    public async Task ASkippedRunsReleasedPackagesJoinTheRunningOnesCardAndGroundText()
    {
        var peer = Peer("DE1", "SharpAstro/ghostapp", "D:/src/ghostapp");
        peer.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = await ProducerWithFollowUp(Record("SharpAstro/ghostapp", 9, package: true));
        node.Flights.Fleet = new FakeFleet(peer);
        _github.UnclonedRepos.Add("ghostapp");
        var landing = LandProducer(node);
        await peer.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The landing's run is the one running; a second carrying a release must join it, not drop it.
        await node.Flights.EnsurePeerFollowUpFlightsAsync("TIA1", [new PackageWait { Id = "Other.Pkg", Line = "3.4", Above = "3.4.1", Producer = "TIA1" }], Ct);
        peer.Gate.SetResult();
        await landing;

        var card = node.State.Cards().Where(c => c.Kind == FlightCommander.FollowUpPackageKind).ShouldHaveSingleItem();
        card.Title.ShouldContain("Other.Pkg 3.4 above 3.4.1");
        card.Detail.ShouldNotBeNull().ShouldContain("Other.Pkg 3.4 above 3.4.1");
    }

    [Fact]
    public void JoiningReleasesKeepsEachPackageOnce()
    {
        var a = new PackageWait { Id = "A", Line = "1.0", Producer = "TIA1" };
        var b = new PackageWait { Id = "B", Line = "2.0", Producer = "TIA1" };

        FlightCommander.MergeReleased([a], [a, b]).ShouldBe([a, b]);
    }

    private sealed class FakeFleet(params IFleetPeer[] peers) : IFleet
    {
        IReadOnlyList<IFleetPeer> IFleet.Peers() => peers;
    }

    private sealed class FakePeer(string name) : IFleetPeer
    {
        public string Name => name;

        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasToken => true;

        public bool HasGrant => true;

        public RepoInfo[] Repos { get; init; } = [];

        public List<CommandRequest> Sent { get; } = [];

        /// <summary>When set, a create-flight waits on it (an event, no spin) after raising <see cref="Reached"/>.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => Task.FromResult(new NodeInfo
        {
            WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1, StartedAt = Start,
            DataFolder = "/data", SocketPath = "/sock", Stream = "s", Maintenance = false, AcceptsFleetFlights = true,
        });

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => Task.FromResult<RepoInfo[]?>(Repos);

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new FleetLoad(null, [], []));

        public async Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken)
        {
            Sent.Add(command);
            if (Gate is { } gate && command.Verb == Verbs.CreateFlight)
            {
                Reached.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return new CommandResult { Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = true, Made = "PIL180", Seq = 1 };
        }
    }
}
