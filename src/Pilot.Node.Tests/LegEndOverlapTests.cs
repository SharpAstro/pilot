using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The overlap check at a leg's end (#564): a finished leg whose plane changed a file another flight in the air changes, and that the planner
/// did not name to <c>touches</c>, rings the overlap card and holds the next leg; one that stayed to its planned files rings nothing.
/// </summary>
public sealed class LegEndOverlapTests : LegCommanderTestsBase
{
    private string Origin => Path.Combine(_root, "origin.git");

    public LegEndOverlapTests()
    {
        _legs.Models = new RoleModels(null);
    }

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
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(flight.Worktree, file), "class X {}\n");
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
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(plane, file), code + "\n");
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
    public async Task AFinishedLegThatChangedAFileAnotherFlightChangesRingsOneOverlapCardAndHoldsTheNextLeg()
    {
        await FlyingAsync("parser.cs", "shared.cs");
        OtherFlight("TIA2", "shared.cs", "elsewhere.cs");
        // The planner named only the parser; shared.cs was not foreseen.
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();

        await FinishParserAsync();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.OverlapKind);
        card.Subject.ShouldBe(Verbs.FlightTarget("TIA1"));
        card.Title.ShouldContain("shared.cs");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.ConnectOverlap, Verbs.Separate]);
        var row = _state.Flights().Single(f => f.Code == "TIA1").Overlaps.Single();
        (row.Other, row.Answer, row.AfterLeg).ShouldBe(("TIA2", OverlapAnswer.Pending, "parser"));
        row.Files.ShouldBe(["shared.cs"]);
        // The next leg is not started: no validator, no worker.
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));

        // Answered, the pair is not asked again for the same file.
        (await Leg(Verbs.Separate, "flight/TIA1", "TIA2")).Ok.ShouldBeTrue();
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("planner"));
    }

    [Fact]
    public async Task ConnectOnALegEndCardConnectsTheFlightHoldsTheNextLegAndStartsNoPlanner()
    {
        await FlyingAsync("parser.cs", "shared.cs");
        OtherFlight("TIA2", "shared.cs");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await FinishParserAsync();
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
    public async Task ParallelOnALegEndCardStartsNoPlannerLetsTheNextLegStartAndIsNotAskedAgainForTheSameFiles()
    {
        await FlyingAsync("parser.cs", "shared.cs");
        OtherFlight("TIA2", "shared.cs", "fresh.cs");
        (await Leg(Verbs.Touches, "flight/TIA1", "parser.cs")).Ok.ShouldBeTrue();
        await FinishParserAsync();

        (await Leg(Verbs.Separate, "flight/TIA1", "TIA2")).Ok.ShouldBeTrue();

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("planner"));
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");

        // A later leg end sharing only the answered file rings nothing; a new shared file rings again.
        await _legs.CheckOverlapAsync("TIA1", "render", Ct);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        var flight = _state.Flights().Single(f => f.Code == "TIA1");
        File.WriteAllText(Path.Combine(flight.Worktree, "fresh.cs"), "class Y {}\n");
        Git(flight.Worktree, "add", "fresh.cs");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fresh");
        await _legs.CheckOverlapAsync("TIA1", "render", Ct);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.OverlapKind);
        card.Title.ShouldContain("fresh.cs");
        card.Title.ShouldNotContain("shared.cs");
    }

    [Fact]
    public async Task ABaseMergePushRunsTheCheck()
    {
        await FlyingAsync("parser.cs", "shared.cs");
        OtherFlight("TIA2", "shared.cs");
        File.WriteAllText(Path.Combine(Repo, "newer.cs"), "class Z {}\n");
        Git(Repo, "add", "newer.cs");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "main moves");
        Git(Repo, "push", "-q", "origin", "main");

        foreach (var leg in _state.Legs("TIA1"))
        {
            _state.PutLeg(leg with { State = LegState.Done });
        }

        (await _legs.BringUpToDateAsync("TIA1", Ct)).ShouldBeNull();

        _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.OverlapKind).Title.ShouldContain("shared.cs");
    }

    [Fact]
    public async Task AFinishedLegThatStayedToItsPlannedFilesRingsNothing()
    {
        await FlyingAsync("parser.cs", "shared.cs");
        OtherFlight("TIA2", "shared.cs");
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
        await FlyingAsync("parser.cs", "shared.cs");
        OtherFlight("TIA2", "shared.cs");
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
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The leg is done and the check is parked on GitHub: the sweep must not start the next leg's validator.
        L("parser").State.ShouldBe(LegState.Done);
        await _legs.AdvanceFlightAsync("TIA1", Ct);
        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));

        release.SetResult();
        (await finishing).Ok.ShouldBeTrue();
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegCommander.OverlapKind);
        _workers.Started.Select(w => w.Name).ShouldNotContain(n => n.Contains("render"));
    }
}
