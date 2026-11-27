using System.Diagnostics;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The watchdog of #1377: it waits on the node's exit (no polling of the node), gives a successor a grace to answer, starts the node when none
/// does, never starts a second node when one answers, and stops on the exit codes that must not be retried. Every unit test runs on a fake process
/// table, a fake starter and a fake clock whose timers fire at once, so nothing waits and nothing runs.
/// </summary>
public sealed class NodeWatchdogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 16, 40, 0, TimeSpan.Zero);

    // A clock whose timers fire at once and move it by their due time: a Task.Delay costs no wall clock and is recorded.
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;

        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => Now;

        // Task.Delay(.., TimeProvider, ..) hands its due time to CreateTimer and never calls Change, so the timer fires from either.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class Timer(FakeTime clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    clock.Delays.Add(dueTime);
                    clock.Now += dueTime;
                    ThreadPool.QueueUserWorkItem(_ => callback(state));
                }
                return true;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // The machine: who answers on the socket is a script, a process ends as scripted, and a start hands out the next pid.
    private sealed class FakeHost(CancellationTokenSource stop) : IWatchdogHost
    {
        public Func<int?> Answering { get; set; } = () => null;

        /// <summary>The exit each wait gets, in order; once they run out the watchdog's owner ends (the token is cancelled).</summary>
        public Queue<WatchdogExit?> Exits { get; } = new();

        public List<WatchdogStart> Starts { get; } = [];

        public List<int> Waited { get; } = [];

        public int AnswerCalls { get; private set; }

        public int NextPid { get; set; } = 100;

        public int?[] StartResults { get; set; } = [];

        public bool Wanted { get; set; } = true;

        public List<string> Said { get; } = [];

        public Task<int?> AnsweringPidAsync(CancellationToken cancellationToken)
        {
            AnswerCalls++;
            return Task.FromResult(Answering());
        }

        public Task<WatchdogExit?> WaitForExitAsync(int pid, TimeSpan max, CancellationToken cancellationToken)
        {
            Waited.Add(pid);
            if (Exits.Count == 0)
            {
                stop.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Exits.Dequeue());
        }

        public int? StartNode(WatchdogStart how)
        {
            Starts.Add(how);
            return Starts.Count <= StartResults.Length ? StartResults[Starts.Count - 1] : NextPid++;
        }

        public bool StillWanted() => Wanted;

        public void Say(string line) => Said.Add(line);
    }

    private readonly FakeTime _time = new();
    private readonly CancellationTokenSource _stop = new();

    private FakeHost Host() => new(_stop);

    // A deadline so a watchdog stuck on a fake fails the test in seconds instead of holding the run.
    private Task<int> Run(FakeHost host) => new NodeWatchdog(host, _time).RunAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task ItWaitsOnTheNodesExitAndDoesNotAskTheSocketWhileItRuns()
    {
        var host = Host();
        host.Answering = () => host.AnswerCalls == 1 ? 10 : null;
        // Two waits end with the node still running (the entry's timer wake); the third finds the owner gone.
        host.Exits.Enqueue(null);
        host.Exits.Enqueue(null);

        await Run(host);

        host.Waited.ShouldBe([10, 10, 10], "every wait is on the process itself");
        host.AnswerCalls.ShouldBe(1, "the socket is asked once, to find the node, and never while the node runs");
        host.Starts.ShouldBeEmpty();
        _time.Delays.ShouldBeEmpty("no timer, no poll: the clock never moved");
    }

    [Fact]
    public async Task ASuccessorThatAnswersWithinTheGraceIsFollowedAndNoNodeIsStarted()
    {
        var host = Host();
        // 10 answers at the start; after its exit the socket is silent for 3 asks (the update's successor waits for --after), then pid 20 answers.
        var asks = 0;
        host.Answering = () => ++asks == 1 ? 10 : asks >= 5 ? 20 : null;
        host.Exits.Enqueue(new WatchdogExit(0));

        await Run(host);

        host.Starts.ShouldBeEmpty();
        host.Waited.ShouldBe([10, 20]);
        host.AnswerCalls.ShouldBe(5);
        _time.Delays.ShouldAllBe(d => d == NodeWatchdog.GraceStep, "the grace waits a step at a time, on the clock");
    }

    [Fact]
    public async Task WhenNoSuccessorAnswersWithinTheGraceTheNodeIsStartedAgainAndSaysHowLongItWasDown()
    {
        var host = Host();
        var asks = 0;
        host.Answering = () => ++asks == 1 ? 10 : null;
        host.Exits.Enqueue(new WatchdogExit(137));

        await Run(host);

        var start = host.Starts.ShouldHaveSingleItem();
        start.SecondsDown.ShouldBe((int)NodeWatchdog.Grace.TotalSeconds);
        start.PreviousExit.ShouldBe(137);
        start.FastDeaths.ShouldBe(1);
        _time.Delays.Sum(d => d.TotalSeconds).ShouldBeGreaterThanOrEqualTo(NodeWatchdog.Grace.TotalSeconds);
        host.Said.ShouldContain(l => l.Contains("restarted the node as pid 100") && l.Contains("30 s down") && l.Contains("137"));
    }

    [Fact]
    public async Task ANodeThatAlreadyAnswersAtTheStartIsNeverJoinedByASecondOne()
    {
        var host = Host();
        host.Answering = () => 10;
        host.Exits.Enqueue(new WatchdogExit(0));

        await Run(host);

        host.Starts.ShouldBeEmpty("another node answers, so a start would only be refused with AlreadyRunning");
    }

    [Fact]
    public async Task NoNodeAtTheStartIsStartedOnce_WithNothingToSayAboutAGap()
    {
        var host = Host();
        host.Exits.Enqueue(new WatchdogExit(1));
        host.Answering = () => host.Starts.Count == 0 ? null : 100;

        await Run(host);

        var first = host.Starts[0];
        (first.SecondsDown, first.PreviousExit, first.FastDeaths).ShouldBe((0, null, 0));
        NodeWatchdog.Encode(first).ShouldBeNull("a first start tells the node nothing");
    }

    [Theory]
    [InlineData(NodeExitCodes.AlreadyRunning)]
    [InlineData(NodeExitCodes.StoreTooNew)]
    public async Task AnExitCodeThatWouldBeRefusedAgainEndsTheWatchdogWithThatCode(int code)
    {
        var host = Host();
        host.Answering = () => host.AnswerCalls == 1 ? 10 : null;
        host.Exits.Enqueue(new WatchdogExit(code));

        (await Run(host)).ShouldBe(code);

        host.Starts.ShouldBeEmpty();
        _time.Delays.ShouldBeEmpty("no grace is waited for a node that must not be restarted");
    }

    [Fact]
    public async Task ANodeAPersonStartedDuringTheBackoffIsFollowedAndNoSecondIsStarted()
    {
        var host = Host();
        // Nobody answers until the back-off has been waited; then a node does (pid 77), and it later exits for good.
        host.Answering = () => _time.Delays.Contains(NodeWatchdog.Backoff) ? 77 : null;
        for (var i = 0; i < 3; i++)
        {
            host.Exits.Enqueue(new WatchdogExit(1));
        }
        host.Exits.Enqueue(new WatchdogExit(NodeExitCodes.AlreadyRunning));

        await Run(host);

        host.Starts.Count.ShouldBe(3, "the fourth start never happens: pid 77 answered after the back-off");
        host.Waited.ShouldContain(77);
        host.Said.ShouldContain(l => l.Contains("a node answers as pid 77"));
    }

    [Fact]
    public async Task RepeatedFastDeathsWaitTheBackoffBeforeTheStartAndTellTheNode()
    {
        var host = Host();
        // Nobody answers; each started node dies at once.
        for (var i = 0; i < 3; i++)
        {
            host.Exits.Enqueue(new WatchdogExit(1));
        }

        await Run(host);

        host.Starts.Select(s => s.FastDeaths).ShouldBe([0, 1, 2, 3]);
        _time.Delays.Count(d => d == NodeWatchdog.Backoff).ShouldBe(1, "only the start after the third death waits the back-off");
        host.Said.ShouldContain(l => l.Contains("died within") && l.Contains("waiting 5 min"));
    }

    [Fact]
    public async Task DeathsFarApartAreNotFastAndNeverBackOff()
    {
        var host = Host();
        host.Exits.Enqueue(new WatchdogExit(1));
        host.Exits.Enqueue(new WatchdogExit(1));
        host.Exits.Enqueue(new WatchdogExit(1));
        // Each node runs for ten minutes before it dies: the clock moves while the wait is on.
        host.Answering = () => null;
        var waits = 0;
        var slow = new SlowHost(host, _time, () => ++waits);

        await new NodeWatchdog(slow, _time).RunAsync(_stop.Token);

        host.Starts.Select(s => s.FastDeaths).ShouldBe([0, 1, 1, 1]);
        _time.Delays.ShouldNotContain(NodeWatchdog.Backoff);
    }

    // Wraps the fake: a node that ran ten minutes before it ended.
    private sealed class SlowHost(FakeHost inner, FakeTime time, Func<int> count) : IWatchdogHost
    {
        public Task<int?> AnsweringPidAsync(CancellationToken cancellationToken) => inner.AnsweringPidAsync(cancellationToken);

        public Task<WatchdogExit?> WaitForExitAsync(int pid, TimeSpan max, CancellationToken cancellationToken)
        {
            count();
            time.Now += TimeSpan.FromMinutes(10);
            return inner.WaitForExitAsync(pid, max, cancellationToken);
        }

        public int? StartNode(WatchdogStart how) => inner.StartNode(how);

        public bool StillWanted() => inner.StillWanted();

        public void Say(string line) => inner.Say(line);
    }

    [Fact]
    public async Task AStartThatDidNotHappenIsTriedAgainAfterAWaitAndCountsAsADeath()
    {
        var host = Host();
        host.StartResults = [null, 200];

        await Run(host);

        host.Starts.Count.ShouldBe(2);
        host.Waited.ShouldBe([200]);
        _time.Delays.ShouldContain(NodeWatchdog.RetryAfterFailedStart);
        host.Starts[1].FastDeaths.ShouldBe(1);
    }

    [Fact]
    public async Task WhenTheLogonEntryIsGoneBeforeAStartNothingIsStartedAndTheWatchdogEnds()
    {
        var host = Host();
        host.Wanted = false;

        (await Run(host)).ShouldBe(NodeExitCodes.Stopped);

        host.Starts.ShouldBeEmpty();
        host.Waited.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenTheEntryIsRemovedWhileTheNodeRunsTheTimerWakeNoticesAndTheWatchdogEnds()
    {
        var host = Host();
        host.Answering = () => 10;
        var wakes = 0;
        // Each wait on the node times out (null) once; the entry goes after the first wake.
        var timed = new TimedOutHost(host, () =>
        {
            if (++wakes == 2)
            {
                host.Wanted = false;
            }
        });

        (await new NodeWatchdog(timed, _time).RunAsync(_stop.Token)).ShouldBe(NodeExitCodes.Stopped);

        wakes.ShouldBe(2);
        host.Starts.ShouldBeEmpty();
        host.Said.ShouldContain(l => l.Contains("logon entry is gone"));
    }

    private sealed class TimedOutHost(FakeHost inner, Action onWait) : IWatchdogHost
    {
        public Task<int?> AnsweringPidAsync(CancellationToken cancellationToken) => inner.AnsweringPidAsync(cancellationToken);

        public Task<WatchdogExit?> WaitForExitAsync(int pid, TimeSpan max, CancellationToken cancellationToken)
        {
            max.ShouldBe(NodeWatchdog.EntryCheck, "every wait on the node has a deadline");
            onWait();
            return Task.FromResult<WatchdogExit?>(null);
        }

        public int? StartNode(WatchdogStart how) => inner.StartNode(how);

        public bool StillWanted() => inner.StillWanted();

        public void Say(string line) => inner.Say(line);
    }

    [Fact]
    public void WhatTheWatchdogTellsTheNodeReadsBackAndIsSaidInTheRunRecordsWords()
    {
        var how = new WatchdogStart(31, 137, 2);

        var back = NodeWatchdog.Decode(NodeWatchdog.Encode(how)).ShouldNotBeNull();

        back.ShouldBe(how);
        NodeWatchdog.Said(back).ShouldNotBeNull().ShouldStartWith("restarted by the watchdog after 31 s");
        NodeWatchdog.Decode(null).ShouldBeNull();
        NodeWatchdog.Decode("junk").ShouldBeNull();
        NodeWatchdog.Said(null).ShouldBeNull();
    }

    [Fact]
    public void ANodeStartedAfterFastDeathsRaisesOneCardNamingThem()
    {
        var data = Directory.CreateTempSubdirectory("p2x-wd-").FullName;
        try
        {
            var record = new NodeExitRecord(data, _time) { WatchdogValue = () => "down=30;exit=1;fast=3" };
            record.Begin(new NodeRunFacts(100, T0, "2.1.1", "pid 7 pilot-node", "restarted by the watchdog after 30 s", null), null);
            var state = new NodeState(new InMemoryJournal(), _time);

            record.RaiseCards(state, _ => { });

            var card = state.Cards(openOnly: true).ShouldHaveSingleItem();
            card.Kind.ShouldBe(NodeLifecycleLog.RestartLoopKind);
            card.Bin.ShouldBe(CardBin.ForYou);
            card.Detail.ShouldNotBeNull().ShouldContain("3 nodes died within 5 min");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void ARestartAfterOneDeathRaisesNoLoopCard()
    {
        var data = Directory.CreateTempSubdirectory("p2x-wd-").FullName;
        try
        {
            var record = new NodeExitRecord(data, _time) { WatchdogValue = () => "down=30;exit=1;fast=1" };
            record.Begin(new NodeRunFacts(100, T0, "2.1.1", null, "x", null), null);
            var state = new NodeState(new InMemoryJournal(), _time);

            record.RaiseCards(state, _ => { });

            state.Cards().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void TheCrashDumpCheckListsTheExactRegCommandWhenWerHasNoSettingAndAsksForNoElevation()
    {
        var setup = new NodeSetup(new NodeOptions("sock", @"C:\data"), @"C:\bin\pilot-node.exe", @"C:\claude") { CrashDumpSetting = () => (null, null) };

        var check = setup.CrashDumpsCheck();

        check.Ok.ShouldBeFalse();
        check.Fix.ShouldBeNull("HKLM needs an administrator, so setup lists the command and does not elevate");
        check.Line.ShouldContain(@"reg add ""HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\pilot-node.exe"" /v DumpType /t REG_DWORD /d 1 /f");
        check.Line.ShouldContain("crash-dumps");
    }

    [Fact]
    public void TheCrashDumpCheckIsDoneOnceWerHasADumpTypeAndAFolder()
    {
        var setup = new NodeSetup(new NodeOptions("sock", @"C:\data"), @"C:\bin\pilot-node.exe", @"C:\claude") { CrashDumpSetting = () => (1, @"C:\data\crash-dumps") };

        var check = setup.CrashDumpsCheck();

        check.Ok.ShouldBeTrue();
        check.Line.ShouldContain(@"C:\data\crash-dumps");
    }

    /// <summary>
    /// The real watchdog on a sleeping stand-in (pwsh Start-Sleep, never a spinning one): kill it, and a new stand-in is started within a minute.
    /// Out of the default run (Integration and Slow); it ends every process it started.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Slow")]
    public async Task KillingTheSleepingStandInStartsANewOneWithinAMinute()
    {
        var data = Directory.CreateTempSubdirectory("p2x-wd-").FullName;
        var started = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var host = new StandInHost("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 300"], data, started, firstStarted, second);
        try
        {
            var watchdog = new NodeWatchdog(host, TimeProvider.System).RunAsync(cts.Token);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token); // budget: a pwsh stand-in starts in about a second
            var clock = Stopwatch.StartNew();
            using (var first = Process.GetProcessById(started.First()))
            {
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync(cts.Token);
            }

            await second.Task.WaitAsync(TimeSpan.FromSeconds(60), cts.Token); // budget: the done-when is a new node within a minute, and the real grace is 30 s

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60)); // budget: the same minute, asserted
            started.Count.ShouldBe(2);
            cts.Cancel();
            await watchdog;
        }
        finally
        {
            // The host set it in this process's environment for the node it starts; nothing else in the test host may inherit it.
            Environment.SetEnvironmentVariable(NodeWatchdog.Variable, null);
            foreach (var pid in started)
            {
                try
                {
                    using var left = Process.GetProcessById(pid);
                    left.Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                    // already gone
                }
            }
            Directory.Delete(data, recursive: true);
        }
    }

    private sealed class StandInHost(string path, IReadOnlyList<string> args, string data, System.Collections.Concurrent.ConcurrentQueue<int> started, TaskCompletionSource firstStarted, TaskCompletionSource second)
        : RealWatchdogHost(path, args, Path.Combine(data, "none.sock"), data, () => true)
    {
        // No node socket in the stand-in's world: nothing ever answers, so the grace runs out and the node is started again.
        public override Task<int?> AnsweringPidAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public override int? StartNode(WatchdogStart how)
        {
            var pid = base.StartNode(how);
            if (pid is { } p)
            {
                started.Enqueue(p);
                if (started.Count == 1)
                {
                    firstStarted.TrySetResult();
                }
                if (started.Count == 2)
                {
                    second.TrySetResult();
                }
            }
            return pid;
        }
    }
}
