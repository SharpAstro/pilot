using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The GUI lane (pilot #1082): a <c>pilot run --gui</c> job waits for the machine's GUI slot (per node, across repos) as well as an
/// ordinary one, names the GUI job it waits for, never holds an ordinary job, and is refused at once where there is no desktop or the
/// lane is off. Fake launcher and fake desktop probe: no process, no display.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class GuiLaneTests : JobTestsBase
{
    public GuiLaneTests() : base(fakeLauncher: true)
    {
    }

    private string Repo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private CommandResult RunIn(JobRunner jobs, string cwd, string command, bool gui, bool alone = false) =>
        jobs.Run(new CommandRequest
        {
            Verb = Verbs.Run,
            Target = "session/a1b2c3d4",
            Args = [.. gui ? new[] { Verbs.GuiArg } : [], .. alone ? new[] { Verbs.AloneArg } : [], cwd, "0", command],
            Who = "se1-tia1-parser",
        });

    private string HandIn(JobRunner jobs, string cwd, string command, bool gui)
    {
        var id = RunIn(jobs, cwd, command, gui).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    private JobRunner Lane(string? desktopRefusal = null, int slots = 1) => LaneAndGone(desktopRefusal, slots).Jobs;

    private (JobRunner Jobs, Func<string, Task> Gone) LaneAndGone(string? desktopRefusal = null, int slots = 1)
    {
        var (jobs, _, _, gone) = Idle();
        jobs.MaxRunning = 4;
        jobs.SiblingsOf = _ => [];
        jobs.OneHeavyAtATime = false;
        jobs.GuiSlots = slots;
        jobs.DesktopProbe = () => desktopRefusal;
        return (jobs, gone);
    }

    [Fact]
    public async Task AGuiJobStartsOnANodeWithTheLaneAndItsRowIsMarkedGui()
    {
        var jobs = Lane();

        var id = HandIn(jobs, Repo("tianwen"), Sleep(30), gui: true);

        await Until(id, j => j.State == JobState.Running);
        Row(id).Gui.ShouldBe(true);
    }

    [Fact]
    public async Task ASecondGuiJobOfAnotherRepoWaitsNamingTheFirstAndAnOrdinaryJobBehindItStarts()
    {
        var jobs = Lane();
        var first = HandIn(jobs, Repo("tianwen"), Sleep(30), gui: true);
        await Until(first, j => j.State == JobState.Running);

        var second = HandIn(jobs, Repo("pilot"), Sleep(30), gui: true);
        var ordinary = HandIn(jobs, Repo("LAN.Lib"), Sleep(30), gui: false);

        await Until(ordinary, j => j.State == JobState.Running);
        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).Step.ShouldNotBeNull().ShouldContain(first);
        Row(ordinary).Gui.ShouldBeNull();
    }

    [Fact]
    public async Task TheGuiSlotIsFreedWhenTheGuiJobEndsAndTheWaitingOneStarts()
    {
        var (jobs, gone) = LaneAndGone();
        var first = HandIn(jobs, Repo("tianwen"), Sleep(30), gui: true);
        await Until(first, j => j.State == JobState.Running);
        var second = HandIn(jobs, Repo("pilot"), Sleep(30), gui: true);
        Row(second).State.ShouldBe(JobState.Queued);

        Fake!.Finish(first);
        await gone(first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        await Until(second, j => j.State == JobState.Running);
    }

    [Fact]
    public void AGuiJobOnANodeWithNoDesktopIsRefusedAtOnceAndNeverQueued()
    {
        var jobs = Lane(desktopRefusal: "the node runs in session 0, which has no desktop");

        var result = RunIn(jobs, Repo("tianwen"), Sleep(30), gui: true);

        result.Made.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain("session 0");
        _state.Jobs().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, null, "the GUI lane is off (gui-slots is 0)")]
    [InlineData(0, "no interactive desktop", "the GUI lane is off (gui-slots is 0)")]
    [InlineData(1, "no interactive desktop", "no interactive desktop")]
    [InlineData(1, null, null)]
    public void GuiWhyIsTheLaneOffAtZeroSlotsElseTheDesktopsAnswer(int slots, string? desktop, string? why) =>
        // #1085: what NodeApp puts in NodeInfo.CanRunGui and GuiReason, which pilot status's gui line reads (CliTests).
        Lane(desktop, slots).GuiWhy().ShouldBe(why);

    [Fact]
    public void AGuiJobWithGuiSlotsZeroIsRefusedAtOnceAndNeverQueued()
    {
        var jobs = Lane(slots: 0);

        var result = RunIn(jobs, Repo("tianwen"), Sleep(30), gui: true);

        result.Made.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain("gui-slots");
        _state.Jobs().ShouldBeEmpty();
        jobs.GuiWhy().ShouldNotBeNull();
    }

    [Fact]
    public void GuiWithAloneIsRefused()
    {
        var jobs = Lane();

        var result = RunIn(jobs, Repo("tianwen"), Sleep(30), gui: true, alone: true);

        result.Error.ShouldNotBeNull().ShouldContain(Verbs.AloneArg);
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void ARowFromBeforeTheLaneReadsNullNotGui()
    {
        var row = System.Text.Json.JsonSerializer.Deserialize("""{"id":"j1","command":"x","cwd":"y","state":"queued","logPath":"l","timeout":"02:00:00","startedAt":"2026-01-01T00:00:00+00:00"}""", PilotJsonContext.Default.JobRow);

        row.ShouldNotBeNull().Gui.ShouldBeNull();
    }

    [Fact]
    public void TheDesktopAnswersByPlatformFacts()
    {
        DesktopCapability.WhyLinux(null, null).ShouldNotBeNull();
        DesktopCapability.WhyLinux(":0", null).ShouldBeNull();
        DesktopCapability.WhyLinux(null, "wayland-0").ShouldBeNull();
        DesktopCapability.WhyWindows(0, "WinSta0").ShouldNotBeNull();
        DesktopCapability.WhyWindows(2, "Service-0x0-3e7$").ShouldNotBeNull();
        DesktopCapability.WhyWindows(2, "WinSta0").ShouldBeNull();
    }
}
