using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// How a GUI job starts and ends (pilot #1082): Normal priority for its whole tree, the inspector pair with a fresh token and a new
/// empty artifacts directory at launch only, no idle card, and its timeout ends its tree so the next GUI job starts. The fake launcher
/// stands in for the process; one Slow test runs a real tree through the real launcher.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class GuiJobLaunchTests : JobTestsBase
{
    public GuiJobLaunchTests() : base(fakeLauncher: true)
    {
    }

    private string Repo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private string HandIn(JobRunner jobs, string cwd, string command, bool gui, long timeoutSeconds = 0)
    {
        var id = jobs.Run(new CommandRequest
        {
            Verb = Verbs.Run,
            Target = "session/a1b2c3d4",
            Args = [.. gui ? new[] { Verbs.GuiArg } : [], cwd, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), command],
            Who = "se1-tia1-parser",
        }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private (JobRunner Jobs, Clock Clock, Dictionary<string, TimeSpan> Cpu, Func<string, Task> Gone) Lane()
    {
        var lane = Idle();
        lane.Jobs.MaxRunning = 4;
        lane.Jobs.SiblingsOf = _ => [];
        lane.Jobs.OneHeavyAtATime = false;
        lane.Jobs.GuiSlots = 1;
        lane.Jobs.DesktopProbe = () => null;
        return lane;
    }

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    [Fact]
    public async Task AGuiJobLaunchesAtNormalWhileAnOrdinaryJobKeepsTheNodesPriority()
    {
        var (jobs, _, _, _) = Lane();
        jobs.Priority = JobPriority.Idle;

        var gui = HandIn(jobs, Repo("tianwen"), Sleep(30), gui: true);
        var ordinary = HandIn(jobs, Repo("pilot"), Sleep(30), gui: false);
        await Until(gui, j => j.State == JobState.Running);
        await Until(ordinary, j => j.State == JobState.Running);

        Fake!.Job(gui).Launch.Priority.ShouldBe(JobPriority.Normal);
        Fake.Job(ordinary).Launch.Priority.ShouldBe(JobPriority.Idle);
    }

    [Fact]
    public async Task AGuiJobGetsTheInspectorPairWithATokenPerJobAndNotOnTheRowOrInTheLog()
    {
        var (jobs, _, _, gone) = Lane();
        var first = HandIn(jobs, Repo("tianwen"), Sleep(30), gui: true);
        await Until(first, j => j.State == JobState.Running);
        var firstEnv = Fake!.Job(first).Launch.Environment.ShouldNotBeNull();
        await gone(first);
        var second = HandIn(jobs, Repo("tianwen"), Sleep(30), gui: true);
        await Until(second, j => j.State == JobState.Running);
        var secondEnv = Fake.Job(second).Launch.Environment.ShouldNotBeNull();

        firstEnv[JobRunner.InspectorVariable].ShouldBe("1");
        var token = firstEnv[JobRunner.InspectorTokenVariable];
        token.Length.ShouldBeGreaterThanOrEqualTo(32);
        token.ShouldNotBe(secondEnv[JobRunner.InspectorTokenVariable]);
        var stored = System.Text.Json.JsonSerializer.Serialize(Row(first), PilotJsonContext.Default.JobRow);
        stored.ShouldNotContain(token);
        Row(first).ExtraEnvironment.ShouldBeNull();
        var logText = File.Exists(Row(first).LogPath) ? ReadLog(Row(first).LogPath) : "";
        logText.ShouldNotContain(token);
    }

    [Fact]
    public async Task AnOrdinaryJobGetsNoInspectorVariablesAndNoArtifactsDirectory()
    {
        var (jobs, _, _, _) = Lane();

        var id = HandIn(jobs, Repo("pilot"), Sleep(30), gui: false);
        await Until(id, j => j.State == JobState.Running);

        var env = Fake!.Job(id).Launch.Environment.ShouldNotBeNull();
        env.ContainsKey(JobRunner.InspectorVariable).ShouldBeFalse();
        env.ContainsKey(JobRunner.InspectorTokenVariable).ShouldBeFalse();
        env.ContainsKey(JobRunner.ArtifactsVariable).ShouldBeFalse();
    }

    [Fact]
    public async Task TheArtifactsDirectoryOfAGuiJobExistsAndIsEmptyAtStart()
    {
        var (jobs, _, _, _) = Lane();

        var id = HandIn(jobs, Repo("tianwen"), Sleep(30), gui: true);
        await Until(id, j => j.State == JobState.Running);

        var dir = Fake!.Job(id).Launch.Environment.ShouldNotBeNull()[JobRunner.ArtifactsVariable];
        Directory.Exists(dir).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(dir).ShouldBeEmpty();
        Path.GetFileName(dir).ShouldBe($"{id}.artifacts");
    }

    [Fact]
    public async Task AGuiJobUsingNoCpuRaisesNoIdleCardWhileAnOrdinaryOneStill()
    {
        var (jobs, clock, cpu, _) = Lane();
        jobs.MaxRunning = 2;
        var gui = HandIn(jobs, Repo("tianwen"), Sleep(600), gui: true);
        var ordinary = HandIn(jobs, Repo("pilot"), Sleep(600), gui: false);
        await Until(gui, j => j.State == JobState.Running);
        await Until(ordinary, j => j.State == JobState.Running);
        // Both slots are held, so this one queues behind them and an idle card is due for any holder.
        var behind = HandIn(jobs, Repo("LAN.Lib"), Echo("later"), gui: false);
        cpu[gui] = TimeSpan.FromSeconds(1);
        cpu[ordinary] = TimeSpan.FromSeconds(1);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        clock.Advance(TimeSpan.FromMinutes(16));
        cpu[gui] = TimeSpan.FromSeconds(2);
        cpu[ordinary] = TimeSpan.FromSeconds(2);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        Row(behind).State.ShouldBe(JobState.Queued);
        IdleCards().Select(c => c.Subject).ShouldBe([Verbs.JobTarget(ordinary)]);
    }

    [Fact]
    public async Task AGuiJobPastItsTimeoutIsTerminatedAndTheNextGuiJobStarts()
    {
        var (jobs, clock, _, _) = Lane();
        var first = HandIn(jobs, Repo("tianwen"), Sleep(600), gui: true, timeoutSeconds: 60);
        await Until(first, j => j.State == JobState.Running);
        var second = HandIn(jobs, Repo("pilot"), Sleep(600), gui: true);
        Row(second).State.ShouldBe(JobState.Queued);

        clock.Advance(TimeSpan.FromSeconds(61));
        var ended = await Until(first, j => j.EndedAt is not null);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        ended.Error.ShouldNotBeNull().ShouldStartWith("stopped after its timeout");
        Fake!.Terminated.ShouldContain(first);
        await Until(second, j => j.State == JobState.Running);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARealGuiJobTreeIsGoneAfterACancelAndAfterATimeoutWithNoWindowNeeded()
    {
        // Slow: a real launcher and a real child process, no window or GPU. The child waits on a sleep timer (no busy loop) and the
        // job waits for the child; a kill of the wrapper alone would leave it, so only the named job object ends both.
        RealLauncher();
        _jobs.GuiSlots = 1;
        _jobs.DesktopProbe = () => null;
        var cwd = Repo("tianwen");

        var cancelled = await StartTreeAsync(cwd, 0, "cancel.pid");
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(cancelled.Id), Who = "controller" });
        await AssertTreeGoneAsync(cancelled);

        var timedOut = await StartTreeAsync(cwd, 20, "timeout.pid");
        var ended = await Ended(timedOut.Id);
        ended.Error.ShouldNotBeNull().ShouldStartWith("stopped after its timeout");
        await AssertTreeGoneAsync(timedOut);
    }

    private async Task<(string Id, int Child, int Wrapper)> StartTreeAsync(string cwd, long timeoutSeconds, string pidName)
    {
        var pidFile = Path.Combine(_root, pidName);
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; $p.WaitForExit()"
            : $"sleep 120 & echo $! > '{pidFile}'; wait";
        var id = _jobs.Run(new CommandRequest
        {
            Verb = Verbs.Run,
            Target = "session/a1b2c3d4",
            Args = [Verbs.GuiArg, cwd, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), command],
            Who = "se1-tia1-parser",
        }).Made!;
        var running = await Until(id, j => j.State == JobState.Running);
        return (id, await PidIn(pidFile), running.Pid!.Value);
    }

    private static async Task AssertTreeGoneAsync((string Id, int Child, int Wrapper) tree)
    {
        // A delay with a ceiling, as the other real-process tests wait.
        for (var i = 0; i < 100 && (IsAlive(tree.Child) || IsAlive(tree.Wrapper)); i++)
        {
            await Task.Delay(100);
        }
        IsAlive(tree.Child).ShouldBeFalse($"the child of {tree.Id} outlived its job");
        IsAlive(tree.Wrapper).ShouldBeFalse($"the wrapper of {tree.Id} outlived its job");
    }
}
