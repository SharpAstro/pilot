using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>How an agents row, a record, a name and v1's registry make a <see cref="SessionRow"/>.</summary>
public sealed class SessionRowTests : IDisposable
{
    private readonly TempProfile _profile = new TempProfile();

    public void Dispose() => _profile.Dispose();

    [Theory]
    [InlineData("busy", "working", SessionState.Working)]
    [InlineData("idle", null, SessionState.Idle)]
    [InlineData("waiting", null, SessionState.Waiting)]
    [InlineData("busy", null, SessionState.Working)]
    [InlineData(null, "working", SessionState.Working)]
    [InlineData(null, "blocked", SessionState.Ended)]
    [InlineData(null, "done", SessionState.Ended)]
    [InlineData(null, "failed", SessionState.Failed)]
    // A background job Claude Code calls done once a turn ends, its process still there and idle.
    [InlineData("idle", "done", SessionState.Idle)]
    [InlineData("busy", "failed", SessionState.Failed)]
    [InlineData(null, null, SessionState.Ended)]
    public void AStateComesFromTheStatusWhileItRunsElseFromTheState(string? status, string? state, SessionState expected)
    {
        SessionStates.Map(status, state).ShouldBe(expected);
    }

    [Fact]
    public void AnInteractiveRowAndABackgroundRowMakeTheirRows()
    {
        var rows = ClaudeAgentsJson.Parse(AgentRows.List(
            AgentRows.Interactive(AgentRows.SessionA, 4242, "tianwen-lib-3a", status: "waiting"),
            AgentRows.Background(AgentRows.SessionB, "PIL5 node", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionC, "old job", "done"))).ShouldNotBeNull();
        _profile.WriteSessionRecord(4242, "{\"pid\":4242,\"status\":\"waiting\",\"waitingFor\":\"approve Bash\",\"bridgeSessionId\":\"session_01Fake\"}");

        var interactive = SessionRowBuilder.Build(rows[0], TranscriptFacts.None, _profile.Profile.ReadSessionRecord(4242), "repo", SessionRoleInfo.AdHoc);
        var background = SessionRowBuilder.Build(rows[1], TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc);
        var done = SessionRowBuilder.Build(rows[2], TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc);

        // An interactive row has no id: the short id is the session id's first eight digits.
        interactive.Id.ShouldBe("a1b2c3d4");
        interactive.SessionId.ShouldBe(AgentRows.SessionA);
        interactive.Name.ShouldBe("tianwen-lib-3a");
        interactive.Kind.ShouldBe(SessionKind.Interactive);
        interactive.Cwd.ShouldBe("C:\\work\\repo");
        interactive.State.ShouldBe(SessionState.Waiting);
        interactive.WaitingFor.ShouldBe("approve Bash");
        interactive.RemoteControlUrl.ShouldBe("https://claude.ai/code/session_01Fake");
        interactive.StartedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1792000000000));

        background.Id.ShouldBe("b2c3d4e5");
        background.Kind.ShouldBe(SessionKind.Background);
        background.State.ShouldBe(SessionState.Working);
        background.WaitingFor.ShouldBeNull();
        background.RemoteControlUrl.ShouldBeNull();

        done.State.ShouldBe(SessionState.Ended);
    }

    [Fact]
    public void ASessionWithoutANameIsNamedByItsId()
    {
        var agent = new ClaudeAgentRow(null, AgentRows.SessionA, 1, "C:\\work", "interactive", null, "idle", null, null, null);

        SessionRowBuilder.Build(agent, TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc).Name.ShouldBe("a1b2c3d4");
    }

    [Fact]
    public void AWaitingSessionWithNoWaitingForSaysTheQuestionTheTranscriptHolds()
    {
        var agent = new ClaudeAgentRow(null, AgentRows.SessionA, 1, "C:\\work", "interactive", "x", "waiting", null, null, null);
        var facts = TranscriptFacts.None with { PendingQuestion = "Which option?" };

        SessionRowBuilder.Build(agent, facts, null, null, SessionRoleInfo.AdHoc).WaitingFor.ShouldBe("Which option?");
        SessionRowBuilder.Build(agent with { Status = "idle" }, facts, null, null, SessionRoleInfo.AdHoc).WaitingFor.ShouldBeNull();
    }

    [Fact]
    public void AListingThatIsNotAnArrayIsNoListing()
    {
        ClaudeAgentsJson.Parse("").ShouldBeNull();
        ClaudeAgentsJson.Parse("claude: not logged in").ShouldBeNull();
        ClaudeAgentsJson.Parse("{\"error\":1}").ShouldBeNull();
        ClaudeAgentsJson.Parse("[]").ShouldBeEmpty();
        // A row with no session id, or one that is no id, has no transcript to follow.
        ClaudeAgentsJson.Parse("[{\"name\":\"x\"},{\"sessionId\":\"../x\"}]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("du1-watchtower")]
    [InlineData("watchtower")]
    [InlineData("autopilot")]
    [InlineData("pilot-dashboard")]
    public void AWatchtowerIsNamedLikeOne(string name)
    {
        SessionRoles.Resolve(name, AgentRows.SessionA, V1Registry.Empty).ShouldBe(new SessionRoleInfo(SessionRole.Watchtower, null, null, null));
    }

    [Theory]
    [InlineData("PIL5 node", "PIL5", "node")]
    [InlineData("TIA1611a release notes", "TIA1611a", "release notes")]
    [InlineData("DPS1 scaffold", "DPS1", "scaffold")]
    public void AWorkersFlightAndLegComeFromItsName(string name, string flight, string leg)
    {
        SessionRoles.Resolve(name, AgentRows.SessionA, V1Registry.Empty).ShouldBe(new SessionRoleInfo(SessionRole.Worker, flight, leg, null));
    }

    [Theory]
    [InlineData("tianwen-lib-3a")]
    [InlineData("pilot-43")]
    [InlineData("Review Measurements phase 2 epic")]
    [InlineData("w-node")]
    [InlineData(null)]
    [InlineData("")]
    public void AnythingElseIsAdHoc(string? name)
    {
        SessionRoles.Resolve(name, AgentRows.SessionA, V1Registry.Empty).ShouldBe(SessionRoleInfo.AdHoc);
    }

    [Fact]
    public void ARegisteredWorkerCarriesItsBudgetAndEverySessionItRanAs()
    {
        _profile.WriteWorker("node", $$"""
            {"name":"node","agent":"PIL5 node","budget":10000000,"id":"b2c3d4e5","sessionId":"{{AgentRows.SessionB}}",
             "sessions":[{"id":"b2c3d4e5","sessionId":"{{AgentRows.SessionB}}"},{"id":"c3d4e5f6","sessionId":"{{AgentRows.SessionC}}"}]}
            """);
        var registry = V1Registry.Load(_profile.V1);

        SessionRoles.Resolve("PIL5 node", AgentRows.SessionB, registry).ShouldBe(new SessionRoleInfo(SessionRole.Worker, "PIL5", "node", 10_000_000));
        // A later session of the same leg, started by `say`, under another name.
        SessionRoles.Resolve("something else", AgentRows.SessionC, registry).ShouldBe(new SessionRoleInfo(SessionRole.Worker, null, "node", 10_000_000));
    }

    [Fact]
    public void AWorkerWhosePlanHadNoFlightNumberFliesAsItsLeg()
    {
        _profile.WriteWorker("scaffold", $$"""{"name":"scaffold","agent":"w-scaffold","budget":5000000,"sessionId":"{{AgentRows.SessionB}}"}""");
        var registry = V1Registry.Load(_profile.V1);

        SessionRoles.Resolve("w-scaffold", AgentRows.SessionB, registry).ShouldBe(new SessionRoleInfo(SessionRole.Worker, null, "scaffold", 5_000_000));
    }

    [Fact]
    public void APilotIsTheSessionAPlanNames()
    {
        _profile.WritePlan("5acad24d-63ac-450d-9ae5-7850b56176cf", "# Flight plan: made up\n\nFlight: TIA2\n\nObjective: nothing.\n\nPilot session: 5acad24d-63ac-450d-9ae5-7850b56176cf\n");
        var registry = V1Registry.Load(_profile.V1);

        SessionRoles.Resolve("pilot-tianwen-2", "5acad24d-63ac-450d-9ae5-7850b56176cf", registry).Role.ShouldBe(SessionRole.Pilot);
        SessionRoles.Resolve("pilot-tianwen-2", AgentRows.SessionA, registry).Role.ShouldBe(SessionRole.AdHoc);
    }

    [Fact]
    public void AStateFolderThatIsNotThereIsAnEmptyRegistry()
    {
        var registry = V1Registry.Load(Path.Combine(_profile.Folder, "nowhere"));

        registry.IsPilot(AgentRows.SessionA).ShouldBeFalse();
        registry.WorkerOfSession(AgentRows.SessionA).ShouldBeNull();
    }

    [Fact]
    public void ARepoIsTheGitRootOfTheFolderAndALinkedWorktreeMapsToItsMainCheckout()
    {
        var main = Path.Combine(_profile.Folder, "code", "pilot");
        Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "w-node"));
        Directory.CreateDirectory(Path.Combine(main, "src", "deep"));
        var worktree = Path.Combine(main, ".claude", "worktrees", "w-node");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "w-node")}\n");
        // A worktree kept elsewhere still names its repo by the main checkout.
        var elsewhere = Path.Combine(_profile.Folder, "trees", "w-gui");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "w-gui").Replace('\\', '/')}\n");

        RepoOf.Find(Path.Combine(main, "src", "deep")).ShouldBe("pilot");
        RepoOf.Find(Path.Combine(worktree, "src")).ShouldBe("pilot");
        RepoOf.Find(elsewhere).ShouldBe("pilot");
        RepoOf.Find(Path.Combine(_profile.Folder, "claude")).ShouldBeNull();
        RepoOf.Find("").ShouldBeNull();
    }
}
