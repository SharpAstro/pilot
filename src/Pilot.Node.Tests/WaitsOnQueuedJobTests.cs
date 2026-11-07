using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #848: a worker that waits on the node, and that the node already took over once (its finish was refused, so the leg still
/// flies), no longer rings "waits on you" on the second look while a job that wakes it is queued or running: its leg's check, or a job of
/// its own. On 2026-11-07 three such rings came while the queue was jammed, and each was dismissed; a stuck queue is the queue-jam card's
/// to ring. Set up as the c17099 tests of <see cref="FalseRingTests"/> are: a real flight and plane, the node's own commander, a manual clock.
/// </summary>
public sealed class WaitsOnQueuedJobTests : ReviewTestsBase
{
    /// <summary>What the three dismissed rings said, near enough: no question, and the node holds the next move.</summary>
    private const string WaitsOnTheCheck = "Leg is finished and fully committed; it waits only on the node's check";

    private readonly ManualTime _watchTime = new(DateTimeOffset.UtcNow);
    private readonly List<CommandRequest> _said = [];

    private LegWatch Watch() =>
        new(_state, (command, token) =>
        {
            if (command.Verb != Verbs.Say)
            {
                return _legs.RunAsync(command, token);
            }
            _said.Add(command);
            return Task.FromResult(_state.Complete(command, null));
        }, _watchTime) { Pilot = "pilot" };

    /// <summary>A leg started by the node, a commit made and its step ticked, and a file left uncommitted: the node's finish for it is refused.</summary>
    private async Task<(FlightRow Flight, LegRow Leg)> FlyingLegThatCannotFinish(string issue)
    {
        var flight = await NewFlight(issue, "render");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "render"), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        File.WriteAllText(Path.Combine(flight.Worktree, $"render-{Guid.NewGuid():N}.txt"), "work\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "render work");
        var leg = _state.Legs(flight.Code).Single(l => l.Name == "render");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "render"), leg.Worker!, "1")).Ok.ShouldBeTrue();
        File.WriteAllText(Path.Combine(flight.Worktree, "left-over.txt"), "not committed\n");
        return (flight, _state.Legs(flight.Code).Single(l => l.Name == "render"));
    }

    private void WorkerIs(LegRow leg, string waitingFor) =>
        _state.PutSession(new SessionRow
        {
            Id = leg.WorkerJob!, SessionId = leg.WorkerJob + "-0000-4000-8000-000000000000", Name = leg.Worker!, Kind = SessionKind.Background, Cwd = "/work",
            Role = SessionRole.Worker, Flight = leg.Flight, Leg = leg.Name, ContextTokens = 1000, ContextWindow = 200_000, State = SessionState.Waiting, WaitingFor = waitingFor, SpendWeighted = 0,
            LastResponseAt = _watchTime.GetUtcNow(), StartedAt = _watchTime.GetUtcNow(),
        });

    private async Task Look(LegWatch watch)
    {
        _watchTime.Advance(TimeSpan.FromMinutes(5));
        await watch.LookAsync(Ct);
    }

    /// <summary>The leg's own check, as the node queues or runs it: the leg's flight and leg, and no owner.</summary>
    private JobRow CheckJobOf(FlightRow flight, JobState state, string id = "j9001") => new()
    {
        Id = id, Kind = JobKind.Check, Command = "dotnet test", Cwd = flight.Worktree, Flight = flight.Code, Leg = "render", State = state,
        LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(30), StartedAt = _watchTime.GetUtcNow(),
        QueuedAt = state == JobState.Queued ? _watchTime.GetUtcNow() : null,
    };

    /// <summary>A job the worker handed over itself (<c>pilot run</c>): owned by its row, with no flight or leg of its own on the row.</summary>
    private JobRow OwnJobOf(LegRow leg, JobState state, string id = "j9002") => new()
    {
        Id = id, Command = "dotnet build Pilot.slnx -c Release", Cwd = "/work", Owner = leg.WorkerJob, OwnerName = leg.Worker, State = state,
        LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(30), StartedAt = _watchTime.GetUtcNow(),
        QueuedAt = state == JobState.Queued ? _watchTime.GetUtcNow() : null,
    };

    private JobRow Ended(JobRow job) => job with { State = JobState.Succeeded, ExitCode = 0, EndedAt = _watchTime.GetUtcNow() };

    private bool Rings() => Open().Any(c => c.Kind == LegWatch.Waiting);

    /// <summary>The node's first look: its finish is tried and refused (the plane holds a file not committed), the worker is told, no card.</summary>
    private async Task<LegWatch> FirstLookTriesTheFinish(LegRow leg, FlightRow flight, string waitingFor = WaitsOnTheCheck)
    {
        WorkerIs(leg, waitingFor);
        var watch = Watch();
        await Look(watch);
        Rings().ShouldBeFalse();
        _said.ShouldNotBeEmpty();
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Flying);
        return watch;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondLookWhileTheLegsCheckJobIsQueuedRaisesNoCard_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84801");
        var watch = await FirstLookTriesTheFinish(leg, flight);
        _state.PutJob(CheckJobOf(flight, JobState.Queued));

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await Look(watch);
        }

        Rings().ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
        // Nothing finished the leg or failed it: the check's end wakes the worker.
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondLookWhileTheLegsCheckJobRunsRaisesNoCard_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84802");
        var watch = await FirstLookTriesTheFinish(leg, flight);
        _state.PutJob(CheckJobOf(flight, JobState.Running));

        await Look(watch);

        Rings().ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondLookWhileAJobOfTheWorkersOwnRunsRaisesNoCard_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84803");
        var watch = await FirstLookTriesTheFinish(leg, flight);
        _state.PutJob(OwnJobOf(leg, JobState.Running));

        await Look(watch);

        Rings().ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondLookWhileAJobOfTheWorkersOwnIsQueuedRaisesNoCard_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84804");
        var watch = await FirstLookTriesTheFinish(leg, flight);
        _state.PutJob(OwnJobOf(leg, JobState.Queued));

        await Look(watch);

        Rings().ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnceTheJobHasEndedTheSecondLookRingsAsBefore_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84805");
        var watch = await FirstLookTriesTheFinish(leg, flight);
        var check = CheckJobOf(flight, JobState.Queued);
        _state.PutJob(check);
        await Look(watch);
        Rings().ShouldBeFalse();

        _state.PutJob(Ended(check));
        await Look(watch);

        var card = Open().Single(c => c.Kind == LegWatch.Waiting);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("waits on you");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnceAJobOfTheWorkersOwnHasEndedTheSecondLookRingsAsBefore_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84806");
        var watch = await FirstLookTriesTheFinish(leg, flight);
        var own = OwnJobOf(leg, JobState.Running);
        _state.PutJob(own);
        await Look(watch);
        Rings().ShouldBeFalse();

        _state.PutJob(Ended(own));
        await Look(watch);

        Open().Single(c => c.Kind == LegWatch.Waiting).Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobOfAnotherLegAndAnotherOwnerDoesNotStandTheRingDown_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84807");
        var watch = await FirstLookTriesTheFinish(leg, flight);
        // Queued ahead of everything, but nobody here waits on it: another flight's check, and a job some other session owns.
        _state.PutJob(CheckJobOf(flight, JobState.Queued, "j9003") with { Flight = "OTHER1", Leg = "elsewhere" });
        _state.PutJob(OwnJobOf(leg, JobState.Running, "j9004") with { Owner = "someone-else", OwnerName = "another-session" });

        await Look(watch);

        Open().Single(c => c.Kind == LegWatch.Waiting).Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AQuestionToThePersonStillRingsAtOnceWhateverItsJobs_PIL848()
    {
        var (flight, leg) = await FlyingLegThatCannotFinish("84808");
        WorkerIs(leg, "Should I delete the old render path, or keep it behind a flag?");
        _state.PutJob(CheckJobOf(flight, JobState.Queued));
        _state.PutJob(OwnJobOf(leg, JobState.Running));

        await Look(Watch());

        var card = Open().Single(c => c.Kind == LegWatch.Waiting);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("Should I delete the old render path");
    }
}
