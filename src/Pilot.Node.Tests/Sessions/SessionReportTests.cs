using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>A session reports its own state (idle, working, blocked); the node prefers that while it is the newest word on a live session.</summary>
public sealed class SessionReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private static ClaudeAgentRow Agent(int? pid, string? status = "idle") =>
        ClaudeAgentsJson.Parse(AgentRows.List(AgentRows.Background(AgentRows.SessionB, "x", "done", pid: pid, status: status)))!.Single();

    private static SessionRow Build(ClaudeAgentRow agent, HookState? hook = null, SessionReport? report = null) =>
        SessionRowBuilder.Build(agent, TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc, hook, report);

    private static SessionReport Blocked(string? message = "needs the VPN") => new(ReportBook.Blocked, message, 1, T0);

    [Fact]
    public void ALiveSessionThatReportedBlockedIsWaitingWithItsMessageWhileTheListingSaysIdle()
    {
        var row = Build(Agent(4343, status: "idle"), report: Blocked());

        row.State.ShouldBe(SessionState.Waiting);
        row.Reported.ShouldBe("blocked");
        row.ReportedAt.ShouldBe(T0);
        row.WaitingFor.ShouldBe("needs the VPN");
        row.WaitingSince.ShouldBe(T0);
        Build(Agent(4343, status: "idle"), report: Blocked(null)).WaitingFor.ShouldBe("blocked (no reason given)");
    }

    [Fact]
    public void WorkingAndIdleReportsOverrideTheListing()
    {
        var working = Build(Agent(4343, status: "idle"), report: new SessionReport(ReportBook.Working, null, 1, T0));
        working.State.ShouldBe(SessionState.Working);
        working.Reported.ShouldBe("working");

        var idle = Build(Agent(4343, status: "busy"), report: new SessionReport(ReportBook.Idle, null, 1, T0));
        idle.State.ShouldBe(SessionState.Idle);
        idle.Reported.ShouldBe("idle");
    }

    [Fact]
    public void ASessionWithNoReportBuildsTheRowItBuiltBefore()
    {
        var hook = new HookState("Stop", T0, SessionState.Idle, null, null);
        foreach (var agent in new[] { Agent(4343, status: "busy"), Agent(4343, status: "waiting"), Agent(null, status: null) })
        {
            Build(agent, hook, report: null).ShouldBe(SessionRowBuilder.Build(agent, TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc, hook));
            Build(agent).Reported.ShouldBeNull();
            Build(agent).ReportedAt.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("UserPromptSubmit", SessionState.Working)]
    [InlineData("PreToolUse", SessionState.Working)]
    public void AHookNewerThanTheReportOverridesIt(string hookEvent, SessionState hooked)
    {
        var hook = new HookState(hookEvent, T0.AddSeconds(5), hooked, null, null);

        var row = Build(Agent(4343, status: "idle"), hook, Blocked());

        row.State.ShouldBe(hooked);
        row.Reported.ShouldBeNull();
        row.ReportedAt.ShouldBeNull();

        // A hook older than the report loses to it.
        Build(Agent(4343), hook with { At = T0.AddSeconds(-5) }, Blocked()).State.ShouldBe(SessionState.Waiting);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("Notification")]
    public void TheTurnsOwnStopAfterABlockedReportDoesNotClearIt(string hookEvent)
    {
        // PreToolUse at T0 (the report command itself), report blocked at T1, then the turn ends: Stop at T2.
        var hook = new HookState(hookEvent, T0.AddSeconds(5), SessionState.Idle, null, null);

        var row = Build(Agent(4343, status: "idle"), hook, Blocked());

        row.State.ShouldBe(SessionState.Waiting);
        row.Reported.ShouldBe("blocked");
        row.WaitingFor.ShouldBe("needs the VPN");

        // A Stop clears a working report, and an ended session clears a blocked one.
        Build(Agent(4343), hook, new SessionReport(ReportBook.Working, null, 1, T0)).State.ShouldBe(SessionState.Idle);
        var ended = new HookState("SessionEnd", T0.AddSeconds(5), SessionState.Ended, null, null);
        Build(Agent(null, status: null), ended, Blocked()).Reported.ShouldBeNull();
    }

    [Fact]
    public void AReportWithASeqNotAboveTheLastAcceptedOneIsIgnored()
    {
        var book = new ReportBook();

        book.Accept(AgentRows.SessionB, "blocked", "first", seq: 5, T0).ShouldBeTrue();
        book.Accept(AgentRows.SessionB, "working", null, seq: 5, T0.AddSeconds(1)).ShouldBeFalse();
        book.Accept(AgentRows.SessionB, "working", null, seq: 4, T0.AddSeconds(2)).ShouldBeFalse();
        book.Of(AgentRows.SessionB)!.State.ShouldBe("blocked");

        book.Accept(AgentRows.SessionB, "idle", null, seq: 6, T0.AddSeconds(3)).ShouldBeTrue();
        book.Of(AgentRows.SessionB)!.State.ShouldBe("idle");

        // No seq: the receive time in ms, which a later receive time beats.
        book.Accept(AgentRows.SessionB, "working", null, seq: null, T0.AddSeconds(4)).ShouldBeTrue();
        book.Accept(AgentRows.SessionB, "idle", null, seq: null, T0.AddSeconds(3)).ShouldBeFalse();
    }

    [Fact]
    public void AReportOnASessionWithNoProcessDoesNotMakeItLive()
    {
        var row = Build(Agent(null, status: null), report: new SessionReport(ReportBook.Working, null, 1, T0));

        row.State.ShouldBe(SessionState.Ended);
        row.Reported.ShouldBeNull();
        Build(Agent(null, status: null), report: Blocked()).State.ShouldBe(SessionState.Ended);
    }

    [Fact]
    public async Task TheReportStateCommandStoresTheReportAndRefusesWithoutASessionOrWithAnUnknownState()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutSession(new SessionRow
        {
            Id = AgentRows.SessionA[..8], SessionId = AgentRows.SessionA, Name = "de1-pil1-report", Kind = SessionKind.Background, Cwd = "/r",
            Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
        });

        async Task<CommandResult> Send(string target, string[] args, string? sessionId, string who = "gui", bool agent = false) =>
            (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.ReportState, Target = target, Args = args, Who = who, Agent = agent, SessionId = sessionId }, Ct)).Response.ShouldNotBeNull();

        var noSession = await Send(Verbs.NodeTarget, ["blocked", "", "why"], sessionId: null);
        noSession.Ok.ShouldBeFalse();
        noSession.Error.ShouldNotBeNull().ShouldContain("CLAUDE_CODE_SESSION_ID");

        var badState = await Send(Verbs.NodeTarget, ["sleeping"], AgentRows.SessionA);
        badState.Ok.ShouldBeFalse();
        badState.Error.ShouldNotBeNull().ShouldContain("idle, working or blocked");

        (await Send(Verbs.NodeTarget, ["idle"], "ffffffff-0000-4000-8000-000000000000")).Error.ShouldNotBeNull().ShouldContain("no session");

        var ok = await Send(Verbs.NodeTarget, ["blocked", "", "needs a login"], AgentRows.SessionA);
        ok.Ok.ShouldBeTrue();
        node.App.Services.GetRequiredService<ReportBook>().Of(AgentRows.SessionA).ShouldNotBeNull().Message.ShouldBe("needs a login");

        // A session reports its own state only; the person may report any.
        var other = await Send(Verbs.SessionTarget(AgentRows.SessionA[..8]), ["idle"], null, who: "de1-pil9-other", agent: true);
        other.Ok.ShouldBeFalse();
        other.Error.ShouldNotBeNull().ShouldContain("own state");
        (await Send(Verbs.SessionTarget(AgentRows.SessionA[..8]), ["working"], null)).Ok.ShouldBeTrue();
    }
}
