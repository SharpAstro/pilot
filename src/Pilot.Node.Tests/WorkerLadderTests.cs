using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A leg redone steps its fresh worker up the model ladder by attempt (PIL402): attempt 3 on opus, 4 on opus at xhigh.</summary>
public sealed class WorkerLadderTests : LegCommanderTestsBase
{
    private static NodeSettings Set(NodeSettings settings, string key, params string[] values) =>
        NodeSettings.With(settings, key, values).Settings.ShouldNotBeNull();

    private static RoleModels Models(params (string Key, string Value)[] settings) =>
        new(settings.Aggregate(new NodeSettings(), (s, kv) => Set(s, kv.Key, kv.Value)));

    private async Task GoAround(int times)
    {
        for (var i = 0; i < times; i++)
        {
            (await Leg(Verbs.GoAround, "flight/TIA1/parser", "again")).Ok.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(1, "sonnet", null)]
    [InlineData(2, "sonnet", null)]
    [InlineData(3, "opus", null)]
    [InlineData(4, "opus", "xhigh")]
    [InlineData(7, "opus", "xhigh")]
    public void TheLadderStepsByAttempt(int attempt, string model, string? effort) =>
        Models().WorkerFor(null, null, attempt).ShouldBe((model, effort));

    [Fact]
    public void ALegPlannedOnOpusAtHighStepsToXhighAndNeverDown()
    {
        var models = Models();
        models.WorkerFor("opus", "high", 1).ShouldBe(("opus", "high"));
        models.WorkerFor("opus", "high", 3).ShouldBe(("opus", "high"));
        models.WorkerFor("opus", "high", 4).ShouldBe(("opus", "xhigh"));
        models.WorkerFor("opus", "max", 4).ShouldBe(("opus", "max"));
        models.WorkerFor("opus", null, 3).ShouldBe(("opus", null));
        models.WorkerFor("claude-opus-5-5[1m]", "low", 3).ShouldBe(("claude-opus-5-5[1m]", "low"));
        // A model the ladder cannot rank stays as planned; an unrankable max is no cap.
        models.WorkerFor("fable", "high", 3).ShouldBe(("fable", "high"));
        Models(("worker-model-max", "fable")).WorkerFor(null, null, 3).ShouldBe(("opus", null));
    }

    [Fact]
    public void TheMaxSettingsCapTheLadder()
    {
        Models(("worker-model-max", "sonnet")).WorkerFor(null, null, 3).ShouldBe(("sonnet", null));
        Models(("worker-model-max", "sonnet")).WorkerFor("opus", null, 3).ShouldBe(("opus", null));
        Models(("worker-effort-max", "high")).WorkerFor(null, null, 4).ShouldBe(("opus", "high"));
        Models(("worker-effort-max", "high")).WorkerFor(null, "max", 4).ShouldBe(("opus", "max"));
    }

    [Fact]
    public void ALadderSwitchedOffKeepsTheLegsModel()
    {
        var off = Models(("worker-ladder", "off"));
        off.WorkerFor("sonnet", "high", 4).ShouldBe(("sonnet", "high"));
        off.WorkerFor(null, null, 3).ShouldBe(("sonnet", null));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondGoAroundStartsOpusAndAThirdOpusAtXhighAndTheRowRecordsIt()
    {
        _legs.Models = new RoleModels(null);
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        (_workers.Started[^1].Model, L("parser").AttemptModel).ShouldBe(("sonnet", "sonnet"));

        await GoAround(1);
        _workers.Started[^1].Model.ShouldBe("sonnet");

        await GoAround(1);
        (_workers.Started[^1].Model, _workers.Started[^1].Effort).ShouldBe(("opus", null));
        (L("parser").AttemptModel, L("parser").AttemptEffort, L("parser").GoArounds).ShouldBe(("opus", null, 2));

        await GoAround(1);
        (_workers.Started[^1].Model, _workers.Started[^1].Effort).ShouldBe(("opus", "xhigh"));
        (L("parser").AttemptModel, L("parser").AttemptEffort).ShouldBe(("opus", "xhigh"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALegPlannedOnOpusAtHighStepsToXhighOnTheFourthAttempt()
    {
        _legs.Models = new RoleModels(null);
        await FlightWithTwoLegs();
        _state.PutLeg(L("parser") with { Model = "opus", Effort = "high" });
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        await GoAround(2);
        (_workers.Started[^1].Model, _workers.Started[^1].Effort).ShouldBe(("opus", "high"));
        await GoAround(1);
        (_workers.Started[^1].Model, _workers.Started[^1].Effort).ShouldBe(("opus", "xhigh"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheMaxSettingAndTheSwitchHoldTheLadderOnALiveLeg()
    {
        _legs.Models = Models(("worker-model-max", "sonnet"));
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        await GoAround(2);
        _workers.Started[^1].Model.ShouldBe("sonnet");

        _legs.Models = Models(("worker-ladder", "off"));
        await GoAround(1);
        (_workers.Started[^1].Model, L("parser").AttemptModel).ShouldBe(("sonnet", "sonnet"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANudgeOrResumeOfAnAttemptThreeSessionStartsNoWorkerAndKeepsItsModel()
    {
        _legs.Models = new RoleModels(null);
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        await GoAround(2);
        var started = _workers.Started.Count;
        var job = L("parser").WorkerJob;

        // A nudge or Resume is a Say to the live session: it never reaches a worker start.
        await Leg(Verbs.Say, Verbs.SessionTarget(L("parser").Worker!), "carry on");

        _workers.Started.Count.ShouldBe(started);
        (L("parser").WorkerJob, L("parser").AttemptModel).ShouldBe((job, "opus"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedLegOffersARedoNamingTheNextRedoAndItsModel()
    {
        _legs.Models = new RoleModels(null);
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        await GoAround(1);
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "stuck")).Ok.ShouldBeTrue();
        _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed").Choices.Single(c => c.Verb == Verbs.GoAround).Label.ShouldBe("Redo leg parser (redo 2, on Opus)");

        await GoAround(1);
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "stuck again")).Ok.ShouldBeTrue();
        _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed").Choices.Single(c => c.Verb == Verbs.GoAround).Label.ShouldBe("Redo leg parser (redo 3, on Opus at xhigh)");
    }

    [Theory]
    [InlineData(0, "Redo leg parser")]
    [InlineData(1, "Redo leg parser (redo 2, on Opus)")]
    [InlineData(2, "Redo leg parser (redo 3, on Opus at xhigh)")]
    public void TheRedoLabelNamesTheNextAttemptOnceItIsASecondRedoOrLater(int goArounds, string label) =>
        Models().RedoLabel(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, GoArounds = goArounds }).ShouldBe(label);
}
