using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// GitHub's Priority issue field in admission (#570): where the repo has it, it alone says priority and orders the queue; where it has
/// none the <c>pilot: priority</c> label stands in; a label found beside the field is moved onto it once and removed.
/// </summary>
public sealed class IssuePriorityTests : LegCommanderTestsBase
{
    private void Settings(int cap, bool oneFlightPerRepo = false)
    {
        var settings = NodeSettings.With(new NodeSettings(), "opus-cap", [cap.ToString()]).Settings!;
        settings = NodeSettings.With(settings, "worker-model", ["opus"]).Settings!;
        settings = NodeSettings.With(settings, "one-flight-per-repo", [oneFlightPerRepo ? "on" : "off"]).Settings!;
        _legs.Models = new RoleModels(settings);
    }

    private void OtherOpus(string name) =>
        _state.PutSession(new SessionRow
        {
            Id = name[^8..], SessionId = name[^8..] + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/elsewhere",
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

    private async Task Flight(int issue, string leg)
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue.ToString(), "Work"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, $"flight/TIA{issue}", $"name={leg}", "goal=Do it", "done-when=It is done")).Ok.ShouldBeTrue();
    }

    private LegRow Leg1(string code, string name) => _state.Legs(code).Single(l => l.Name == name);

    private FlightRow Row(string code) => _state.Flights().Single(f => f.Code == code);

    private async Task Start(int issue, string leg) => (await Leg(Verbs.StartLeg, Verbs.LegTarget($"TIA{issue}", leg))).Ok.ShouldBeTrue();

    [Fact]
    public async Task AHighIssueIsAdmittedOverTheOpusCapAndAnUnsetOneWaits()
    {
        Settings(cap: 1);
        _github.HasPriorityField = true;
        _github.IssuePriorities[1] = IssuePriority.High;
        await Flight(1, "one");
        await Flight(2, "two");
        OtherOpus("du1-other-worker");

        await Start(1, "one");
        await Start(2, "two");

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        Leg1("TIA1", "one").Queued.ShouldBeNull();
        Leg1("TIA2", "two").Queued.ShouldNotBeNull().ShouldStartWith("waiting for an Opus slot");
        Row("TIA1").IssuePriority.ShouldBe(IssuePriority.High);
        Row("TIA1").HasPriorityField.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUrgentIssueIsAdmittedOverTheCapAndALowOrMediumOneIsNot()
    {
        Settings(cap: 1);
        _github.HasPriorityField = true;
        _github.IssuePriorities[1] = IssuePriority.Urgent;
        _github.IssuePriorities[2] = IssuePriority.Medium;
        await Flight(1, "one");
        await Flight(2, "two");
        OtherOpus("du1-other-worker");

        await Start(2, "two");
        await Start(1, "one");

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        Leg1("TIA2", "two").Queued.ShouldNotBeNull();
    }

    [Fact]
    public void AQueueIsOrderedUrgentHighMediumUnsetLowAndOldestFirstWithinEach()
    {
        var at = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        FleetFlight F(string code, IssuePriority? level, int minutes) => new("DU1", code, "tianwen", FlightPhase.Ready) { Level = level, Since = at.AddMinutes(minutes) };
        var input = new AdmissionInput([],
            [F("LOW1", IssuePriority.Low, 0), F("NONE1", null, 1), F("MED1", IssuePriority.Medium, 2), F("HIGH2", IssuePriority.High, 4), F("URG1", IssuePriority.Urgent, 5),
             F("HIGH1", IssuePriority.High, 3), F("NONE0", null, 0)], 0, false);

        var order = Admission.Order(input, "DU1", ["LOW1", "NONE1", "MED1", "HIGH2", "URG1", "HIGH1", "NONE0"]);

        order.ShouldBe(["URG1", "HIGH1", "HIGH2", "MED1", "NONE0", "NONE1", "LOW1"]);
    }

    [Fact]
    public void TheQueueTheClientsShowIsOrderedByLevelThenAge()
    {
        var at = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        FlightRow Row(string code, IssuePriority? level, int minutes) => new()
        {
            Code = code, Repo = "tianwen", RepoPath = "/r", Branch = "flight/" + code, Title = code, CreatedAt = at.AddMinutes(minutes), Queued = "waiting",
            HasPriorityField = true, IssuePriority = level,
        };

        var queue = QueuedFlight.Of([Row("A", IssuePriority.Low, 0), Row("B", null, 1), Row("C", IssuePriority.High, 5), Row("D", IssuePriority.Urgent, 9)], [], new AdmissionRow { Priority = ["C", "D"] });

        queue.Select(q => q.Code).ShouldBe(["D", "C", "B", "A"]);
        queue[0].Level.ShouldBe(IssuePriority.Urgent);
    }

    [Fact]
    public async Task ARepoWithTheFieldIgnoresAStrayLabelAndTheLabelIsTakenOff()
    {
        Settings(cap: 1);
        _github.HasPriorityField = true;
        _github.IssuePriorities[1] = IssuePriority.Low;
        _github.IssueLabels[1] = [PilotMarks.Priority];
        await Flight(1, "one");
        OtherOpus("du1-other-worker");

        await Start(1, "one");

        _workers.Started.ShouldBeEmpty();
        Leg1("TIA1", "one").Queued.ShouldNotBeNull().ShouldStartWith("waiting for an Opus slot");
        _github.IssuePriorities[1].ShouldBe(IssuePriority.Low);
        _github.IssueLabels[1].ShouldNotContain(PilotMarks.Priority);
    }

    [Fact]
    public async Task ARepoWithoutTheFieldUsesTheLabelAsBeforeWithNoError()
    {
        Settings(cap: 1);
        _github.HasPriorityField = false;
        _github.IssueLabels[1] = [PilotMarks.Priority];
        await Flight(1, "one");
        await Flight(2, "two");
        OtherOpus("du1-other-worker");

        await Start(1, "one");
        await Start(2, "two");

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        Leg1("TIA2", "two").Queued.ShouldNotBeNull();
        _github.PrioritySets.ShouldBeEmpty();
        _github.IssueLabels[1].ShouldContain(PilotMarks.Priority);
        Row("TIA1").HasPriorityField.ShouldBeFalse();
        _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheMigrationSetsAnEmptyFieldToHighRemovesTheLabelAndDoesItOnce()
    {
        Settings(cap: 0);
        _github.HasPriorityField = true;
        _github.IssueLabels[1] = [PilotMarks.Priority];
        await Flight(1, "one");

        await Start(1, "one");

        _github.PrioritySets.ShouldHaveSingleItem().ShouldBe((1, (IssuePriority?)IssuePriority.High));
        _github.IssuePriorities[1].ShouldBe(IssuePriority.High);
        _github.IssueLabels[1].ShouldNotContain(PilotMarks.Priority);
        Row("TIA1").IssuePriority.ShouldBe(IssuePriority.High);

        // Asked again: the label is gone, so nothing is written a second time.
        await _legs.AdvanceAllAsync(Ct);
        await Flight(2, "two");
        await Start(2, "two");
        _github.PrioritySets.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheMigrationKeepsTheFieldsOwnValueAndStillRemovesTheLabel()
    {
        Settings(cap: 0);
        _github.HasPriorityField = true;
        _github.IssuePriorities[1] = IssuePriority.Medium;
        _github.IssueLabels[1] = [PilotMarks.Priority];
        await Flight(1, "one");

        await Start(1, "one");

        _github.PrioritySets.ShouldBeEmpty();
        _github.IssuePriorities[1].ShouldBe(IssuePriority.Medium);
        _github.IssueLabels[1].ShouldNotContain(PilotMarks.Priority);
        Row("TIA1").IssuePriority.ShouldBe(IssuePriority.Medium);
    }

    [Fact]
    public async Task AMigrationGitHubRefusesLeavesTheRowAsItWasAndTheStartGoesOn()
    {
        Settings(cap: 0);
        _github.HasPriorityField = true;
        _github.IssueLabels[1] = [PilotMarks.Priority];
        _github.PrioritySetFails = "forbidden";
        await Flight(1, "one");

        await Start(1, "one");

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        Row("TIA1").IssuePriority.ShouldBeNull();
        _github.IssueLabels[1].ShouldContain(PilotMarks.Priority);
    }
}
