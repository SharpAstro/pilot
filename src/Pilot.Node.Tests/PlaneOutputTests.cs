using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A dotnet job in a plane builds into the plane's own artifacts while job-plane-output is on, and takes no sibling lock (PIL724); off by default since planes build on the packages (PIL802).</summary>
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

    // A null setting leaves what a runner starts with, which is the node's default.
    private JobRunner Runner(bool? planeOutput)
    {
        var (jobs, _, _, _) = Idle();
        jobs.MaxRunning = 2;
        if (planeOutput is { } setting)
        {
            jobs.PlaneOutput = setting;
        }
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
        // Nothing saved means the default, which is off since planes build on the packages (PIL802); a saved on turns it on.
        NodeSettings.JobPlaneOutputDefault.ShouldBeFalse();
        new NodeSettings().JobPlaneOutput.ShouldBeNull();
        _jobs.PlaneOutput.ShouldBeFalse();
        NodeSettings.With(new NodeSettings(), NodeSettings.JobPlaneOutputKey, ["on"]).Settings!.JobPlaneOutput.ShouldBe(true);
        NodeSettings.With(new NodeSettings(), NodeSettings.JobPlaneOutputKey, ["off"]).Settings!.JobPlaneOutput.ShouldBe(false);
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
    public async Task ByDefaultTheSettingIsOffAndTwoPlanesWhoseCommandsHoldNoCloneRunAtOnce()
    {
        // Nothing is set on the runner: the setting is off (PIL802), and the real sibling sets are asked. These commands are no dotnet
        // build, so they hold no clone and neither plane holds the other back; that a plane's build reaches no clone is SiblingReachTests'
        // and SiblingSourceTests' to show, with a project file in the plane.
        var jobs = Runner(planeOutput: null);
        jobs.PlaneOutput.ShouldBeFalse();
        jobs.SiblingsOf = null;
        var first = Hand(jobs, "echo 'one'; sleep 20", PlaneIn("pil1"));
        var second = Hand(jobs, "echo 'two'; sleep 20", PlaneIn("pil2"));
        await Until(first, j => j.State == JobState.Running);
        await Until(second, j => j.State == JobState.Running);
        await CancelAll(jobs, first, second);
    }

    [Fact]
    public async Task WithTheSettingSetOnTwoPlanesBuildingTheSameSiblingRunAtOnce()
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
        // The second slot stands idle for the hold, and the line says so and since when (PIL802).
        var idleSince = Row(second).HoldIdleSince.ShouldNotBeNull();
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe($"1 running of 2 slots, 1 idle behind the sibling hold since {LocalTime(idleSince)}, 1 waiting on a sibling hold");
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
    public async Task TurningTheSettingOffMakesAQueuedPlaneJobWaitOnTheSiblingHoldAgain()
    {
        var jobs = Runner(planeOutput: true);
        jobs.MaxRunning = 1;
        var first = Hand(jobs, "echo 'one'; sleep 20", _root);
        await Until(first, j => j.State == JobState.Running);
        // Queued for the slot while the setting is on, so its sibling set may already be worked out as none.
        var second = Hand(jobs, "echo 'two'; sleep 20", PlaneIn("pil2"));
        Row(second).State.ShouldBe(JobState.Queued);

        jobs.PlaneOutput = false;
        jobs.MaxRunning = 2;
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        // It now builds into the sibling's own obj, so it waits for the main clone's build instead of starting unlocked.
        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).WaitsOnSibling.ShouldBe(true);
        await CancelAll(jobs, first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(second, j => j.State == JobState.Running);
        await CancelAll(jobs, second);
    }

    [Fact]
    public void TheSlotsLineIsSilentWithNothingQueued()
    {
        JobQueue.SlotsLine([]).ShouldBeNull();
    }

    private static string LocalTime(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task ASlotTheSiblingHoldLeavesIdleIsSaidSinceWhenAndClearedOnceNoneIs()
    {
        var (jobs, clock, _, _) = Idle();
        jobs.MaxRunning = 3;
        jobs.PlaneOutput = false;
        // Two planes build the shared clone, so the second waits on the first; "free" builds nothing and holds nothing.
        jobs.SiblingsOf = job => job.Command.Contains("free") ? [] : [Path.Combine(Path.GetTempPath(), "org-fake", "DIR.Lib")];
        var first = Hand(jobs, "echo 'one'; sleep 20", PlaneIn("pil1"));
        await Until(first, j => j.State == JobState.Running);
        var began = clock.GetUtcNow();
        var second = Hand(jobs, "echo 'two'; sleep 20", PlaneIn("pil2"));

        // 1 of 3 slots runs and the head waits only on the hold: 2 stand idle, since the moment it began.
        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).HoldIdleSince.ShouldBe(began);
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe($"1 running of 3 slots, 2 idle behind the sibling hold since {LocalTime(began)}, 1 waiting on a sibling hold");

        // The hold goes on: a later job it shares the clone with joins it, and the time stays the first one.
        clock.Advance(TimeSpan.FromHours(1));
        var third = Hand(jobs, "echo 'three'; sleep 20", PlaneIn("pil3"));
        Row(third).HoldIdleSince.ShouldBe(began);
        Row(second).HoldIdleSince.ShouldBe(began);
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe($"1 running of 3 slots, 2 idle behind the sibling hold since {LocalTime(began)}, 2 waiting on a sibling hold");

        // A job that holds no clone starts ahead of the held ones and takes a slot; one idle is still the hold's doing.
        var free1 = Hand(jobs, "echo 'free1'; sleep 20", PlaneIn("pil4"));
        await Until(free1, j => j.State == JobState.Running);
        Row(free1).HoldIdleSince.ShouldBeNull();
        Row(second).HoldIdleSince.ShouldBe(began);
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe($"2 running of 3 slots, 1 idle behind the sibling hold since {LocalTime(began)}, 2 waiting on a sibling hold");

        // The last slot is taken: nothing stands idle, so the field is cleared and the line is as it was before.
        var free2 = Hand(jobs, "echo 'free2'; sleep 20", PlaneIn("pil5"));
        await Until(free2, j => j.State == JobState.Running);
        Row(second).HoldIdleSince.ShouldBeNull();
        Row(third).HoldIdleSince.ShouldBeNull();
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe("3 running of 3 slots, 2 waiting on a sibling hold");

        // A slot frees while the hold stands: idle again, from now on, not from the first time.
        clock.Advance(TimeSpan.FromMinutes(5));
        await CancelAll(jobs, free2);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        Row(second).HoldIdleSince.ShouldBe(clock.GetUtcNow());
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe($"2 running of 3 slots, 1 idle behind the sibling hold since {LocalTime(clock.GetUtcNow())}, 2 waiting on a sibling hold");

        await CancelAll(jobs, first, free1);
    }

    [Fact]
    public async Task WithEverySlotTakenTheSiblingHoldLeavesNoneIdleAndTheLineIsAsBefore()
    {
        var (jobs, _, _, _) = Idle();
        jobs.MaxRunning = 1;
        jobs.PlaneOutput = false;
        jobs.SiblingsOf = _ => [Path.Combine(Path.GetTempPath(), "org-fake", "DIR.Lib")];
        var first = Hand(jobs, "echo 'one'; sleep 20", PlaneIn("pil1"));
        await Until(first, j => j.State == JobState.Running);
        // Held by the sibling build, but the one slot is taken anyway: no slot stands idle for the hold.
        var second = Hand(jobs, "echo 'two'; sleep 20", PlaneIn("pil2"));

        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).WaitsOnSibling.ShouldBe(true);
        Row(second).HoldIdleSince.ShouldBeNull();
        JobQueue.SlotsLine(_state.Jobs()).ShouldBe("1 running of 1 slot, 1 waiting on a sibling hold");
        await CancelAll(jobs, first);
    }

    [Fact]
    public void TheSlotsLineNamesTheBuildHoldWhenNoQueuedJobWaitsOnASibling()
    {
        var since = new DateTimeOffset(2026, 11, 7, 22, 43, 0, TimeSpan.Zero);
        JobRow Make(string id, JobState state, bool? sibling = null, DateTimeOffset? idle = null) => new()
        {
            Id = id, Command = "dotnet test X --no-build", Cwd = _root, State = state, LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(30),
            StartedAt = since, Slots = state == JobState.Queued ? 4 : null, WaitsOnSibling = sibling, HoldIdleSince = idle,
        };

        // Idle slots, a hold that began at 22:43, and every queued job on a sibling hold: the sibling hold, with the count of the waiting.
        JobQueue.SlotsLine([Make("j1", JobState.Running), Make("j2", JobState.Running), Make("j3", JobState.Queued, true, since), Make("j4", JobState.Queued, true, since)])
            .ShouldBe($"2 running of 4 slots, 2 idle behind the sibling hold since {LocalTime(since)}, 2 waiting on a sibling hold");
        // A queued test that waits for its own plane's build only: the build hold, and no sibling count.
        JobQueue.SlotsLine([Make("j1", JobState.Running), Make("j3", JobState.Queued, false, since.AddMinutes(3)), Make("j4", JobState.Queued, false, since)])
            .ShouldBe($"1 running of 4 slots, 3 idle behind the build hold since {LocalTime(since)}");
        // The field says a hold left a slot idle, but none is free by the rows: the old text.
        JobQueue.SlotsLine([Make("j1", JobState.Running), Make("j2", JobState.Running), Make("j5", JobState.Running), Make("j6", JobState.Running), Make("j3", JobState.Queued, true, since)])
            .ShouldBe("4 running of 4 slots, 1 waiting on a sibling hold");
        // A node that does not say it (an older one, or a queue held by memory): the old text.
        JobQueue.SlotsLine([Make("j1", JobState.Running), Make("j3", JobState.Queued, true)]).ShouldBe("1 running of 4 slots, 1 waiting on a sibling hold");
        JobQueue.SlotsLine([Make("j1", JobState.Running), Make("j3", JobState.Queued, false)]).ShouldBe("1 running of 4 slots");
    }

    [Fact]
    public void ANoBuildJobInAPlaneWithNoNodeBuildGetsANoticeAndNothingElseDoes()
    {
        var plane = PlaneIn("pil3");
        var inPlane = Path.Combine(plane, "src");
        const string consume = "dotnet test Pilot.Node.Tests -c Release --no-build";

        var notice = PlaneOutput.NoBuildNotice(true, consume, plane).ShouldNotBeNull();
        notice.ShouldContain("no node build yet");
        notice.ShouldContain("pilot run -- dotnet build -c Release");
        notice.ShouldNotContain("\n");
        PlaneOutput.NoBuildNotice(true, "cd src && dotnet run --project X --no-build", inPlane).ShouldNotBeNull();

        // A plane that has been through a node build has an artifacts/bin, and an obj alone (a build that failed) is no build.
        Directory.CreateDirectory(Path.Combine(plane, "artifacts", "obj"));
        PlaneOutput.NoBuildNotice(true, consume, plane).ShouldNotBeNull();
        Directory.CreateDirectory(Path.Combine(plane, "artifacts", "bin"));
        PlaneOutput.NoBuildNotice(true, consume, plane).ShouldBeNull();

        var bare = PlaneIn("pil4");
        // Not with the setting off, not for a command that builds itself, not for a command that is no build step at all, not outside a plane.
        PlaneOutput.NoBuildNotice(false, consume, bare).ShouldBeNull();
        PlaneOutput.NoBuildNotice(true, "dotnet test Pilot.Node.Tests -c Release", bare).ShouldBeNull();
        PlaneOutput.NoBuildNotice(true, "dotnet build -c Release && dotnet test --no-build", bare).ShouldBeNull();
        PlaneOutput.NoBuildNotice(true, "echo hello", bare).ShouldBeNull();
        PlaneOutput.NoBuildNotice(true, consume, Path.Combine(_root, "org", "pilot")).ShouldBeNull();
    }

    [Fact]
    public void BothJobStartsOpenTheLogWithTheNoticeAfterTheShellHeader()
    {
        var plane = PlaneIn("pil5");
        var log = Path.Combine(_root, "n.log");
        const string notice = "pilot: it's a plane with no node build yet";

        var unix = DetachedShell.UnixStart("dotnet test --no-build", plane, log, log + ".exit", notice: notice);
        var wrapper = string.Join('\n', unix.ArgumentList);
        wrapper.ShouldContain("printf '%s\\n' 'pilot: it'\\''s a plane with no node build yet'");
        wrapper.IndexOf(ShellScript.LogHeader(DetachedShell.UnixShell()), StringComparison.Ordinal)
            .ShouldBeLessThan(wrapper.IndexOf("no node build yet", StringComparison.Ordinal));
        string.Join('\n', DetachedShell.UnixStart("dotnet test --no-build", plane, log, log + ".exit").ArgumentList).ShouldNotContain("no node build yet");

        if (OperatingSystem.IsWindows())
        {
            using var windows = DetachedShell.For("j1", "dotnet test --no-build", plane, log, log + ".exit", notice: notice);
            var script = File.ReadAllText(windows.Script!);
            script.ShouldContain("Write-Output 'pilot: it''s a plane with no node build yet'");
            script.IndexOf(ShellScript.LogHeader(windows.Shell), StringComparison.Ordinal)
                .ShouldBeLessThan(script.IndexOf("no node build yet", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The job a worker would hand the node: a test over a plane the node has not built. With job-plane-output on it reads the
    /// plane's artifacts, which are not there, so the log says so on its first lines instead of leaving a bare failure; and the
    /// job still ends by itself (it waits for nothing). A plane with an artifacts/bin gets no such line.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANoBuildJobInAPlaneWithNoNodeBuildOpensItsLogWithTheNoticeAndStillEnds()
    {
        var jobs = Runner(planeOutput: true);
        var plane = PlaneIn("pil6");

        var first = Hand(jobs, "dotnet test --no-build --filter FullyQualifiedName~ParserTests", plane);
        await Until(first, j => j.EndedAt is not null);
        // A child the job left behind (an MSBuild worker node) may still hold the log open for writing, so it is read shared.
        var lines = ReadLog(Row(first).LogPath).ReplaceLineEndings("\n").Split('\n');
        lines[0].ShouldStartWith("shell: ");
        lines[1].ShouldBe(PlaneOutput.NoBuildNotice(true, "dotnet test --no-build", plane));

        Directory.CreateDirectory(Path.Combine(plane, "artifacts", "bin"));
        var second = Hand(jobs, "dotnet test --no-build --filter FullyQualifiedName~ParserTests", plane);
        await Until(second, j => j.EndedAt is not null);
        ReadLog(Row(second).LogPath).ShouldNotContain("no node build yet");
    }

    /// <summary>
    /// A temp org (outside the repo, removed with the test's folder): a lib clone and planes of an app repo whose project references
    /// ../lib and runs. The planes' variables are the whole of what moves the output; MSBuild reads them from the process environment.
    /// </summary>
    private (string Org, string Lib, string[] Planes) LibAndPlanes(string orgName, params string[] codes)
    {
        var org = Path.Combine(_root, orgName);
        var lib = Path.Combine(org, "lib");
        Directory.CreateDirectory(Path.Combine(lib, ".git"));
        File.WriteAllText(Path.Combine(lib, "lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(lib, "Lib.cs"), "namespace Lib; public static class Hello { public static int One => 1; }");
        var app = Path.Combine(org, "app");
        Directory.CreateDirectory(Path.Combine(app, ".git"));
        var planes = codes.Select(code => Path.Combine(app, ".claude", "worktrees", code)).ToArray();
        foreach (var plane in planes)
        {
            Directory.CreateDirectory(plane);
            File.WriteAllText(Path.Combine(plane, "app.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"../../../../lib/lib.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(plane, "Program.cs"), "System.Console.WriteLine(Lib.Hello.One + 1);");
        }
        return (org, lib, planes);
    }

    private static async Task<(int Code, string Output)> Dotnet(string cwd, bool planeOutput, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        BuildEnvironment.Apply(start);
        if (planeOutput)
        {
            foreach (var (name, value) in PlaneOutput.Variables(true, cwd))
            {
                start.Environment[name] = value;
            }
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }

    /// <summary>
    /// The mechanism, with real builds and only the environment the node gives a job: a plane's project that references a project in
    /// a sibling clone builds both into the plane's artifacts (obj and bin of each), the sibling clone and the plane's own folders
    /// stay bare, and a <c>run --no-build</c> in the same environment finds what that build left. Without the variables the same
    /// <c>run --no-build</c> finds nothing, which is what a worker's own build outside the node leaves behind (the notice's case).
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABuildWithThePlaneVariablesPutsEveryProjectUnderThePlaneAndARunNoBuildFindsIt()
    {
        var (org, lib, planes) = LibAndPlanes("slow-one", "p1");
        var plane = planes[0];
        var artifacts = Path.Combine(plane, "artifacts");
        PlaneOutput.NoBuildNotice(true, "dotnet run --no-build -c Release", plane).ShouldNotBeNull();

        // Nothing is built yet: a run --no-build has nothing to run.
        (await Dotnet(plane, planeOutput: true, "run", "--no-build", "-c", "Release")).Code.ShouldNotBe(0);

        var build = await Dotnet(plane, planeOutput: true, "build", "-c", "Release", "-nologo");
        build.Code.ShouldBe(0, build.Output);

        foreach (var project in new[] { "lib", "app" })
        {
            Directory.Exists(Path.Combine(artifacts, "obj", project)).ShouldBeTrue($"{project}'s obj is under the plane\n{build.Output}");
            Directory.Exists(Path.Combine(artifacts, "bin", project)).ShouldBeTrue($"{project}'s bin is under the plane\n{build.Output}");
        }
        Directory.Exists(Path.Combine(lib, "obj")).ShouldBeFalse("the lib clone has no obj");
        Directory.Exists(Path.Combine(lib, "bin")).ShouldBeFalse("the lib clone has no bin");
        Directory.Exists(Path.Combine(plane, "obj")).ShouldBeFalse("the plane's own folder has no obj");
        Directory.Exists(Path.Combine(plane, "bin")).ShouldBeFalse("the plane's own folder has no bin");
        PlaneOutput.NoBuildNotice(true, "dotnet run --no-build -c Release", plane).ShouldBeNull();

        var run = await Dotnet(plane, planeOutput: true, "run", "--no-build", "-c", "Release");
        run.Code.ShouldBe(0, run.Output);
        run.Output.Trim().ShouldBe("2");

        // The environment is what finds it: a run --no-build without it looks in the normal bin, where nothing was built.
        (await Dotnet(plane, planeOutput: false, "run", "--no-build", "-c", "Release")).Code.ShouldNotBe(0);

        // Landing a plane removes its folder, and with it everything the build wrote.
        Directory.Delete(plane, recursive: true);
        string.Join("|", Directory.EnumerateFiles(org, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(org, f).Replace(Path.DirectorySeparatorChar, '/')).Order())
            .ShouldBe("lib/Lib.cs|lib/lib.csproj", "nothing of the per-plane build remains once the plane is gone");
    }

    /// <summary>Two real builds at once of two planes that share the lib clone: each writes lib's obj and bin under its own plane, and nothing under the clone.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task TwoPlanesBuildingASiblingAtOnceWriteLibsOutputUnderEachPlaneAndNotTheClone()
    {
        var (org, lib, planes) = LibAndPlanes("slow-two", "p1", "p2");

        var results = await Task.WhenAll(planes.Select(plane => Dotnet(plane, planeOutput: true, "build", "-c", "Release", "-nologo")));

        foreach (var (result, plane) in results.Zip(planes))
        {
            result.Code.ShouldBe(0, result.Output);
            Directory.Exists(Path.Combine(plane, "artifacts", "obj", "lib")).ShouldBeTrue($"lib's obj is under {plane}\n{result.Output}");
            Directory.Exists(Path.Combine(plane, "artifacts", "bin", "lib")).ShouldBeTrue($"lib's bin is under {plane}\n{result.Output}");
        }
        Directory.Exists(Path.Combine(lib, "obj")).ShouldBeFalse("the lib clone has no obj");
        Directory.Exists(Path.Combine(lib, "bin")).ShouldBeFalse("the lib clone has no bin");

        // Nothing of the build lands outside the planes: removing the planes (what landing does) leaves the org as it was.
        foreach (var plane in planes)
        {
            Directory.Delete(plane, recursive: true);
        }
        string.Join("|", Directory.EnumerateFiles(org, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(org, f).Replace(Path.DirectorySeparatorChar, '/')).Order())
            .ShouldBe("lib/Lib.cs|lib/lib.csproj", "nothing of the per-plane build remains once the planes are gone");
    }
}
