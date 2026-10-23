using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A planner finds its flight looks like part of another: one card asks, the answer is kept per pair, and the flight's leg planning waits.</summary>
public sealed class OverlapTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2o-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    public OverlapTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        (_flights, _legs) = Commanders(_state);
    }

    private string Repo => Path.Combine(_root, "tianwen");

    private (FlightCommander, LegCommander) Commanders(NodeState state)
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

    private Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        Leg(_legs, verb, target, args);

    private static Task<CommandResult> Leg(LegCommander legs, string verb, string target, params string[] args) =>
        legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    /// <summary>TIA1 (the one TIA2 looks like part of) and TIA2.</summary>
    private async Task TwoFlights()
    {
        foreach (var issue in new[] { "1", "2" })
        {
            (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue, $"Issue {issue}"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        }
    }

    private Task<CommandResult> Ask(string why = "it edits the same parser") => Leg(Verbs.Overlap, "flight/TIA2", "TIA1", why);

    private CardRow[] Cards() => [.. _state.Cards().Where(c => c.Kind == LegCommander.OverlapKind)];

    [Fact]
    public async Task OverlapOnAFreshPairRingsOneCardWithFoldAndPlanSeparatelyAndTellsThePlannerToEndItsTurn()
    {
        await TwoFlights();

        var asked = await Ask();

        asked.Ok.ShouldBeTrue();
        asked.Made.ShouldNotBeNull().ShouldContain("end your turn");
        var card = Cards().ShouldHaveSingleItem();
        (card.Bin, card.State, card.Subject).ShouldBe((CardBin.Ring, CardState.Open, "flight/TIA2"));
        card.Title.ShouldBe("TIA2 looks like part of TIA1: it edits the same parser");
        card.Choices.Select(c => (c.Label, c.Verb, c.Target, string.Join(' ', c.Args))).ShouldBe(
        [
            ("Fold into TIA1", Verbs.Fold, "flight/TIA2", "TIA1"),
            ("Plan separately", Verbs.Separate, "flight/TIA2", "TIA1"),
        ]);
        var recorded = _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldHaveSingleItem();
        (recorded.Other, recorded.Why, recorded.Answer).ShouldBe(("TIA1", "it edits the same parser", OverlapAnswer.Pending));
        _state.Flights().Single(f => f.Code == "TIA1").Overlaps.ShouldBeEmpty();
    }

    [Fact]
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
    public async Task PlanLegAndPlannedAreRefusedWhileTheAnswerWaitsAndNameTheOtherFlightAndTheCard()
    {
        await TwoFlights();
        await Ask();
        var card = Cards().Single();

        var plan = await Leg(Verbs.PlanLeg, "flight/TIA2", "name=parser", "goal=g", "done-when=d");
        var planned = await Leg(Verbs.Planned, "flight/TIA2", "done");

        foreach (var refused in new[] { plan, planned })
        {
            refused.Ok.ShouldBeFalse();
            refused.Error.ShouldNotBeNull().ShouldContain("TIA1");
            refused.Error.ShouldContain($"card {card.Id}");
        }
        _state.Legs("TIA2").ShouldBeEmpty();
        // The other flight is not held by it.
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=g", "done-when=d")).Ok.ShouldBeTrue();
    }

    [Fact]
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
        (closed.State, closed.Outcome).ShouldBe((CardState.Closed, "plan separately from TIA1"));
        _workers.Stopped.ShouldBe(["job00001"]);
        _workers.Started.Count.ShouldBe(2);
        var fresh = _workers.Started[1];
        fresh.Name.ShouldBe("du1-tia2-plan");
        fresh.Brief.ShouldContain("TIA1");
        fresh.Brief.ShouldContain("the controller chose to plan TIA2 separately");
        fresh.Brief.ShouldContain("flight connect TIA2 TIA1");

        // Its legs can be planned now.
        (await Leg(Verbs.PlanLeg, "flight/TIA2", "name=parser", "goal=g", "done-when=d")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AskingAnAnsweredPairAgainReturnsTheRecordedAnswerWithNoNewCard()
    {
        await TwoFlights();
        await Ask();
        (await Leg(Verbs.Separate, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        var cards = _state.Cards().Length;

        var again = await Ask();

        again.Ok.ShouldBeTrue();
        again.Made.ShouldNotBeNull().ShouldContain("answered before: plan TIA2 separately");
        _state.Cards().Length.ShouldBe(cards);
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Closed);
        // Answering it again starts no further planner.
        (await Leg(Verbs.Separate, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        _workers.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FoldIsNotBuiltYetSoItIsRefusedAndNothingIsRecordedOrStopped()
    {
        await TwoFlights();
        await Ask();

        (await Leg(Verbs.Fold, "flight/TIA2", "TIA1")).Error.ShouldNotBeNull().ShouldContain("not built yet");

        _state.Flights().Single(f => f.Code == "TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Pending);
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Open);
        (await Leg(Verbs.Separate, "flight/TIA2", "TIA9")).Error.ShouldNotBeNull().ShouldContain("no overlap");
    }

    [Fact]
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
}
