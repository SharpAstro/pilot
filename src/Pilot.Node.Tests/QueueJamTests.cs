using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A queue that has stopped moving rings one card (pilot #848): jobs wait, the oldest past the limit, and none has started in that
/// time. The reader works from the job rows alone, so these tests build rows for it, and drive a runner with a fake clock for the card.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class QueueJamTests : JobTestsBase
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan After = TimeSpan.FromMinutes(30);

    private JobRow Row(string id, JobState state, DateTimeOffset startedAt, DateTimeOffset? queuedAt = null, string command = "dotnet build Pilot.slnx", string? owner = "se1-tia1-parser", int? pid = null, string? step = null, JobKind kind = JobKind.Run) => new()
    {
        Id = id, Command = command, Cwd = _root, OwnerName = owner, Owner = owner is null ? null : "a1b2c3d4", Kind = kind, State = state, Step = step,
        LogPath = Path.Combine(_root, id + ".log"), Timeout = JobRunner.DefaultTimeout, StartedAt = startedAt, QueuedAt = queuedAt, Pid = pid,
    };

    private JobRow Running(string id, DateTimeOffset startedAt, string command = "dotnet build Pilot.slnx", string? owner = "se1-tia1-parser") =>
        Row(id, JobState.Running, startedAt, command: command, owner: owner, pid: 4000);

    private JobRow Waiting(string id, DateTimeOffset since, string? step = null, JobKind kind = JobKind.Run) =>
        Row(id, JobState.Queued, since, queuedAt: since, step: step, kind: kind);

    /// <summary>The clones a job's build compiles, fixed: a sleeping holder and a job that says <c>sib</c> share two of them.</summary>
    private IReadOnlyCollection<string> Clones(JobRow job)
    {
        if (job.Command.StartsWith("sleep", StringComparison.Ordinal) || job.Command.Contains("'sib'", StringComparison.Ordinal))
        {
            return [Path.Combine(_root, "DIR.Lib"), Path.Combine(_root, "Fonts.Lib")];
        }
        return [];
    }

    private static IReadOnlyCollection<string> NoClones(JobRow job) => [];

    private static void NoBadDashes(QueueJam.Reading jam)
    {
        foreach (var text in new[] { jam.Title, jam.Detail })
        {
            text.ShouldNotContain("—");
            text.ShouldNotContain("--");
        }
    }

    private static void Look(JobRunner jobs) => jobs.Dispatch(JobRunner.LookAgain.Instance);

    private CardRow[] JamCards(bool openOnly = true) => [.. _state.Cards(openOnly).Where(c => c.Kind == JobRunner.QueueJammedKind)];

    private string HandBench(JobRunner jobs, string command)
    {
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [Verbs.AloneArg, _root, "0", command], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    /// <summary>A run handed over by no session: it keeps no owner's queue order, so it may start ahead of a held job of the session's.</summary>
    private string HandFromNode(JobRunner jobs, string command)
    {
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.NodeTarget, Args = [_root, "0", command], Who = "controller" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    // The reader.

    [Fact]
    public void TheIncidentReadsAsOneJamNamingTheHolderItsClonesTheCountAndTheOldestWait()
    {
        var rows = new List<JobRow> { Running("j4131", T0, "dotnet build TianWen.slnx -c Release --no-build", owner: "se1-tia9-build") };
        for (var i = 0; i < 19; i++)
        {
            rows.Add(Waiting($"j{4132 + i}", T0.AddMinutes(i), i == 0
                ? $"{QueueJam.NextInLine}waiting for job j4131's build of the sibling clones DIR.Lib, Fonts.Lib (se1-tia9-build)"
                : $"queued, {i} ahead of it; another job is running"));
        }

        var jam = QueueJam.Read(rows, 1, T0.AddMinutes(155), After, Clones).ShouldNotBeNull();

        jam.Title.ShouldBe("19 jobs have waited up to 2 h 35 m; the queue has not moved since j4131 started");
        jam.Holder!.Id.ShouldBe("j4131");
        jam.Detail.ShouldContain("j4131 holds the queue (se1-tia9-build, `dotnet build TianWen.slnx`, held for 2 h 35 m)");
        jam.Detail.ShouldContain("It holds 2 sibling clones (DIR.Lib, Fonts.Lib) and fills the node's only slot");
        jam.Detail.ShouldContain("The oldest waiting job is j4132 (se1-tia1-parser), `dotnet build Pilot.slnx`");
        jam.Detail.ShouldContain("Next in line is j4132 (se1-tia1-parser), which waits: waiting for job j4131's build of the sibling clones DIR.Lib, Fonts.Lib (se1-tia9-build)");
        NoBadDashes(jam);
    }

    [Fact]
    public void AQueueThatStartedAJobWithinTheLimitIsNotJammedHoweverLongOneJobWaits()
    {
        var rows = new[] { Running("j2", T0.AddMinutes(140)), Waiting("j3", T0), Waiting("j4", T0.AddMinutes(1)) };

        QueueJam.Read(rows, 1, T0.AddMinutes(155), After, NoClones).ShouldBeNull();
        // Past the limit since that start, the same rows are a jam.
        QueueJam.Read(rows, 1, T0.AddMinutes(171), After, NoClones).ShouldNotBeNull();
    }

    [Fact]
    public void AJobThatEndedAfterRunningCountsAsAStartButOneCancelledBeforeItsTurnDoesNot()
    {
        var ended = Row("j2", JobState.Succeeded, T0.AddMinutes(150), pid: 4001);
        var dropped = Row("j9", JobState.Cancelled, T0.AddMinutes(150), queuedAt: T0.AddMinutes(150));
        var now = T0.AddMinutes(160);

        QueueJam.Read([ended, Waiting("j3", T0)], 1, now, After, NoClones).ShouldBeNull();
        // The cancelled row's StartedAt is its hand-over time and no process ever ran: it moved nothing.
        var jam = QueueJam.Read([dropped, Running("j1", T0.AddMinutes(-60)), Waiting("j3", T0)], 1, now, After, NoClones).ShouldNotBeNull();
        jam.Title.ShouldContain("since j1 started");
        QueueJam.LastStart([dropped, ended]).ShouldBe(ended);
        QueueJam.LastStart([dropped]).ShouldBeNull();
    }

    [Fact]
    public void AnEmptyQueueAndAYoungQueueAreNotJammed()
    {
        QueueJam.Read([Running("j1", T0)], 1, T0.AddHours(5), After, NoClones).ShouldBeNull();
        QueueJam.Read([Running("j1", T0), Waiting("j2", T0.AddMinutes(40))], 1, T0.AddMinutes(60), After, NoClones).ShouldBeNull();
        // Exactly at the limit is not past it.
        QueueJam.Read([Running("j1", T0), Waiting("j2", T0)], 1, T0.Add(After), After, NoClones).ShouldBeNull();
        QueueJam.Read([Running("j1", T0), Waiting("j2", T0)], 1, T0.Add(After).AddSeconds(1), After, NoClones).ShouldNotBeNull();
    }

    [Fact]
    public void AJobRunningWithoutClonesIsNamedByTheSlotItFills()
    {
        var rows = new[] { Running("j1", T0), Running("j2", T0.AddMinutes(1)), Waiting("j3", T0.AddMinutes(2), $"{QueueJam.NextInLine}2 jobs are running, 2 at a time; needs about 3 GB") };

        var jam = QueueJam.Read(rows, 2, T0.AddMinutes(100), After, NoClones).ShouldNotBeNull();

        jam.Holder!.Id.ShouldBe("j1");
        jam.Title.ShouldStartWith("1 job has waited 1 h 38 m;");
        jam.Detail.ShouldContain("It fills 2 of the node's 2 slots");
        jam.Detail.ShouldNotContain("sibling clone");
        jam.Detail.ShouldContain("Next in line is j3 (se1-tia1-parser), which waits: 2 jobs are running, 2 at a time; needs about 3 GB");
    }

    [Fact]
    public void WithNothingRunningTheCardSaysTheSlotsSitIdleAndThereIsNoHolderToCancel()
    {
        var rows = new[] { Row("j1", JobState.Succeeded, T0, pid: 4001), Waiting("j2", T0.AddMinutes(5), $"{QueueJam.NextInLine}waiting for 4 GB of free memory") };

        var one = QueueJam.Read(rows, 1, T0.AddMinutes(90), After, NoClones).ShouldNotBeNull();
        var two = QueueJam.Read(rows, 2, T0.AddMinutes(90), After, NoClones).ShouldNotBeNull();

        one.Holder.ShouldBeNull();
        one.Title.ShouldBe("1 job has waited 1 h 25 m; the queue has not moved since j1 started");
        one.Detail.ShouldContain("No job is running, so the node's only slot sits idle");
        two.Detail.ShouldContain("No job is running, so the node's 2 slots sit idle");
        one.Detail.ShouldContain("which waits: waiting for 4 GB of free memory");
        NoBadDashes(one);
    }

    [Fact]
    public void AJobThatNeverStartedAtAllIsSaidSo()
    {
        var jam = QueueJam.Read([Waiting("j2", T0)], 1, T0.AddHours(1), After, NoClones).ShouldNotBeNull();

        jam.Title.ShouldBe("1 job has waited 1 h 0 m; no job has started");
    }

    [Fact]
    public void ABenchAtTheHeadWithNothingRunningIsLeftToTheBenchBusyCardButOneWaitingOnARunningJobIsAJam()
    {
        var head = Waiting("j2", T0, $"{QueueJam.NextInLine}a benchmark waits for a quiet CPU (95% now, 25% at most)", JobKind.Bench);
        var later = Waiting("j3", T0.AddMinutes(1), "queued, 1 ahead of it; a benchmark (j2) holds the node");
        var done = Row("j1", JobState.Succeeded, T0.AddMinutes(-10), pid: 4001);

        QueueJam.Read([done, head, later], 1, T0.AddMinutes(90), After, NoClones).ShouldBeNull();
        QueueJam.Read([done, Running("j0", T0.AddMinutes(-20)), head, later], 1, T0.AddMinutes(90), After, NoClones).ShouldNotBeNull();
    }

    [Fact]
    public void APausedHolderIsSaidPausedAndStillFillsItsSlot()
    {
        var holder = Running("j1", T0) with { PausedAt = T0.AddMinutes(5) };

        var jam = QueueJam.Read([holder, Waiting("j2", T0)], 1, T0.AddMinutes(45), After, NoClones).ShouldNotBeNull();

        jam.Detail.ShouldContain("held for 45 m, paused)");
        jam.Detail.ShouldContain("It fills the node's only slot");
    }

    [Fact]
    public void NoTextHoldsAnEmDashOrADoubleHyphenWhateverACommandOrAStepCarried()
    {
        var holder = Running("j1", T0, "dotnet test --no-build", owner: "se1--odd");
        var waiting = Waiting("j2", T0, $"{QueueJam.NextInLine}waits — see --verbose");

        var jam = QueueJam.Read([holder, waiting], 1, T0.AddMinutes(45), After, NoClones).ShouldNotBeNull();

        NoBadDashes(jam);
        jam.Detail.ShouldContain("which waits: waits, see -verbose");
        // The flags of a command are not quoted: its program and target name the job.
        jam.Detail.ShouldNotContain("no-build");
    }

    [Fact]
    public void CalmSaysWhyTheJamIsOverTheQueueIsEmptyOrAJobStarted()
    {
        QueueJam.Calm([Running("j1", T0)], T0.AddHours(3), After).ShouldBe("the queue is empty");
        QueueJam.Calm([Running("j9", T0.AddMinutes(150)), Waiting("j10", T0)], T0.AddMinutes(155), After).ShouldBe("the queue moved: j9 started");
        QueueJam.Calm([Running("j1", T0), Waiting("j10", T0.AddMinutes(100))], T0.AddMinutes(120), After).ShouldContain("not jammed");
    }

    // The card.

    [Fact]
    public async Task AJammedQueueRingsOneCardNamingTheHolderItsClonesTheCountAndTheOldestWait()
    {
        var (jobs, clock, _, gone) = Idle();
        jobs.SiblingsOf = Clones;
        var holder = Hand(jobs, Sleep(30));
        var first = Hand(jobs, Echo("a"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Hand(jobs, Echo("b"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Hand(jobs, Echo("c"));

        // The wait is 29 minutes: under the limit.
        clock.Advance(TimeSpan.FromMinutes(27));
        Look(jobs);
        JamCards(openOnly: false).ShouldBeEmpty();

        clock.Advance(TimeSpan.FromMinutes(2));
        Look(jobs);
        Look(jobs);
        var card = JamCards().ShouldHaveSingleItem();
        card.Kind.ShouldBe(JobRunner.QueueJammedKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.Ring, JobRunner.QueueJamSubject));
        card.Title.ShouldBe($"3 jobs have waited up to 31 m; the queue has not moved since {holder} started");
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain($"{holder} holds the queue (se1-tia1-parser, ");
        detail.ShouldContain("held for 31 m)");
        detail.ShouldContain("It holds 2 sibling clones (DIR.Lib, Fonts.Lib) and fills the node's only slot");
        detail.ShouldContain($"The oldest waiting job is {first} (se1-tia1-parser)");
        detail.ShouldContain($"Next in line is {first} (se1-tia1-parser), which waits: another job is running");
        var choice = card.Choices.ShouldHaveSingleItem();
        (choice.Verb, choice.Target).ShouldBe((Verbs.CancelJob, Verbs.JobTarget(holder)));
        choice.Label.ShouldContain(holder);
        card.Summary.ShouldNotBeNullOrWhiteSpace();
        (card.Title + detail).ShouldNotContain("--");
        (card.Title + detail).ShouldNotContain("—");

        await gone(holder);
    }

    [Fact]
    public async Task TheCardsTextFollowsTheCountsOnLaterLooksWithNoSecondCardAndItClosesWhenAJobStarts()
    {
        var (jobs, clock, _, gone) = Idle();
        jobs.SiblingsOf = Clones;
        var holder = Hand(jobs, Sleep(30));
        var first = Hand(jobs, Echo("a"));
        clock.Advance(TimeSpan.FromMinutes(31));
        Look(jobs);
        var card = JamCards().ShouldHaveSingleItem();
        card.Title.ShouldStartWith("1 job has waited 31 m;");

        clock.Advance(TimeSpan.FromMinutes(10));
        Hand(jobs, Echo("b"));
        Look(jobs);
        var later = JamCards(openOnly: false).ShouldHaveSingleItem();
        later.Id.ShouldBe(card.Id);
        later.State.ShouldBe(CardState.Open);
        later.Title.ShouldBe($"2 jobs have waited up to 41 m; the queue has not moved since {holder} started");
        later.At.ShouldBe(card.At);

        // The holder ends and the head starts: the queue moved.
        await gone(holder);
        Look(jobs);
        JamCards().ShouldBeEmpty();
        var closed = JamCards(openOnly: false).ShouldHaveSingleItem();
        closed.Id.ShouldBe(card.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldBe($"the queue moved: {first} started");
        closed.ClosedBy.ShouldBe("node");
    }

    [Fact]
    public async Task TheCardClosesWhenTheQueueEmpties()
    {
        var (jobs, clock, _, gone) = Idle();
        jobs.SiblingsOf = Clones;
        var holder = Hand(jobs, Sleep(30));
        var a = Hand(jobs, Echo("a"));
        var b = Hand(jobs, Echo("b"));
        clock.Advance(TimeSpan.FromMinutes(35));
        Look(jobs);
        JamCards().ShouldHaveSingleItem();

        jobs.Dispatch(new JobRunner.Withdrawn(a, "controller"));
        JamCards().ShouldHaveSingleItem();
        jobs.Dispatch(new JobRunner.Withdrawn(b, "controller"));

        JamCards().ShouldBeEmpty();
        JamCards(openOnly: false).ShouldHaveSingleItem().Outcome.ShouldBe("the queue is empty");
        await gone(holder);
    }

    [Fact]
    public async Task AQueueThatKeepsStartingJobsNeverRingsThoughAHeldJobWaitsLongAndRingsOnceItStandsStill()
    {
        var (jobs, clock, _, gone) = Idle();
        jobs.MaxRunning = 4;
        jobs.SiblingsOf = Clones;
        var holder = Hand(jobs, Sleep(30));
        // Shares the holder's clones, so it waits at the head for as long as the holder runs.
        var held = Hand(jobs, Echo("sib"));
        Current(held).Step.ShouldNotBeNull().ShouldContain("sibling clone");

        clock.Advance(TimeSpan.FromMinutes(20));
        var free = HandFromNode(jobs, Echo("free"));
        // It started ahead of the held job: it has a process, and the held one still waits.
        await Until(free, j => j.EndedAt is not null);
        Current(free).Pid.ShouldNotBeNull();
        Current(held).State.ShouldBe(JobState.Queued);

        // The held job has waited 40 minutes, and a job started 20 minutes ago: the queue moves.
        clock.Advance(TimeSpan.FromMinutes(20));
        Look(jobs);
        JamCards(openOnly: false).ShouldBeEmpty();

        // 55 minutes in, the last start is 35 minutes back: it has stood still past the limit.
        clock.Advance(TimeSpan.FromMinutes(15));
        Look(jobs);
        var card = JamCards().ShouldHaveSingleItem();
        card.Title.ShouldBe($"1 job has waited 55 m; the queue has not moved since {free} started");
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain($"{holder} holds the queue");
        detail.ShouldContain($"The oldest waiting job is {held}");

        await gone(holder);
    }

    private JobRow Current(string id) => _state.Jobs().Single(j => j.Id == id);

    [Fact]
    public void ABenchAtTheHeadWaitingForAQuietCpuIsLeftToTheBenchBusyCard()
    {
        double? cpu = 95;
        var (jobs, clock, _, _) = Idle(() => cpu);
        var bench = HandBench(jobs, Echo("bench"));
        var later = Hand(jobs, Echo("later"));
        Current(bench).State.ShouldBe(JobState.Queued);
        Current(later).State.ShouldBe(JobState.Queued);

        clock.Advance(TimeSpan.FromMinutes(31));
        Look(jobs);
        _state.Cards(openOnly: true).Count(c => c.Kind == JobRunner.BenchBusyKind).ShouldBe(1);
        clock.Advance(TimeSpan.FromMinutes(30));
        Look(jobs);
        JamCards(openOnly: false).ShouldBeEmpty();
    }

    [Fact]
    public async Task AJamReadFromTheRowsAfterARestartKeepsTheCardOpenAndItsTime()
    {
        var (jobs, clock, _, gone) = Idle();
        jobs.SiblingsOf = Clones;
        var holder = Hand(jobs, Sleep(30));
        Hand(jobs, Echo("a"));
        clock.Advance(TimeSpan.FromMinutes(31));
        Look(jobs);
        var card = JamCards().ShouldHaveSingleItem();

        // A node that has just started: its queue is empty until the recovery hands the rows over, and its clock has run as long.
        var (restarted, clock2, _, _) = Idle();
        restarted.SiblingsOf = Clones;
        clock2.Advance(TimeSpan.FromMinutes(31));
        Look(restarted);
        Look(restarted);

        var after = JamCards(openOnly: false).ShouldHaveSingleItem();
        after.Id.ShouldBe(card.Id);
        after.State.ShouldBe(CardState.Open);
        after.At.ShouldBe(card.At);
        after.Title.ShouldBe(card.Title);
        await gone(holder);
    }

    [Fact]
    public async Task AJamThePersonClosedIsNotRungAgainOnTheNextLook()
    {
        var (jobs, clock, _, gone) = Idle();
        jobs.SiblingsOf = Clones;
        var holder = Hand(jobs, Sleep(30));
        Hand(jobs, Echo("a"));
        clock.Advance(TimeSpan.FromMinutes(31));
        Look(jobs);
        var card = JamCards().ShouldHaveSingleItem();

        _state.CloseCard(card.Id, "dismissed", "controller").ShouldBeTrue();
        Look(jobs);
        Look(jobs);

        JamCards().ShouldBeEmpty();
        JamCards(openOnly: false).ShouldHaveSingleItem().Id.ShouldBe(card.Id);
        await gone(holder);
    }
}
