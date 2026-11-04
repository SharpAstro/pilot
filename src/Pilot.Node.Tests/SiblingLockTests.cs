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

    private JobRunner Runner(Func<string, string[]> setOf, int slots = 2)
    {
        var (jobs, _, _, _) = Idle();
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
        Row(second).Step.ShouldNotBeNull().ShouldContain("shares the sibling clone dir.lib with job " + first);
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(first), Who = "controller" });
        await Until(first, j => j.EndedAt is not null);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(second, j => j.State == JobState.Running);
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(second), Who = "controller" });
        await Until(second, j => j.EndedAt is not null);
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
        jobs.SiblingsOf = job => SiblingSet.Of(job.Command.Replace("sleep 20", "dotnet test X --no-build"), plane1);
        var first = Hand(jobs, "echo 'one'; sleep 20");
        var second = Hand(jobs, "echo 'two'; sleep 20");
        await Until(first, j => j.State == JobState.Running);
        await Until(second, j => j.State == JobState.Running);
        foreach (var id in new[] { first, second })
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
        _ = plane2;
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
