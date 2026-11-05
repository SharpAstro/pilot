using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Two dotnet jobs whose builds share a sibling clone never run at once (PIL703).</summary>
[Collection(JobsCollection.Name)]
public sealed class SiblingLockTests : JobTestsBase
{
    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    private static string Clone(string name) => Path.Combine(Path.GetTempPath(), "org-fake", name);

    // The runner's clock is the test's: the tree is looked at again only when it is advanced past TreeEvery.
    private Clock? _clock;

    private JobRunner Runner(Func<string, string[]> setOf, int slots = 2)
    {
        var (jobs, clock, _, _) = Idle();
        _clock = clock;
        jobs.MaxRunning = slots;
        jobs.SiblingsOf = job => setOf(job.Command);
        return jobs;
    }

    [Fact]
    public async Task ASecondJobSharingASiblingWaitsNamingTheCloneAndTheRunningJob()
    {
        var jobs = Runner(c => c.Contains("one") ? [Clone("pilot"), Clone("DIR.Lib")] : [Clone("tianwen"), Clone("dir.lib")]);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        var second = Hand(jobs, "echo 'two'; sleep 20");

        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).Step.ShouldNotBeNull().ShouldContain($"waiting for job {first}'s build of the sibling clone dir.lib");
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(first), Who = "controller" });
        await Until(first, j => j.EndedAt is not null);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(second, j => j.State == JobState.Running);
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(second), Who = "controller" });
        await Until(second, j => j.EndedAt is not null);
    }

    [Fact]
    public async Task AHolderPastItsBuildReleasesTheClonesWhileItStillRunsAndTheNextStarts()
    {
        var jobs = Runner(_ => [Clone("pilot"), Clone("DIR.Lib")]);
        jobs.TreeEvery = TimeSpan.FromMilliseconds(50);
        var past = false;
        jobs.PastBuild = (_, _) => Volatile.Read(ref past);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        var second = Hand(jobs, "echo 'two'; sleep 20");

        // Still building: the second stays queued, naming the build.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).Step.ShouldNotBeNull().ShouldContain($"waiting for job {first}'s build");

        // Past its build: the clones are let go at once, the first still runs, the second starts.
        Volatile.Write(ref past, true);
        _clock!.Advance(TimeSpan.FromSeconds(5));
        // The runner is not started here, so the test is the dispatcher: it sees the release (which Pumps a started one) and looks again.
        await Until(first, j => j.Step is { } s && s.Contains("clones released"));
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(second, j => j.State == JobState.Running);
        Row(first).State.ShouldBe(JobState.Running);

        foreach (var id in new[] { first, second })
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
    }

    [Fact]
    public void PastBuildNeedsNoBuildingProcessAndALaterSegmentSeen()
    {
        const string command = "cd src && dotnet build X.slnx -c Release && dotnet test Y --no-build";
        var cwd = _root;
        string[] building = ["dotnet.exe build X.slnx -c Release", "dotnet C:\\sdk\\MSBuild.dll -maxcpucount"];
        string[] nodeReuse = ["dotnet C:\\sdk\\MSBuild.dll /nodeReuse:true -nodemode:1", "VBCSCompiler.exe"];

        SiblingSet.PastBuild(command, cwd, building).ShouldBeFalse();
        SiblingSet.PastBuild(command, cwd, [.. building, "dotnet test Y --no-build"]).ShouldBeFalse();
        SiblingSet.PastBuild(command, cwd, nodeReuse).ShouldBeFalse();
        SiblingSet.PastBuild(command, cwd, [.. nodeReuse, "dotnet.exe test Y --no-build"]).ShouldBeTrue();
        // The real Windows shape: pwsh starts dotnet by its quoted path, which holds a space.
        const string dotnet = "\"C:\\Program Files\\dotnet\\dotnet.exe\"";
        const string msbuild = "\"C:\\Program Files\\dotnet\\sdk\\10.0.302\\MSBuild.dll\"";
        string[] realBuilding = [$"{dotnet} build X.slnx -c Release", $"{dotnet} {msbuild} -maxcpucount -verbosity:m"];
        string[] realNodeReuse = [$"{dotnet} {msbuild} -nodemode:1 /nodeReuse:true", "VBCSCompiler.exe"];
        SiblingSet.PastBuild(command, cwd, realBuilding).ShouldBeFalse();
        SiblingSet.PastBuild(command, cwd, [.. realBuilding, $"{dotnet} test Y --no-build"]).ShouldBeFalse();
        SiblingSet.PastBuild(command, cwd, realNodeReuse).ShouldBeFalse();
        SiblingSet.PastBuild(command, cwd, [.. realNodeReuse, $"{dotnet} test Y --no-build"]).ShouldBeTrue();
        // A last segment that builds holds to its end.
        SiblingSet.PastBuild("dotnet test Y --no-build && dotnet build X", cwd, ["dotnet build X"]).ShouldBeFalse();
        // No building segment at all: nothing to release.
        SiblingSet.PastBuild("dotnet test Y --no-build", cwd, ["dotnet test Y --no-build"]).ShouldBeFalse();
    }

    [Fact]
    public async Task JobsWithDisjointSiblingSetsRunSideBySide()
    {
        var jobs = Runner(c => c.Contains("one") ? [Clone("pilot")] : [Clone("tianwen")]);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        var second = Hand(jobs, "echo 'two'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        await Until(second, j => j.State == JobState.Running);
        foreach (var id in new[] { first, second })
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
    }

    [Fact]
    public async Task AJobWithNoSiblingSetIsNeverHeld()
    {
        var jobs = Runner(c => c.Contains("one") ? [Clone("pilot")] : []);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        var second = Hand(jobs, "echo 'two'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        await Until(second, j => j.State == JobState.Running);
        foreach (var id in new[] { first, second })
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
    }

    private async Task CancelAll(JobRunner jobs, params string[] ids)
    {
        foreach (var id in ids)
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
    }

    // "one" builds pilot; "two" and "four" build pilot too; "three" builds nothing.
    private static string[] Sets(string command) =>
        command.Contains("three") || command.Contains("five") ? [] : [Clone("pilot")];

    [Fact]
    public async Task ANoBuildJobBehindASiblingHeldHeadStartsOutOfTurn()
    {
        var jobs = Runner(Sets, slots: 3);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        var held = Hand(jobs, "echo 'two'; sleep 20");
        var free = Hand(jobs, "echo 'three'; sleep 20");

        await Until(free, j => j.State == JobState.Running);
        Row(free).Step.ShouldNotBeNull().ShouldContain("started ahead of 1 held job");
        Row(held).State.ShouldBe(JobState.Queued);
        Row(held).Step.ShouldNotBeNull().ShouldContain($"next in line: waiting for job {first}'s build");
        await CancelAll(jobs, first, free);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(held, j => j.State == JobState.Running);
        await CancelAll(jobs, held);
    }

    [Fact]
    public async Task AJobSharingTheHeldHeadsClonesDoesNotJumpIt()
    {
        var jobs = Runner(Sets, slots: 3);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        var held = Hand(jobs, "echo 'two'; sleep 20");
        var same = Hand(jobs, "echo 'four'; sleep 20");

        Row(same).State.ShouldBe(JobState.Queued);
        Row(same).Step.ShouldNotBeNull().ShouldContain($"waits behind job {held}, which shares its clone pilot");
        await CancelAll(jobs, first);
    }

    [Fact]
    public async Task AHeadWaitingForMemoryStillBlocksASmallJobBehindIt()
    {
        long free = 1L << 40;
        var clock = new Clock();
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), clock)
        {
            FreeMemory = () => free,
        };
        jobs.MaxRunning = 3;
        jobs.MinFreeMemory = 1L << 30;
        jobs.SiblingsOf = job => Sets(job.Command);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        free = 0;
        var head = Hand(jobs, "echo 'five'; sleep 20");
        var small = Hand(jobs, "echo 'three'; sleep 20");

        Row(head).State.ShouldBe(JobState.Queued);
        Row(small).State.ShouldBe(JobState.Queued);
        Row(head).Step.ShouldNotBeNull().ShouldContain("free memory");
        await CancelAll(jobs, first);
    }

    [Fact]
    public async Task TheHeldHeadStartsWhenTheHolderEndsAheadOfALaterSameCloneJob()
    {
        var jobs = Runner(Sets, slots: 3);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        var held = Hand(jobs, "echo 'two'; sleep 20");
        var same = Hand(jobs, "echo 'four'; sleep 20");

        await CancelAll(jobs, first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(held, j => j.State == JobState.Running);
        Row(same).State.ShouldBe(JobState.Queued);
        await CancelAll(jobs, held);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(same, j => j.State == JobState.Running);
        await CancelAll(jobs, same);
    }

    /// <summary>A temp org with a repo "pilot" (its own .git folder) and planes pil1 and pil2, whose props use sibling A, which uses B.</summary>
    private (string Org, string Repo, string Plane1, string Plane2) Org()
    {
        var org = Path.Combine(_root, "org");
        var repo = Path.Combine(org, "pilot");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "Directory.Build.props"),
            "<Project><PropertyGroup><UseLocalA Condition=\"Exists('../A')\">true</UseLocalA></PropertyGroup></Project>");
        string[] planes = [Path.Combine(repo, ".claude", "worktrees", "pil1"), Path.Combine(repo, ".claude", "worktrees", "pil2")];
        foreach (var plane in planes)
        {
            Directory.CreateDirectory(Path.Combine(plane, "src"));
            File.WriteAllText(Path.Combine(plane, ".git"), "gitdir: nowhere");
            File.WriteAllText(Path.Combine(plane, "Directory.Build.props"),
                "<Project><PropertyGroup><UseLocalA Condition=\"Exists('../../A')\">true</UseLocalA></PropertyGroup></Project>");
        }
        Directory.CreateDirectory(Path.Combine(org, "A"));
        Directory.CreateDirectory(Path.Combine(org, "B"));
        File.WriteAllText(Path.Combine(org, "A", "Directory.Build.props"),
            "<Project><PropertyGroup><UseLocalB Condition=\"Exists('../B')\">true</UseLocalB></PropertyGroup></Project>");
        return (org, repo, planes[0], planes[1]);
    }

    [Fact]
    public void SiblingSetResolvesAPlanesSiblingsTransitivelyBesideItsRepo()
    {
        var (org, repo, plane, _) = Org();

        var set = SiblingSet.Of("dotnet build -c Release", plane);

        set.ShouldContain(Path.Combine(org, "A"), new PathComparer());
        set.ShouldContain(Path.Combine(org, "B"), new PathComparer());
        SiblingSet.Of("echo hello", plane).ShouldBeEmpty();
    }

    [Fact]
    public void TestOnlyJobsInTwoPlanesHoldNoCloneAndRunAtOnce()
    {
        var (_, _, plane1, plane2) = Org();

        SiblingSet.Of("dotnet test Pilot.Node.Tests -c Release --no-build", plane1).ShouldBeEmpty();
        SiblingSet.Of("cd src && dotnet test X --no-build --filter A", plane2).ShouldBeEmpty();
        SiblingSet.Of("./run-checks.sh", plane1).ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoTestOnlyJobsInTwoPlanesRunSideBySide()
    {
        var (_, _, plane1, plane2) = Org();
        var (jobs, _, _, _) = Idle();
        jobs.MaxRunning = 2;
        jobs.SiblingsOf = job => SiblingSet.Of("dotnet test X --no-build", job.Command.Contains("one") ? plane1 : plane2);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        var second = Hand(jobs, "echo 'two'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        await Until(second, j => j.State == JobState.Running);
        foreach (var id in new[] { first, second })
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
    }

    [Fact]
    public void ABuildAndTestCommandStillYieldsTheClones()
    {
        var (org, _, plane, _) = Org();

        var set = SiblingSet.Of("cd src && dotnet build X.slnx -c Release && dotnet test Y --no-build", plane);

        set.ShouldContain(Path.Combine(org, "A"), new PathComparer());
        set.ShouldContain(Path.Combine(org, "B"), new PathComparer());
        SiblingSet.Of("dotnet test Y -c Release", plane).ShouldNotBeEmpty();
        SiblingSet.Of("dotnet run --project Y", plane).ShouldNotBeEmpty();
        SiblingSet.Of("msbuild X.sln", plane).ShouldNotBeEmpty();
    }

    [Fact]
    public void APlaneJobLacksTheMainCloneAndAMainCloneJobHasIt()
    {
        var (org, repo, plane, _) = Org();

        SiblingSet.Of("dotnet build", plane).ShouldNotContain(repo, new PathComparer());
        var main = SiblingSet.Of("dotnet build", repo);
        main.ShouldContain(repo, new PathComparer());
        main.ShouldContain(Path.Combine(org, "A"), new PathComparer());
    }

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value);
    }
}
