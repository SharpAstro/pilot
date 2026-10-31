using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A planner finds its flight looks like part of another: one card asks, the answer is kept per pair, and the flight's leg planning waits.</summary>
public abstract class OverlapTestsBase : IDisposable
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "p2o-" + Guid.NewGuid().ToString("N")[..8]);
    private protected readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private protected readonly FakeWorkers _workers = new();
    private protected readonly FlightCommander _flights;
    private protected readonly LegCommander _legs;

    protected OverlapTestsBase()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        (_flights, _legs) = Commanders(_state);
    }

    private protected string Repo => Path.Combine(_root, "tianwen");

    private protected (FlightCommander, LegCommander) Commanders(NodeState state)
    {
        var git = new GitCli();
        var flights = new FlightCommander(state, new Planes(git), TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        return (flights, new LegCommander(state, _workers, new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, TimeProvider.System));
    }

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        // A plane's git may still be letting go of a folder while it is walked (seen as DirectoryNotFound mid-delete): tried again.
        for (var attempt = 1; Directory.Exists(_root); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    private protected static string Git(string cwd, params string[] args)
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

    private protected Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        Leg(_legs, verb, target, args);

    private protected static Task<CommandResult> Leg(LegCommander legs, string verb, string target, params string[] args) =>
        legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    /// <summary>TIA1 (the one TIA2 looks like part of) and TIA2.</summary>
    private protected async Task TwoFlights()
    {
        foreach (var issue in new[] { "1", "2" })
        {
            (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue, $"Issue {issue}"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        }
    }

    private protected Task<CommandResult> Ask(string why = "it edits the same parser") => Leg(Verbs.Overlap, "flight/TIA2", "TIA1", why);

    private protected CardRow[] Cards() => [.. _state.Cards().Where(c => c.Kind == LegCommander.OverlapKind)];

    private protected FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);
}

/// <summary>Part 1 of OverlapTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class OverlapTests1 : OverlapTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task OverlapOnAFreshPairRingsOneCardWithFoldConnectAndPlanInParallelAndTellsThePlannerToEndItsTurn()
    {
        await TwoFlights();

        var asked = await Ask();

        asked.Ok.ShouldBeTrue();
        asked.Made.ShouldNotBeNull().ShouldContain("end your turn");
        var card = Cards().ShouldHaveSingleItem();
        (card.Bin, card.State, card.Subject).ShouldBe((CardBin.Ring, CardState.Open, "flight/TIA2"));
        card.Title.ShouldBe("Connect TIA2 to TIA1 (it edits the same parser)");
        card.Choices.Select(c => (c.Label, c.Verb, c.Target, string.Join(' ', c.Args))).ShouldBe(
        [
            ("Fold into TIA1", Verbs.Fold, "flight/TIA2", "TIA1"),
            ("Connect to TIA1", Verbs.ConnectOverlap, "flight/TIA2", "TIA1"),
            ("Plan TIA2 in parallel with TIA1", Verbs.Separate, "flight/TIA2", "TIA1"),
        ]);
        card.Choices.ShouldNotContain(c => c.Label.Contains("Leave it"));
        var recorded = _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldHaveSingleItem();
        (recorded.Other, recorded.Why, recorded.Answer).ShouldBe(("TIA1", "it edits the same parser", OverlapAnswer.Pending));
        recorded.Files.ShouldBeNull();
        recorded.Node.ShouldBeNull();
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheSharedFilesAreNamedOnTheCardWithBothFlightsAndWhatEachChoiceDoes()
    {
        await TwoFlights();

        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA1", "file=src/DetachedShell.cs", "file=src/Parser.cs", "it edits the same parser")).Ok.ShouldBeTrue();

        var card = Cards().ShouldHaveSingleItem();
        card.Title.ShouldBe("Connect TIA2 to TIA1 (both change DetachedShell.cs, Parser.cs)");
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("TIA2 (Issue 2) overlaps TIA1 (Issue 1)");
        detail.ShouldContain("Both change 2 files: DetachedShell.cs, Parser.cs.");
        detail.ShouldContain("it edits the same parser");
        detail.ShouldContain("Connect holds TIA2 until TIA1 has landed");
        detail.ShouldContain("Plan in parallel");
        detail.ShouldContain("Fold takes TIA2's issue into TIA1");
        // The files last: the card's detail is cut at a few rows, and a long list must not cut what each choice does.
        detail.IndexOf("Both change", StringComparison.Ordinal).ShouldBeGreaterThan(detail.IndexOf("Fold takes", StringComparison.Ordinal));
        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldHaveSingleItem().Files.ShouldBe(["src/DetachedShell.cs", "src/Parser.cs"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALongListOfSharedFilesIsCutInTheTitleAndWholeInTheDetail()
    {
        await TwoFlights();
        string[] files = [.. Enumerable.Range(1, 7).Select(i => $"src/File{i}.cs")];

        (await Leg(Verbs.Overlap, "flight/TIA2", ["TIA1", .. files.Select(f => "file=" + f), "same area"])).Ok.ShouldBeTrue();

        var card = Cards().Single();
        card.Title.ShouldBe("Connect TIA2 to TIA1 (both change File1.cs, File2.cs, File3.cs, File4.cs and 3 more)");
        card.Detail.ShouldNotBeNull().ShouldContain("File7.cs");
        card.Detail.ShouldContain("Both change 7 files: File1.cs");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFoldPickTitlesTheCardAFoldAndKeepsThePickOnTheRow()
    {
        await TwoFlights();

        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA1", "pick=fold", "file=src/DetachedShell.cs", "file=src/Parser.cs", "same goal")).Ok.ShouldBeTrue();

        var card = Cards().ShouldHaveSingleItem();
        card.Title.ShouldBe("Fold TIA2 into TIA1 (both change DetachedShell.cs, Parser.cs)");
        // Still all three choices: the title says what the planner leans to, the controller answers.
        card.Choices.Select(c => c.Label).ShouldBe(["Fold into TIA1", "Connect to TIA1", "Plan TIA2 in parallel with TIA1"]);
        var recorded = F("TIA2").Overlaps.ShouldHaveSingleItem();
        (recorded.Pick, recorded.Why).ShouldBe((Verbs.OverlapPickFold, "same goal"));
        recorded.Files.ShouldBe(["src/DetachedShell.cs", "src/Parser.cs"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AConnectPickTitlesTheCardAConnectAsDoesNoPick()
    {
        await TwoFlights();

        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA1", "pick=connect", "file=src/Parser.cs", "it edits the same parser")).Ok.ShouldBeTrue();

        Cards().ShouldHaveSingleItem().Title.ShouldBe("Connect TIA2 to TIA1 (both change Parser.cs)");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Pick.ShouldBe(Verbs.OverlapPickConnect);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUnknownPickIsRefusedAndNothingIsRecorded()
    {
        await TwoFlights();

        var asked = await Leg(Verbs.Overlap, "flight/TIA2", "TIA1", "pick=leave", "file=src/Parser.cs", "same parser");

        asked.Ok.ShouldBeFalse();
        asked.Error.ShouldNotBeNull().ShouldContain("pick= is fold or connect");
        Cards().ShouldBeEmpty();
        F("TIA2").Overlaps.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ConnectAnswerRecordsItConnectsTheFlightClosesTheCardAndStartsAFreshPlannerThatKnowsIt()
    {
        await TwoFlights();
        (await Leg(Verbs.PlanFlight, "flight/TIA2")).Ok.ShouldBeTrue();
        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA1", "file=src/Parser.cs", "same parser")).Ok.ShouldBeTrue();
        var connect = Cards().Single().Choices.Single(c => c.Verb == Verbs.ConnectOverlap);

        var answered = await Leg(connect.Verb, connect.Target, connect.Args);

        answered.Ok.ShouldBeTrue();
        answered.Made.ShouldNotBeNull().ShouldContain("TIA2 is connected to TIA1");
        var asker = F("TIA2");
        asker.Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Connect);
        asker.Connections.ShouldBe(["TIA1"]);
        asker.State.ShouldBe(FlightState.Held);
        var closed = Cards().Single();
        (closed.State, closed.Outcome).ShouldBe((CardState.Closed, "connect TIA2 to TIA1"));
        _workers.Stopped.ShouldBe(["job00001"]);
        _workers.Started.Count.ShouldBe(2);
        var fresh = _workers.Started[1];
        fresh.Name.ShouldBe("du1-tia2-plan");
        fresh.Brief.ShouldContain("the controller chose to connect TIA2 to it");
        fresh.Brief.ShouldContain("both change src/Parser.cs");
        // Its legs can be planned now, and it still waits for TIA1.
        (await Leg(Verbs.PlanLeg, "flight/TIA2", "name=parser", "goal=Do the work", "done-when=Its check passes")).Ok.ShouldBeTrue();
        F("TIA2").State.ShouldBe(FlightState.Held);
        // Asked again, the answer comes back with no card; answered again, nothing starts.
        (await Ask()).Made.ShouldNotBeNull().ShouldContain("TIA2 is connected to TIA1 and waits for it to land");
        (await Leg(Verbs.ConnectOverlap, "flight/TIA2", "TIA1")).Made.ShouldNotBeNull().ShouldContain("already");
        _workers.Started.Count.ShouldBe(2);
        Cards().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ConnectAnswerIsRefusedWhenTheOtherWaitsForTheFlightAndTheCardStaysOpen()
    {
        await TwoFlights();
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA1", Args = ["TIA2"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        await Ask();

        var refused = await Leg(Verbs.ConnectOverlap, "flight/TIA2", "TIA1");

        refused.Error.ShouldNotBeNull().ShouldContain("neither would leave");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Pending);
        F("TIA2").Connections.ShouldBeEmpty();
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Open);
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUnknownALandedAndTheSameFlightAreRefused()
    {
        await TwoFlights();

        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA9", "why")).Error.ShouldBe("There is no flight TIA9");
        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA2", "why")).Error.ShouldBe("TIA2 cannot overlap itself");
        _state.PutFlight(_state.Flights().Single(f => f.Code == "TIA1") with { State = FlightState.Landed });
        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA1", "why")).Error.ShouldNotBeNull().ShouldStartWith("TIA1 has landed");

        Cards().ShouldBeEmpty();
        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PlanLegAndPlannedAreRefusedWhileTheAnswerWaitsAndNameTheOtherFlightAndTheCard()
    {
        await TwoFlights();
        await Ask();
        var card = Cards().Single();

        var plan = await Leg(Verbs.PlanLeg, "flight/TIA2", "name=parser", "goal=Do the work", "done-when=Its check passes");
        var planned = await Leg(Verbs.Planned, "flight/TIA2", "done");

        foreach (var refused in new[] { plan, planned })
        {
            refused.Ok.ShouldBeFalse();
            refused.Error.ShouldNotBeNull().ShouldContain("TIA1");
            refused.Error.ShouldContain($"card {card.Id}");
        }
        _state.Legs("TIA2").ShouldBeEmpty();
        // The other flight is not held by it.
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Do the work", "done-when=Its check passes")).Ok.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AskingAPendingPairAgainRingsNoSecondCard()
    {
        await TwoFlights();
        await Ask();

        var again = await Ask("a different wording");

        again.Ok.ShouldBeTrue();
        again.Made.ShouldNotBeNull().ShouldContain("asked about already");
        Cards().ShouldHaveSingleItem();
        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldHaveSingleItem().Why.ShouldBe("it edits the same parser");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PlanSeparatelyRecordsTheAnswerClosesTheCardStopsTheWaitingPlannerAndStartsAFreshOneThatKnowsIt()
    {
        await TwoFlights();
        (await Leg(Verbs.PlanFlight, "flight/TIA2")).Ok.ShouldBeTrue();
        var waiting = _workers.Started.ShouldHaveSingleItem();
        waiting.Brief.ShouldNotContain("Overlaps the controller has answered");
        await Ask();
        var card = Cards().Single();
        var separate = card.Choices.Single(c => c.Verb == Verbs.Separate);

        // The planner, ended its turn, is still listed as started lately: the fresh one is not refused for it.
        var answered = await Leg(separate.Verb, separate.Target, separate.Args);

        answered.Ok.ShouldBeTrue();
        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Separate);
        var closed = Cards().Single();
        (closed.State, closed.Outcome).ShouldBe((CardState.Closed, "plan in parallel with TIA1"));
        _workers.Stopped.ShouldBe(["job00001"]);
        _workers.Started.Count.ShouldBe(2);
        var fresh = _workers.Started[1];
        fresh.Name.ShouldBe("du1-tia2-plan");
        fresh.Brief.ShouldContain("TIA1");
        fresh.Brief.ShouldContain("the controller chose to plan TIA2 in parallel");
        fresh.Brief.ShouldContain("flight connect TIA2 TIA1");

        // Its legs can be planned now.
        (await Leg(Verbs.PlanLeg, "flight/TIA2", "name=parser", "goal=Do the work", "done-when=Its check passes")).Ok.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AskingAnAnsweredPairAgainReturnsTheRecordedAnswerWithNoNewCard()
    {
        await TwoFlights();
        await Ask();
        (await Leg(Verbs.Separate, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        var cards = _state.Cards().Length;

        var again = await Ask();

        again.Ok.ShouldBeTrue();
        again.Made.ShouldNotBeNull().ShouldContain("answered before: plan TIA2 in parallel");
        _state.Cards().Length.ShouldBe(cards);
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Closed);
        // Answering it again starts no further planner.
        (await Leg(Verbs.Separate, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        _workers.Started.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task FoldRecordsItOnThePairClosesTheCardAndAddsTheIssueToTheOtherFlightWhosePlannerIsToldToPlanLegsAfterItsOwn()
    {
        await TwoFlights();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Rework the parser", "done-when=Its check passes")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanFlight, "flight/TIA2")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        _workers.Started.Select(w => w.Name).ShouldBe(["du1-tia2-plan", "du1-tia1-plan"]);
        await Ask();
        var fold = Cards().Single().Choices.Single(c => c.Verb == Verbs.Fold);

        var answered = await Leg(fold.Verb, fold.Target, fold.Args);

        answered.Ok.ShouldBeTrue();
        answered.Made.ShouldNotBeNull().ShouldContain("TIA1");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Fold);
        var closed = Cards().Single();
        (closed.State, closed.Outcome).ShouldBe((CardState.Closed, "TIA2 folded into TIA1"));
        var folded = F("TIA1").FoldedIn.ShouldHaveSingleItem();
        (folded.Number, folded.From).ShouldBe((2, "TIA2"));

        // Both planners are stopped (the asker's, and the other's, so one planner is one flight's), and one is started on TIA1.
        _workers.Stopped.ShouldBe(["job00001", "job00002"]);
        _workers.Started.Count.ShouldBe(3);
        var fresh = _workers.Started[2];
        fresh.Name.ShouldBe("du1-tia1-plan");
        fresh.Brief.ShouldContain("Issues folded into this flight");
        fresh.Brief.ShouldContain("#2, from flight TIA2");
        fresh.Brief.ShouldContain("gh issue view 2 --comments");
        fresh.Brief.ShouldContain("plan its points as legs after the ones this flight has");
        fresh.Brief.ShouldContain("- parser (planned): Rework the parser");
    }

    [Theory]
    [InlineData(TrackerKind.Shortcut, TrackerKind.GitHub)]
    [InlineData(TrackerKind.GitHub, TrackerKind.Shortcut)]
    [Trait("Category", "Slow")]
    public async Task FoldAcrossTrackersIsRefusedNamingBothWhileTheOverlapItselfIsAllowed(TrackerKind asker, TrackerKind other)
    {
        await TwoFlights();
        _state.PutFlight(F("TIA2") with { Tracker = asker });
        _state.PutFlight(F("TIA1") with { Tracker = other });
        (await Ask()).Ok.ShouldBeTrue();

        var folded = await Leg(Verbs.Fold, "flight/TIA2", "TIA1");

        folded.Ok.ShouldBeFalse();
        folded.Error.ShouldNotBeNull().ShouldContain(asker.ToString());
        folded.Error.ShouldContain(other.ToString());
        folded.Error.ShouldContain("TIA1");
        F("TIA2").State.ShouldNotBe(FlightState.Landed);
        F("TIA1").FoldedIn.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task FoldRetiresTheAskingFlightLandedAndMarkedFoldedWithNoLegsAndItsPlaneGoneButItsBranchKept()
    {
        await TwoFlights();
        var before = F("TIA2");
        Directory.Exists(before.Worktree).ShouldBeTrue();
        await Ask();

        (await Leg(Verbs.Fold, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();

        var retired = F("TIA2");
        (retired.State, retired.FoldedInto).ShouldBe((FlightState.Landed, "TIA1"));
        retired.LandedAt.ShouldNotBeNull();
        _state.Legs("TIA2").ShouldBeEmpty();
        Directory.Exists(retired.Worktree).ShouldBeFalse();
        Git(Repo, "branch", "--list", before.Branch).ShouldNotBeEmpty();
        // A Handled card says it was folded, and no plain landed card rings.
        var card = _state.Cards().Single(c => c.Kind == FlightCommander.FoldedKind);
        (card.Bin, card.Subject, card.Title).ShouldBe((CardBin.Handled, "flight/TIA2", "TIA2 folded into TIA1"));
        _state.Cards().ShouldNotContain(c => c.Kind == "landed");
        F("TIA1").FoldedInto.ShouldBeNull();
        F("TIA1").State.ShouldNotBe(FlightState.Landed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheOtherFlightsPrBodyClosesBothIssues()
    {
        await TwoFlights();
        await Ask();
        (await Leg(Verbs.Fold, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();

        var body = LegCommander.PrBody(F("TIA1"), _state.Legs("TIA1"));

        body.ShouldContain("Closes #1.");
        body.ShouldContain("Closes #2 (folded in from TIA2).");
        LegCommander.PrBody(F("TIA2"), []).ShouldNotContain("#1");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task FoldingIntoALandedFlightIsRefusedAndNothingChanges()
    {
        await TwoFlights();
        await Ask();
        _state.PutFlight(F("TIA1") with { State = FlightState.Landed });

        var refused = await Leg(Verbs.Fold, "flight/TIA2", "TIA1");

        refused.Error.ShouldNotBeNull().ShouldStartWith("TIA1 has landed");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Pending);
        F("TIA2").State.ShouldNotBe(FlightState.Landed);
        F("TIA1").FoldedIn.ShouldBeEmpty();
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Open);
        _workers.Started.ShouldBeEmpty();
    }
}

/// <summary>Part 2 of OverlapTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class OverlapTests2 : OverlapTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task FoldingIntoAFlightWhosePrIsOpenIsRefusedForThePrWouldNotCloseTheIssue()
    {
        await TwoFlights();
        await Ask();
        _state.PutFlight(F("TIA1") with { PullRequest = 70, PullRequestUrl = "https://github.com/x/tianwen/pull/70" });

        (await Leg(Verbs.Fold, "flight/TIA2", "TIA1")).Error.ShouldNotBeNull().ShouldContain("PR #70");

        F("TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Pending);
        F("TIA1").FoldedIn.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task FoldNeedsAnAskedPairAndAnsweringItAgainStartsNoFurtherPlanner()
    {
        await TwoFlights();
        (await Leg(Verbs.Fold, "flight/TIA2", "TIA1")).Error.ShouldNotBeNull().ShouldContain("no overlap");
        await Ask();
        (await Leg(Verbs.Fold, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        var started = _workers.Started.Count;

        var again = await Leg(Verbs.Fold, "flight/TIA2", "TIA1");

        again.Ok.ShouldBeTrue();
        _workers.Started.Count.ShouldBe(started);
        F("TIA1").FoldedIn.ShouldHaveSingleItem();
        // A folded pair is not separated afterwards.
        (await Leg(Verbs.Separate, "flight/TIA2", "TIA1")).Error.ShouldNotBeNull().ShouldContain("answered fold already");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFoldedIssueAndTheRetirementSurviveAReplayOfTheJournal()
    {
        var folder = Path.Combine(_root, "journal-fold");
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var (flights, legs) = Commanders(state);
            foreach (var issue in new[] { "1", "2" })
            {
                (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue, $"Issue {issue}"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            }
            (await Leg(legs, Verbs.Overlap, "flight/TIA2", "TIA1", "same parser")).Ok.ShouldBeTrue();
            (await Leg(legs, Verbs.Fold, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        }

        using var again = new FileJournal(folder);
        var restored = new NodeState(again, TimeProvider.System);
        var other = restored.Flights().Single(f => f.Code == "TIA1");
        var folded = other.FoldedIn.ShouldHaveSingleItem();
        (folded.Number, folded.From).ShouldBe((2, "TIA2"));
        var asker = restored.Flights().Single(f => f.Code == "TIA2");
        (asker.State, asker.FoldedInto, asker.Overlaps.Single().Answer).ShouldBe((FlightState.Landed, "TIA1", OverlapAnswer.Fold));
        LegCommander.PrBody(other, []).ShouldContain("Closes #2");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightWaitingForTheFoldedOneNowWaitsForTheOtherAcrossAReplayAndFliesOnlyWhenTheOtherLands()
    {
        var folder = Path.Combine(_root, "journal-waiting");
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var (flights, legs) = Commanders(state);
            foreach (var issue in new[] { "1", "2", "3" })
            {
                (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue, $"Issue {issue}"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            }
            (await flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA3", Args = ["TIA2"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            state.Flights().Single(f => f.Code == "TIA3").State.ShouldBe(FlightState.Held);
            (await Leg(legs, Verbs.Overlap, "flight/TIA2", "TIA1", "same parser")).Ok.ShouldBeTrue();

            (await Leg(legs, Verbs.Fold, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();

            // TIA2's issue is TIA1's work now and has not landed: TIA3 still waits, for TIA1.
            var waiting = state.Flights().Single(f => f.Code == "TIA3");
            waiting.State.ShouldBe(FlightState.Held);
            waiting.Connections.ShouldBe(["TIA1"]);
        }

        using var again = new FileJournal(folder);
        var restored = new NodeState(again, TimeProvider.System);
        var replayed = restored.Flights().Single(f => f.Code == "TIA3");
        replayed.State.ShouldBe(FlightState.Held);
        replayed.Connections.ShouldBe(["TIA1"]);

        var (restoredFlights, _) = Commanders(restored);
        (await restoredFlights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = "flight/TIA1", Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        restored.Flights().Single(f => f.Code == "TIA3").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task FoldingDropsAConnectionThatWouldWaitOnItselfOrCycle()
    {
        await TwoFlights();
        // TIA1 waits for TIA2, and TIA2 is folded into TIA1: TIA1 cannot wait for itself.
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA1", Args = ["TIA2"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        await Ask();

        (await Leg(Verbs.Fold, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();

        var other = F("TIA1");
        other.Connections.ShouldBeEmpty();
        other.State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheRecordedAnswerSurvivesAReplayOfTheJournal()
    {
        var folder = Path.Combine(_root, "journal");
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var (flights, legs) = Commanders(state);
            foreach (var issue in new[] { "1", "2" })
            {
                (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue, $"Issue {issue}"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            }
            (await Leg(legs, Verbs.Overlap, "flight/TIA2", "TIA1", "same parser")).Ok.ShouldBeTrue();
            (await Leg(legs, Verbs.Separate, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        }

        using var again = new FileJournal(folder);
        var restored = new NodeState(again, TimeProvider.System);
        var overlap = restored.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldHaveSingleItem();
        (overlap.Other, overlap.Why, overlap.Answer).ShouldBe(("TIA1", "same parser", OverlapAnswer.Separate));
        restored.Cards().Single(c => c.Kind == LegCommander.OverlapKind).State.ShouldBe(CardState.Closed);
        // And so is asked no more: the answer comes back with no card.
        var (_, legsAgain) = Commanders(restored);
        var repeat = await Leg(legsAgain, Verbs.Overlap, "flight/TIA2", "TIA1", "same parser");
        repeat.Made.ShouldNotBeNull().ShouldContain("answered before");
        restored.Cards().Count(c => c.Kind == LegCommander.OverlapKind).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePlannersBriefMakesItLookFirstBeforeItPlansAnyLeg()
    {
        await TwoFlights();

        (await Leg(Verbs.PlanFlight, "flight/TIA2")).Ok.ShouldBeTrue();

        var brief = _workers.Started.ShouldHaveSingleItem().Brief;
        var look = brief.IndexOf("## Look first", StringComparison.Ordinal);
        var plan = brief.IndexOf("## Plan each leg", StringComparison.Ordinal);
        look.ShouldBeGreaterThan(-1);
        look.ShouldBeLessThan(plan);
        var section = brief[look..plan];
        // The planner lists its legs' files and runs the overlap check across nodes and PRs (#375), which answers per flight and PR.
        section.ShouldContain("list the files your legs will change");
        section.ShouldContain("flight touches TIA2 <file> <file>");
        section.ShouldContain("on this node and on each reachable node");
        section.ShouldContain("every open PR");
        section.ShouldContain("PIL294 (DE1): connect, both change DetachedShell.cs, JobRunner.cs");
        // An unknown answer is no "no overlap".
        section.ShouldContain("unknown");
        section.ShouldContain(" status`");
        // What to do with each answer: overlap asks the controller (with the shared files), connect holds the flight (on another node too), a peer's flight is never folded into, a PR is named.
        section.ShouldContain("flight overlap TIA2 <other> pick=fold file=<shared file>");
        section.ShouldContain("end your turn at once");
        section.ShouldContain("connect it to the other or plan in parallel");
        section.ShouldContain("flight connect TIA2 <other>");
        section.ShouldContain("The other may be a flight of another node");
        section.ShouldContain("cannot be folded into from here");
        section.ShouldContain("in your planned summary");
        section.ShouldContain("parallel (no file in common)");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task SeparateWithASharedFileThenConnectHoldsTheFlightOnTheOtherAndItsLegsCanStillBePlannedAndPlannedSaid()
    {
        await TwoFlights();
        (await Leg(Verbs.PlanFlight, "flight/TIA2")).Ok.ShouldBeTrue();
        (await Ask("both edit src/Parser.cs")).Ok.ShouldBeTrue();
        (await Leg(Verbs.Separate, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        _workers.Started[1].Brief.ShouldContain("both edit src/Parser.cs");

        // The fresh planner finds the shared file still changes and connects.
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA2", Args = ["TIA1"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        held.Connections.ShouldBe(["TIA1"]);
        (await Leg(Verbs.PlanLeg, "flight/TIA2", "name=parser", "goal=Rework the parser", "done-when=Its check passes")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA2", "name=render", "goal=Render it", "done-when=Its check passes")).Ok.ShouldBeTrue();
        var planned = await Leg(Verbs.Planned, "flight/TIA2", "connected to TIA1: both edit src/Parser.cs");
        planned.Ok.ShouldBeTrue();
        var card = _state.Cards().Single(c => c.Kind == LegCommander.PlannedKind && c.Subject == "flight/TIA2");
        card.Detail.ShouldNotBeNull().ShouldContain("connected to TIA1");
        _state.Legs("TIA2").Length.ShouldBe(2);
        // It still waits: the legs are planned, not flying.
        F("TIA2").State.ShouldBe(FlightState.Held);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightWhosePlannerRaisesNoOverlapPlansAndSaysPlannedAsTodayWithNoOverlapCard()
    {
        await TwoFlights();
        (await Leg(Verbs.PlanFlight, "flight/TIA2")).Ok.ShouldBeTrue();

        (await Leg(Verbs.PlanLeg, "flight/TIA2", "name=docs", "goal=Write the docs", "done-when=Its check passes")).Ok.ShouldBeTrue();
        var planned = await Leg(Verbs.Planned, "flight/TIA2", "one leg");

        planned.Ok.ShouldBeTrue();
        Cards().ShouldBeEmpty();
        F("TIA2").Overlaps.ShouldBeEmpty();
        F("TIA2").State.ShouldBe(FlightState.Flying);
        _state.Cards().Single(c => c.Kind == LegCommander.PlannedKind && c.Subject == "flight/TIA2").Choices.Single().Label.ShouldBe("Start docs");
    }

    /// <summary>
    /// A flight journaled before overlaps and folds has neither property: it reads back with both empty and writes again.
    /// The generated reader leaves a missing property's initialiser out, so with an initialiser alone both were null and
    /// the node died writing its next snapshot (SE1, 2026-10-24).
    /// </summary>
    [Fact]
    public void AFlightJournaledBeforeOverlapsReadsBackWithNoneAndWritesAgain()
    {
        var row = new FlightRow
        {
            Code = "PIL81", Repo = "pilot", RepoPath = Repo, Issue = 81, Branch = "flight/pil81", Worktree = Repo, Base = "main",
            State = FlightState.Landed, CreatedAt = DateTimeOffset.UtcNow,
        };
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(row, PilotJsonContext.Default.FlightRow))!.AsObject();
        json.Remove("overlaps").ShouldBeTrue();
        json.Remove("foldedIn").ShouldBeTrue();

        var old = System.Text.Json.JsonSerializer.Deserialize(json.ToJsonString(), PilotJsonContext.Default.FlightRow)!;

        old.Overlaps.ShouldBeEmpty();
        old.FoldedIn.ShouldBeEmpty();
        Should.NotThrow(() => System.Text.Json.JsonSerializer.Serialize(old, PilotJsonContext.Default.FlightRow));
    }
}
