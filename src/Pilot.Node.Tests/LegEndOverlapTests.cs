using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The overlap check at a leg's end (#564): a finished leg whose plane changed a file another flight in the air changes, and that the planner
/// did not name to <c>touches</c>, is connected by the node itself (a Handled card, the next leg held until the other lands), and rings the
/// overlap card only when the connect would cycle or the other has not moved for 2 h; one that stayed to its planned files does neither.
/// </summary>
/// <remarks>Slow: every test builds a bare origin, pushes to it and flies a plane in a real worktree (2 to 7 s each, run alone).</remarks>
[Trait("Category", "Slow")]
public sealed class LegEndOverlapTests : LegCommanderTestsBase
{
    private string Origin => Path.Combine(_root, "origin.git");

    public LegEndOverlapTests()
    {
        _legs.Models = new RoleModels(null);
    }

    /// <summary>Lines that make a file hot on a side (OverlapChoice.HotLines is 100).</summary>
    private const string Hot = "*120";

    /// <summary>A file spec: a path, or <c>path*N</c> to write N lines (default 1).</summary>
    private static (string File, int Lines) Spec(string spec) =>
        spec.Split('*') is [var file, var n] ? (file, int.Parse(n)) : (spec, 1);

    /// <summary>Flight TIA1 with its leg "parser" flying and one commit in the plane changing <paramref name="files"/>; a bare origin to fetch from.</summary>
    private async Task<FlightRow> FlyingAsync(params string[] files)
    {
        Git(_root, "init", "-q", "--bare", "-b", "main", "origin.git");
        Git(Repo, "remote", "add", "origin", Origin);
        // The planes are worktrees of this repo and share its config: the node's own merges need an identity, which CI runners lack.
        Git(Repo, "config", "user.email", "t@example.com");
        Git(Repo, "config", "user.name", "t");
        Git(Repo, "push", "-q", "origin", "main");
        await FlightWithTwoLegs();
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        var flight = _state.Flights().Single();
        foreach (var spec in files)
        {
            var (file, lines) = Spec(spec);
            var path = Path.Combine(flight.Worktree, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Concat(Enumerable.Repeat("class X {}\n", lines)));
            Git(flight.Worktree, "add", file);
        }
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "The parser");
        return _state.Flights().Single();
    }

    /// <summary>Another flight of this node whose pushed branch changes <paramref name="files"/>.</summary>
    private void OtherFlight(string code, params string[] files)
    {
        var branch = $"flight/{code.ToLowerInvariant()}";
        var tia1 = _state.Flights().Single(f => f.Code == "TIA1");
        var plane = Path.Combine(_root, "other-" + code);
        Git(Repo, "worktree", "add", "-q", "-b", branch, plane, "main");
        foreach (var spec in files)
        {
            var (file, lines) = Spec(spec);
            var path = Path.Combine(plane, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Concat(Enumerable.Repeat(code + "\n", lines)));
            Git(plane, "add", file);
        }
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", code);
        Git(plane, "push", "-q", "origin", branch);
        _state.PutFlight(tia1 with { Code = code, Issue = 2, Branch = branch, Worktree = plane, Overlaps = [], PlannedFiles = null });
    }

    private async Task FinishParserAsync()
    {
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();
        L("parser").State.ShouldBe(LegState.Done);
    }

    [Fact]
    public async Task TouchesRecordsTheFilesItWasGivenOnTheFlightAndKeepsTheUnionOfAskedFiles()
    {
        await FlyingAsync();

        (await Leg(Verbs.Touches, "flight/TIA1", "src\\Parser.cs", "./src/Lexer.cs")).Ok.ShouldBeTrue();
        _state.Flights().Single().PlannedFiles.ShouldBe(["src/Parser.cs", "src/Lexer.cs"]);

        (await Leg(Verbs.Touches, "flight/TIA1", "src/Parser.cs", "src/Other.cs")).Ok.ShouldBeTrue();
        _state.Flights().Single(f => f.Code == "TIA1").PlannedFiles.ShouldBe(["src/Parser.cs", "src/Lexer.cs", "src/Other.cs"]);
    }

    [Fact]
    public async Task AnUnforeseenOverlapWithALocalFlightIsConnectedByTheNodeWithAHandledNoteNoRingAndTheNextLegHeld()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot, "elsewhere.cs");
        // The planner named only the parser; shared.cs was not foreseen.
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        // No ring: the node connected the flight itself and said so on a Handled card.
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapConnectedKind);
        note.Bin.ShouldBe(CardBin.Handled);
        note.Subject.ShouldBe(Verbs.FlightTarget("TIA1"));
        note.Title.ShouldContain("TIA1");
        note.Title.ShouldContain("TIA2");
        note.Title.ShouldContain("shared.cs");
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.Connections.ShouldBe(["TIA2"]);
        var row = flight.Overlaps.Single();
        (row.Other, row.Answer, row.AfterLeg).ShouldBe(("TIA2", OverlapAnswer.Connect, "parser"));
        row.Files.ShouldBe(["shared.cs"]);
        row.Why.ShouldContain("the node connected it");
        // The next leg is held until TIA2 lands: no validator, no worker, no planner.
        flight.State.ShouldBe(FlightState.Held);
        L("parser").State.ShouldBe(LegState.Done);
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render") || n.Contains("plan"));
    }

    [Fact]
    public async Task APeerThatLandsBetweenTheCheckAndTheConnectIsNotToldAsAWaitAndItsRowSaysNothingIsWaitedFor()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        const string gitHub = "SharpAstro/tianwen";
        Git(Repo, "config", $"url.{Origin.Replace('\\', '/')}.insteadOf", $"https://github.com/{gitHub}.git");
        Git(Repo, "remote", "set-url", "origin", $"https://github.com/{gitHub}.git");
        var theirs = _state.Flights().Single() with
        {
            Code = "PIL9", Issue = 9, Branch = "flight/pil9", RepoPath = "D:/src/tianwen", Worktree = "D:/src/tianwen/.claude/worktrees/pil9",
            Overlaps = [], PlannedFiles = null, Connections = [], PeerConnections = [], LastMovedAt = _clock.GetUtcNow().AddMinutes(-5),
        };
        var peer = new FakePeer("DE1")
        {
            Flights = [theirs],
            Repos = [new RepoInfo { Name = "tianwen", Path = "D:/src/tianwen", GitHub = gitHub }],
            Changes = new Dictionary<string, string> { ["PIL9"] = "120\tshared.cs" },
        };
        var fleet = new FakeFleet();
        fleet.Peers.Add(peer);
        _flights.Fleet = fleet;
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        peer.AfterChangesRead = () => peer.Flights = [theirs with { State = FlightState.Landed }];

        await FinishParserAsync();

        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.PeerConnections.ShouldBeEmpty();
        var row = flight.Overlaps.Single();
        row.Answer.ShouldBe(OverlapAnswer.Connect);
        row.AnsweredByNode.ShouldBe(OverlapRow.AnsweredByNodeLanded);
        row.Why.ShouldContain("nothing to wait for");
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapConnectedKind);
        note.Detail.ShouldContain("nothing to wait for");
        note.Detail.ShouldNotContain("waits until");
        flight.State.ShouldNotBe(FlightState.Held);
    }

    [Fact]
    public async Task AnUnforeseenOverlapWithAPeerFlightIsConnectedByTheNodeToo()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        // The peer's checkout is told apart by its GitHub remote; the fetch still comes from the bare origin.
        const string gitHub = "SharpAstro/tianwen";
        Git(Repo, "config", $"url.{Origin.Replace('\\', '/')}.insteadOf", $"https://github.com/{gitHub}.git");
        Git(Repo, "remote", "set-url", "origin", $"https://github.com/{gitHub}.git");
        var theirs = _state.Flights().Single() with
        {
            Code = "PIL9", Issue = 9, Branch = "flight/pil9", RepoPath = "D:/src/tianwen", Worktree = "D:/src/tianwen/.claude/worktrees/pil9",
            Overlaps = [], PlannedFiles = null, Connections = [], PeerConnections = [], LastMovedAt = _clock.GetUtcNow().AddMinutes(-5),
        };
        var peer = new FakePeer("DE1")
        {
            Flights = [theirs],
            Repos = [new RepoInfo { Name = "tianwen", Path = "D:/src/tianwen", GitHub = gitHub }],
            Changes = new Dictionary<string, string> { ["PIL9"] = $"120\tshared.cs\n3\telsewhere.cs" },
        };
        var fleet = new FakeFleet();
        fleet.Peers.Add(peer);
        _flights.Fleet = fleet;
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapConnectedKind);
        note.Bin.ShouldBe(CardBin.Handled);
        note.Title.ShouldContain("PIL9");
        note.Title.ShouldContain("DE1");
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.PeerConnections.Select(c => (c.Code, c.Node)).ShouldBe([("PIL9", "DE1")]);
        var row = flight.Overlaps.Single();
        (row.Other, row.Node, row.Answer, row.AfterLeg).ShouldBe(("PIL9", "DE1", OverlapAnswer.Connect, "parser"));
        flight.State.ShouldBe(FlightState.Held);
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));
    }

    [Fact]
    public async Task ADeadlockingConnectIsNotMadeByTheNodeButRingsWithTheReasonAndBothChoices()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        // TIA2 already waits for TIA1: connecting TIA1 to it would leave neither flying.
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA2") with { Connections = ["TIA1"] });
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.OverlapKind);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldContain("did not connect it itself");
        card.Detail.ShouldContain("already waits for TIA1");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.ConnectOverlap, Verbs.Separate]);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapConnectedKind);
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.Connections.ShouldBeEmpty();
        (flight.Overlaps.Single().Other, flight.Overlaps.Single().Answer).ShouldBe(("TIA2", OverlapAnswer.Pending));
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));
    }

    [Fact]
    public async Task AnOtherThatHasNotMovedForTwoHoursIsNotConnectedToByTheNodeButRingsWithHowLongItIsStuckAndAParallelChoice()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA2") with { LastMovedAt = _clock.GetUtcNow().AddHours(-3) });
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.OverlapKind);
        card.Detail.ShouldNotBeNull().ShouldContain("TIA2 has not moved for 3 h");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.Separate, Verbs.ConnectOverlap]);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapConnectedKind);
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.Connections.ShouldBeEmpty();
        flight.Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Pending);
        L("render").State.ShouldBe(LegState.Planned);
    }

    [Fact]
    public async Task AnOtherThatMovedLessThanTwoHoursAgoIsConnectedTo()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA2") with { LastMovedAt = _clock.GetUtcNow().AddHours(-1).AddMinutes(-59) });
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Flights().Single(f => f.Code == "TIA1").Connections.ShouldBe(["TIA2"]);
    }

    [Fact]
    public async Task ConnectOnALegEndCardConnectsTheFlightHoldsTheNextLegAndStartsNoPlanner()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        // TIA2 has stood still, so the node rings instead of connecting.
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA2") with { LastMovedAt = _clock.GetUtcNow().AddHours(-5) });
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await FinishParserAsync();
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegCommander.OverlapKind);
        var started = _workers.Started.Count;

        (await Leg(Verbs.ConnectOverlap, "flight/TIA1", "TIA2")).Ok.ShouldBeTrue();

        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.Connections.ShouldBe(["TIA2"]);
        flight.State.ShouldBe(FlightState.Held);
        flight.Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Connect);
        L("parser").State.ShouldBe(LegState.Done);
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Count.ShouldBe(started);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("planner") || n.Contains("render"));
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
    }

    [Fact]
    public async Task ParallelOnALegEndCardStartsNoPlannerLetsTheNextLegStartAndIsNotAskedAgainForANewSharedFile()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot, "fresh.cs");
        // TIA2 has stood still, so the node rings instead of connecting.
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA2") with { LastMovedAt = _clock.GetUtcNow().AddHours(-5) });
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await FinishParserAsync();

        (await Leg(Verbs.Separate, "flight/TIA1", "TIA2")).Ok.ShouldBeTrue();

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("planner"));
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");

        // A later leg end sharing only the answered file rings nothing; a new shared file rings nothing either (PIL589).
        await _legs.CheckOverlapAsync("TIA1", "render", Ct);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        File.WriteAllText(Path.Combine(flight.Worktree, "fresh.cs"), "class Y {}\n");
        Git(flight.Worktree, "add", "fresh.cs");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fresh");
        await _legs.CheckOverlapAsync("TIA1", "render", Ct);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var row = _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single();
        row.Answer.ShouldBe(OverlapAnswer.Separate);
        row.Files.ShouldBe(["fresh.cs", "shared.cs"]);
        row.AfterLeg.ShouldBe("render");
        _state.Flights().Single(f => f.Code == "TIA1").State.ShouldNotBe(FlightState.Held);
    }

    /// <summary>TIA2 holds an answered row about TIA1 (the card was TIA2's), and TIA1 has none about TIA2.</summary>
    private void AnswerOnTheOthersCard(OverlapAnswer answer, params string[] files)
    {
        var tia2 = _state.Flights().Single(f => f.Code == "TIA2");
        _state.PutFlight(tia2 with { Overlaps = [new OverlapRow { Other = "TIA1", Files = files, Pick = Verbs.OverlapPickConnect, Why = "asked", Answer = answer, At = DateTimeOffset.UtcNow }] });
    }

    [Theory]
    [InlineData(OverlapAnswer.Separate)]
    [InlineData(OverlapAnswer.Connect)]
    public async Task AnAnswerOnTheOthersCardCoversThePairSoNoCardRingsAndTheFilesAreRecordedHere(OverlapAnswer answer)
    {
        await FlyingAsync("parser.cs", "shared.cs", "fresh.cs");
        OtherFlight("TIA2", "shared.cs", "fresh.cs");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        AnswerOnTheOthersCard(answer, "shared.cs");

        await _legs.CheckOverlapAsync("TIA1", "parser", Ct);

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.State.ShouldNotBe(FlightState.Held);
        var row = flight.Overlaps.Single();
        (row.Other, row.Answer, row.AfterLeg).ShouldBe(("TIA2", OverlapAnswer.Separate, "parser"));
        row.Files.ShouldBe(["fresh.cs"]);
    }

    /// <summary>TIA2 holds a pending row about TIA1 (its own overlap card is open), from a leg end (<paramref name="afterLeg"/>) or a planner's ask (null).</summary>
    private void PendingOnTheOthersCard(string? afterLeg, params string[] files)
    {
        var tia2 = _state.Flights().Single(f => f.Code == "TIA2");
        _state.PutFlight(tia2 with { Overlaps = [new OverlapRow { Other = "TIA1", Files = files, Pick = Verbs.OverlapPickConnect, Why = "asked", Answer = OverlapAnswer.Pending, AfterLeg = afterLeg, At = DateTimeOffset.UtcNow }] });
        _state.RaiseCard(CardBin.Ring, LegCommander.OverlapKind, Verbs.FlightTarget("TIA2"), "TIA2 overlaps TIA1 (shared.cs)", "asked", []);
    }

    [Fact]
    public async Task AMutualOverlapIsAnsweredParallelByTheNodeOnBothSidesWithOneHandledCardAndNoRing()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        PendingOnTheOthersCard("parser", "shared.cs");

        await FinishParserAsync();

        var tia2 = _state.Flights().Single(f => f.Code == "TIA2");
        tia2.Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Separate);
        var tia1 = _state.Flights().Single(f => f.Code == "TIA1");
        var row = tia1.Overlaps.Single();
        (row.Other, row.Answer, row.AfterLeg).ShouldBe(("TIA2", OverlapAnswer.Separate, "parser"));
        row.Files.ShouldBe(["shared.cs"]);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().Where(c => c.Kind == LegCommander.OverlapKind).ShouldAllBe(c => c.Bin == CardBin.Ring);
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapParallelKind);
        note.Bin.ShouldBe(CardBin.Handled);
        note.Title.ShouldContain("TIA1");
        note.Title.ShouldContain("TIA2");
        note.Title.ShouldContain("shared.cs");
        // TIA1's next leg starts; neither flight is held by a pending row.
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
        tia1.Overlaps.ShouldNotContain(o => o.Answer == OverlapAnswer.Pending);
        tia2.Overlaps.ShouldNotContain(o => o.Answer == OverlapAnswer.Pending);
    }

    [Fact]
    public async Task AMutualOverlapWhereTheOthersRowCameFromAPlannerAskRestartsItsPlanner()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        PendingOnTheOthersCard(null, "shared.cs");

        await _legs.CheckOverlapAsync("TIA1", "parser", Ct);

        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Separate);
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Separate);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapParallelKind);
        // The planner is launched in the background: wait for its start to be recorded.
        await Eventually.Holds(() => _workers.Started.Any(w => w.Name == "du1-tia2-plan"), Ct);
        _workers.Started.Select(w => w.Name).ShouldContain(n => n.Contains("tia2") && n.Contains("plan"));
    }

    /// <summary>TIA1 (parser done) holds a pending leg-end row about <paramref name="other"/> with its overlap card open.</summary>
    private async Task Tia1PendingAboutAsync(string other, string? node = null)
    {
        await FinishParserAsync();
        var tia1 = _state.Flights().Single(f => f.Code == "TIA1");
        _state.PutFlight(tia1 with { Overlaps = [new OverlapRow { Other = other, Node = node, Files = ["shared.cs"], Pick = Verbs.OverlapPickConnect, Why = "asked", Answer = OverlapAnswer.Pending, AfterLeg = "parser", At = DateTimeOffset.UtcNow }] });
        _state.RaiseCard(CardBin.Ring, LegCommander.OverlapKind, Verbs.FlightTarget("TIA1"), $"TIA1/parser overlaps {other} (shared.cs)", "asked", []);
    }

    [Fact]
    public async Task APendingRowWhoseLocalOtherHasLandedIsClosedByTheSweepWithAHandledLineAndTheNextLegStarts()
    {
        await FlyingAsync("parser.cs");
        OtherFlight("TIA2", "shared.cs");
        await Tia1PendingAboutAsync("TIA2");
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA2") with { State = FlightState.Landed });

        await _legs.AnswerLandedPendingAsync(Ct);

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapLandedKind);
        note.Bin.ShouldBe(CardBin.Handled);
        note.Title.ShouldBe("TIA2 had landed: TIA1 goes on, no answer needed");
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        flight.Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Connect);
        flight.Connections.ShouldBeEmpty();
        flight.State.ShouldNotBe(FlightState.Held);
        L("parser").State.ShouldBe(LegState.Done);
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");

        // A second sweep has nothing left to answer.
        var cards = _state.Cards().Length;
        await _legs.AnswerLandedPendingAsync(Ct);
        _state.Cards().Length.ShouldBe(cards);
    }

    [Fact]
    public async Task APendingRowWhoseOtherLandedOnAPeerIsClosedByTheSweepToo()
    {
        await FlyingAsync("parser.cs");
        const string gitHub = "SharpAstro/tianwen";
        Git(Repo, "config", $"url.{Origin.Replace('\\', '/')}.insteadOf", $"https://github.com/{gitHub}.git");
        Git(Repo, "remote", "set-url", "origin", $"https://github.com/{gitHub}.git");
        var theirs = _state.Flights().Single() with
        {
            Code = "PIL9", Issue = 9, Branch = "flight/pil9", RepoPath = "D:/src/tianwen", Worktree = "D:/src/tianwen/.claude/worktrees/pil9",
            Overlaps = [], PlannedFiles = null, Connections = [], PeerConnections = [],
        };
        var peer = new FakePeer("DE1") { Flights = [theirs], Repos = [new RepoInfo { Name = "tianwen", Path = "D:/src/tianwen", GitHub = gitHub }] };
        var fleet = new FakeFleet();
        fleet.Peers.Add(peer);
        _flights.Fleet = fleet;
        await Tia1PendingAboutAsync("PIL9", "DE1");

        // Still flying there: the row stays pending and its card open.
        await _legs.AnswerLandedPendingAsync(Ct);
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Pending);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegCommander.OverlapKind);

        peer.Flights = [theirs with { State = FlightState.Landed }];
        await _legs.AnswerLandedPendingAsync(Ct);

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().Single(c => c.Kind == LegCommander.OverlapLandedKind).Title.ShouldBe("PIL9 had landed: TIA1 goes on, no answer needed");
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Connect);
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
    }

    [Fact]
    public async Task APlannerAskedPendingRowWhoseOtherLandedHasItsPlannerRestartedWithTheAnswer()
    {
        await FlyingAsync("parser.cs");
        OtherFlight("TIA2", "shared.cs");
        PendingOnTheOthersCard(null, "shared.cs");
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA1") with { State = FlightState.Landed });

        await _legs.AnswerLandedPendingAsync(Ct);

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().Single(c => c.Kind == LegCommander.OverlapLandedKind).Title.ShouldBe("TIA1 had landed: TIA2 goes on, no answer needed");
        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Connect);
        await Eventually.Holds(() => _workers.Started.Any(w => w.Name == "du1-tia2-plan"), Ct);
        // The restarted planner is told the truth: the node closed it, as the other had landed; the controller did not choose a wait.
        var brief = _workers.Started.Last(w => w.Name == "du1-tia2-plan").Brief;
        brief.ShouldContain("TIA1 had landed when the overlap was found, so the node closed it itself");
        brief.ShouldContain("there is nothing to wait for");
        brief.ShouldNotContain("the controller chose to connect");
        brief.ShouldNotContain("is held until TIA1 has landed");
    }

    [Fact]
    public async Task ThePlannerBriefCreditsAnOverlapTheNodeConnectedItselfToTheNodeNotTheController()
    {
        await FlyingAsync("parser.cs");
        OtherFlight("TIA2", "shared.cs");
        var tia1 = _state.Flights().Single(f => f.Code == "TIA1");
        var row = new OverlapRow
        {
            Other = "TIA2", Files = ["shared.cs"], Pick = Verbs.OverlapPickConnect, Why = "found at parser", Answer = OverlapAnswer.Connect, AfterLeg = "parser",
            At = DateTimeOffset.UtcNow, AnsweredByNode = OverlapRow.AnsweredByNodeConnected,
        };
        _state.PutFlight(tia1 with { Overlaps = [row] });

        var brief = Briefs.Plan(_state.Flights().Single(f => f.Code == "TIA1"), [], "pilot");

        brief.ShouldContain("the node connected TIA1 to it itself");
        brief.ShouldNotContain("the controller chose to connect");
    }

    [Fact]
    public async Task AOneWayRowWhoseOtherHasNotLandedIsLeftPendingBySweep()
    {
        await FlyingAsync("parser.cs");
        OtherFlight("TIA2", "shared.cs");
        await Tia1PendingAboutAsync("TIA2");

        await _legs.AnswerLandedPendingAsync(Ct);

        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Pending);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapLandedKind);
    }

    [Fact]
    public async Task AOneWayOverlapIsConnectedByTheNodeWhenTheOthersPendingRowNamesAnotherFlight()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        var tia2 = _state.Flights().Single(f => f.Code == "TIA2");
        _state.PutFlight(tia2 with { Overlaps = [new OverlapRow { Other = "TIA9", Files = ["x.cs"], Pick = Verbs.OverlapPickConnect, Why = "asked", Answer = OverlapAnswer.Pending, At = DateTimeOffset.UtcNow }] });

        await _legs.CheckOverlapAsync("TIA1", "parser", Ct);

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapParallelKind);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapConnectedKind);
        _state.Flights().Single(f => f.Code == "TIA1").Connections.ShouldBe(["TIA2"]);
    }

    [Fact]
    public async Task AnUnansweredPairIsConnectedByTheNodeForANewSharedFile()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await _legs.CheckOverlapAsync("TIA1", "parser", Ct);

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().Single(c => c.Kind == LegCommander.OverlapConnectedKind).Title.ShouldContain("shared.cs");
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Connect);
    }

    [Fact]
    public async Task AConnectedPairStillHoldsTheNextLegAndRecordsANewFileOnTheRowWithoutACard()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot, "fresh.cs");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await FinishParserAsync();
        // The node connected the flight at the leg's end.
        _state.Flights().Single(f => f.Code == "TIA1").State.ShouldBe(FlightState.Held);
        L("render").State.ShouldBe(LegState.Planned);

        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        File.WriteAllText(Path.Combine(flight.Worktree, "fresh.cs"), "class Y {}\n");
        Git(flight.Worktree, "add", "fresh.cs");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fresh");
        await _legs.CheckOverlapAsync("TIA1", "parser", Ct);

        // It waits for TIA2 already: nothing to ask, the new file is recorded on the row.
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var after = _state.Flights().Single(f => f.Code == "TIA1");
        after.State.ShouldBe(FlightState.Held);
        after.Connections.ShouldBe(["TIA2"]);
        after.Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Connect);
        after.Overlaps.Single().Files.ShouldBe(["fresh.cs", "shared.cs"]);
    }

    [Fact]
    public async Task WithOneOtherAnsweredParallelAndOneNotOnlyTheUnansweredIsConnectedTo()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot, "other.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        // TIA2 has stood still, so its card rings and is answered parallel.
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA2") with { LastMovedAt = _clock.GetUtcNow().AddHours(-5) });
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await FinishParserAsync();
        (await Leg(Verbs.Separate, "flight/TIA1", "TIA2")).Ok.ShouldBeTrue();
        OtherFlight("TIA3", "other.cs" + Hot);

        await _legs.CheckOverlapAsync("TIA1", "render", Ct);

        // The earlier ring for TIA2 was answered; the new overlap with TIA3 raises none.
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapConnectedKind);
        note.Title.ShouldContain("TIA3");
        note.Title.ShouldNotContain("shared.cs");
        var rows = _state.Flights().Single(f => f.Code == "TIA1").Overlaps;
        rows.Single(o => o.Other == "TIA2").Answer.ShouldBe(OverlapAnswer.Separate);
        rows.Single(o => o.Other == "TIA3").Answer.ShouldBe(OverlapAnswer.Connect);
    }

    /// <summary>An open PR no flight owns, changing <paramref name="files"/>.</summary>
    private void FlightlessPr(int number, params string[] files)
    {
        var repo = _state.Flights().Single(f => f.Code == "TIA1").RepoPath;
        var prs = _github.OpenPrs.TryGetValue(repo, out var have) ? have : [];
        _github.OpenPrs[repo] = [.. prs, new PullRequestStatus { Number = number, Title = "Somebody's PR", Head = $"someone/pr{number}" }];
        _github.PrFiles[number] = files;
    }

    [Fact]
    public async Task AnUnforeseenOverlapWithAFlightlessOpenPrIsAnsweredParallelByTheNodeWithAHandledNoteAndNoRing()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        FlightlessPr(77, "shared.cs", "elsewhere.cs");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var row = _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single();
        (row.Other, row.Answer, row.AfterLeg).ShouldBe(("#77", OverlapAnswer.Separate, "parser"));
        row.Files.ShouldBe(["shared.cs"]);
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapParallelKind);
        note.Bin.ShouldBe(CardBin.Handled);
        note.Title.ShouldContain("#77");
        note.Title.ShouldContain("shared.cs");
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
    }

    [Fact]
    public async Task AFlightOverlapIsConnectedByTheNodeWhileThePrOverlapIsAnsweredParallelByTheNode()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot, "pr.cs");
        OtherFlight("TIA2", "shared.cs" + Hot);
        FlightlessPr(78, "pr.cs");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await _legs.CheckOverlapAsync("TIA1", "parser", Ct);

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().Single(c => c.Kind == LegCommander.OverlapConnectedKind).Title.ShouldContain("TIA2");
        var rows = _state.Flights().Single(f => f.Code == "TIA1").Overlaps;
        rows.Single(o => o.Other == "TIA2").Answer.ShouldBe(OverlapAnswer.Connect);
        rows.Single(o => o.Other == "#78").Answer.ShouldBe(OverlapAnswer.Separate);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapParallelKind && c.Title.Contains("#78"));
    }

    [Fact]
    public async Task ANewFileSharedWithAPrAnsweredByTheNodeRingsNothingAndIsUnionedOnTheRow()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        FlightlessPr(79, "shared.cs", "fresh.cs");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await _legs.CheckOverlapAsync("TIA1", "parser", Ct);

        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        File.WriteAllText(Path.Combine(flight.Worktree, "fresh.cs"), "class Y {}\n");
        Git(flight.Worktree, "add", "fresh.cs");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fresh");
        await _legs.CheckOverlapAsync("TIA1", "render", Ct);

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var row = _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single();
        row.Answer.ShouldBe(OverlapAnswer.Separate);
        row.Files.ShouldBe(["fresh.cs", "shared.cs"]);
        row.AfterLeg.ShouldBe("render");
    }

    [Fact]
    public async Task ASmallOneWayOverlapOnOneTestFileIsPlannedInParallelByTheNodeWithAHandledNoteAndNoRing()
    {
        await FlyingAsync("parser.cs", "src/Foo.Tests/FooTests.cs" + Hot);
        OtherFlight("TIA2", "src/Foo.Tests/FooTests.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var tia1 = _state.Flights().Single(f => f.Code == "TIA1");
        var row = tia1.Overlaps.Single();
        (row.Other, row.Answer, row.AfterLeg).ShouldBe(("TIA2", OverlapAnswer.Separate, "parser"));
        row.Files.ShouldBe(["src/Foo.Tests/FooTests.cs"]);
        var note = _state.Cards().Single(c => c.Kind == LegCommander.OverlapParallelKind);
        note.Bin.ShouldBe(CardBin.Handled);
        note.Title.ShouldContain("TIA2");
        note.Title.ShouldContain("FooTests.cs");
        // The other side is never touched, and the next leg is free.
        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldBeEmpty();
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
    }

    [Fact]
    public async Task AOneWayOverlapOnFourSharedFilesIsConnectedByTheNode()
    {
        string[] shared = ["a.cs", "b.cs", "c.cs", "d.cs"];
        await FlyingAsync(["parser.cs", .. shared]);
        OtherFlight("TIA2", shared);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapConnectedKind);
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Connect);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapParallelKind);
        L("render").State.ShouldBe(LegState.Planned);
    }

    [Fact]
    public async Task AOneWayOverlapOnASourceFileBothFlightsChangeByAHundredLinesIsConnectedByTheNode()
    {
        await FlyingAsync("parser.cs", "src/Hot.cs*100");
        OtherFlight("TIA2", "src/Hot.cs*100");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapConnectedKind);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapParallelKind);
    }

    [Fact]
    public async Task ABaseMergePushRunsTheCheck()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        File.WriteAllText(Path.Combine(Repo, "newer.cs"), "class Z {}\n");
        Git(Repo, "add", "newer.cs");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "main moves");
        Git(Repo, "push", "-q", "origin", "main");

        foreach (var leg in _state.Legs("TIA1"))
        {
            _state.PutLeg(leg with { State = LegState.Done });
        }

        (await _legs.BringUpToDateAsync("TIA1", Ct)).ShouldBeNull();

        _state.Cards().Single(c => c.Kind == LegCommander.OverlapConnectedKind).Title.ShouldContain("shared.cs");
    }

    [Fact]
    public async Task AFinishedLegThatStayedToItsPlannedFilesRingsNothing()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs", "shared.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.ShouldBeEmpty();
        // The flight went on: its next leg is being validated.
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
    }

    [Fact]
    public async Task ASweepWhileTheLegEndCheckIsParkedStartsNothingOfTheFlight()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.OnOpenPrs = async () =>
        {
            reached.TrySetResult();
            await release.Task;
        };
        var finishing = Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed");
        await reached.Task.WaitAsync(Eventually.Cap, Ct);

        // The leg is done and the check is parked on GitHub: the sweep must not start the next leg's validator.
        L("parser").State.ShouldBe(LegState.Done);
        await _legs.AdvanceFlightAsync("TIA1", Ct);
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));

        release.SetResult();
        (await finishing).Ok.ShouldBeTrue();
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapConnectedKind);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));
    }

    [Fact]
    public async Task ALegCheckThatOutlastsTheMarkLifeStillHoldsTheSweepOffWhileTheRingIsParked()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");

        // The leg's own check is parked, and the node's time runs past the mark's life while it runs.
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _checks.OnRun = async () =>
        {
            checking.TrySetResult();
            await checkDone.Task;
        };
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.OnOpenPrs = async () =>
        {
            reached.TrySetResult();
            await release.Task;
        };
        var finishing = Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed");
        await checking.Task.WaitAsync(Eventually.Cap, Ct);
        _clock.Advance(LegCommander.LegEndMarkLife + TimeSpan.FromMinutes(1));
        checkDone.SetResult();
        await reached.Task.WaitAsync(Eventually.Cap, Ct);

        // Recorded done after the mark's life had passed since the finish began: the sweep still starts nothing.
        L("parser").State.ShouldBe(LegState.Done);
        await _legs.AdvanceFlightAsync("TIA1", Ct);
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));

        release.SetResult();
        (await finishing).Ok.ShouldBeTrue();
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapConnectedKind);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));
    }

    [Fact]
    public async Task ARefusedFinishDoesNotClearTheMarkOfTheFinishWhoseRingIsParked()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        OtherFlight("TIA2", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.OnOpenPrs = async () =>
        {
            reached.TrySetResult();
            await release.Task;
        };
        var finishing = Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed");
        await reached.Task.WaitAsync(Eventually.Cap, Ct);

        // A second finish of the done leg is refused; it must not take away the first one's hold.
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "again")).Ok.ShouldBeFalse();
        await _legs.AdvanceFlightAsync("TIA1", Ct);
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));

        release.SetResult();
        (await finishing).Ok.ShouldBeTrue();
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OverlapConnectedKind);
    }

    [Fact]
    public async Task AFinishWhoseCheckThrowsLeavesNoMarkSoTheNextLegStartsAtOnce()
    {
        await FlyingAsync("parser.cs", "shared.cs" + Hot);
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");

        _checks.OnRun = () => Task.FromException(new InvalidOperationException("no shell"));
        await Should.ThrowAsync<InvalidOperationException>(Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed"));

        _checks.OnRun = null;
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();

        // Nothing rang and no holder leaked: the next leg is started straight away, with no wait for the mark's life.
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
    }

    private sealed class FakeFleet : IFleet
    {
        public List<IFleetPeer> Peers { get; } = [];

        IReadOnlyList<IFleetPeer> IFleet.Peers() => [.. Peers];
    }

    /// <summary>A peer holding the repo and flying <see cref="Flights"/>; it answers flight-changes from <see cref="Changes"/> (the lines it says per flight code).</summary>
    private sealed class FakePeer(string name) : IFleetPeer
    {
        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasGrant => true;

        public FlightRow[] Flights { get; set; } = [];

        public RepoInfo[] Repos { get; init; } = [];

        public Dictionary<string, string> Changes { get; init; } = [];

        /// <summary>Run once the changes of a flight were read: a race, as the peer's flight landing between that read and the connect.</summary>
        public Action? AfterChangesRead { get; set; }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
                DataFolder = "/data", SocketPath = "/sock", Stream = "s",
            });

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => Task.FromResult<RepoInfo[]?>(Repos);

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new FleetLoad(null, Flights));

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken)
        {
            command.Verb.ShouldBe(Verbs.FlightChanges, "a peer is only read here, never sent anything else");
            var code = command.Target[(command.Target.LastIndexOf('/') + 1)..];
            var result = new CommandResult { Verb = command.Verb, Target = command.Target, Who = command.Who, Seq = 0, Ok = true };
            AfterChangesRead?.Invoke();
            return Task.FromResult(Changes.TryGetValue(code, out var made)
                ? result with { Made = made }
                : result with { Ok = false, Error = $"the plane of {code} is gone" });
        }
    }
}
