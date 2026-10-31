using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A command that still waits for a commander's lock after 5 s is answered with what holds it, "waiting for &lt;verb target&gt; (since
/// HH:mm:ss)", and the CLI prints that and exits as queued (#338). The command stays queued and runs once, when the lock frees.
/// </summary>
public sealed class LockWaitTests : LegCommanderTestsBase, IDisposable
{
    // The lock is held until after the answer, so the answer can only be the patience's (LockWait.Patience, 5 s): this bound
    // says only that it came rather than hung. At 6 s a loaded Windows runner missed it by 0.6 s (#379, 2026-10-28).
    private static readonly TimeSpan Answered = TimeSpan.FromSeconds(15);

    private static string Said(NamedLock gate, string holds) =>
        $"waiting for {holds} (since {gate.Holder!.Since.ToLocalTime():HH:mm:ss})";

    private static async Task Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "it did not come about");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>A real node with two flights, TIA1 and TIA2, on a repo of its own; its folder goes with <see cref="DisposeAsync"/>.</summary>
    private async Task<NodeHarness> NodeWithTwoFlights()
    {
        SeedRepo.Make(_nodeRoot);
        var node = await NodeHarness.StartAsync(Ct);
        _node = node;
        foreach (var (issue, title) in new[] { ("1", "Planets"), ("2", "Moons") })
        {
            var made = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Path.Combine(_nodeRoot, "tianwen")), Args = [issue, title], Who = "gui" }, Ct);
            made.Response!.Ok.ShouldBeTrue(made.Response.Error);
        }
        return node;
    }

    private readonly string _nodeRoot = Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8]);
    private NodeHarness? _node;

    public new void Dispose()
    {
        _node?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_nodeRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_nodeRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_nodeRoot, recursive: true);
        }
        base.Dispose();
    }

    private static CommandRequest Connect(string? requestId) =>
        new() { Verb = Verbs.Connect, Target = Verbs.FlightTarget("TIA1"), Args = ["TIA2"], Who = "gui", RequestId = requestId };

    [Fact]
    public async Task TheLockSaysWhatHoldsItAndSinceWhen()
    {
        var gate = new NamedLock(TimeProvider.System);
        gate.Holder.ShouldBeNull();

        await gate.WaitAsync(Verbs.Merge, "flight/TIA1", Ct);
        var holder = gate.Holder.ShouldNotBeNull();
        (holder.Verb, holder.Target).ShouldBe((Verbs.Merge, "flight/TIA1"));
        holder.ToString().ShouldBe($"merge flight/TIA1 (since {holder.Since.ToLocalTime():HH:mm:ss})");

        gate.Release();
        gate.Holder.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALegCommandWaitingPastFiveSecondsIsAnsweredWithWhatHoldsTheLockAndRunsOnceItFrees()
    {
        await FlightWithTwoLegs();
        await _legs.Lock.WaitAsync(Verbs.Merge, "flight/TIA1", Ct);
        var clock = Stopwatch.StartNew();

        var queued = await Leg(Verbs.Check, "flight/TIA1/parser", "1");

        clock.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(4.5));
        clock.Elapsed.ShouldBeLessThan(Answered);
        queued.Ok.ShouldBeTrue(queued.Error);
        queued.Waiting.ShouldBe(Said(_legs.Lock, "merge flight/TIA1"));

        // Queued, not done: the tick is not made while the lock is held, and is made once it frees.
        L("parser").Checklist[0].Done.ShouldBeFalse();
        _legs.Lock.Release();
        await Until(() => L("parser").Checklist[0].Done);
        await Until(() => _state.Cards().Any(c => c.Kind == LockWait.QueuedKind));
        var card = _state.Cards().Single(c => c.Kind == LockWait.QueuedKind);
        (card.Bin, card.Title).ShouldBe((CardBin.Handled, "check flight/TIA1/parser ran after it waited for the lock"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACommandThatGetsTheLockWithinFiveSecondsAnswersAsBefore()
    {
        await FlightWithTwoLegs();
        await _legs.Lock.WaitAsync(Verbs.Merge, "flight/TIA1", Ct);
        var command = Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Task.Delay(500, Ct);
        command.IsCompleted.ShouldBeFalse();
        _legs.Lock.Release();

        var result = await command;

        result.Ok.ShouldBeTrue(result.Error);
        result.Waiting.ShouldBeNull();
        L("parser").Checklist[0].Done.ShouldBeTrue();
        _state.Cards().ShouldNotContain(c => c.Kind == LockWait.QueuedKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightCommandWaitingPastFiveSecondsIsQueuedAndARetryByItsIdRunsItOnce()
    {
        var node = await NodeWithTwoFlights();
        var flights = node.App.Services.GetRequiredService<FlightCommander>();
        await flights.Lock.WaitAsync(Verbs.Merge, "flight/TIA9", Ct);
        var clock = Stopwatch.StartNew();

        var queued = (await node.Client.SendAsync(Connect("again"), Ct)).Response!;

        clock.Elapsed.ShouldBeLessThan(Answered);
        queued.Ok.ShouldBeTrue(queued.Error);
        queued.Waiting.ShouldBe(Said(flights.Lock, "merge flight/TIA9"));
        node.State.Flights().Single(f => f.Code == "TIA1").Connections.ShouldBeEmpty();

        // The lock frees: the command runs, and a retry by its id is handed what it ended with rather than running it again.
        flights.Lock.Release();
        var retried = (await node.Client.SendAsync(Connect("again"), Ct)).Response!;

        retried.Ok.ShouldBeTrue(retried.Error);
        retried.Waiting.ShouldBeNull();
        node.State.Flights().Single(f => f.Code == "TIA1").Connections.ShouldBe(["TIA2"]);
        node.State.Cards().Where(c => c.Kind == LockWait.QueuedKind).ShouldHaveSingleItem().Title.ShouldBe("connect flight/TIA1 ran after it waited for the lock");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheCliPrintsWhatHoldsTheLockAndExitsAsQueued()
    {
        var node = await NodeWithTwoFlights();
        var flights = node.App.Services.GetRequiredService<FlightCommander>();
        await flights.Lock.WaitAsync(Verbs.Merge, "flight/TIA9", Ct);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "flight", "connect", "TIA1", "TIA2"], _nodeRoot, output, error, Ct, agent: false, requestId: "cli-1");

        exit.ShouldBe(PilotCommand.ExitQueued, $"{output}{error}");
        exit.ShouldNotBe(0);
        output.ToString().ShouldContain($"queued: connect flight/TIA1: {Said(flights.Lock, "merge flight/TIA9")}");
        error.ToString().ShouldBeEmpty();

        // Queued runs once: the retry the node dedupes by that id waits for the command and says it is done.
        flights.Lock.Release();
        var retryOutput = new StringWriter();
        var retry = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "flight", "connect", "TIA1", "TIA2"], _nodeRoot, retryOutput, new StringWriter(), Ct, agent: false, requestId: "cli-1");

        retry.ShouldBe(0, retryOutput.ToString());
        retryOutput.ToString().ShouldContain("ok: connect flight/TIA1");
        node.State.Flights().Single(f => f.Code == "TIA1").Connections.ShouldBe(["TIA2"]);
    }
}
