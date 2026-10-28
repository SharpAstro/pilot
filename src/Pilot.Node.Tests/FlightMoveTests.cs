using System.Diagnostics;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node takes over a flight another node hands it (<c>receive-flight</c>, #376): the whole record arrives, the plane is made
/// from the branch the sender pushed, the flight says where it came from, and its next leg starts by the normal advance. A node in
/// maintenance, one that has the code already, and one with no checkout of the repo refuse it, with the reason.
/// </summary>
public sealed class FlightMoveTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string GitHub = "SharpAstro/tianwen";
    private const string Code = "TIA900";
    private const string Branch = "flight/tia900";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2v-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly NodeState _sender = new(new InMemoryJournal(), TimeProvider.System);
    private readonly RoleModels _models = new(null);
    private readonly FakeWorkers _workers = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;
    private readonly string _pushedHead;

    public FlightMoveTests()
    {
        // Two machines' clones of one GitHub repo: both name github.com as origin, which this machine's git turns into the bare folder.
        SeedRepo.Make(Path.Combine(_root, "receiver"));
        var origin = Path.Combine(_root, "receiver", "origin.git");
        Git(Receiving, "config", "remote.origin.url", $"https://github.com/{GitHub}.git");
        Git(Receiving, "config", $"url.{origin}.insteadOf", $"https://github.com/{GitHub}.git");
        Directory.CreateDirectory(Path.Combine(_root, "sender"));
        Git(Path.Combine(_root, "sender"), "clone", "-q", origin, "tianwen");
        Git(Sending, "config", "user.email", "t@example.com");
        Git(Sending, "config", "user.name", "t");
        Git(Sending, "config", "remote.origin.url", $"https://github.com/{GitHub}.git");
        Git(Sending, "config", $"url.{origin}.insteadOf", $"https://github.com/{GitHub}.git");

        // The sender's plane: a branch with a leg's work on it, pushed.
        Git(Sending, "switch", "-q", "-c", Branch);
        File.WriteAllText(Path.Combine(Sending, "parser.txt"), "the parser leg's work\n");
        Git(Sending, "add", ".");
        Git(Sending, "commit", "-q", "-m", "parser");
        Git(Sending, "push", "-q", "origin", Branch);
        _pushedHead = Git(Sending, "rev-parse", "HEAD");

        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System) { Models = _models, Machine = "DE1" };
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DE1", RepoRootsGiven: [Path.Combine(_root, "receiver")]);
        _flights.Repos = new RepoCatalog(_state, options);
        _legs = new LegCommander(_state, _workers, new FakeChecks(), new FakeGitHub(), git, new Planes(git), _flights, options, TimeProvider.System) { Models = new RoleModels(null) };
        _flights.Advance = _legs.AdvanceFlightAsync;
        Fleet(true);
    }

    private string Receiving => Path.Combine(_root, "receiver", "tianwen");

    private string Sending => Path.Combine(_root, "sender", "tianwen");

    private string Plane => Path.Combine(Receiving, ".claude", "worktrees", "tia900");

    public void Dispose()
    {
        try
        {
            Git(Receiving, "worktree", "prune");
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

    private void Fleet(bool willing) =>
        _models.Apply(NodeSettings.With(new NodeSettings(), NodeSettings.AcceptFleetFlightsKey, [willing ? "on" : "off"]).Settings!);

    private void Maintenance(bool on) =>
        _models.Apply(NodeSettings.With(NodeSettings.With(new NodeSettings(), NodeSettings.AcceptFleetFlightsKey, ["on"]).Settings!, NodeSettings.MaintenanceKey, [on ? "on" : "off"]).Settings!);

    /// <summary>The flight as SE1 holds it: a done leg with its report and a go-around, a planned one after it, a PR, a review and cards, one of them open.</summary>
    private FlightMove Sent()
    {
        var at = DateTimeOffset.UtcNow.AddHours(-3);
        _sender.PutFlight(new FlightRow
        {
            Code = Code, Repo = "tianwen", RepoPath = Sending, Issue = 900, Title = "Planets", Branch = Branch, Worktree = Path.Combine(Sending, ".claude", "worktrees", "tia900"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = at, PullRequest = 41, PullRequestUrl = $"https://github.com/{GitHub}/pull/41",
            Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "se1-tia900-review", Report = "the parser misses a case", StartedAt = at, FinishedAt = at.AddMinutes(5), GoAround = 1 },
        });
        _sender.PutLeg(new LegRow
        {
            Flight = Code, Name = "parser", Order = 1, Goal = "Parse the ephemeris", DoneWhen = "Tests pass", State = LegState.Done, GoArounds = 1, ReviewGoArounds = 1,
            Report = "parser in, after a go-around", Worker = "se1-tia900-parser", StartedAt = at, FinishedAt = at.AddHours(1),
            Checklist = [new CheckItem { Text = "Write it", Done = true }],
            Artifacts = [new Artifact { Title = "capture", Location = "C:/x/capture.png", At = at }],
        });
        _sender.PutLeg(new LegRow
        {
            Flight = Code, Name = "render", Order = 2, Goal = "Draw the planets", DoneWhen = "Shown", State = LegState.Planned, Checklist = [new CheckItem { Text = "Draw them" }],
        });
        _sender.RaiseCard(CardBin.Ring, "pr-ready", Verbs.FlightTarget(Code), "TIA900's PR is ready", "review asked for changes",
            [new CardChoice { Label = "Redo leg", Verb = Verbs.GoAround, Target = Verbs.LegTarget(Code, "parser") }], $"https://github.com/{GitHub}/pull/41");
        _sender.RaiseCard(CardBin.Handled, "folded", Verbs.FlightTarget(Code), "something handled on SE1");
        _sender.RaiseCard(CardBin.ForYou, "pr-ready", Verbs.FlightTarget("TIA1"), "another flight's card");
        return FlightMoves.Gather(_sender, Code, "SE1");
    }

    private static CommandRequest Under(FlightMove move, string grant = "SE1") => move.Command("SE1/node") with { Grant = grant };

    private Task<CommandResult> Receive(FlightMove move, string grant = "SE1") => _flights.RunAsync(Under(move, grant), Ct);

    private void NothingTaken()
    {
        _state.Flights().ShouldBeEmpty();
        _state.Legs().ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
        Directory.Exists(Plane).ShouldBeFalse();
    }

    [Fact]
    public async Task AHandedFlightArrivesWithItsLegsReportsPrReviewAndOpenCardsOnAPlaneAtThePushedHead()
    {
        var move = Sent();
        var peer = new HandingPeer(_flights);

        var result = await peer.SendAsync(move, "SE1/node", Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe(Code);
        result.Way.ShouldBe("TIA900 moved from SE1 after leg parser");

        var flight = _state.Flights().ShouldHaveSingleItem();
        (flight.Code, flight.State, flight.Title, flight.Issue, flight.Branch, flight.Base).ShouldBe((Code, FlightState.Flying, "Planets", 900, Branch, "origin/main"));
        (flight.PullRequest, flight.PullRequestUrl).ShouldBe((41, $"https://github.com/{GitHub}/pull/41"));
        var review = flight.Review.ShouldNotBeNull();
        (review.State, review.Reviewer, review.Report, review.GoAround).ShouldBe((ReviewState.Changes, "se1-tia900-review", "the parser misses a case", 1));

        // The paths are this machine's own, not SE1's.
        (flight.Repo, Path.GetFullPath(flight.RepoPath)).ShouldBe(("tianwen", Path.GetFullPath(Receiving)));
        Path.GetFullPath(flight.Worktree).ShouldBe(Path.GetFullPath(Plane));
        Git(Plane, "rev-parse", "HEAD").ShouldBe(_pushedHead);
        Git(Plane, "rev-parse", "--abbrev-ref", "HEAD").ShouldBe(Branch);
        File.ReadAllText(Path.Combine(Plane, "parser.txt")).Trim().ShouldBe("the parser leg's work");

        // Where it came from, as a client says it.
        var from = flight.MovedFrom.ShouldNotBeNull();
        (from.From, from.AfterLeg).ShouldBe(("SE1", "parser"));
        from.Text.ShouldBe("moved from SE1 after leg parser");

        var legs = _state.Legs(Code);
        legs.Select(l => (l.Name, l.Order, l.State)).ShouldBe([("parser", 1, LegState.Done), ("render", 2, LegState.Planned)]);
        var parser = legs[0];
        (parser.Report, parser.GoArounds, parser.ReviewGoArounds, parser.Goal, parser.DoneWhen).ShouldBe(("parser in, after a go-around", 1, 1, "Parse the ephemeris", "Tests pass"));
        parser.Checklist.Select(c => (c.Text, c.Done)).ShouldBe([("Write it", true)]);
        parser.Artifacts.Select(a => a.Title).ShouldBe(["capture"]);

        // Only the open card about the flight comes over, raised again here; a Handled card and another flight's stay where they were.
        var cards = _state.Cards();
        var open = cards.Where(c => c.Kind == "pr-ready").ShouldHaveSingleItem();
        (open.Bin, open.State, open.Subject, open.Title, open.Detail, open.Link).ShouldBe((CardBin.Ring, CardState.Open, "flight/TIA900", "TIA900's PR is ready", "review asked for changes", $"https://github.com/{GitHub}/pull/41"));
        var choice = open.Choices.ShouldHaveSingleItem();
        (choice.Label, choice.Verb, choice.Target).ShouldBe(("Redo leg", Verbs.GoAround, "flight/TIA900/parser"));
        var received = cards.Where(c => c.Kind == FlightCommander.ReceivedKind).ShouldHaveSingleItem();
        (received.Bin, received.Title).ShouldBe((CardBin.Handled, "TIA900 moved from SE1 after leg parser"));
        cards.ShouldNotContain(c => c.Kind == "folded" || c.Subject == "flight/TIA1");
    }

    [Fact]
    public async Task TheNextPlannedLegStartsOnTheReceivedPlaneByTheNormalAdvance()
    {
        var result = await Receive(Sent());

        result.Ok.ShouldBeTrue(result.Error);
        // A leg after a done one is read against the tree first, as on any node: its validator starts in the received plane, and its go starts the worker.
        var validator = _workers.Started.ShouldHaveSingleItem();
        (validator.Name, Path.GetFullPath(validator.RepoPath), validator.Plane).ShouldBe(("de1-tia900-render-validator", Path.GetFullPath(Receiving), "tia900"));
        _state.Legs(Code).Single(l => l.Name == "render").Validation.ShouldBe(LegValidation.Pending);

        (await _legs.RunAsync(new CommandRequest { Verb = Verbs.ValidateLeg, Target = Verbs.LegTarget(Code, "render"), Args = ["go", "the tree has the parser"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        var started = _workers.Started.Last();
        (started.Name, Path.GetFullPath(started.RepoPath), started.Plane).ShouldBe(("de1-tia900-render", Path.GetFullPath(Receiving), "tia900"));
        var render = _state.Legs(Code).Single(l => l.Name == "render");
        (render.State, render.Worker).ShouldBe((LegState.Flying, "de1-tia900-render"));
        // The leg done on SE1 is not flown again.
        _state.Legs(Code).Single(l => l.Name == "parser").State.ShouldBe(LegState.Done);
    }

    [Fact]
    public async Task AFlightWithNoLegLeftToStartGoesOnToItsPrAsAnyOtherDoes()
    {
        var move = Sent();
        move = move with { Legs = [.. move.Legs.Where(l => l.Name == "parser")], Flight = move.Flight with { PullRequest = null, PullRequestUrl = null, Review = null } };

        (await Receive(move)).Ok.ShouldBeTrue();

        // Every leg is done, so the advance owes it its PR and then its review, which starts: never a leg.
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("de1-tia900-review");
        _state.Flights().Single().PullRequest.ShouldNotBeNull();
    }

    [Fact]
    public void TheMoveCrossesTheWireAsJsonWithItsProvenance()
    {
        var flight = Sent().Flight with { MovedFrom = new FlightMoved { From = "SE1", AfterLeg = "parser", At = DateTimeOffset.UnixEpoch } };

        var json = JsonSerializer.Serialize(flight, PilotJsonContext.Default.FlightRow);
        var back = JsonSerializer.Deserialize(json, PilotJsonContext.Default.FlightRow).ShouldNotBeNull();

        json.ShouldContain("\"movedFrom\"");
        back.MovedFrom.ShouldBe(flight.MovedFrom);
        // A flight journaled before moves existed reads back with none.
        JsonSerializer.Deserialize(JsonSerializer.Serialize(flight with { MovedFrom = null }, PilotJsonContext.Default.FlightRow), PilotJsonContext.Default.FlightRow)!.MovedFrom.ShouldBeNull();
        new FlightMoved { From = "SE1", At = DateTimeOffset.UnixEpoch }.Text.ShouldBe("moved from SE1");

        var command = Under(FlightMove.Read(Sent().Command("x").Args[0]).ShouldNotBeNull());
        (command.Verb, command.Target).ShouldBe((Verbs.ReceiveFlight, "flight/TIA900"));
        FlightMove.Read("not json").ShouldBeNull();
    }

    [Fact]
    public async Task ANodeInMaintenanceRefusesWithTheReasonAndTakesNothing()
    {
        Maintenance(true);

        var result = await Receive(Sent());

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("DE1 is in maintenance: it takes no new flights");
        NothingTaken();
    }

    [Fact]
    public async Task ACodeTheNodeHasAlreadyIsRefusedAndTheFlightHereIsLeftAsItIs()
    {
        var here = new FlightRow
        {
            Code = Code, Repo = "tianwen", RepoPath = Receiving, Issue = 900, Branch = "flight/tia900-own", Worktree = "/elsewhere", Base = "origin/main",
            State = FlightState.Landed, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        _state.PutFlight(here);

        var result = await Receive(Sent());

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("TIA900 is already here (landed on DE1): a code is one flight on a node for good");
        _state.Flights().ShouldHaveSingleItem().ShouldBe(here);
        _state.Legs().ShouldBeEmpty();
        Directory.Exists(Plane).ShouldBeFalse();
    }

    [Fact]
    public async Task ARepoTheNodeHasNoCheckoutOfIsRefusedNamingIt()
    {
        var move = Sent() with { GitHub = "SharpAstro/other" };

        var result = await Receive(move);

        result.Error.ShouldBe("DE1 has no checkout of SharpAstro/other");
        NothingTaken();

        // Nor can it tell the repo when the sender's has no GitHub remote.
        (await Receive(move with { GitHub = null })).Error.ShouldBe("TIA900's repo has no GitHub remote on SE1, so DE1 cannot tell which of its checkouts is it");
        NothingTaken();
    }

    [Fact]
    public async Task ABranchThatWasNotPushedIsRefusedAndNoPlaneIsLeftBehind()
    {
        var move = Sent();
        move = move with { Flight = move.Flight with { Branch = "flight/tia900-unpushed" } };

        var result = await Receive(move);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldStartWith("flight/tia900-unpushed is not on origin of ");
        NothingTaken();
    }

    [Fact]
    public async Task ANodeThatDoesNotTakeFleetFlightsALegStillFlyingAndACommandNotUnderAGrantAreRefused()
    {
        var move = Sent();

        Fleet(false);
        (await Receive(move)).Error.ShouldBe("DE1 does not take a flight another node hands it (accept-fleet-flights is off)");
        Fleet(true);

        (await Receive(move with { Legs = [.. move.Legs.Select(l => l.Name == "render" ? l with { State = LegState.Flying } : l)] })).Error
            .ShouldBe("TIA900's leg render is still flying on SE1: it is handed over once the leg has finished");

        (await _flights.RunAsync(move.Command("tests"), Ct)).Error
            .ShouldBe("receive-flight is taken from another node of the fleet, under its grant, not from a client of DE1");

        (await Receive(move with { Flight = move.Flight with { State = FlightState.Held } })).Error.ShouldBe("TIA900 is held: only a flying flight is handed over");

        var wrongTarget = Under(move) with { Target = "flight/TIA1" };
        (await _flights.RunAsync(wrongTarget, Ct)).Error.ShouldBe("flight/TIA1 is not the flight in the record, TIA900");

        (await _flights.RunAsync(Under(move) with { Args = ["{"] }, Ct)).Error.ShouldStartWith("receive-flight takes the flight's whole record as JSON");
        NothingTaken();
    }

    /// <summary>A peer whose receive-flight is this node's: what the sender's node reaches over the LAN, under its grant.</summary>
    private sealed class HandingPeer(FlightCommander receiver) : IFleetPeer
    {
        public string Endpoint => "de1.local:47731";

        public bool HasGrant => true;

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) =>
            receiver.RunAsync(command with { Grant = "SE1" }, cancellationToken);
    }
}
