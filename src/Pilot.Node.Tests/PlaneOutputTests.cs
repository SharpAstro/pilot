using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A dotnet job in a plane builds into the plane's own artifacts when job-plane-output is on, and takes no sibling lock (PIL724).</summary>
[Collection(JobsCollection.Name)]
public sealed class PlaneOutputTests : JobTestsBase
{
    private string PlaneIn(string code)
    {
        var plane = Path.Combine(_root, "org", "pilot", ".claude", "worktrees", code);
        Directory.CreateDirectory(plane);
        return plane;
    }

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    private string Hand(JobRunner jobs, string command, string cwd)
    {
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [cwd, "0", command], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private JobRunner Runner(bool planeOutput)
    {
        var (jobs, _, _, _) = Idle();
        jobs.MaxRunning = 2;
        jobs.PlaneOutput = planeOutput;
        jobs.SiblingsOf = _ => [Path.Combine(Path.GetTempPath(), "org-fake", "DIR.Lib")];
        return jobs;
    }

    private async Task CancelAll(JobRunner jobs, params string[] ids)
    {
        foreach (var id in ids)
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
    }

    [Fact]
    public void TheSettingIsOffByDefaultAndListed()
    {
        NodeSettings.Keys.ShouldContain(NodeSettings.JobPlaneOutputKey);
        new NodeSettings().JobPlaneOutput.ShouldBeNull();
        NodeSettings.With(new NodeSettings(), NodeSettings.JobPlaneOutputKey, ["on"]).Settings!.JobPlaneOutput.ShouldBe(true);
        NodeSettings.With(new NodeSettings(), NodeSettings.JobPlaneOutputKey, ["maybe"]).Error.ShouldNotBeNull();
    }

    [Fact]
    public void ThePlaneVariablesAreSetOnlyWithTheSettingOnAndOnlyForAPlane()
    {
        var plane = PlaneIn("pil1");
        var inPlane = Path.Combine(plane, "src");

        PlaneOutput.Variables(true, plane).ShouldBe(new Dictionary<string, string>
        {
            ["UseArtifactsOutput"] = "true",
            ["ArtifactsPath"] = Path.Combine(plane, "artifacts"),
        });
        // From a folder inside the plane the root is still the plane's.
        PlaneOutput.Variables(true, inPlane)["ArtifactsPath"].ShouldBe(Path.Combine(plane, "artifacts"));
        PlaneOutput.Variables(false, plane).ShouldBeEmpty();
        // The main clone is no plane.
        PlaneOutput.Variables(true, Path.Combine(_root, "org", "pilot")).ShouldBeEmpty();
        PlaneOutput.Variables(true, Path.Combine(_root, "org", "pilot", ".claude", "worktrees")).ShouldBeEmpty();
    }

    [Fact]
    public void BothJobStartsCarryTheVariables()
    {
        var plane = PlaneIn("pil2");
        var extra = PlaneOutput.Variables(true, plane);
        var log = Path.Combine(_root, "x.log");

        var unix = DetachedShell.UnixStart("dotnet build", plane, log, log + ".exit", extra: extra);
        unix.Environment["UseArtifactsOutput"].ShouldBe("true");
        unix.Environment["ArtifactsPath"].ShouldBe(Path.Combine(plane, "artifacts"));
        DetachedShell.UnixStart("dotnet build", plane, log, log + ".exit").Environment.ContainsKey("ArtifactsPath").ShouldBeFalse();

        var windows = DetachedShell.WindowsWrapper("in.ps1", "out.ps1", "x.exit", extra);
        windows.ShouldContain("$env:UseArtifactsOutput = 'true'");
        windows.ShouldContain($"$env:ArtifactsPath = '{Path.Combine(plane, "artifacts")}'");
        DetachedShell.WindowsWrapper("in.ps1", "out.ps1", "x.exit").ShouldNotContain("ArtifactsPath");
    }

    [Fact]
    public async Task WithTheSettingOnTwoPlanesBuildingTheSameSiblingRunAtOnce()
    {
        var jobs = Runner(planeOutput: true);
        var first = Hand(jobs, "echo 'one'; sleep 20", PlaneIn("pil1"));
        var second = Hand(jobs, "echo 'two'; sleep 20", PlaneIn("pil2"));
        await Until(first, j => j.State == JobState.Running);
        await Until(second, j => j.State == JobState.Running);
        await CancelAll(jobs, first, second);
    }

    [Fact]
    public async Task WithTheSettingOffTheSecondPlaneWaitsOnTheSiblingHold()
    {
        var jobs = Runner(planeOutput: false);
        var first = Hand(jobs, "echo 'one'; sleep 20", PlaneIn("pil1"));
        await Until(first, j => j.State == JobState.Running);
        var second = Hand(jobs, "echo 'two'; sleep 20", PlaneIn("pil2"));

        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).Step.ShouldNotBeNull().ShouldContain($"waiting for job {first}'s build");
        // The jobs view says how many run against how many could, and who waits on a sibling hold.
        Row(second).Slots.ShouldBe(2);
        Row(second).WaitsOnSibling.ShouldBe(true);
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe("1 running of 2 slots, 1 waiting on a sibling hold");
        await CancelAll(jobs, first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(second, j => j.State == JobState.Running);
        await CancelAll(jobs, second);
    }

    [Fact]
    public async Task WithTheSettingOnAJobOutsideAPlaneStillHoldsTheSiblings()
    {
        var jobs = Runner(planeOutput: true);
        var first = Hand(jobs, "echo 'one'; sleep 20", _root);
        await Until(first, j => j.State == JobState.Running);
        var second = Hand(jobs, "echo 'two'; sleep 20", PlaneIn("pil2"));
        // The plane's job takes no lock, so it is held by nothing and runs beside the main clone's job.
        await Until(second, j => j.State == JobState.Running);
        var third = Hand(jobs, "echo 'three'; sleep 20", _root);
        Row(third).State.ShouldBe(JobState.Queued);
        await CancelAll(jobs, first, second);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(third, j => j.State == JobState.Running);
        await CancelAll(jobs, third);
    }

    [Fact]
    public void TheSlotsLineIsSilentWithNothingQueued()
    {
        JobQueue.SlotsLine([]).ShouldBeNull();
    }

    /// <summary>
    /// A temp org: a lib clone and two planes of an app repo whose project references ../lib. Two real builds at once, each with the
    /// plane variables, write lib's obj under their own plane and nothing under the lib clone.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task TwoPlanesBuildingASiblingAtOnceWriteLibsObjUnderEachPlaneAndNotTheClone()
    {
        var org = Path.Combine(_root, "slow-org");
        var lib = Path.Combine(org, "lib");
        Directory.CreateDirectory(Path.Combine(lib, ".git"));
        File.WriteAllText(Path.Combine(lib, "lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(lib, "Lib.cs"), "namespace Lib; public static class Hello { public static int One => 1; }");
        var app = Path.Combine(org, "app");
        Directory.CreateDirectory(Path.Combine(app, ".git"));
        string[] planes = [Path.Combine(app, ".claude", "worktrees", "p1"), Path.Combine(app, ".claude", "worktrees", "p2")];
        foreach (var plane in planes)
        {
            Directory.CreateDirectory(plane);
            File.WriteAllText(Path.Combine(plane, "app.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"../../../../lib/lib.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(plane, "App.cs"), "namespace App; public static class Run { public static int Two => Lib.Hello.One + 1; }");
        }

        async Task<(int Code, string Output)> Build(string plane)
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = plane, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "build", "-c", "Release", "-nologo" })
            {
                start.ArgumentList.Add(arg);
            }
            BuildEnvironment.Apply(start);
            foreach (var (name, value) in PlaneOutput.Variables(true, plane))
            {
                start.Environment[name] = value;
            }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return (process.ExitCode, await output + await error);
        }

        var results = await Task.WhenAll(planes.Select(Build));

        foreach (var (result, plane) in results.Zip(planes))
        {
            result.Code.ShouldBe(0, result.Output);
            Directory.Exists(Path.Combine(plane, "artifacts", "obj", "lib")).ShouldBeTrue($"lib's obj is under {plane}\n{result.Output}");
        }
        Directory.Exists(Path.Combine(lib, "obj")).ShouldBeFalse("the lib clone has no obj");
        Directory.Exists(Path.Combine(lib, "bin")).ShouldBeFalse("the lib clone has no bin");

        // Nothing of the build lands outside the planes: removing the planes (what landing does) leaves the org as it was.
        foreach (var plane in planes)
        {
            Directory.Delete(plane, recursive: true);
        }
        var left = Directory.EnumerateFiles(org, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(org, f).Replace(Path.DirectorySeparatorChar, '/')).Order().ToArray();
        left.ShouldBe(["lib/Lib.cs", "lib/lib.csproj"], "nothing of the per-plane build remains once the planes are gone");
    }
}
