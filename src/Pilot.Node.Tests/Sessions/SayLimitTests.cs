using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>While the account is on a usage or spend limit the node's own says wait for the reset and the brain is not asked (PIL1035). Fakes and a manual clock only.</summary>
public sealed class SayLimitTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string LimitText = "the intercom did not deliver it: You've hit your individual spend limit. Your 5-hour limit resets 8:50pm (Australia/Sydney)";

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 14, 3, 0, 0, TimeSpan.Zero));
    private readonly TempProfile _profile = new();
    private readonly FakeClaudeAgents _agents = new();
    private readonly NodeState _state;
    private readonly TurnOutage _outage;
    private readonly FakeSessionDelivery _delivery;
    private readonly StringWriter _log = new();
    private readonly SessionMessenger _messenger;
    private readonly SessionTracker _tracker;

    public SayLimitTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _outage = new TurnOutage(_state);
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance);
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, _log) { Outage = _outage, Clock = _time };
    }

    public void Dispose() => _profile.Dispose();

    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static CommandRequest Say(string who, params string[] text) =>
        new() { Verb = Verbs.Say, Target = Verbs.SessionTarget("b2c3d4e5"), Args = text, Who = who };

    private async Task BusySessionAsync()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "working", pid: 4343, status: "busy", cwd: Cwd));
        await _tracker.PollAsync(Ct);
    }

    private DateTimeOffset OpenLimit()
    {
        var until = AccountLimit.ParseText(LimitText, _time.GetUtcNow()).ShouldNotBeNull();
        _outage.OpenLimit(until, "test", _time.GetUtcNow());
        return until;
    }

    private void PassReset(DateTimeOffset until)
    {
        _time.Advance(until - _time.GetUtcNow() + TimeSpan.FromMinutes(1));
        _outage.Look(_state.Sessions(), _time.GetUtcNow());
    }

    [Fact]
    public async Task ANodeSayRefusedWithTheLimitTextOpensTheLimitUntilItsReset()
    {
        await BusySessionAsync();
        _delivery.RelayFailure = new SessionDeliveryException(LimitText["the intercom did not deliver it: ".Length..]);

        var result = await _messenger.SayAsync(Say(LegWatch.Who, "wake up"), Ct);

        result.Ok.ShouldBeFalse();
        var expected = AccountLimit.ParseText(LimitText, _time.GetUtcNow()).ShouldNotBeNull();
        _outage.LimitUntil.ShouldBe(expected);
    }

    [Fact]
    public async Task NodeSaysAreHeldWhileTheLimitIsOnThenDeliveredOnceEachInOrderAfterTheReset()
    {
        await BusySessionAsync();
        var until = OpenLimit();

        var first = await _messenger.SayAsync(Say(LegWatch.Who, "first"), Ct);
        var second = await _messenger.SayAsync(Say(LegWatch.Who, "second"), Ct);
        var again = await _messenger.SayAsync(Say(LegWatch.Who, "first"), Ct);

        _delivery.Relays.ShouldBeEmpty();
        first.Waiting.ShouldNotBeNull().ShouldContain($"{until.ToLocalTime():HH:mm}");
        second.Waiting.ShouldNotBeNull();
        again.Waiting.ShouldNotBeNull();

        PassReset(until);
        await _messenger.HeldDelivery;

        _delivery.Relays.Select(r => r.Text).ToArray().Length.ShouldBe(2);
        _delivery.Relays[0].Text.ShouldContain("first");
        _delivery.Relays[1].Text.ShouldContain("second");
        _outage.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task AnOwnersSayIsDeliveredDuringTheLimit()
    {
        await BusySessionAsync();
        OpenLimit();

        var result = await _messenger.SayAsync(Say("gui", "do this"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain("do this");
    }

    [Fact]
    public async Task AnAskOfTheBrainDuringTheLimitFailsAtOnceNamingTheLimitAndTouchesNoStream()
    {
        var until = OpenLimit();
        var touched = false;
        var brain = new BrainClient(() => { touched = true; return null; }, _time) { Outage = _outage };

        var ex = await Should.ThrowAsync<BrainDownException>(() => brain.AskAsync("summary", "what is up", TimeSpan.FromSeconds(30), Ct));

        ex.Message.ShouldContain("usage limit");
        ex.Message.ShouldContain($"{until.ToLocalTime():HH:mm}");
        touched.ShouldBeFalse();
    }
}
