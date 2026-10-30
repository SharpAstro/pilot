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
/// maintenance, one that has the code already, and one with no checkout of the repo refuse it, with the reason. And the sending side
/// (<c>move-flight</c>): a node in maintenance moves its flight once its leg has finished, the plane made safe first, and on any failure
/// keeps the flight, says why on the row, and offers a Retry move; the flight is flown on one node only, at every moment.
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

    // The sending node, SE1: its own state, settings, workers and git (which a test makes refuse a push), reaching DE1 through a peer.
    private readonly RoleModels _senderModels = new(null);
    private readonly FakeWorkers _senderWorkers = new();
    private readonly RefusingGit _senderGit = new(new GitCli());
    private readonly FlightCommander _senderFlights;
    private readonly LegCommander _senderLegs;
    private readonly MovePeer _peer;

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

        var senderOptions = new NodeOptions(Path.Combine(_root, "sender.sock"), Path.Combine(_root, "sender-data"), MachinePrefix: "SE1", RepoRootsGiven: [Path.Combine(_root, "sender")]);
        _peer = new MovePeer(_flights) { Checkout = Receiving, State = _state };
        _senderFlights = new FlightCommander(_sender, new Planes(git), TimeProvider.System) { Models = _senderModels, Machine = "SE1", Fleet = new MoveFleet(_peer) };
        _senderLegs = new LegCommander(_sender, _senderWorkers, new FakeChecks(), new FakeGitHub(), _senderGit, new Planes(git), _senderFlights, senderOptions, TimeProvider.System) { Models = _senderModels };
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

    // The flight's advance runs after the receive-flight is answered; a test looks at what it did once it has finished.
    private async Task<CommandResult> Receive(FlightMove move, string grant = "SE1")
    {
        var result = await _flights.RunAsync(Under(move, grant), Ct);
        await _flights.WhenAdvancedAsync();
        return result;
    }

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
        result.Error.ShouldBe("TIA900 is already here (landed on DE1): a code is one flight on a node for good, unless the flight moved away from here and comes back");
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

        (await Receive(move with { Flight = move.Flight with { State = FlightState.Landed } })).Error.ShouldBe("TIA900 is landed: only a flight in the air is handed over");

        var wrongTarget = Under(move) with { Target = "flight/TIA1" };
        (await _flights.RunAsync(wrongTarget, Ct)).Error.ShouldBe("flight/TIA1 is not the flight in the record, TIA900");

        (await _flights.RunAsync(Under(move) with { Args = ["{"] }, Ct)).Error.ShouldStartWith("receive-flight takes the flight's whole record as JSON");
        NothingTaken();
    }

    private string SenderPlane => Path.Combine(Sending, ".claude", "worktrees", "tia900");

    private void SenderMaintenance(bool on) =>
        _senderModels.Apply(NodeSettings.With(new NodeSettings(), NodeSettings.MaintenanceKey, [on ? "on" : "off"]).Settings!);

    /// <summary>
    /// The flight on SE1, whose plane is a worktree of its clone on the pushed branch: the parser leg as given, a render leg planned after it,
    /// and a card about the flight. With <paramref name="unpushedCommit"/> the plane holds a commit of its own that origin lacks.
    /// </summary>
    private void Fly(LegState parser, bool unpushedCommit = false)
    {
        Git(Sending, "switch", "-q", "main");
        Git(Sending, "worktree", "add", SenderPlane, Branch);
        if (unpushedCommit)
        {
            File.WriteAllText(Path.Combine(SenderPlane, "render-prep.txt"), "prepared on SE1\n");
            Git(SenderPlane, "add", ".");
            Git(SenderPlane, "commit", "-q", "-m", "prepare render");
        }
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        _sender.PutFlight(new FlightRow
        {
            Code = Code, Repo = "tianwen", RepoPath = Sending, Issue = 900, Title = "Planets", Branch = Branch, Worktree = SenderPlane,
            Base = "origin/main", State = FlightState.Flying, CreatedAt = at,
        });
        _sender.PutLeg(new LegRow
        {
            Flight = Code, Name = "parser", Order = 1, Goal = "Parse the ephemeris", DoneWhen = "Tests pass", State = parser, Report = parser == LegState.Done ? "parser in" : null,
            Worker = "se1-tia900-parser", WorkerJob = parser == LegState.Flying ? "job-parser" : null, StartedAt = at, FinishedAt = parser == LegState.Done ? at.AddMinutes(30) : null,
        });
        _sender.PutLeg(new LegRow { Flight = Code, Name = "render", Order = 2, Goal = "Draw the planets", DoneWhen = "Shown", State = LegState.Planned });
        _sender.RaiseCard(CardBin.Ring, "next-step", Verbs.FlightTarget(Code), "TIA900 waits to start render");
    }

    private FlightRow OnSender => _sender.Flights().Single(f => f.Code == Code);

    private string OriginHead() => Git(Receiving, "ls-remote", "origin", $"refs/heads/{Branch}").Split('\t')[0];

    /// <summary>The flight stayed on SE1: flying there, no leg started, nothing taken by DE1, and the reason on its row and on a Retry card.</summary>
    private CardRow StayedWith(string failure)
    {
        var flight = OnSender;
        (flight.State, flight.MovedTo).ShouldBe((FlightState.Flying, null));
        flight.MoveFailure.ShouldBe(failure);
        _sender.Legs(Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Planned);
        _senderWorkers.Started.ShouldBeEmpty();
        NothingTaken();
        var card = _sender.Cards(openOnly: true).Single(c => c.Kind == LegCommander.MoveFailedKind);
        (card.Bin, card.Subject, card.Title).ShouldBe((CardBin.Ring, "flight/TIA900", failure[..failure.IndexOf(": ", StringComparison.Ordinal)]));
        var retry = card.Choices.ShouldHaveSingleItem();
        (retry.Label, retry.Verb, retry.Target).ShouldBe(("Retry move", Verbs.MoveFlight, "flight/TIA900"));
        return card;
    }

    private Task<CommandResult> Retry(CardRow card) =>
        _senderLegs.RunAsync(new CommandRequest { Verb = card.Choices[0].Verb, Target = card.Choices[0].Target, Args = card.Choices[0].Args, Who = "tests" }, Ct);

    /// <summary>The flight is DE1's now: landed here saying where it went, flying there, and only there.</summary>
    private void MovedAway()
    {
        var flight = OnSender;
        (flight.State, flight.MoveFailure).ShouldBe((FlightState.Landed, null));
        flight.MovedTo.ShouldNotBeNull().Text.ShouldBe("moved to DE1 after leg parser");
        _state.Flights().ShouldHaveSingleItem().State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task TheLegInFlightFinishesFirstThenTheFlightMovesAndTheNextLegStartsOnlyOnTheReceiver()
    {
        Fly(LegState.Flying, unpushedCommit: true);
        SenderMaintenance(true);
        var planeHead = Git(SenderPlane, "rev-parse", "HEAD");

        // The leg is in flight: the sweep leaves it, and the flight, alone. Nothing is pushed or sent.
        await _senderLegs.AdvanceAllAsync(Ct);
        OnSender.State.ShouldBe(FlightState.Flying);
        _peer.Sent.ShouldBeEmpty();
        _senderWorkers.Started.ShouldBeEmpty();
        OriginHead().ShouldBe(_pushedHead);

        // Its leg finishes, and the flight goes out at once, in the same breath.
        var finished = await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = Verbs.LegTarget(Code, "parser"), Args = ["done", "parser in"], Who = "tests" }, Ct);

        finished.Ok.ShouldBeTrue(finished.Error);
        // Made safe first: the plane's own commit is on origin, read back equal to the plane's head.
        OriginHead().ShouldBe(planeHead);
        _peer.Sent.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.ReceiveFlight);
        MovedAway();
        _sender.Legs(Code).Select(l => (l.Name, l.State)).ShouldBe([("parser", LegState.Done), ("render", LegState.Planned)]);
        // The sender no longer flies it: no leg started here, the parser's worker was stopped, its plane is gone, its card closed and a line names DE1.
        _senderWorkers.Started.ShouldBeEmpty();
        _senderWorkers.Stopped.ShouldBe(["job-parser"]);
        Directory.Exists(SenderPlane).ShouldBeFalse();
        _sender.Cards(openOnly: true).ShouldBeEmpty();
        var line = _sender.Cards().Single(c => c.Kind == LegCommander.MovedAwayKind);
        (line.Bin, line.Title).ShouldBe((CardBin.Handled, "TIA900 moved to DE1 after leg parser"));
        line.Detail.ShouldNotBeNull().ShouldContain("SE1 is in maintenance");

        // The next leg starts only on the receiver, from the plane as pushed.
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("de1-tia900-render-validator");
        _state.Legs(Code).Single(l => l.Name == "render").Validation.ShouldBe(LegValidation.Pending);
        Git(Plane, "rev-parse", "HEAD").ShouldBe(planeHead);
        File.ReadAllText(Path.Combine(Plane, "render-prep.txt")).Trim().ShouldBe("prepared on SE1");
        _state.Flights().Single().MovedFrom.ShouldNotBeNull().Text.ShouldBe("moved from SE1 after leg parser");

        // Another sweep on SE1 starts and sends nothing: the flight is the receiver's alone.
        await _senderLegs.AdvanceAllAsync(Ct);
        _senderWorkers.Started.ShouldBeEmpty();
        _peer.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AtNoPointDoBothNodesFlyTheFlight()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        var observed = new List<string>();
        string? refusedStart = null;

        // Before DE1 has the flight, SE1 has it and DE1 has not; a leg asked to start on SE1 meanwhile is refused.
        _peer.BeforeReceive = async () =>
        {
            observed.Add($"before: sender {OnSender.State}, receiver {_state.Flights().Length} flights, sender starts {_senderWorkers.Started.Count}");
            refusedStart = (await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget(Code, "render"), Who = "tests" }, Ct)).Error;
        };
        // After DE1 has taken it, and before SE1 has written that down, SE1 still starts nothing.
        _peer.AfterReceive = () =>
            observed.Add($"after: sender {OnSender.State}, receiver {_state.Flights().Single().State}, sender starts {_senderWorkers.Started.Count}, sender legs flying {_sender.Legs(Code).Count(l => l.State == LegState.Flying)}");

        await _senderLegs.AdvanceAllAsync(Ct);

        observed.ShouldBe(["before: sender Flying, receiver 0 flights, sender starts 0", "after: sender Flying, receiver Flying, sender starts 0, sender legs flying 0"]);
        refusedStart.ShouldNotBeNull().ShouldContain("the flight is being moved to another node");
        MovedAway();
        _senderWorkers.Started.ShouldBeEmpty();
        // Moved, the flight on SE1 is no flying flight any sweep or leg verb takes up again.
        await _senderLegs.AdvanceAllAsync(Ct);
        (await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget(Code, "render"), Who = "tests" }, Ct)).Error.ShouldNotBeNull().ShouldContain("landed");
        _senderWorkers.Started.ShouldBeEmpty();
        _peer.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task APushBehindTheRemoteLeavesTheFlightHereWithTheReasonAndARetryThatRunsTheMoveAgain()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        // Someone pushed to the branch from elsewhere: origin holds a commit the plane lacks.
        Git(_root, "clone", "-q", Path.Combine(_root, "receiver", "origin.git"), "elsewhere");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Git(elsewhere, "config", "user.email", "t@example.com");
        Git(elsewhere, "config", "user.name", "t");
        Git(elsewhere, "switch", "-q", Branch);
        File.WriteAllText(Path.Combine(elsewhere, "fix.txt"), "a fix from the controller\n");
        Git(elsewhere, "add", ".");
        Git(elsewhere, "commit", "-q", "-m", "fix");
        Git(elsewhere, "push", "-q", "origin", Branch);
        var remoteHead = OriginHead();

        await _senderLegs.AdvanceAllAsync(Ct);

        var card = StayedWith("TIA900 could not move to DE1: the push was refused, the remote holds commits the plane lacks. Merge them in, or move it again.");
        // Never forced: origin holds what it did.
        OriginHead().ShouldBe(remoteHead);
        _peer.Sent.ShouldBeEmpty();
        // The sweep leaves it be: a failed move is moved again by a person's Retry, not retried every minute.
        await _senderLegs.AdvanceAllAsync(Ct);
        _senderWorkers.Started.ShouldBeEmpty();
        _sender.Cards(openOnly: true).Count(c => c.Kind == LegCommander.MoveFailedKind).ShouldBe(1);

        // Merged in, the Retry runs the move again, and it goes.
        Git(SenderPlane, "fetch", "-q", "origin");
        Git(SenderPlane, "merge", "--ff-only", "-q", $"origin/{Branch}");
        var retried = await Retry(card);

        retried.Ok.ShouldBeTrue(retried.Error);
        retried.Made.ShouldBe("TIA900 moved to DE1 after leg parser");
        MovedAway();
        Git(Plane, "rev-parse", "HEAD").ShouldBe(remoteHead);
        _sender.Cards(openOnly: true).ShouldBeEmpty();
    }

    [Fact]
    public async Task APushTheRemoteRefusesLeavesTheFlightHereWithTheReason()
    {
        Fly(LegState.Done, unpushedCommit: true);
        SenderMaintenance(true);
        _senderGit.RefusePush = "! [remote rejected] flight/tia900 -> flight/tia900 (protected branch hook declined)";

        await _senderLegs.AdvanceAllAsync(Ct);

        var card = StayedWith("TIA900 could not move to DE1: the push was refused (! [remote rejected] flight/tia900 -> flight/tia900 (protected branch hook declined)). Look at flight/tia900 on origin, then move it again.");
        OriginHead().ShouldBe(_pushedHead);
        _peer.Sent.ShouldBeEmpty();

        _senderGit.RefusePush = null;
        (await Retry(card)).Ok.ShouldBeTrue();
        MovedAway();
    }

    [Fact]
    public async Task ADirtyPlaneIsNotMovedAndTheFlightStaysHereWithTheReason()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        File.WriteAllText(Path.Combine(SenderPlane, "stray.txt"), "not committed\n");

        await _senderLegs.AdvanceAllAsync(Ct);

        var card = StayedWith("TIA900 could not move to DE1: the plane holds 1 change not committed (?? stray.txt). Commit or drop it, then move it again.");
        _peer.Sent.ShouldBeEmpty();
        OriginHead().ShouldBe(_pushedHead);

        File.Delete(Path.Combine(SenderPlane, "stray.txt"));
        (await Retry(card)).Ok.ShouldBeTrue();
        MovedAway();
    }

    [Fact]
    public async Task WhenNoNodeTakesItTheFlightStaysHereAndNamesEachNodeAndWhy()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        _peer.Maintenance = true;

        await _senderLegs.AdvanceAllAsync(Ct);

        var card = StayedWith("TIA900 could not move: no node of the fleet takes it (DE1: in maintenance). Put one out of maintenance and willing (accept-fleet-flights on), or move it again later.");
        _peer.Sent.ShouldBeEmpty();

        _peer.Maintenance = false;
        (await Retry(card)).Ok.ShouldBeTrue();
        MovedAway();
    }

    [Fact]
    public async Task ANodeThatKnowsNoPeerKeepsItsFlightAndSaysSo()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        _senderFlights.Fleet = null;

        await _senderLegs.AdvanceAllAsync(Ct);

        StayedWith("TIA900 could not move: this node knows no other node of the fleet. Grant it one, or fly the flight on here.");
    }

    [Fact]
    public async Task WhenTheReceiverRefusesTheFlightStaysHereAndNoPlaneIsLeftOnTheReceiver()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        Fleet(false);

        await _senderLegs.AdvanceAllAsync(Ct);

        var card = StayedWith("TIA900 could not move to DE1: DE1 refused it (DE1 does not take a flight another node hands it (accept-fleet-flights is off)). Put that right there, or move it again.");
        _peer.Sent.ShouldHaveSingleItem();
        // The branch was pushed, harmlessly; the sender's plane is as it was.
        Directory.Exists(SenderPlane).ShouldBeTrue();

        Fleet(true);
        (await Retry(card)).Ok.ShouldBeTrue();
        MovedAway();
    }

    [Fact]
    public async Task AFlightWithALegFlyingIsRefusedAHandMoveAndNothingIsPushed()
    {
        Fly(LegState.Flying);

        var refused = await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.MoveFlight, Target = "flight/TIA900", Who = "tests" }, Ct);

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldBe("TIA900/parser is flying: a flight moves once its leg has finished");
        OnSender.MoveFailure.ShouldBeNull();
        _sender.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.MoveFailedKind);
        _peer.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task AHandMoveOutsideMaintenanceMovesTheFlightAsWell()
    {
        Fly(LegState.Done);

        var moved = await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.MoveFlight, Target = "flight/TIA900", Who = "tests" }, Ct);

        moved.Ok.ShouldBeTrue(moved.Error);
        MovedAway();
        _sender.Cards().Single(c => c.Kind == LegCommander.MovedAwayKind).Detail.ShouldNotBeNull().ShouldNotContain("maintenance");
    }

    [Fact]
    public async Task AHeldFlightMovesByHandAndWaitsThereForTheFlightItWaitedForHere()
    {
        Fly(LegState.Planned);
        _sender.PutFlight(new FlightRow
        {
            Code = "TIA901", Repo = "tianwen", RepoPath = Sending, Issue = 901, Branch = "flight/tia901", Worktree = "/nowhere", Base = "origin/main", State = FlightState.Flying,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        _sender.PutFlight(OnSender with { State = FlightState.Held, Connections = ["TIA901"] });

        var moved = await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.MoveFlight, Target = "flight/TIA900", Who = "tests" }, Ct);

        moved.Ok.ShouldBeTrue(moved.Error);
        (OnSender.State, OnSender.MovedTo.ShouldNotBeNull().To).ShouldBe((FlightState.Landed, "DE1"));
        // TIA901 is SE1's, so on DE1 the flight waits for it on SE1, and nothing of it starts meanwhile.
        var there = _state.Flights().ShouldHaveSingleItem();
        (there.State, there.Connections.Length).ShouldBe((FlightState.Held, 0));
        var wait = there.PeerConnections.ShouldHaveSingleItem();
        (wait.Code, wait.Node).ShouldBe(("TIA901", "SE1"));
        _workers.Started.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeldFlightWaitingForAFlightOfTheReceiverWaitsForItThereAndFliesOnceItHasLanded(bool landed)
    {
        _state.PutFlight(new FlightRow
        {
            Code = "TIA433", Repo = "tianwen", RepoPath = Receiving, Issue = 433, Branch = "flight/tia433", Worktree = "/nowhere", Base = "origin/main",
            State = landed ? FlightState.Landed : FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        var move = Sent();
        move = move with { Flight = move.Flight with { State = FlightState.Held, PeerConnections = [new PeerConnection { Code = "TIA433", Node = "DE1", Since = DateTimeOffset.UnixEpoch }] } };

        (await Receive(move)).Ok.ShouldBeTrue();

        var flight = _state.Flights().Single(f => f.Code == Code);
        flight.PeerConnections.ShouldBeEmpty();
        flight.Connections.ShouldBe(["TIA433"]);
        flight.State.ShouldBe(landed ? FlightState.Flying : FlightState.Held);
    }

    [Fact]
    public async Task ANodeOutOfMaintenanceKeepsItsFlightsAndFliesThemOn()
    {
        Fly(LegState.Done);
        SenderMaintenance(false);

        await _senderLegs.AdvanceAllAsync(Ct);

        _peer.Sent.ShouldBeEmpty();
        OnSender.State.ShouldBe(FlightState.Flying);
        // Its next leg is read against the tree first, as on any node: the flight carries on here.
        _senderWorkers.Started.ShouldHaveSingleItem().Name.ShouldBe("se1-tia900-render-validator");
    }

    [Fact]
    public async Task AFlightThatWaitsForAMovedOneKeepsWaiting()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        _sender.PutFlight(new FlightRow
        {
            Code = "TIA901", Repo = "tianwen", RepoPath = Sending, Issue = 901, Branch = "flight/tia901", Worktree = "/nowhere", Base = "origin/main", State = FlightState.Held,
            Connections = [Code], CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await _senderLegs.AdvanceAllAsync(Ct);

        OnSender.MovedTo.ShouldNotBeNull().To.ShouldBe("DE1");
        // TIA900 has left this node but has not landed: what waits for it is not let go.
        (await _senderFlights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA901", Args = [Code], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _sender.Flights().Single(f => f.Code == "TIA901").State.ShouldBe(FlightState.Held);
    }

    private async Task SweepSenderAsync() => await _senderLegs.AdvanceAllAsync(Ct);

    /// <summary>The flight is flown by nobody on SE1 (no leg, review or PR starts) while its move is unknown, and DE1 flies it if it holds it.</summary>
    private async Task SenderStartsNothingAsync()
    {
        await SweepSenderAsync();
        _senderWorkers.Started.ShouldBeEmpty();
        (await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget(Code, "render"), Who = "tests" }, Ct)).Error
            .ShouldNotBeNull().ShouldContain("it is not known yet whether DE1 took the flight");
        _senderWorkers.Started.ShouldBeEmpty();
        OnSender.State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task AReplyLostAfterTheReceiverTookTheFlightIsSettledByAskingItAndOnlyTheReceiverFliesIt()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        _peer.Reply = Lost.AfterTaking;

        await SweepSenderAsync();

        // The answer never came, but DE1 says it holds the flight, so it moved: SE1 lets it go and flies nothing.
        MovedAway();
        _senderWorkers.Started.ShouldBeEmpty();
        Directory.Exists(SenderPlane).ShouldBeFalse();
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("de1-tia900-render-validator");
        await SweepSenderAsync();
        _senderWorkers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReplyLostWhileTheReceiverCannotBeAskedKeepsTheFlightOnNoNodeOfTheSenderUntilItIsKnown()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        _peer.Reply = Lost.AfterTaking;
        // DE1 took the flight, the reply was lost, and DE1 cannot be asked either.
        _peer.AfterReceive = () => _peer.Reachable = false;

        await SweepSenderAsync();

        var unknown = OnSender.MoveUnknown.ShouldNotBeNull();
        (unknown.To, unknown.Endpoint).ShouldBe(("DE1", "de1.local:47731"));
        OnSender.MoveFailure.ShouldNotBeNull().ShouldStartWith("TIA900 may have moved to DE1: its answer did not come");
        var card = _sender.Cards(openOnly: true).Single(c => c.Kind == LegCommander.MoveFailedKind);
        card.Choices.ShouldHaveSingleItem().Label.ShouldBe("Retry move");
        Directory.Exists(SenderPlane).ShouldBeTrue();
        // One node flies it: DE1, which holds it; SE1 starts nothing, by sweep or by hand, and is not moved again until it is known.
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("de1-tia900-render-validator");
        await SenderStartsNothingAsync();
        (await Retry(card)).Error.ShouldNotBeNull().ShouldContain("not known yet whether DE1 took it");
        _peer.Sent.Count.ShouldBe(1);

        // DE1 answers again: it holds the flight, so it moved, and SE1 lets go.
        _peer.Reachable = true;
        await SweepSenderAsync();
        MovedAway();
        OnSender.MoveUnknown.ShouldBeNull();
        Directory.Exists(SenderPlane).ShouldBeFalse();
        _senderWorkers.Started.ShouldBeEmpty();
        _peer.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AStillRunningReplyIsNotAMoveUntilTheReceiverHoldsTheFlight()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        // Past the reply deadline DE1 answers Ok, "still running": the flight is not yet DE1's, and may never be.
        _peer.Reply = Lost.StillRunningUntaken;

        await SweepSenderAsync();

        OnSender.MoveUnknown.ShouldNotBeNull().To.ShouldBe("DE1");
        OnSender.MoveFailure.ShouldNotBeNull().ShouldContain("it was still taking it when its reply deadline passed");
        OnSender.MovedTo.ShouldBeNull();
        _state.Flights().ShouldBeEmpty();
        await SenderStartsNothingAsync();

        // The receive finishes late, on DE1: the next look finds it there, and the flight is DE1's alone.
        _peer.Reply = Lost.None;
        (await _flights.RunAsync(_peer.Sent[0] with { Grant = "SE1" }, Ct)).Ok.ShouldBeTrue();
        await _flights.WhenAdvancedAsync();
        await SweepSenderAsync();
        MovedAway();
        _senderWorkers.Started.ShouldBeEmpty();
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("de1-tia900-render-validator");
    }

    [Fact]
    public async Task AFlightTheReceiverDoesNotHoldAfterTheSettlingTimeStaysHereAsAFailedMoveAndRetriesWithoutFlyingTwice()
    {
        Fly(LegState.Done);
        SenderMaintenance(true);
        _peer.Reply = Lost.BeforeTaking;

        await SweepSenderAsync();

        OnSender.MoveUnknown.ShouldNotBeNull();
        await SenderStartsNothingAsync();
        _state.Flights().ShouldBeEmpty();

        // Past the settling time, with DE1 answering that it does not hold the flight, the move is a failed one: the flight stays, held in maintenance.
        _senderLegs.MoveSettle = TimeSpan.Zero;
        await SweepSenderAsync();
        OnSender.MoveUnknown.ShouldBeNull();
        var card = StayedWith("TIA900 did not move to DE1: DE1 does not hold it 0 minutes after it was sent, so the flight stays here. Move it again.");
        _senderWorkers.Started.ShouldBeEmpty();

        // The receive had in fact arrived late: the retry is answered taken by the same hand-over's idempotence, and the flight is DE1's only.
        _peer.Reply = Lost.None;
        (await _flights.RunAsync(_peer.Sent[0] with { Grant = "SE1" }, Ct)).Ok.ShouldBeTrue();
        await _flights.WhenAdvancedAsync();
        var retried = await Retry(card);
        retried.Ok.ShouldBeTrue(retried.Error);
        MovedAway();
        _state.Flights().ShouldHaveSingleItem();
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("de1-tia900-render-validator");
        _senderWorkers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheSameHandOverAgainIsAnsweredTakenAndDoesNothingTwice()
    {
        var move = Sent() with { Head = _pushedHead };
        (await Receive(move)).Ok.ShouldBeTrue();
        var flight = _state.Flights().Single();
        var cards = _state.Cards().Length;
        var started = _workers.Started.Count;

        var again = await Receive(move);

        again.Ok.ShouldBeTrue(again.Error);
        (again.Made, again.Way).ShouldBe((Code, "TIA900 moved from SE1 after leg parser"));
        _state.Flights().ShouldHaveSingleItem().ShouldBe(flight);
        _state.Cards().Length.ShouldBe(cards);
        _workers.Started.Count.ShouldBe(started);

        // Not another node's hand-over, nor one of a head this plane lacks.
        (await Receive(move with { From = "XX1" })).Error.ShouldNotBeNull().ShouldStartWith("TIA900 is already here (flying on DE1)");
        (await Receive(move with { Head = new string('a', 40) })).Error.ShouldNotBeNull().ShouldStartWith("TIA900 is already here (flying on DE1)");
    }

    [Fact]
    public async Task AFlightThatMovedAwayComesBackAndReplacesItsMovedAwayRow()
    {
        var move = Sent() with { Head = _pushedHead };
        (await Receive(move)).Ok.ShouldBeTrue();
        // DE1 hands it on to SE1: its plane is gone, its branch is kept, its row says where it went.
        Git(Receiving, "worktree", "remove", "--force", Plane);
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed, MovedTo = new FlightMovedTo { To = "SE1", AfterLeg = "parser", At = DateTimeOffset.UtcNow } });
        // SE1 flew on and pushed.
        Git(Sending, "switch", "-q", Branch);
        File.WriteAllText(Path.Combine(Sending, "more.txt"), "flown on SE1\n");
        Git(Sending, "add", ".");
        Git(Sending, "commit", "-q", "-m", "more");
        Git(Sending, "push", "-q", "origin", Branch);
        var head = Git(Sending, "rev-parse", "HEAD");

        var back = await Receive(move with { Head = head });

        back.Ok.ShouldBeTrue(back.Error);
        var flight = _state.Flights().ShouldHaveSingleItem();
        (flight.State, flight.MovedTo).ShouldBe((FlightState.Flying, null));
        flight.MovedFrom.ShouldNotBeNull().From.ShouldBe("SE1");
        Git(Plane, "rev-parse", "HEAD").ShouldBe(head);
        File.ReadAllText(Path.Combine(Plane, "more.txt")).Trim().ShouldBe("flown on SE1");
        _state.Legs(Code).Select(l => l.Name).ShouldBe(["parser", "render"]);
    }

    [Fact]
    public async Task TheReceiveIsAnsweredBeforeTheFlightsAdvanceIsDone()
    {
        var gate = new TaskCompletionSource();
        _flights.Advance = async (_, _) => await gate.Task;

        var result = await _flights.RunAsync(Under(Sent()), Ct);

        // Taken and answered while the advance (a validator starting) has not yet run.
        result.Ok.ShouldBeTrue(result.Error);
        result.Waiting.ShouldBeNull();
        _workers.Started.ShouldBeEmpty();
        _state.Flights().ShouldHaveSingleItem();

        gate.SetResult();
        await _flights.WhenAdvancedAsync();
    }

    [Fact]
    public async Task AnAdvanceThatThrowsAfterTheReceiveIsACardNotAFailedReceive()
    {
        _flights.Advance = (_, _) => throw new InvalidOperationException("the launcher is down");

        var result = await Receive(Sent());

        result.Ok.ShouldBeTrue(result.Error);
        _state.Flights().ShouldHaveSingleItem();
        _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.NextStepKind).Detail.ShouldBe("the launcher is down");
    }

    /// <summary>Git for the sending node, which a test makes refuse a push, as a remote that rejects it.</summary>
    private sealed class RefusingGit(IGit inner) : IGit
    {
        public string? RefusePush { get; set; }

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) =>
            RefusePush is { } refused && args.Count > 0 && args[0] == "push" ? Task.FromResult(new GitRun(1, "", refused)) : inner.RunAsync(args, cwd, cancellationToken);
    }

    private sealed class MoveFleet(params IFleetPeer[] peers) : IFleet
    {
        public IReadOnlyList<IFleetPeer> Peers() => peers;
    }

    /// <summary>DE1 as SE1 reaches it: its node, its repos and load as it says them, and its receive-flight as this test node's.</summary>
    private sealed class MovePeer(FlightCommander receiver) : IFleetPeer
    {
        public string Endpoint => "de1.local:47731";

        public bool HasGrant => true;

        public bool Maintenance { get; set; }

        public string? Checkout { get; init; }

        public Func<Task>? BeforeReceive { get; set; }

        public Action? AfterReceive { get; set; }

        public List<CommandRequest> Sent { get; } = [];

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => Task.FromResult(new NodeInfo
        {
            WireVersion = NodeWire.Version, Version = "0", MachineName = "DE1", MachinePrefix = "DE1", ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
            DataFolder = "/data", SocketPath = "/sock", Stream = "s", Maintenance = Maintenance, AcceptsFleetFlights = true,
        });

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RepoInfo[]?>([new RepoInfo { Name = "tianwen", Path = Checkout!, GitHub = GitHub }]);

        /// <summary>The flights DE1 says it has: its node's state, as the stream's snapshot carries them.</summary>
        public NodeState? State { get; init; }

        /// <summary>How the answer to the receive-flight goes wrong, when it does: the flight is taken or not, and the sender hears nothing, or only "still running".</summary>
        public Lost Reply { get; set; }

        /// <summary>Whether the peer can be asked what it holds.</summary>
        public bool Reachable { get; set; } = true;

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) =>
            Reachable ? Task.FromResult(new FleetLoad(null, State?.Flights() ?? [], null)) : throw new TimeoutException("de1.local:47731 did not answer within 8 s");

        public async Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken)
        {
            Sent.Add(command);
            if (BeforeReceive is { } before)
            {
                await before();
            }
            if (Reply == Lost.BeforeTaking)
            {
                throw new TimeoutException("de1.local:47731 did not answer within 100 s");
            }
            if (Reply == Lost.StillRunningUntaken)
            {
                return new CommandResult
                {
                    Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = true, Seq = 0,
                    Waiting = "still running after 90 s; its outcome follows on the stream, check pilot status",
                };
            }
            var result = await receiver.RunAsync(command with { Grant = "SE1" }, cancellationToken);
            // Over the wire the receiver goes on after it has answered; what it did then is what a test looks at.
            await receiver.WhenAdvancedAsync();
            AfterReceive?.Invoke();
            return Reply switch
            {
                Lost.AfterTaking => throw new TimeoutException("de1.local:47731 did not answer within 100 s"),
                Lost.StillRunning => result with { Waiting = "still running after 90 s; its outcome follows on the stream, check pilot status" },
                _ => result,
            };
        }
    }

    // The reply of a receive-flight that goes wrong: never taken and no answer; taken and no answer; taken and answered "still running"; and answered "still running" without being taken.
    private enum Lost { None, BeforeTaking, AfterTaking, StillRunning, StillRunningUntaken }

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
