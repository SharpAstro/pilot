using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary><c>pilot session end &lt;id&gt; &lt;why&gt;</c> (#542): the node ends a background session with a reason, cancels its jobs, and keeps who asked.</summary>
public sealed class SessionEndTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-end-" + Guid.NewGuid().ToString("N"));
    private readonly SessionLedger _ledger;
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;
    private readonly SessionMessenger _messenger;
    private readonly List<(string[] Owners, string Who)> _cancelled = [];

    public SessionEndTests()
    {
        _ledger = new SessionLedger(SessionLedger.FileIn(_folder), TimeProvider.System);
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance) { Launched = _ledger };
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, new StringWriter(), ledger: _ledger)
        {
            CancelJobs = (owners, who) =>
            {
                _cancelled.Add(([.. owners], who));
                return 1;
            },
        };
    }

    public void Dispose()
    {
        _profile.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static CommandRequest End(string id, string who, params string[] why) =>
        new() { Verb = Verbs.EndSession, Target = Verbs.SessionTarget(id), Args = why, Who = who };

    private async Task StartBackgroundAsync()
    {
        _ledger.Started(AgentRows.SessionA[..8], "pil5-end", SessionRoles.Resolve("pil5-end", background: true), SessionStartReasons.LegLaunch, "claude-sonnet-5-5", null);
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "pil5-end", "working", pid: 1, status: "busy"));
        await _tracker.PollAsync(Ct);
    }

    [Fact]
    public async Task EndingABackgroundSessionStopsItCancelsItsJobsAndLedgersTheReasonAndWhoAsked()
    {
        await StartBackgroundAsync();

        var result = await _messenger.EndAsync(End("a1b2c3d4", "de1-watchtower", "a duplicate", "of", "the", "reviewer"), Ct);

        result.Ok.ShouldBeTrue();
        _delivery.Calls.ShouldBe([$"stop {AgentRows.SessionA}"]);
        var (owners, who) = _cancelled.ShouldHaveSingleItem();
        owners.ShouldContain("a1b2c3d4");
        who.ShouldBe("de1-watchtower");
        var entry = _ledger.Find(["a1b2c3d4"]).ShouldNotBeNull();
        entry.EndReason.ShouldBe($"{SessionEndReasons.Watchtower}: a duplicate of the reviewer");
        entry.EndedBy.ShouldBe("de1-watchtower");
    }

    [Fact]
    public async Task APersonsEndIsLedgeredAsAPersons()
    {
        await StartBackgroundAsync();

        (await _messenger.EndAsync(End("a1b2c3d4", "cli", "not wanted"), Ct)).Ok.ShouldBeTrue();

        _ledger.Find(["a1b2c3d4"])!.EndReason.ShouldBe($"{SessionEndReasons.Person}: not wanted");
    }

    [Fact]
    public async Task EndingWithNoReasonIsRefusedAndStopsNothing()
    {
        await StartBackgroundAsync();

        var result = await _messenger.EndAsync(End("a1b2c3d4", "de1-watchtower", "  "), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("needs a reason");
        _delivery.Calls.ShouldBeEmpty();
        _cancelled.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInteractiveSessionIsRefusedAndLeftAlone()
    {
        _agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, "pil5-int"));
        await _tracker.PollAsync(Ct);

        var result = await _messenger.EndAsync(End("a1b2c3d4", "de1-watchtower", "why"), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("interactive");
        _delivery.Calls.ShouldBeEmpty();
        _cancelled.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEndedSessionIsRefused()
    {
        await StartBackgroundAsync();
        _agents.Json = AgentRows.List();
        await _tracker.PollAsync(Ct);

        var result = await _messenger.EndAsync(End("a1b2c3d4", "de1-watchtower", "why"), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("already");
        _delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWatchtowerMayEndASessionAndAWorkerOnlyItsOwnFlightsAndThePersonAny()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lookout = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        node.State.PutSession(new SessionRow
        {
            Id = "b1b2b3b4", SessionId = "b1b2b3b4-0000-4000-8000-000000000002", Name = "du1-tia2-parser", Kind = SessionKind.Background, Cwd = "/r",
            Role = SessionRole.Worker, Flight = "TIA2", ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
        });
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/TIA1", Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = "du1-tia1-parser" });

        async Task<string?> Error(string who, bool agent) =>
            (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.EndSession, Target = "session/b1b2b3b4", Args = ["why"], Who = who, Agent = agent }, Ct)).Response.ShouldNotBeNull().Error;

        (await Error(lookout, true) ?? "").ShouldNotContain("controller's");
        (await Error(lookout, true) ?? "").ShouldNotContain("not TIA1's session");
        (await Error("du1-tia1-parser", true)).ShouldBe("session/b1b2b3b4 is not TIA1's session: a flight's session stops and compacts its own flight's only");
        (await Error("gui", false) ?? "").ShouldNotContain("not TIA1's session");
    }
}
