using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The poller sweep (pilot #870) over an in-memory node state, the real per-caller tally on a fake process table and a clock a test
/// moves: no real process is read or ended, and nothing waits.
/// </summary>
public sealed class PollerSweepTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 10, 0, 20, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot = Start.AddDays(-2);
    private const string Plane = @"C:\repos\pilot\.claude\worktrees\pil870";

    private sealed class Table : IProcessTable
    {
        public List<ProcessEntry> Entries { get; } = [];
        public Dictionary<int, DateTimeOffset> Starts { get; } = [];
        public Dictionary<int, string?> Lines { get; } = [];
        public List<int> Killed { get; } = [];

        public void Add(int pid, int parent, string name, DateTimeOffset start, string? line = null)
        {
            Entries.Add(new ProcessEntry(pid, parent, name));
            Starts[pid] = start;
            Lines[pid] = line;
        }

        public void Remove(int pid)
        {
            Entries.RemoveAll(e => e.Pid == pid);
            Starts.Remove(pid);
        }

        public IReadOnlyList<ProcessEntry> Snapshot() => [.. Entries];

        public DateTimeOffset? StartOf(int pid) => Starts.TryGetValue(pid, out var start) ? start : null;

        public void Kill(int pid) => Killed.Add(pid);

        public ProcessDetail? DetailsOf(int pid) => Lines.TryGetValue(pid, out var line) ? new ProcessDetail(line, null) : null;
    }

    private readonly Table _table = new();
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly CommandCallers _callers;
    private readonly PollerSweep _sweep;
    private readonly List<int> _live = [];

    public PollerSweepTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _callers = new CommandCallers(_table, _time) { SnapshotEvery = TimeSpan.Zero };
        // systemd -> bash 20 (a watch loop) -> pilot 31 (one command of it).
        _table.Add(1, 0, "systemd", Boot);
        _table.Add(20, 1, "bash", Boot.AddHours(2), "bash -c \"while true; do pilot status; sleep 10; done\"");
        _table.Add(31, 20, "pilot.exe", Start, "pilot status");
        _sweep = new PollerSweep(_state, _callers, _table, _time) { Windows = true, LivePids = () => [.. _live] };
    }

    // One clock hour of n requests from the peer, then the clock at the start of the next hour plus a little.
    private void Hour(int peer, int n)
    {
        for (var i = 0; i < n; i++)
        {
            _callers.CountPeer(peer);
        }
        _time.Advance(NodeTraffic.HourOf(_time.GetUtcNow()).AddHours(1).AddSeconds(30) - _time.GetUtcNow());
    }

    private CardRow[] Open() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == PollerSweep.Kind)];

    [Fact]
    public void AFakeCallerAt300AnHourFor3HoursRingsExactlyOneCardNamingIt()
    {
        Hour(31, 300);
        _sweep.SweepOnce();
        Open().ShouldBeEmpty("one full hour over the rate is not a poller yet");
        Hour(31, 300);
        Hour(31, 300);
        _sweep.SweepOnce().ShouldBe([(20, "bash")]);
        _sweep.SweepOnce();

        var card = Open().ShouldHaveSingleItem();
        card.Kind.ShouldBe(PollerSweep.Kind);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("20");
        card.Title.ShouldContain("bash");
        card.Choices.Select(c => (c.Verb, c.Target)).ShouldBe([(Verbs.EndPoller, card.Subject)]);
        card.Detail.ShouldNotBeNull();
        card.Detail.ShouldContain("parent 1 (systemd)");
        card.Detail.ShouldContain("300 times an hour for 3 hours");
        card.Detail.ShouldContain("while true; do pilot status");
        card.Detail.ShouldContain("Stop-Process -Id 20 -Force");
    }

    [Fact]
    public void AJobsProcessAtThatRateRingsNone()
    {
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = Plane, State = JobState.Running, LogPath = @"C:\data\j1.log",
            Timeout = TimeSpan.FromHours(5), StartedAt = Start, Pid = 20, ProcessStartedAt = Start,
        });
        Hour(31, 300);
        Hour(31, 300);
        Hour(31, 300);
        _sweep.SweepOnce();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public void ASessionsProcessAtThatRateRingsNone()
    {
        // claude 10 is a live session; the watch loop 20 runs under it.
        _table.Add(10, 1, "claude", Boot.AddHours(1), "claude --resume");
        _table.Entries.RemoveAll(e => e.Pid == 20);
        _table.Add(20, 10, "bash", Boot.AddHours(2), "bash loop");
        _live.Add(10);
        Hour(31, 300);
        Hour(31, 300);
        Hour(31, 300);
        _sweep.SweepOnce();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public void ThePilotMcpServerUnderASessionAtThatRateRingsNone()
    {
        // A session's own pilot mcp server is walked past to claude 40, which is the live session.
        _table.Add(40, 1, "claude", Boot, "claude");
        _table.Add(41, 40, "pilot.exe", Start, "pilot mcp");
        _live.Add(40);
        for (var h = 0; h < 3; h++)
        {
            Hour(41, 300);
        }
        _sweep.SweepOnce();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public void AStoppedCallersCardClosesAfterAFullHourUnderTheRateAndWhenItsProcessIsGone()
    {
        Hour(31, 300);
        Hour(31, 300);
        Hour(31, 300);
        _sweep.SweepOnce();
        Open().Length.ShouldBe(1);

        // A full hour of 5 requests: under the rate.
        Hour(31, 5);
        _sweep.SweepOnce();
        Open().ShouldBeEmpty();

        // And a card whose process went away.
        Hour(31, 300);
        Hour(31, 300);
        _sweep.SweepOnce();
        Open().Length.ShouldBe(1);
        _table.Remove(20);
        _sweep.SweepOnce();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public void ADismissedCardIsNotRaisedAgainForTheSamePidAndStart()
    {
        Hour(31, 300);
        Hour(31, 300);
        _sweep.SweepOnce();
        var card = Open().ShouldHaveSingleItem();
        _state.CloseCard(card.Id, "dismissed", "gui").ShouldBeTrue();

        Hour(31, 300);
        _sweep.SweepOnce();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public void PressingEndKillsOnlyWhileTheStartStillMatches()
    {
        Hour(31, 300);
        Hour(31, 300);
        _sweep.SweepOnce();
        var card = Open().ShouldHaveSingleItem();

        // The pid is another process by now.
        _table.Starts[20] = Boot.AddDays(1);
        _sweep.EndOnWord(new CommandRequest { Verb = Verbs.EndPoller, Target = card.Subject, Who = "gui" }).Error.ShouldNotBeNull();
        _table.Killed.ShouldBeEmpty();

        _table.Starts[20] = Boot.AddHours(2);
        _sweep.EndOnWord(new CommandRequest { Verb = Verbs.EndPoller, Target = card.Subject, Who = "gui" }).Error.ShouldBeNull();
        _table.Killed.ShouldBe([20]);
        Open().ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Kind == PollerSweep.EndedKind && c.Bin == CardBin.Handled);
        _sweep.EndOnWord(new CommandRequest { Verb = Verbs.EndPoller, Target = "process:999@1", Who = "gui" }).Error.ShouldNotBeNull();
    }

    [Fact]
    public void EndingAPollerIsThePersonsAloneAndAnAgentsPressIsRefused()
    {
        Standing.YourOnlyVerbs.ShouldContain(Verbs.EndPoller);
        var press = new CommandRequest { Verb = Verbs.EndPoller, Target = "process:20@1", Who = "cli", Agent = true };
        Standing.YourOnly(press, _state).ShouldBeTrue();
    }
}
