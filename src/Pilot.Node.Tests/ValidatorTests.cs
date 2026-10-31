using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A leg that has earlier legs is read against the tree by a validator before its worker starts; a first leg is not.</summary>
public sealed class ValidatorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2v-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    public ValidatorTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, new FakeChecks(), new FakeGitHub(), git, new Planes(git), _flights, options, TimeProvider.System)
        {
            Models = new RoleModels(null),
        };
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

    private Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private LegRow L(string name) => _state.Legs("TIA1").Single(l => l.Name == name);

    private const string Render = "flight/TIA1/render";

    /// <summary>A flight whose first leg, parser, is done, so render is the leg to start.</summary>
    private async Task FlightAfterItsFirstLeg()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse the ephemeris", "done-when=The parser tests pass")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw the planets", "done-when=The capture shows them", "item=Draw them")).Ok.ShouldBeTrue();
    }

    private async Task FinishParser()
    {
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        _state.PutLeg(L("parser") with { State = LegState.Done });
        _workers.Started.Clear();
    }

    private Task<CommandResult> Answer(string answer, params string[] report) =>
        Leg(Verbs.ValidateLeg, Render, [answer, .. report]);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFirstLegStartsItsWorkerWithNoValidator()
    {
        await FlightAfterItsFirstLeg();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-parser");
        L("parser").State.ShouldBe(LegState.Flying);
        L("parser").Validation.ShouldBe(LegValidation.None);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALaterLegStartsAValidatorOnTheWorkerModelAtLowEffortAndNoWorker()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();

        (await Leg(Verbs.StartLeg, Render)).Ok.ShouldBeTrue();

        var validator = _workers.Started.ShouldHaveSingleItem();
        validator.Name.ShouldBe("du1-tia1-render-validator");
        validator.Effort.ShouldBe("low");
        validator.Model.ShouldBe(_legs.Models.Model(RoleModels.Worker));
        validator.Brief.ShouldContain("validator of leg `render`");
        validator.Brief.ShouldContain("Draw the planets");
        validator.Brief.ShouldContain("- parser: Parse the ephemeris");
        validator.Brief.ShouldContain("leg validate no-go");
        L("render").State.ShouldBe(LegState.Planned);
        (L("render").Validation, L("render").Validator).ShouldBe((LegValidation.Pending, "du1-tia1-render-validator"));
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.ValidatingKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ValidateLegGoStartsTheWorker()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();
        await Leg(Verbs.StartLeg, Render);

        (await Answer("go", "types and files all there")).Ok.ShouldBeTrue();

        _workers.Started.Select(w => w.Name).ShouldBe(["du1-tia1-render-validator", "du1-tia1-render"]);
        (L("render").State, L("render").Validation, L("render").Worker).ShouldBe((LegState.Flying, LegValidation.Go, "du1-tia1-render"));
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.ValidatingKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ValidateLegNoGoRingsWithTheReportAndItsChoicesAndTheLegStaysPlanned()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();
        await Leg(Verbs.StartLeg, Render);

        (await Answer("no-go", "Planet.cs is gone: ls src/Planet.cs")).Ok.ShouldBeTrue();

        _workers.Started.Count(w => w.Name == "du1-tia1-render").ShouldBe(0);
        (L("render").State, L("render").Validation).ShouldBe((LegState.Planned, LegValidation.NoGo));
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.NoGoKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.Ring, Render));
        card.Detail.ShouldBe("Planet.cs is gone: ls src/Planet.cs");
        card.Choices.Select(c => (c.Label, c.Verb, c.Target)).ShouldBe(
        [
            ("Go anyway", Verbs.StartLeg, Render),
            ("Remove leg", Verbs.RemoveLeg, Render),
            ("Re-plan", Verbs.PlanFlight, "flight/TIA1"),
        ]);
        card.Choices[0].Args.ShouldBe(["anyway"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ValidateLegIsRefusedWhenNothingIsPendingAndFromTheNode()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();

        (await Answer("go")).Error.ShouldBe("TIA1/render is not waiting for a validation");

        await Leg(Verbs.StartLeg, Render);
        var fromNode = await _legs.RunAsync(new CommandRequest { Verb = Verbs.ValidateLeg, Target = Render, Args = ["go"], Who = "node" }, Ct);
        fromNode.Ok.ShouldBeFalse();
        L("render").Validation.ShouldBe(LegValidation.Pending);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task GoAnywayStartsTheWorkerWithNoSecondValidatorAndClosesTheRing()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();
        await Leg(Verbs.StartLeg, Render);
        await Answer("no-go", "stale");

        (await Leg(Verbs.StartLeg, Render, "anyway")).Ok.ShouldBeTrue();

        _workers.Started.Select(w => w.Name).ShouldBe(["du1-tia1-render-validator", "du1-tia1-render"]);
        L("render").State.ShouldBe(LegState.Flying);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.NoGoKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheSweepStartsNoSecondValidatorWhileOneIsPendingOrAfterANoGo()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();

        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-render-validator");

        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.Count.ShouldBe(1);

        await Answer("no-go", "stale");
        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.Count.ShouldBe(1);
        L("render").State.ShouldBe(LegState.Planned);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PlanningALegAgainValidatesItAfreshAndRemovingItClosesTheNoGoRing()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();
        await Leg(Verbs.StartLeg, Render);
        await Answer("no-go", "stale");

        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw the planets from Planet2", "done-when=The capture shows them")).Ok.ShouldBeTrue();

        L("render").Validation.ShouldBe(LegValidation.None);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.NoGoKind);

        (await Leg(Verbs.StartLeg, Render)).Ok.ShouldBeTrue();
        // A second validator, for the leg as planned again.
        _workers.Started.Count(w => w.Name == "du1-tia1-render-validator").ShouldBe(2);
        await Answer("no-go", "stale again");

        (await Leg(Verbs.RemoveLeg, Render, "failed validation")).Ok.ShouldBeTrue();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.NoGoKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASpentFiveHourWindowHoldsTheValidatorsStartAsItHoldsAWorkers()
    {
        await FlightAfterItsFirstLeg();
        await FinishParser();
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = 95, ResetsAt = DateTimeOffset.UtcNow.AddHours(1) } });

        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldBeEmpty();
        L("render").Validation.ShouldBe(LegValidation.None);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.NextStepKind && c.Title.StartsWith("TIA1 waits to start render: the 5-hour window is 95 % spent", StringComparison.Ordinal));

        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = 4, ResetsAt = DateTimeOffset.UtcNow.AddHours(6) } });
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-render-validator");
    }
}
