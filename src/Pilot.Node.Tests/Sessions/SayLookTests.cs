using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// A say looks at its target alone (PIL794): its process, its session record, hooks and transcript, with no listing of every
/// session. The poll before each say started <c>claude agents</c>, which on a loaded DE1 waited behind every other process
/// start: an owner's say took 51 s for a 185 ms relay (2026-11-07).
/// </summary>
public sealed class SayLookTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Pid = 4343;

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly HashSet<int> _alive = [Pid];
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;
    private readonly StringWriter _log = new StringWriter();
    private readonly SessionMessenger _messenger;

    public SayLookTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options() with { IsAlive = p => _alive.Contains(p) }, () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, _log);
    }

    public void Dispose() => _profile.Dispose();

    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static CommandRequest Say(string id, string text, string who = "tests") =>
        new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = who };

    [Fact]
    public async Task ASayToALiveIdleWatchtowerListsNoSessions()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "de1-watchtower", "done", pid: Pid, status: "idle", cwd: Cwd));
        _profile.WriteSessionRecord(Pid, """{"status":"idle"}""");
        await _tracker.PollAsync(Ct);
        _agents.Listings.ShouldBe(1);

        var result = await _messenger.SayAsync(Say("b2c3d4e5", "a chat message"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(SayWays.Relayed);
        _agents.Listings.ShouldBe(1);
    }

    [Fact]
    public async Task ASessionBusySinceTheLastPollIsSeenBusyByItsRecordAndRelayedToNotStopped()
    {
        // Idle at the listing, mid-turn now: stopping it for a resume would cut its turn off, which the fresh look is there to prevent.
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", pid: Pid, status: "idle", cwd: Cwd));
        await _tracker.PollAsync(Ct);
        _profile.WriteSessionRecord(Pid, """{"status":"busy"}""");

        var result = await _messenger.SayAsync(Say("b2c3d4e5", "while you work"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(SayWays.Relayed);
        _delivery.Calls.ShouldBe(["relay pil5-say-bg"]);
        _agents.Listings.ShouldBe(1);
    }

    [Fact]
    public async Task ATargetWhoseProcessWentIsLookedAtByAFullPoll()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "working", pid: Pid, status: "busy", cwd: Cwd));
        _profile.WriteSessionRecord(Pid, """{"status":"busy"}""");
        await _tracker.PollAsync(Ct);
        _alive.Clear();
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));

        var result = await _messenger.SayAsync(Say("b2c3d4e5", "wake up"), Ct);

        _agents.Listings.ShouldBe(2);
        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(SayWays.Resumed);
    }

    [Fact]
    public async Task ASayIsNotHeldBehindAListingStuckWaitingForItsProcessStart()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "de1-watchtower", "done", pid: Pid, status: "idle", cwd: Cwd));
        _profile.WriteSessionRecord(Pid, """{"status":"idle"}""");
        await _tracker.PollAsync(Ct);
        _agents.Stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuckPoll = _tracker.PollAsync(Ct);

        var say = _messenger.SayAsync(Say("b2c3d4e5", "a chat message"), Ct);
        var first = await Task.WhenAny(say, Task.Delay(TimeSpan.FromSeconds(5), Ct));

        first.ShouldBe(say, "the say waited for the background poll's listing");
        (await say).Way.ShouldBe(SayWays.Relayed);
        stuckPoll.IsCompleted.ShouldBeFalse();
        // The measure of a say on a loaded node: its whole time under 2 s while the listing has not even started its process.
        var line = _log.ToString().Trim();
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        var total = System.Text.RegularExpressions.Regex.Match(line, @"delivered \((\d+) ms: gate \d+ ms, look \d+ ms, choose \d+ ms, deliver \d+ ms\)");
        total.Success.ShouldBeTrue(line);
        long.Parse(total.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBeLessThan(2000, line);
        _agents.Stuck.SetResult();
        await stuckPoll;
    }

    [Fact]
    public async Task ALookThatSawTheSessionBusyIsNotUndoneByAListingThatBeganBeforeIt()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", pid: Pid, status: "idle", cwd: Cwd));
        _profile.WriteSessionRecord(Pid, """{"status":"idle"}""");
        await _tracker.PollAsync(Ct);
        _state.Sessions().Single().State.ShouldBe(SessionState.Idle);

        // A listing starts while the session is idle and is stuck; the session goes busy and a say's look sees it; then the
        // listing's answer arrives. What it read is older than the look: it is taken again, and says busy as the record does.
        _agents.Stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listing = _tracker.PollAsync(Ct);
        _profile.WriteSessionRecord(Pid, """{"status":"busy"}""");
        (await _tracker.LookAsync("b2c3d4e5", Ct)).ShouldBeTrue();
        _state.Sessions().Single().State.ShouldBe(SessionState.Working);

        var busy = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "working", pid: Pid, status: "busy", cwd: Cwd));
        _agents.AfterListing = () =>
        {
            _agents.AfterListing = null;
            _agents.Json = busy;
        };
        _agents.Stuck.SetResult();
        await listing;

        _state.Sessions().Single().State.ShouldBe(SessionState.Working, "the older listing's idle was put over the look's busy");
        _agents.Listings.ShouldBe(3);
    }

    [Fact]
    public async Task APersonsSayGoesAheadOfTheNodesOwnQueuedNudgesAtTheGate()
    {
        // The node's nudges held the watchtower's gate 3 to 17 s each while an owner's message queued behind them (DE1, 2026-11-07).
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "de1-watchtower", "working", pid: Pid, status: "busy", cwd: Cwd));
        _profile.WriteSessionRecord(Pid, """{"status":"busy"}""");
        await _tracker.PollAsync(Ct);
        var relaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _delivery.DuringRelay = async () =>
        {
            _delivery.DuringRelay = null;
            inside.TrySetResult();
            await relaying.Task;
        };

        var first = _messenger.SayAsync(Say("b2c3d4e5", "nudge one", LegWatchWho), Ct);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var second = _messenger.SayAsync(Say("b2c3d4e5", "nudge two", LegWatchWho), Ct);
        var third = _messenger.SayAsync(Say("b2c3d4e5", "nudge three", LegWatchWho), Ct);
        var owner = _messenger.SayAsync(Say("b2c3d4e5", "from the owner", "sebastiangodelet@seb-surface/gui"), Ct);
        relaying.SetResult();
        await Task.WhenAll(first, second, third, owner);

        _delivery.Relays.Select(r => r.Text).ShouldBe([NodeVoice.Prefix + "nudge one", "from the owner", NodeVoice.Prefix + "nudge two", NodeVoice.Prefix + "nudge three"]);
    }

    private const string LegWatchWho = Pilot.Node.Flights.LegWatch.Who;

    [Fact]
    public async Task AGateLetsUrgentWaitersInFirstAndACancelledWaiterLeavesTheQueue()
    {
        var gate = new SayGate();
        await gate.WaitAsync(urgent: false, Ct);
        var order = new List<string>();
        using var gone = new CancellationTokenSource();
        var ordinary = gate.WaitAsync(urgent: false, Ct).ContinueWith(_ => order.Add("ordinary"), Ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var cancelled = gate.WaitAsync(urgent: true, gone.Token);
        var urgent = gate.WaitAsync(urgent: true, Ct).ContinueWith(_ => order.Add("urgent"), Ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        gate.Waiting.ShouldBe(3);
        await gone.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(cancelled);
        gate.Waiting.ShouldBe(2);

        gate.Release();
        await urgent;
        gate.Release();
        await ordinary;
        gate.Release();

        order.ShouldBe(["urgent", "ordinary"]);
        gate.Waiting.ShouldBe(0);
        await gate.WaitAsync(urgent: false, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task AListingTakenWhileASessionWasStoppedIsNotAppliedOverTheStop()
    {
        // The listing now runs outside the tracker's gate: one taken before a stop must not bring the stopped row back to live.
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "working", pid: Pid, status: "busy", cwd: Cwd));
        await _tracker.PollAsync(Ct);
        _agents.Stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = _tracker.PollAsync(Ct);

        _tracker.MarkStopped("b2c3d4e5", "person: done");
        // The stuck listing answers with what it read before the stop (the session busy, its process up); the one after it, gone.
        var gone = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        _agents.AfterListing = () =>
        {
            _agents.Json = gone;
            _agents.AfterListing = null;
        };
        var stuck = _agents.Stuck;
        _agents.Stuck = null;
        stuck.SetResult();
        await poll;

        _agents.Listings.ShouldBe(3);
        _state.Sessions().Single().State.ShouldBe(SessionState.Ended);
    }
}
