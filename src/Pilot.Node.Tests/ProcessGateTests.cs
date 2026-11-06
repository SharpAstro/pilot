using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node keeps its background process starts to a small budget and lets a command's own starts bypass it (PIL794): on Windows
/// Process.Start takes one runtime-wide lock, and about 7,000 background git, gh and claude starts an hour queued a command's start
/// behind them. A fake starter stands in for Process.Start; nothing real is started and nothing sleeps.
/// </summary>
public sealed class ProcessGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// A fake <c>Process.Start</c>. A start is told apart by its file name: one the test has not let go blocks inside the starter until
    /// it is (the thread waits, as a start queued on the runtime's lock does), one it has let go returns at once. It hands back this
    /// very process, so nothing is started.
    /// </summary>
    private sealed class FakeStarts(CancellationToken cancellationToken)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _let = new();

        public Process? Start(ProcessStartInfo start)
        {
            Source(_entered, start.FileName).TrySetResult();
            Source(_let, start.FileName).Task.Wait(cancellationToken);
            return Process.GetCurrentProcess();
        }

        /// <summary>Completes once a start of <paramref name="name"/> is inside the starter.</summary>
        public Task Entered(string name) => Source(_entered, name).Task;

        public bool HasEntered(string name) => Source(_entered, name).Task.IsCompleted;

        /// <summary>Lets the start of <paramref name="name"/> return (now, or when it comes).</summary>
        public void Let(string name) => Source(_let, name).TrySetResult();

        private static TaskCompletionSource Source(ConcurrentDictionary<string, TaskCompletionSource> from, string name) =>
            from.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private static ProcessStartInfo Info(string name) => new(name);

    // A background start that holds its slot: it blocks inside the starter, on a pool thread of its own (the starter blocks the thread that calls it).
    private static async Task<Task<Process>> HoldASlotAsync(NodeTraffic traffic, FakeStarts starts, string name)
    {
        var started = Task.Run(() => traffic.StartProcessAsync("git", Info(name), Ct), Ct);
        await starts.Entered(name).WaitAsync(Ct);
        return started;
    }

    [Fact]
    public async Task WithTheBackgroundBudgetFullACommandsStartGoesStraightThroughAndAnotherBackgroundStartWaitsForASlot()
    {
        var starts = new FakeStarts(Ct);
        var gate = new ProcessGate(2);
        var traffic = new NodeTraffic { Gate = gate, Starter = starts.Start };
        var first = await HoldASlotAsync(traffic, starts, "bg1");
        var second = await HoldASlotAsync(traffic, starts, "bg2");
        gate.Free.ShouldBe(0);

        // A command's own flow: no wait, no slot.
        starts.Let("cmd");
        Task<Process> command;
        using (ProcessGate.Foreground())
        {
            command = traffic.StartProcessAsync("git", Info("cmd"), Ct);
        }
        command.IsCompletedSuccessfully.ShouldBeTrue("a start in a command's flow is made on the spot");
        (await command).Dispose();
        gate.Free.ShouldBe(0, "and takes none of the budget");

        // A third background start is queued: the call returns with its wait registered, and nothing has entered the starter.
        starts.Let("bg3");
        var third = traffic.StartProcessAsync("git", Info("bg3"), Ct);
        third.IsCompleted.ShouldBeFalse();
        starts.HasEntered("bg3").ShouldBeFalse();

        // One slot is given back when its start returns, and the waiting one takes it.
        starts.Let("bg1");
        (await first.WaitAsync(Ct)).Dispose();
        (await third.WaitAsync(Ct)).Dispose();
        starts.HasEntered("bg3").ShouldBeTrue();
        starts.HasEntered("bg2").ShouldBeTrue();
        second.IsCompleted.ShouldBeFalse("the other slot's start is still inside the starter");

        starts.Let("bg2");
        (await second.WaitAsync(Ct)).Dispose();
        gate.Free.ShouldBe(2);
    }

    [Fact]
    public async Task ASlotIsGivenBackWhenTheStartReturnsNotWhenTheProcessEnds()
    {
        var starts = new FakeStarts(Ct);
        var gate = new ProcessGate(1);
        var traffic = new NodeTraffic { Gate = gate, Starter = starts.Start };
        starts.Let("a");

        // Not disposed: the process is still "running" and its slot is already free.
        using var running = await traffic.StartProcessAsync("git", Info("a"), Ct);

        gate.Free.ShouldBe(1);
    }

    [Fact]
    public async Task AStartThatFailsGivesItsSlotBackAndAForegroundStartTakesNone()
    {
        var gate = new ProcessGate(1);
        var traffic = new NodeTraffic { Gate = gate, Starter = _ => throw new Win32Exception("not found") };

        await Should.ThrowAsync<Win32Exception>(() => traffic.StartProcessAsync("git", Info("a"), Ct));
        gate.Free.ShouldBe(1);

        var starts = new FakeStarts(Ct);
        var holding = new NodeTraffic { Gate = gate, Starter = starts.Start };
        var held = await HoldASlotAsync(holding, starts, "bg");
        // A foreground start blocked inside the starter holds no slot either: the one slot is still the background start's.
        var foreground = Task.Run(() =>
        {
            using (ProcessGate.Foreground())
            {
                return holding.StartProcessAsync("git", Info("fg"), Ct);
            }
        }, Ct);
        await starts.Entered("fg").WaitAsync(Ct);
        gate.Free.ShouldBe(0);

        starts.Let("fg");
        (await foreground).Dispose();
        gate.Free.ShouldBe(0, "the foreground start never held the slot, so it gave none back");
        starts.Let("bg");
        (await held.WaitAsync(Ct)).Dispose();
        gate.Free.ShouldBe(1);
    }

    [Fact]
    public async Task ABackgroundStartThatIsCancelledWhileItWaitsStartsNothing()
    {
        var starts = new FakeStarts(Ct);
        var gate = new ProcessGate(1);
        var traffic = new NodeTraffic { Gate = gate, Starter = starts.Start };
        var held = await HoldASlotAsync(traffic, starts, "bg");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = traffic.StartProcessAsync("git", Info("late"), cancel.Token);
        waiting.IsCompleted.ShouldBeFalse();
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        starts.HasEntered("late").ShouldBeFalse();
        starts.Let("bg");
        (await held.WaitAsync(Ct)).Dispose();
        gate.Free.ShouldBe(1, "the cancelled wait took no slot");
    }

    [Fact]
    public async Task TheGatesWaitIsPartOfTheStartWaitThatIsLogged()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var text = new StringWriter();
        var starts = new FakeStarts(Ct);
        // Two starts finish on two threads and both may log.
        var traffic = new NodeTraffic { Time = clock, Gate = new ProcessGate(1), Starter = starts.Start, Log = TextWriter.Synchronized(text) };
        var held = Task.Run(async () => (await traffic.StartProcessAsync("claude", Info("bg"), Ct)).Dispose(), Ct);
        await starts.Entered("bg").WaitAsync(Ct);

        // Queued for the one slot; its own starter call takes no time at all.
        starts.Let("queued");
        var queued = traffic.StartProcessAsync("git", Info("queued"), Ct);
        queued.IsCompleted.ShouldBeFalse();
        clock.Now += TimeSpan.FromSeconds(3);
        starts.Let("bg");
        (await queued.WaitAsync(Ct)).Dispose();
        await held.WaitAsync(Ct);

        text.ToString().ShouldContain("timing: process start git waited 3000 ms");
    }

    [Fact]
    public async Task AScopeIsOffOnceItsCommandEndsSoWhatOutlivesTheCommandIsBackground()
    {
        ProcessGate.InForeground.ShouldBeFalse();
        ExecutionContext? captured;
        using (ProcessGate.Foreground())
        {
            ProcessGate.InForeground.ShouldBeTrue();
            captured = ExecutionContext.Capture();
        }

        ProcessGate.InForeground.ShouldBeFalse();
        // What a Task.Run or a continuation made inside the command runs under: the command's captured context, run after it ended.
        var seen = true;
        ExecutionContext.Run(captured!, _ => seen = ProcessGate.InForeground, null);
        seen.ShouldBeFalse();

        // Work that runs while the command does, handed off, is background too; and nesting puts the outer scope back.
        using (ProcessGate.Foreground())
        {
            var inherited = await Task.Run(() => ProcessGate.InForeground, Ct);
            var handedOff = await ProcessGate.Run(() => Task.FromResult(ProcessGate.InForeground));
            using (ProcessGate.Background())
            {
                ProcessGate.InForeground.ShouldBeFalse();
            }
            ProcessGate.InForeground.ShouldBeTrue();
            inherited.ShouldBeTrue("a plain Task.Run inherits the mark while the command runs, which is why a hand-off clears it");
            handedOff.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task AStartInsideACommandOnTheNodeSkipsAFullBudgetAndWhatItSpawnsDoesNotInheritItsPriority()
    {
        var starts = new FakeStarts(Ct);
        var gate = new ProcessGate(2);
        var traffic = new NodeTraffic { Gate = gate, Starter = starts.Start };
        await using var node = await NodeHarness.StartAsync(Ct, traffic: traffic);
        var tracker = node.App.Services.GetRequiredService<SessionTracker>();
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "pil794-target", status: "busy"));
        await tracker.PollAsync(Ct);
        node.State.Sessions().ShouldContain(s => s.Name == "pil794-target");

        // The background budget is full, held by two starts blocked inside the starter.
        var first = await HoldASlotAsync(traffic, starts, "bg1");
        var second = await HoldASlotAsync(traffic, starts, "bg2");
        gate.Free.ShouldBe(0);

        // What the say does in its flow, as the delivery does when it runs claude.
        starts.Let("command");
        Task<Process>? commandStart = null;
        ExecutionContext? ofTheCommand = null;
        var spawnedWhileRunning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnedAfterIt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Delivery.DuringRelay = () =>
        {
            commandStart = traffic.StartProcessAsync("claude", Info("command"), Ct);
            ofTheCommand = ExecutionContext.Capture();
            _ = ProcessGate.Run(() =>
            {
                spawnedWhileRunning.SetResult(ProcessGate.InForeground);
                return Task.CompletedTask;
            });
            _ = Task.Run(async () =>
            {
                await commandEnded.Task;
                spawnedAfterIt.SetResult(ProcessGate.InForeground);
            }, Ct);
            return Task.CompletedTask;
        };

        var said = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget("pil794-target"), Args = ["hello"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        said.Error.ShouldBeNull();
        node.Delivery.Relays.ShouldHaveSingleItem();

        commandStart.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue("the command's start was made on the spot, though both background slots were held");
        (await commandStart).Dispose();
        gate.Free.ShouldBe(0);

        // Fire-and-forget work does not inherit the priority: not while the command ran (handed off), nor after it ended.
        (await spawnedWhileRunning.Task.WaitAsync(Ct)).ShouldBeFalse();
        commandEnded.SetResult();
        (await spawnedAfterIt.Task.WaitAsync(Ct)).ShouldBeFalse();

        // A start made from the command's own context once it has ended queues behind the full budget like any other background start.
        starts.Let("late");
        Task<Process>? lateStart = null;
        ExecutionContext.Run(ofTheCommand!, _ => lateStart = traffic.StartProcessAsync("git", Info("late"), Ct), null);
        lateStart.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
        starts.HasEntered("late").ShouldBeFalse();

        starts.Let("bg1");
        (await first.WaitAsync(Ct)).Dispose();
        (await lateStart.WaitAsync(Ct)).Dispose();
        starts.Let("bg2");
        (await second.WaitAsync(Ct)).Dispose();
    }
}
