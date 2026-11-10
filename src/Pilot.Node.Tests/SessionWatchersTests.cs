using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A session's stop ends the background shells under its process (#870), never one whose pid was reused and never a person's interactive session's.</summary>
public sealed class SessionWatchersTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A table that lives in memory and records its kills, which remove the process.</summary>
    private sealed class Table : IProcessTable
    {
        public readonly List<ProcessEntry> Entries = [];
        public readonly Dictionary<int, DateTimeOffset> Starts = [];
        public readonly List<int> Killed = [];

        public Table Add(int pid, int parent, string name, DateTimeOffset start)
        {
            Entries.Add(new ProcessEntry(pid, parent, name));
            Starts[pid] = start;
            return this;
        }

        public IReadOnlyList<ProcessEntry> Snapshot() => [.. Entries];
        public DateTimeOffset? StartOf(int pid) => Starts.TryGetValue(pid, out var s) ? s : null;

        public void Kill(int pid)
        {
            Killed.Add(pid);
            Entries.RemoveAll(e => e.Pid == pid);
            Starts.Remove(pid);
        }
    }

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly StringWriter _log = new StringWriter();
    private readonly Table _table = new Table();
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;
    private readonly SessionMessenger _messenger;

    public SessionWatchersTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, _log) { Watchers = new SessionWatchers(_table) };
    }

    public void Dispose() => _profile.Dispose();

    private static CommandRequest Stop(string id) => new() { Verb = Verbs.Stop, Target = Verbs.SessionTarget(id), Who = "cli" };

    [Fact]
    public void CaptureReadsEveryDescendantAndNothingElse()
    {
        _table.Add(1, 0, "claude", T0).Add(2, 1, "pwsh", T0).Add(3, 2, "sleep", T0).Add(9, 0, "other", T0);

        var captured = new SessionWatchers(_table).Capture(1);

        captured.Select(w => w.Pid).ShouldBe([2, 3]);
    }

    [Fact]
    public void ASurvivorIsEndedAndAGoneOneIsNot()
    {
        _table.Add(1, 0, "claude", T0).Add(2, 1, "pwsh", T0).Add(3, 1, "sh", T0);
        var watchers = new SessionWatchers(_table);
        var captured = watchers.Capture(1);
        _table.Kill(3);
        _table.Killed.Clear();

        var ended = watchers.EndSurvivors(captured);

        ended.Select(w => w.Pid).ShouldBe([2]);
        _table.Killed.ShouldBe([2]);
    }

    [Fact]
    public void AReusedPidIsSpared()
    {
        _table.Add(1, 0, "claude", T0).Add(2, 1, "pwsh", T0);
        var watchers = new SessionWatchers(_table);
        var captured = watchers.Capture(1);
        _table.Kill(2);
        _table.Killed.Clear();
        _table.Add(2, 77, "notepad", T0.AddMinutes(5));

        watchers.EndSurvivors(captured).ShouldBeEmpty();

        _table.Killed.ShouldBeEmpty();
    }

    [Fact]
    public void ABrokenTableEndsNothingAndThrowsNothing()
    {
        var watchers = new SessionWatchers(new FakeProcessTable { Broken = true });

        watchers.Capture(1).ShouldBeEmpty();
        watchers.EndSurvivors([new SessionWatcher(2, "sh", T0)]).ShouldBeEmpty();
    }

    private async Task StartBackgroundAsync(bool interactive = false)
    {
        _agents.Json = AgentRows.List(interactive
            ? AgentRows.Interactive(AgentRows.SessionA, 1, "mine")
            : AgentRows.Background(AgentRows.SessionA, "pil5-end", "working", pid: 1, status: "busy"));
        await _tracker.PollAsync(Ct);
    }

    [Fact]
    public async Task StoppingABackgroundSessionEndsItsWatchersAndLogsThem()
    {
        _table.Add(1, 0, "claude", T0).Add(2, 1, "pwsh", T0).Add(3, 2, "sleep", T0);
        await StartBackgroundAsync();
        var id = _state.Sessions().Single().Id;

        (await _messenger.StopAsync(Stop(id), Ct)).Ok.ShouldBeTrue();

        _table.Killed.ShouldBe([2, 3]);
        _log.ToString().ShouldContain("watchers: ended 2 of pil5-end: pwsh 2, sleep 3");
    }

    [Fact]
    public async Task AStopWithNothingUnderTheSessionEndsNothingAndLogsNoWatchersLine()
    {
        _table.Add(1, 0, "claude", T0);
        await StartBackgroundAsync();
        var id = _state.Sessions().Single().Id;

        (await _messenger.StopAsync(Stop(id), Ct)).Ok.ShouldBeTrue();

        _table.Killed.ShouldBeEmpty();
        _log.ToString().ShouldNotContain("watchers:");
    }

    [Fact]
    public async Task AnInteractiveSessionIsRefusedAndItsProcessesAreNotTouched()
    {
        _table.Add(1, 0, "claude", T0).Add(2, 1, "pwsh", T0);
        await StartBackgroundAsync(interactive: true);
        var id = _state.Sessions().Single().Id;

        (await _messenger.StopAsync(Stop(id), Ct)).Ok.ShouldBeFalse();

        _table.Killed.ShouldBeEmpty();
    }

    /// <summary>The real thing, with no fake: a parent shell starts a child that loops with its own deadline, the parent is killed as a stop does, and the child goes with <see cref="SessionWatchers"/>.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARealChildLoopOfAStoppedParentEndsWithIt()
    {
        // Slow because it starts a real shell and a real child, and waits for the child's exit.
        var windows = OperatingSystem.IsWindows();
        var parentInfo = windows
            ? new ProcessStartInfo("pwsh", ["-NoProfile", "-Command", "$c = Start-Process pwsh -ArgumentList '-NoProfile','-Command','for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1 }' -PassThru -WindowStyle Hidden; $c.Id; Start-Sleep 60"])
            : new ProcessStartInfo("sh", ["-c", "sh -c 'i=0; while [ $i -lt 60 ]; do sleep 1; i=$((i+1)); done' & echo $!; wait"]);
        parentInfo.RedirectStandardOutput = true;
        parentInfo.UseShellExecute = false;
        parentInfo.CreateNoWindow = true;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var parent = Process.Start(parentInfo).ShouldNotBeNull();
        var childPid = int.Parse((await parent.StandardOutput.ReadLineAsync(deadline.Token)).ShouldNotBeNull().Trim());
        using var child = Process.GetProcessById(childPid);
        try
        {
            var watchers = new SessionWatchers(OsProcessTable.Instance);
            var captured = watchers.Capture(parent.Id);
            captured.ShouldContain(w => w.Pid == childPid);

            parent.Kill();
            await parent.WaitForExitAsync(deadline.Token);
            watchers.EndSurvivors(captured).ShouldContain(w => w.Pid == childPid);

            await child.WaitForExitAsync(deadline.Token);
            child.HasExited.ShouldBeTrue();
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }
}
