using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>A session waiting on its person says since when and what it asks, and a wait whose process is gone is not trusted.</summary>
public sealed class WaitingFactsTests : IDisposable
{
    private const string BashAt = "2026-10-24T08:00:00.000Z";

    private readonly TempProfile _profile = new();

    public void Dispose() => _profile.Dispose();

    private static string ToolUse(string messageId, string toolId, string tool, string input, string at = BashAt) =>
        Lines.Assistant(messageId, input: 1, at: at, content: "[{\"type\":\"tool_use\",\"id\":\"" + toolId + "\",\"name\":\"" + tool + "\",\"input\":" + input + "}]");

    private static ClaudeAgentRow Agent(int? pid, string? status = "waiting", string? state = null) =>
        new(null, AgentRows.SessionA, pid, "C:\\work", "interactive", "x", status, state, null, null);

    private TranscriptFacts Read(params string[] lines)
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), lines);
        return new SessionTranscripts(_profile.Profile, AgentRows.SessionA).Refresh();
    }

    [Fact]
    public void ASessionWaitingOnAPermissionPromptAsksForTheToolAndItsCommand()
    {
        var facts = Read(Lines.Assistant("m1", input: 1), ToolUse("m2", "toolu_b1", "Bash", """{"command":"git push origin main","description":"push"}"""));

        var row = SessionRowBuilder.Build(Agent(1), facts, null, null, SessionRoleInfo.AdHoc);

        row.State.ShouldBe(SessionState.Waiting);
        row.Asking.ShouldBe("Bash: git push origin main");
        row.WaitingSince.ShouldBe(DateTimeOffset.Parse(BashAt));
        row.Gone.ShouldBeFalse();
    }

    [Fact]
    public void AQuestionIsWhatItWaitsOnNotACallItAsksToRun()
    {
        var facts = Read(Lines.Assistant("m1", input: 1),
            ToolUse("m2", "toolu_q1", "AskUserQuestion", """{"questions":[{"question":"Which branch?","header":"Branch"}]}"""));

        var row = SessionRowBuilder.Build(Agent(1), facts, null, null, SessionRoleInfo.AdHoc);

        row.State.ShouldBe(SessionState.Waiting);
        row.Asking.ShouldBeNull();
        row.WaitingFor.ShouldBe("Which branch?");
        row.WaitingSince.ShouldBe(DateTimeOffset.Parse(BashAt));
    }

    [Fact]
    public void WaitingSinceIsWhenTheHookThatPutItInWaitingFired()
    {
        var facts = Read(ToolUse("m1", "toolu_b1", "Bash", """{"command":"ls"}"""));
        var hooked = DateTimeOffset.Parse("2026-10-24T08:00:07Z");
        var hook = new HookState("PermissionRequest", hooked, SessionState.Waiting, "permission to use Bash", null);

        var row = SessionRowBuilder.Build(Agent(1, status: "idle"), facts, null, null, SessionRoleInfo.AdHoc, hook);

        row.State.ShouldBe(SessionState.Waiting);
        row.WaitingSince.ShouldBe(hooked);
        row.Asking.ShouldBe("Bash: ls");
    }

    [Fact]
    public void AToolOtherThanBashIsNamedByItsMostTellingInput()
    {
        var facts = Read(ToolUse("m1", "toolu_e1", "Edit", """{"file_path":"C:\\work\\a.cs","old_string":"x","new_string":"y"}"""));

        SessionRowBuilder.Build(Agent(1), facts, null, null, SessionRoleInfo.AdHoc).Asking.ShouldBe("Edit: C:\\work\\a.cs");
    }

    [Fact]
    public void AMultiLineCommandIsOneLine()
    {
        var facts = Read(ToolUse("m1", "toolu_b1", "Bash", """{"command":"git add -A\ngit commit -m x"}"""));

        SessionRowBuilder.Build(Agent(1), facts, null, null, SessionRoleInfo.AdHoc).Asking.ShouldBe("Bash: git add -A");
    }

    [Fact]
    public void AResolvedToolUseLeavesNothingAsked()
    {
        var facts = Read(ToolUse("m1", "toolu_b1", "Bash", """{"command":"ls"}"""), Lines.ToolResult("toolu_b1"));

        facts.PendingTool.ShouldBeNull();
        var row = SessionRowBuilder.Build(Agent(1), facts, null, null, SessionRoleInfo.AdHoc);
        row.Asking.ShouldBeNull();
        row.WaitingSince.ShouldBeNull();
    }

    [Fact]
    public void OfParallelToolCallsTheOneStillWithoutAResultIsAsked()
    {
        var facts = Read(
            ToolUse("m1", "toolu_a", "Read", """{"file_path":"a.cs"}""", "2026-10-24T08:00:00.000Z"),
            ToolUse("m1", "toolu_b", "Bash", """{"command":"dotnet test"}""", "2026-10-24T08:00:01.000Z"),
            Lines.ToolResult("toolu_a"));

        facts.PendingTool.ShouldNotBeNull().Name.ShouldBe("Bash");
        facts.PendingTool!.At.ShouldBe(DateTimeOffset.Parse("2026-10-24T08:00:01Z"));
    }

    [Fact]
    public void AToolResultThatMentionsUsageStillResolvesItsCall()
    {
        var facts = Read(
            ToolUse("m1", "toolu_b1", "Bash", """{"command":"claude --help"}"""),
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_b1","content":"usage: claude [options]"}]}}""");

        facts.PendingTool.ShouldBeNull();
    }

    [Fact]
    public void ALaterMessageMeansTheEarlierCallRan()
    {
        var facts = Read(ToolUse("m1", "toolu_b1", "Bash", """{"command":"ls"}"""), Lines.Assistant("m2", input: 1));

        facts.PendingTool.ShouldBeNull();
    }

    [Fact]
    public void ASubagentsCallIsNotTheSessionsToAnswer()
    {
        var facts = Read(Lines.Assistant("m1", input: 1, sidechain: true, content: "[{\"type\":\"tool_use\",\"id\":\"toolu_s\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]"));

        facts.PendingTool.ShouldBeNull();
    }

    [Fact]
    public void AnythingButWaitingAsksNothing()
    {
        var facts = Read(ToolUse("m1", "toolu_b1", "Bash", """{"command":"ls"}"""));

        var row = SessionRowBuilder.Build(Agent(1, status: "busy"), facts, null, null, SessionRoleInfo.AdHoc);

        row.Asking.ShouldBeNull();
        row.WaitingSince.ShouldBeNull();
    }

    [Fact]
    public void AWaitingSessionWithNoLiveProcessIsGoneItsLastStateWaiting()
    {
        var facts = Read(ToolUse("m1", "toolu_b1", "Bash", """{"command":"ls"}"""));

        var row = SessionRowBuilder.Build(Agent(null), facts, null, null, SessionRoleInfo.AdHoc);

        row.State.ShouldBe(SessionState.Waiting);
        row.Gone.ShouldBeTrue();
        row.Asking.ShouldBe("Bash: ls");
    }

    [Fact]
    public void ASessionThatIsNotWaitingIsNeverGone()
    {
        SessionRowBuilder.Build(Agent(null, status: null, state: "done"), TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc).Gone.ShouldBeFalse();
        SessionRowBuilder.Build(Agent(1, status: "idle"), TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc).Gone.ShouldBeFalse();
    }

    [Fact]
    public void TheNewFieldsRoundTripThroughTheJsonContext()
    {
        var row = SessionRowBuilder.Build(Agent(null), Read(ToolUse("m1", "toolu_b1", "Bash", """{"command":"ls"}""")), null, null, SessionRoleInfo.AdHoc);

        var json = JsonSerializer.Serialize(row, PilotJsonContext.Default.SessionRow);
        var back = JsonSerializer.Deserialize(json, PilotJsonContext.Default.SessionRow);

        back.ShouldBe(row);
        back!.WaitingSince.ShouldNotBeNull();
        back.Asking.ShouldBe("Bash: ls");
        back.Gone.ShouldBeTrue();
        // A row that is none of it says none of it.
        var plain = JsonSerializer.Serialize(row with { WaitingSince = null, Asking = null, Gone = false }, PilotJsonContext.Default.SessionRow);
        plain.ShouldNotContain("\"gone\"");
        plain.ShouldNotContain("\"asking\"");
        plain.ShouldNotContain("\"waitingSince\"");
    }
}
