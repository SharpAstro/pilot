using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Who made the node's requests (#870), over an in-memory process table and a clock a test moves: no real process is read or
/// started, and nothing waits.
/// </summary>
public sealed class CommandCallersTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 10, 12, 0, 20, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot = Noon.AddDays(-2);
    private const string Loop = "bash -c \"while true; do pilot status; sleep 120; done\"";

    private readonly FakeProcessTable _table = new();
    private readonly ManualTime _time = new(Noon);
    private readonly CommandCallers _callers;

    public CommandCallersTests()
    {
        _callers = new CommandCallers(_table, _time);
        // systemd -> claude (a session) -> bash (its watcher loop); each pilot command is a new child of the bash.
        _table.Add(1, 0, "systemd", Boot)
            .Add(10, 1, "claude", Boot.AddHours(1), "claude --resume")
            .Add(20, 10, "bash", Boot.AddHours(2), Loop);
    }

    private static DateTimeOffset Hour(int h, int day = 10) => new(2026, 11, day, h, 0, 0, TimeSpan.Zero);

    private void RunPilot(int pid, int parent, string line = "pilot status") =>
        _table.Add(pid, parent, "pilot.exe", _time.GetUtcNow(), line);

    [Fact]
    public void ABashLoopThatCallsPilotEveryTwoMinutesIsOneCallerTheBashNotPilotExe()
    {
        for (var pid = 31; pid <= 33; pid++)
        {
            RunPilot(pid, 20);
            _callers.CountPeer(pid);
            _table.Remove(pid);
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        var rows = _callers.Rows(Hour(12));
        var row = rows.ShouldHaveSingleItem();
        row.Kind.ShouldBe(CallerKind.Process);
        row.Pid.ShouldBe(20);
        row.Start.ShouldBe(Boot.AddHours(2));
        row.Name.ShouldBe("bash");
        row.CommandLine.ShouldBe(Loop);
        row.ParentPid.ShouldBe(10);
        row.Via.ShouldBe(CallerVia.None);
        row.Count.ShouldBe(3);
    }

    [Fact]
    public void AGitBashLoopThatForksABashPerCommandIsOneCallerTheLoopBash()
    {
        // MSYS shape: the loop's bash 20 forks bash 21, 22, 23 (one per command), each the parent of a pilot.exe.
        for (var i = 0; i < 3; i++)
        {
            _table.Add(21 + i, 20, "bash.exe", _time.GetUtcNow(), Loop);
            RunPilot(31 + i, 21 + i);
            _callers.CountPeer(31 + i);
            _table.Remove(31 + i).Remove(21 + i);
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        var row = _callers.Rows(Hour(12)).ShouldHaveSingleItem();
        row.Pid.ShouldBe(20);
        row.Name.ShouldBe("bash");
        row.Count.ShouldBe(3);
    }

    [Fact]
    public void APidReusedByANewProcessIsANewCaller()
    {
        // The loop runs once, ends, and the OS hands bash's pid and the pilot's pid to new processes within the minute.
        RunPilot(31, 20);
        _callers.CountPeer(31);
        _table.Remove(31).Remove(20);
        _time.Advance(TimeSpan.FromMinutes(1));
        _table.Add(20, 10, "bash", _time.GetUtcNow(), "bash -c other-loop.sh");
        RunPilot(31, 20);
        _callers.CountPeer(31);

        var rows = _callers.Rows(Hour(12));
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.Pid == 20 && r.Count == 1);
        rows.Select(r => r.Start).Distinct().Count().ShouldBe(2);
        rows.Select(r => r.CommandLine).ShouldBe([Loop, "bash -c other-loop.sh"], ignoreOrder: true);
    }

    [Fact]
    public void RequestsLandInTheUtcClockHourTheyCameIn()
    {
        RunPilot(31, 20);
        _time.Advance(TimeSpan.FromMinutes(59).Add(TimeSpan.FromSeconds(30)));   // 12:59:50
        _callers.CountPeer(31);
        _time.Advance(TimeSpan.FromSeconds(15));                                 // 13:00:05
        _callers.CountPeer(31);
        _callers.CountPeer(31);

        _callers.Rows(Hour(12)).ShouldHaveSingleItem().Count.ShouldBe(1);
        _callers.Rows(Hour(13)).ShouldHaveSingleItem().Count.ShouldBe(2);
        _callers.Rows(Hour(14)).ShouldBeEmpty();
        _callers.Rows().Select(r => (r.Hour, r.Count)).ShouldBe([(Hour(12), 1), (Hour(13), 2)]);
    }

    [Fact]
    public void OnlyTheLastFortyEightHoursAreKept()
    {
        for (var i = 0; i < 50; i++)
        {
            _callers.CountLan("laptop");
            _time.Advance(TimeSpan.FromHours(1));
        }

        var hours = _callers.Rows().Select(r => r.Hour).ToList();
        hours.Count.ShouldBe(CommandCallers.KeptHours);
        hours[0].ShouldBe(Hour(12).AddHours(2));
        _callers.Rows(Hour(12)).ShouldBeEmpty();
    }

    [Fact]
    public void AResolutionIsCachedPerPidAndStartAndTheTableIsReadAtMostEveryFewSeconds()
    {
        RunPilot(31, 20);
        _callers.CountPeer(31);
        var (snapshots, details, entries) = (_table.Snapshots, _table.DetailReads, _table.EntryReads);
        snapshots.ShouldBe(1);

        // The same peer again and again: nothing more is read from the table beyond its start (which is what tells a reused pid).
        for (var i = 0; i < 200; i++)
        {
            _callers.CountPeer(31);
        }
        (_table.Snapshots, _table.DetailReads, _table.EntryReads).ShouldBe((snapshots, details, entries));
        _callers.Rows(Hour(12)).ShouldHaveSingleItem().Count.ShouldBe(201);

        // A new pilot inside the window of the snapshot shares it: it is read on its own, the table is not read again.
        RunPilot(32, 20);
        _time.Advance(TimeSpan.FromSeconds(1));
        _callers.CountPeer(32);
        _table.Snapshots.ShouldBe(1);

        // Past the window, the next new peer reads the table once, and the caller (bash) is still known, its command line unread again.
        RunPilot(33, 20);
        _time.Advance(TimeSpan.FromSeconds(5));
        var before = _table.DetailReads;
        _callers.CountPeer(33);
        _table.Snapshots.ShouldBe(2);
        _table.DetailReads.ShouldBe(before + 1, "only the pilot's own command line (is it the MCP server?); the bash was read once");
        _callers.SnapshotsTaken.ShouldBe(2);
        _callers.Rows(Hour(12)).ShouldHaveSingleItem().Count.ShouldBe(203);
    }

    [Fact]
    public void AResolutionIsWalkedAgainOnceItsLifeIsOver()
    {
        RunPilot(31, 20);
        _callers.CountPeer(31);
        var before = _table.Snapshots;

        _time.Advance(TimeSpan.FromMinutes(3));
        _callers.CountPeer(31);
        _table.Snapshots.ShouldBe(before, "within its life");

        _time.Advance(TimeSpan.FromMinutes(10));
        _callers.CountPeer(31);
        _table.Snapshots.ShouldBe(before + 1);
    }

    [Fact]
    public void AProcessNewerThanTheSnapshotIsWalkedUpWithoutReadingTheTableAgain()
    {
        _callers.CountPeer(20);   // the first snapshot
        _table.Snapshots.ShouldBe(1);
        _table.AddNewer(41, 20, "pilot.exe", _time.GetUtcNow(), "pilot status");

        _callers.CountPeer(41);

        _table.Snapshots.ShouldBe(1);
        _callers.Rows(Hour(12)).ShouldHaveSingleItem().Pid.ShouldBe(20);
    }

    [Fact]
    public void TheMcpServerTheGuiAndTheTuiAreWalkedPastAndSaidInVia()
    {
        // claude -> pilot mcp -> the one it handed over to: the peer is the last, the caller is the session.
        _table.Add(50, 10, "pilot.exe", Boot.AddHours(3), "pilot.exe --node-socket s mcp")
            .Add(51, 50, "pilot.exe", Boot.AddHours(3), "pilot.exe --node-socket s mcp --handed-over")
            .Add(5, 1, "explorer.exe", Boot, "explorer.exe")
            .Add(60, 5, "pilot-gui.exe", Boot.AddHours(4), "pilot-gui.exe")
            .Add(5000, 1, "pwsh", Boot, "pwsh")
            .Add(61, 5000, "pilot-tui", Boot.AddHours(4), "pilot-tui");
        RunPilot(31, 20);
        _callers.CountPeer(31);   // the session's own bash: not through anything
        _callers.CountPeer(51);

        _callers.CountPeer(60);
        _callers.CountPeer(61);

        var rows = _callers.Rows(Hour(12)).ToDictionary(r => r.Pid);
        rows.Keys.ShouldBe([20, 10, 5, 5000], ignoreOrder: true);
        rows[20].Via.ShouldBe(CallerVia.None);
        rows[10].Via.ShouldBe(CallerVia.Mcp);
        rows[10].Name.ShouldBe("claude");
        rows[5].Via.ShouldBe(CallerVia.Gui);
        rows[5].Name.ShouldBe("explorer.exe");
        rows[5000].Via.ShouldBe(CallerVia.Tui);
    }

    [Fact]
    public void ACallerThatRunsThroughTheMcpServerAndWithoutKeepsBothInOneRow()
    {
        _table.Add(50, 10, "pilot.exe", Boot.AddHours(3), "pilot.exe mcp");
        RunPilot(31, 10);
        _callers.CountPeer(50);
        _callers.CountPeer(31);

        var row = _callers.Rows(Hour(12)).ShouldHaveSingleItem();
        row.Pid.ShouldBe(10);
        row.Count.ShouldBe(2);
        row.Via.ShouldBe(CallerVia.Mcp);
    }

    [Fact]
    public void AMovedAsideInstallOfPilotIsStillPilot()
    {
        _table.Add(31, 20, "pilot.exe~old-1", _time.GetUtcNow(), "pilot status");
        _callers.CountPeer(31);
        _callers.Rows(Hour(12)).ShouldHaveSingleItem().Pid.ShouldBe(20);
    }

    [Fact]
    public void APilotWhoseParentIsGoneStandsForItsCaller()
    {
        _table.Add(31, 9999, "pilot.exe", _time.GetUtcNow(), "pilot status");
        _callers.CountPeer(31);

        var row = _callers.Rows(Hour(12)).ShouldHaveSingleItem();
        row.Pid.ShouldBe(31);
        row.Name.ShouldBe("pilot.exe");
        row.ParentPid.ShouldBe(9999);
    }

    [Fact]
    public void AnOrphanKeepsItsDeadParentsPidSoItCanBeToldFromTheLiveOnes()
    {
        _table.Add(70, 4242, "bash", Boot, "bash watch.sh");
        RunPilot(71, 70);
        _callers.CountPeer(71);

        var row = _callers.Rows(Hour(12)).ShouldHaveSingleItem();
        row.Pid.ShouldBe(70);
        row.ParentPid.ShouldBe(4242);
    }

    [Fact]
    public void AReusedPidThatClosesALoopInTheParentsEndsTheWalk()
    {
        _table.Add(80, 81, "pilot.exe", Boot, "pilot status").Add(81, 80, "pilot.exe", Boot, "pilot status");
        _callers.CountPeer(80);
        _callers.Rows(Hour(12)).ShouldHaveSingleItem().Count.ShouldBe(1);
    }

    [Fact]
    public void APeerThatCannotBeResolvedIsCountedAsUnknownAndNothingThrows()
    {
        _callers.CountPeer(null);
        _callers.CountPeer(0);
        _callers.CountPeer(31337);   // not in the table
        _table.Broken = true;
        _callers.CountPeer(20);

        var row = _callers.Rows(Hour(12)).ShouldHaveSingleItem();
        row.Kind.ShouldBe(CallerKind.Unknown);
        row.Name.ShouldBe("unknown");
        row.Pid.ShouldBe(0);
        row.Count.ShouldBe(4);
    }

    [Fact]
    public void ALanRequestIsCountedByItsGrantLabelWithNoPid()
    {
        _callers.CountLan("laptop");
        _callers.CountLan("laptop");
        _callers.CountLan("tablet");

        var rows = _callers.Rows(Hour(12));
        rows.Select(r => (r.Kind, r.Name, r.Pid, r.Count)).ShouldBe([(CallerKind.Lan, "laptop", 0, 2), (CallerKind.Lan, "tablet", 0, 1)]);
        _table.Snapshots.ShouldBe(0, "a LAN request has no process here to look up");
    }

    [Fact]
    public void TheTopThreeAreNamedMostFirstAndAnEmptyHourSaysNone()
    {
        _callers.TopLine(Hour(12)).ShouldBe("top callers: none");
        _table.Add(30, 1, "pwsh", Boot, "pwsh -NoProfile");
        for (var i = 0; i < 9; i++)
        {
            _callers.CountPeer(20);
        }
        for (var i = 0; i < 7; i++)
        {
            _callers.CountLan("laptop");
        }
        for (var i = 0; i < 5; i++)
        {
            _callers.CountPeer(30);
        }
        _callers.CountPeer(null);

        _callers.TopLine(Hour(12)).ShouldBe($"top callers: bash 20 ({Loop}) 9, laptop - (LAN grant) 7, pwsh 30 (pwsh -NoProfile) 5");
        _callers.Top(Hour(12)).Count.ShouldBe(CommandCallers.TopCount);
        _callers.Rows(Hour(12)).Count.ShouldBe(4);
    }

    [Fact]
    public void ALongCommandLineIsClippedAndStaysOnOneLine()
    {
        var wide = "bash -c \"" + new string('x', 200) + "\"\nsecond  line";
        _table.Add(90, 1, "bash", Boot, wide);
        _callers.CountPeer(90);

        var line = _callers.Rows(Hour(12)).ShouldHaveSingleItem().Line();
        line.ShouldNotContain('\n');
        line.ShouldStartWith("bash 90 (bash -c \"xxx");
        line.ShouldContain("...) 1");
        line.Length.ShouldBeLessThan(CallerRow.LineClip + 30);

        _table.Add(91, 1, "bash", Boot, "bash\n-c\t  go");
        _callers.CountPeer(91);
        _callers.Rows(Hour(12)).Single(r => r.Pid == 91).Line().ShouldBe("bash 91 (bash -c go) 1");
    }

    [Fact]
    public void ACallerWithNoCommandLineSaysSo()
    {
        _table.Add(92, 1, "kernel", Boot);
        _callers.CountPeer(92);
        _callers.Rows(Hour(12)).Single(r => r.Pid == 92).Line().ShouldBe("kernel 92 (no command line) 1");
    }

    [Theory]
    [InlineData("pilot.exe --node-socket s mcp", true)]
    [InlineData("\"C:\\Program Files\\pilot\\pilot.exe\" mcp --handed-over", true)]
    [InlineData("pilot status", false)]
    [InlineData("pilot say mcpx", false)]
    [InlineData(null, false)]
    public void OnlyAnArgumentThatIsExactlyMcpMakesTheMcpServer(string? line, bool mcp) =>
        CommandCallers.IsMcp(line).ShouldBe(mcp);

    [Fact]
    public void TheRealTableReadsOneProcessOnItsOwnWithItsParent()
    {
        // This test process: read, never started. Its parent is a live process the OS names.
        var entry = OsProcessTable.Instance.EntryOf(Environment.ProcessId).ShouldNotBeNull();
        entry.Pid.ShouldBe(Environment.ProcessId);
        entry.ParentPid.ShouldBeGreaterThan(0);
        entry.Name.ShouldNotBeNullOrWhiteSpace();
        OsProcessTable.Instance.EntryOf(int.MaxValue - 1).ShouldBeNull();
    }
}
