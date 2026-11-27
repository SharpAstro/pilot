using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The run record of #1377: written at start, given a reason by each planned exit, and read by the next start, which says when the last node vanished.</summary>
public sealed class NodeExitRecordTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 16, 40, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2x-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    private readonly Clock _clock = new(T0);

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private NodeExitRecord Record() => new(_data, _clock);

    private NodeRunFacts Facts(int pid = 100, string? job = null, DateTimeOffset? at = null) =>
        new(pid, at ?? _clock.Now, "2.1.1", "pid 7 pwsh", "arguments: --lan", job);

    private string WriteLog(params string[] lines)
    {
        var path = Path.Combine(_data, "node.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void AStartWritesTheRunRecordWithPidStartVersionAndHowItWasStarted()
    {
        var record = Record();

        record.Begin(Facts(), null).ShouldBeNull();

        var read = Record().Read().ShouldNotBeNull();
        (read.Pid, read.StartedAt, read.Version, read.Parent, read.StartedHow, read.ExitReason).ShouldBe((100, T0, "2.1.1", "pid 7 pwsh", "arguments: --lan", null));
    }

    [Fact]
    public void AStopGivesItsReasonAndTheFirstReasonStays()
    {
        var record = Record();
        record.Begin(Facts(), null);
        _clock.Now = T0.AddMinutes(5);

        record.Planned("signal SIGTERM");
        record.Planned("stop");

        var read = Record().Read().ShouldNotBeNull();
        (read.ExitReason, read.EndedAt).ShouldBe(("signal SIGTERM", T0.AddMinutes(5)));
    }

    [Fact]
    public void ARestartGivesTheRunRecordItsReason()
    {
        var record = Record();
        record.Begin(Facts(), null);
        var restarter = new NodeRestarter(new FakeLifetime(), NullLogger<NodeRestarter>.Instance)
        {
            NodePath = "/opt/pilot/pilot-node",
            RestartedBySystemd = _ => false,
            StartSuccessor = _ => true,
            Exit = record,
        };

        restarter.Restart("a saved setting").ShouldBeNull();

        record.Read().ShouldNotBeNull().ExitReason.ShouldBe("restart: a saved setting");
    }

    [Fact]
    public void ATestHostWithoutABeginWritesNothingForAPlannedExit()
    {
        Record().Planned("stop");

        Record().Read().ShouldBeNull();
    }

    [Fact]
    public void AStartThatFindsNoExitReasonRaisesOneCardNamingTheMinutesAndTheLastLine()
    {
        var first = Record();
        first.Begin(Facts(pid: 100), null);
        var last = T0.AddMinutes(10);
        var log = WriteLog(
            $"{Stamp(T0.AddMinutes(1))} timing: earlier line",
            $"{Stamp(last)} timing: command run session/70b2d8b9 by se1-pil1374-review-notes waited 0 ms, ran 2 ms");
        // The node is back 6 minutes after its last line; its own start lines are stamped after it started and are not "the last line".
        _clock.Now = last.AddMinutes(6);
        File.AppendAllText(log, $"{Stamp(_clock.Now.AddSeconds(1))} pilot-node: skill kept{Environment.NewLine}");
        var second = Record();
        var said = new List<string>();
        var state = new NodeState(new InMemoryJournal(), _clock);

        var found = second.Begin(Facts(pid: 200, at: _clock.Now), log).ShouldNotBeNull();
        second.RaiseCards(state, said.Add);

        found.Minutes.ShouldBe(6);
        found.LastLine.ShouldBe("timing: command run session/70b2d8b9 by se1-pil1374-review-notes waited 0 ms, ran 2 ms");
        var card = state.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Title.ShouldBe("The node was down 6 min");
        card.Kind.ShouldBe(NodeLifecycleLog.DownKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Detail.ShouldNotBeNull().ShouldContain("session/70b2d8b9");
        card.Detail.ShouldContain("pid 100");
        said.ShouldHaveSingleItem().ShouldContain("ended with nothing said");
        said[0].ShouldContain("down 6 min");
    }

    [Fact]
    public void AStartAfterAConsoleCloseRaisesTheCardAndNamesTheSignalButACtrlCRaisesNone()
    {
        var first = Record();
        first.Begin(Facts(pid: 100), null);
        first.Planned("signal CTRL_CLOSE, the console window closing (SIGHUP)");
        _clock.Now = T0.AddMinutes(3);
        var second = Record();
        var state = new NodeState(new InMemoryJournal(), _clock);

        second.Begin(Facts(pid: 200), null).ShouldNotBeNull().Signalled.ShouldNotBeNull().ShouldContain("CTRL_CLOSE");
        second.RaiseCards(state, _ => { });
        state.Cards(openOnly: true).ShouldHaveSingleItem().Detail.ShouldNotBeNull().ShouldContain("was ended by signal CTRL_CLOSE");

        var third = Record();
        third.Begin(Facts(pid: 300), null);
        third.Planned("signal CTRL_C (SIGINT)");
        Record().Begin(Facts(pid: 400), null).ShouldBeNull();
    }

    [Fact]
    public void AStartTheWatchdogRestartedNamesItsOwnGapNotTheTimeSinceTheLastLine()
    {
        Record().Begin(Facts(pid: 100), null);
        _clock.Now = T0.AddMinutes(125);
        var second = new NodeExitRecord(_data, _clock) { WatchdogValue = () => "down=30;exit=-1;fast=1" };

        var found = second.Begin(Facts(pid: 200, at: _clock.Now), null).ShouldNotBeNull();

        found.Title.ShouldBe("The node was down 30 s");
        found.Said.ShouldContain("125 min before this start");
    }

    [Fact]
    public void AStartAfterACleanExitRaisesNoCard()
    {
        var first = Record();
        first.Begin(Facts(pid: 100), null);
        first.Planned("stop");
        _clock.Now = T0.AddMinutes(30);
        var second = Record();
        var state = new NodeState(new InMemoryJournal(), _clock);

        second.Begin(Facts(pid: 200), WriteLog($"{Stamp(T0)} pilot-node: stop")).ShouldBeNull();
        second.RaiseCards(state, _ => { });

        state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public void ANodeInAKillOnCloseJobRaisesOneCardNamingItsParent()
    {
        var record = Record();
        record.Begin(Facts(job: "in a job object that kills its processes when the job closes"), null);
        var state = new NodeState(new InMemoryJournal(), _clock);

        record.RaiseCards(state, _ => { });

        var card = state.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Kind.ShouldBe(NodeLifecycleLog.JobKind);
        card.Detail.ShouldNotBeNull().ShouldContain("pid 7 pwsh");
    }

    [Fact]
    public void ANodeInNoJobOrOneThatDoesNotKillRaisesNoJobCard()
    {
        var record = Record();
        record.Begin(Facts(job: "in a job object that does not kill on close"), null);
        var state = new NodeState(new InMemoryJournal(), _clock);

        record.RaiseCards(state, _ => { });

        state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public void ARecordFromAnOlderNodeWithMembersMissingReadsThemAsNull()
    {
        File.WriteAllText(Path.Combine(_data, NodeExitRecord.FileName), "{\"pid\":5}");

        var read = Record().Read().ShouldNotBeNull();

        (read.Pid, read.StartedAt, read.ExitReason).ShouldBe((5, null, null));
    }

    [Fact]
    public async Task AHostThatStopsWritesStopAsTheReasonInTheRunRecord()
    {
        await using var node = await NodeHarness.StartAsync(TestContext.Current.CancellationToken);
        var path = Path.Combine(node.Options.DataFolder, NodeExitRecord.FileName);
        File.Exists(path).ShouldBeTrue();

        await node.App.StopAsync(TestContext.Current.CancellationToken);

        new NodeExitRecord(node.Options.DataFolder, TimeProvider.System).Read().ShouldNotBeNull().ExitReason.ShouldBe("stop");
    }

    [Fact]
    public async Task ASecondNodeRefusedAsAlreadyRunningLeavesTheHoldersRecordAsItWasAndKeepsItsRefusalInTheStartLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(ct);
        var path = Path.Combine(node.Options.DataFolder, NodeExitRecord.FileName);
        var before = await File.ReadAllBytesAsync(path, ct);

        var exit = await NodeCommandLine.RunAsync(["--node-socket", node.SocketPath, "--data", node.Options.DataFolder], Stream.Null, TextWriter.Null, TextWriter.Null, ct);

        exit.ShouldBe(NodeExitCodes.AlreadyRunning);
        (await File.ReadAllBytesAsync(path, ct)).ShouldBe(before);
        File.ReadAllText(Path.Combine(node.Options.DataFolder, NodeProgram.StartLogFile)).ShouldContain("exited, already running");
    }

    [Fact]
    public void ARestartBuiltWithNoRecordTouchesNoRecordAndCreatesNoFolder()
    {
        var gone = Path.Combine(_data, "never-begun");
        var restarter = new NodeRestarter(new FakeLifetime(), NullLogger<NodeRestarter>.Instance)
        {
            NodePath = "/opt/pilot/pilot-node",
            RestartedBySystemd = _ => false,
            StartSuccessor = _ => true,
        };
        var other = new NodeExitRecord(gone, _clock);
        other.Begin(Facts(), null);
        Directory.Delete(gone, recursive: true);

        restarter.Restart("a saved setting").ShouldBeNull();

        Directory.Exists(gone).ShouldBeFalse();
    }

    [Fact]
    public void PlannedOnARecordWhoseFolderIsGoneDoesNotCreateTheFolderAgain()
    {
        var gone = Path.Combine(_data, "gone");
        var record = new NodeExitRecord(gone, _clock);
        record.Begin(Facts(), null);
        Directory.Delete(gone, recursive: true);

        record.Planned("stop");

        Directory.Exists(gone).ShouldBeFalse();
    }
}
