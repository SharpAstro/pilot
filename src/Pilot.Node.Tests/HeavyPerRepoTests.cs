using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// One heavy job at a time is per repo, never across repos: max-jobs 4 runs a pilot build, a LAN.Lib build and a tianwen test run side
/// by side, and only a second heavy job of the same repo waits (the owner, 2026-11-10: "we can run 4 jobs of diff repos in parallel,
/// the rule we added later was only that it shouldn't run 4 pilot heavies or 4 tianwen heavies"). On DE1 one pilot build held nine
/// queued jobs of three repos with 27 GB free and three slots idle.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class HeavyPerRepoTests : JobTestsBase
{
    public HeavyPerRepoTests() : base(fakeLauncher: true)
    {
    }

    private string Repo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private string HandIn(JobRunner jobs, string cwd, string command)
    {
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [cwd, "0", command], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    [Fact]
    public async Task AHeavyJobOfAnotherRepoStartsBesideARunningOneAndOnlyTheSameReposWaits()
    {
        var (jobs, _, _, gone) = Idle();
        jobs.MaxRunning = 4;
        jobs.SiblingsOf = _ => [];
        var pilot = Repo("pilot");
        var lan = Repo("LAN.Lib");

        var first = HandIn(jobs, pilot, $"{Sleep(30)}; dotnet build Pilot.slnx -c Release");
        await Until(first, j => j.State == JobState.Running);
        var other = HandIn(jobs, lan, $"{Sleep(30)}; dotnet build LAN.Lib.slnx -c Release");
        var same = HandIn(jobs, pilot, $"{Sleep(30)}; dotnet test Pilot.Node.Tests -c Release --filter FullyQualifiedName~FooTests");

        await Until(other, j => j.State == JobState.Running);
        Row(same).State.ShouldBe(JobState.Queued);
        Row(same).Step.ShouldNotBeNull().ShouldContain(first);

        await gone(first);
        await gone(other);
    }

    /// <summary>jobs-per-repo is live: at 2 a second job of the same repo starts beside the first, and a third waits naming both.</summary>
    [Fact]
    public async Task JobsPerRepoLetsThatManyJobsOfOneRepoRunAndTheNextWaitsNamingThem()
    {
        var (jobs, _, _, gone) = Idle();
        jobs.MaxRunning = 4;
        jobs.SiblingsOf = _ => [];
        jobs.OneHeavyAtATime = false;
        jobs.JobsPerRepo = 2;
        var pilot = Repo("pilot");

        var first = HandIn(jobs, pilot, Sleep(30));
        var second = HandIn(jobs, pilot, Sleep(30));
        await Until(first, j => j.State == JobState.Running);
        await Until(second, j => j.State == JobState.Running);
        var third = HandIn(jobs, pilot, Sleep(30));

        Row(third).State.ShouldBe(JobState.Queued);
        Row(third).Step.ShouldNotBeNull().ShouldContain("2 jobs per repo");

        await gone(first);
        await gone(second);
    }
}
