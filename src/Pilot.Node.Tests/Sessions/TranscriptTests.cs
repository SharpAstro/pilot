using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>The transcript parsing v1's <c>Meter</c> and <c>session_view()</c> proved, on made-up transcripts.</summary>
public sealed class TranscriptTests : IDisposable
{
    private readonly TempProfile _profile = new TempProfile();

    public void Dispose() => _profile.Dispose();

    private TranscriptFacts Read(string sessionId = AgentRows.SessionA) => new SessionTranscripts(_profile.Profile, sessionId).Refresh();

    [Fact]
    public void ContextIsTheLastTurnsInputPlusCacheReadPlusCacheCreation()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA),
            Lines.Assistant("m1", input: 5, cacheCreation: 1_000, cacheRead: 50_000, output: 700),
            Lines.User("go on"),
            Lines.Assistant("m2", input: 2, cacheCreation: 1_723, cacheRead: 116_952, output: 651));

        var facts = Read();

        facts.ContextTokens.ShouldBe(2 + 1_723 + 116_952);
    }

    [Theory]
    [InlineData("claude-opus-5-5", null, 200_000)]
    [InlineData("claude-opus-5-5", "claude-opus-5-5", 200_000)]
    [InlineData("claude-opus-5-5", "claude-opus-5-5[1m]", 1_000_000)]
    [InlineData("claude-opus-5-5[1m]", null, 1_000_000)]
    [InlineData(null, "claude-sonnet-5-5[1M]", 1_000_000)]
    [InlineData(null, null, 200_000)]
    public void TheWindowIsOneMillionForAOneMModelElseTwoHundredThousand(string? model, string? requested, long window)
    {
        ContextWindows.For(model, requested).ShouldBe(window);
    }

    [Theory]
    [InlineData(199_999, 200_000)]
    [InlineData(200_000, 200_000)]
    [InlineData(200_001, 1_000_000)]
    [InlineData(562_835, 1_000_000)]
    public void AContextPastTheStandardWindowMeansTheLargerWindowWhateverTheModelNameSays(long context, long window)
    {
        ContextWindows.For("claude-opus-5-5", "claude-opus-5-5", context).ShouldBe(window);
    }

    [Fact]
    public void TheWindowOfARowFollowsTheLastTurnsRequestedModel()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 1, requestedModel: "claude-opus-5-5[1m]"));
        var agent = new ClaudeAgentRow(null, AgentRows.SessionA, 1, "C:\\work", "interactive", "x", "idle", null, null, null);

        var row = SessionRowBuilder.Build(agent, Read(), null, null, SessionRoleInfo.AdHoc);

        row.ContextWindow.ShouldBe(1_000_000);
        row.Model.ShouldBe("claude-opus-5-5");
    }

    [Fact]
    public void ModelEffortAndLastResponseComeFromTheLastRealAssistantTurn()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA),
            Lines.Assistant("m1", input: 1, model: "claude-sonnet-5-5", effort: "low", at: "2026-10-18T09:00:00.000Z"),
            Lines.Assistant("m2", input: 1, model: "claude-opus-5-5", effort: "xhigh", at: "2026-10-18T09:05:00.000Z"),
            // Claude Code's own error line and a subagent's turn are not this session's response.
            Lines.Assistant("m3", model: "<synthetic>", effort: null, at: "2026-10-18T09:06:00.000Z"),
            Lines.Assistant("m4", input: 9, model: "claude-haiku-5-5", effort: "high", at: "2026-10-18T09:07:00.000Z", sidechain: true));

        var facts = Read();

        facts.Model.ShouldBe("claude-opus-5-5");
        facts.Effort.ShouldBe("xhigh");
        facts.LastResponseAt.ShouldBe(DateTimeOffset.Parse("2026-10-18T09:05:00Z"));
    }

    private static string Turn(string id, string? stopReason, string model = "claude-opus-5-5", bool sidechain = false) =>
        "{\"type\":\"assistant\"" + (sidechain ? ",\"isSidechain\":true" : "") + ",\"uuid\":\"u-" + id + "\",\"timestamp\":\"2026-10-18T09:00:00.000Z\",\"message\":{\"id\":\"" + id
        + "\",\"model\":\"" + model + "\",\"stop_reason\":" + (stopReason is null ? "null" : $"\"{stopReason}\"")
        + ",\"usage\":{\"input_tokens\":1,\"output_tokens\":1},\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}}\n";

    [Fact]
    public void TheLastRealMessagesStopReasonIsRead()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA),
            Turn("m1", "end_turn"),
            // A streamed message's first line has no reason yet; a subagent's turn is not this session's.
            Turn("m2", null),
            Turn("m2", "max_tokens"),
            Turn("m3", "tool_use", sidechain: true));

        Read().StopReason.ShouldBe("max_tokens");
    }

    [Fact]
    public void TheRequestBehindTheLastResponseIsTheUserLineBeforeItAndAStreamedMessageKeepsIt()
    {
        static string User(string at) => "{\"type\":\"user\",\"uuid\":\"q-" + at + "\",\"timestamp\":\"2026-10-18T" + at + ".000Z\",\"message\":{\"role\":\"user\",\"content\":\"go\"}}\n";
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), User("08:00:00"), Turn("m1", null));
        // A later user line (the next request) arrives while m1 still streams its last lines: m1's request stays the 08:00 one.
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), User("08:30:00"), Turn("m1", "end_turn"));

        Read().LastRequestAt.ShouldBe(DateTimeOffset.Parse("2026-10-18T08:00:00Z"));

        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Turn("m2", "end_turn"));
        Read().LastRequestAt.ShouldBe(DateTimeOffset.Parse("2026-10-18T08:30:00Z"));
    }

    [Fact]
    public void AUserLineIsReadByItsTopLevelFieldsWhateverItsNestedValuesHold()
    {
        var path = _profile.TranscriptPath(AgentRows.SessionA);
        // A tool result: a large nested message before the line's own timestamp, whose members carry a type, a timestamp and an
        // isSidechain of their own.
        var result = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"timestamp\":\"2026-10-18T07:00:00.000Z\","
            + "\"isSidechain\":true,\"content\":\"" + new string('x', 20_000) + "\"}]},\"isSidechain\":false,\"timestamp\":\"2026-10-18T08:10:00.000Z\"}\n";
        _profile.Append(path, result, Turn("m1", "end_turn"));
        Read().LastRequestAt.ShouldBe(DateTimeOffset.Parse("2026-10-18T08:10:00Z"));

        // An isSidechain that is no boolean is skipped whole: its members are not read as the line's.
        var odd = "{\"type\":\"user\",\"isSidechain\":{\"type\":\"summary\",\"timestamp\":\"2026-10-18T09:00:00.000Z\"},\"timestamp\":\"2026-10-18T08:20:00.000Z\"}\n";
        _profile.Append(path, odd, Turn("m2", "end_turn"));
        Read().LastRequestAt.ShouldBe(DateTimeOffset.Parse("2026-10-18T08:20:00Z"));
    }

    [Fact]
    public void AMessageStillStreamingHasNoStopReasonYetAndAnApiErrorLineIsNoReply()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Turn("m1", "end_turn"), Turn("m2", null));
        Read().StopReason.ShouldBeNull();

        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Turn("m2", "tool_use"), Turn("m3", "stop_sequence", model: "<synthetic>"));
        Read().StopReason.ShouldBe("tool_use");
    }

    [Fact]
    public void ASessionBeforeItsFirstResponseHasNoModelAndNoContext()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.User("hello"));

        var facts = Read();

        facts.Model.ShouldBeNull();
        facts.Effort.ShouldBeNull();
        facts.LastResponseAt.ShouldBeNull();
        facts.ContextTokens.ShouldBe(0);
        facts.SpendWeighted.ShouldBe(0);
    }

    [Fact]
    public void SpendIsInputPlusOneAndAQuarterCacheCreationPlusATenthCacheReadPlusFiveOutput()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 1_000, cacheCreation: 400, cacheRead: 10_000, output: 100));

        Read().SpendWeighted.ShouldBe(1_000 + 500 + 1_000 + 500);
    }

    [Fact]
    public void ASpendCountsEachMessageOnceHoweverManyBlocksItWasStreamedIn()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA),
            Lines.Assistant("m1", input: 10, output: 100, at: "2026-10-18T09:00:00.000Z"),
            Lines.Assistant("m1", input: 10, output: 100, at: "2026-10-18T09:00:01.000Z"),
            Lines.Assistant("m1", input: 10, output: 100, at: "2026-10-18T09:00:02.000Z"));

        Read().SpendWeighted.ShouldBe(10 + 500);
    }

    [Fact]
    public void APartialTrailingLineIsLeftForTheNextReadAndCountedOnceWhenComplete()
    {
        var path = _profile.TranscriptPath(AgentRows.SessionA);
        var transcripts = new SessionTranscripts(_profile.Profile, AgentRows.SessionA);
        var whole = Lines.Assistant("m2", input: 20, output: 100);
        _profile.Append(path, Lines.Assistant("m1", input: 10, output: 100));
        transcripts.Refresh().SpendWeighted.ShouldBe(510);

        // The writer is half way through the second line.
        File.AppendAllText(path, whole[..(whole.Length / 2)]);
        transcripts.Refresh().SpendWeighted.ShouldBe(510);
        transcripts.Refresh().SpendWeighted.ShouldBe(510);

        File.AppendAllText(path, whole[(whole.Length / 2)..] + "\n");
        transcripts.Refresh().SpendWeighted.ShouldBe(510 + 520);
        transcripts.Refresh().SpendWeighted.ShouldBe(510 + 520);
    }

    [Fact]
    public void SubagentFilesCountTowardsSpendAndAMessageInTwoFilesCountsOnce()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 10, output: 100));
        // A copy made by `say` repeats the messages before it under the same ids.
        _profile.Append(_profile.SubagentPath(AgentRows.SessionA, "x1"), Lines.Assistant("m1", input: 10, output: 100, sidechain: true), Lines.Assistant("s1", input: 30, output: 100, sidechain: true));
        _profile.Append(_profile.SubagentPath(AgentRows.SessionA, "x2"), Lines.Assistant("s2", input: 40, output: 100, sidechain: true));

        Read().SpendWeighted.ShouldBe(510 + 530 + 540);
    }

    [Fact]
    public void ALineLongerThanTheReadBufferIsStillRead()
    {
        var filler = new string('x', 3 * 1024 * 1024);
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA),
            Lines.Assistant("m1", input: 10, output: 100, content: "[{\"type\":\"text\",\"text\":\"" + filler + "\"}]"),
            Lines.Assistant("m2", input: 20, output: 100));

        var facts = Read();

        facts.SpendWeighted.ShouldBe(510 + 520);
        facts.ContextTokens.ShouldBe(20);
    }

    [Fact]
    public void ATranscriptRewrittenShorterIsReadFromItsStart()
    {
        var path = _profile.TranscriptPath(AgentRows.SessionA);
        var transcripts = new SessionTranscripts(_profile.Profile, AgentRows.SessionA);
        _profile.Append(path, Lines.Assistant("m1", input: 10, output: 100), Lines.Assistant("m2", input: 10, output: 100));
        transcripts.Refresh().SpendWeighted.ShouldBe(1_020);

        File.WriteAllText(path, Lines.Assistant("m9", input: 1) + "\n");

        transcripts.Refresh().SpendWeighted.ShouldBe(1);
    }

    [Fact]
    public void AQuestionWithoutAResultIsPendingUntilItIsAnswered()
    {
        var path = _profile.TranscriptPath(AgentRows.SessionA);
        var transcripts = new SessionTranscripts(_profile.Profile, AgentRows.SessionA);
        _profile.Append(path, Lines.Assistant("m1", input: 1), Lines.AskQuestion("m2", "toolu_q1", "Which option?"));
        transcripts.Refresh().PendingQuestion.ShouldBe("Which option?");

        _profile.Append(path, Lines.ToolResult("toolu_q1"));
        transcripts.Refresh().PendingQuestion.ShouldBeNull();
    }

    [Fact]
    public void ASessionWithNoTranscriptYetHasNoFacts()
    {
        Read(AgentRows.SessionC).ShouldBe(TranscriptFacts.None);
    }

    [Fact]
    public void ASessionIdThatIsNotAnIdFindsNothing()
    {
        _profile.Profile.FindTranscript("..\\..\\x").ShouldBeNull();
        ClaudeProfile.IsSafeId("a1b2c3d4-0000-4000-8000-000000000001").ShouldBeTrue();
        ClaudeProfile.IsSafeId("../x").ShouldBeFalse();
    }

    [Fact]
    public void ASessionsSubagentsAreListedAsTheirRecordsSayAndOneThatHandedBackIsDone()
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 1));
        var explore = _profile.SubagentPath(AgentRows.SessionA, "a1");
        // Its prompt is a string, or blocks of text; the first line that says something is what the row carries.
        _profile.Append(explore, """{"type":"user","isSidechain":true,"message":{"role":"user","content":"Find the clock\nand say where it ticks"}}""", Lines.Assistant("s1", input: 1));
        File.WriteAllText(Path.ChangeExtension(explore, ".meta.json"),
            """{"agentType":"Explore","description":"Find the clock","toolUseId":"toolu_1","finished":true}""");
        var review = _profile.SubagentPath(AgentRows.SessionA, "b2");
        _profile.Append(review, """{"type":"user","isSidechain":true,"message":{"role":"user","content":[{"type":"text","text":"\n  Review the diff\nthen the tests"}]}}""", Lines.Assistant("s2", input: 1));
        File.WriteAllText(Path.ChangeExtension(review, ".meta.json"), """{"agentType":"general-purpose","description":"Review it","toolUseId":"toolu_2"}""");
        // One with no record and a long prompt line: clipped; one with no prompt yet has none.
        var wide = _profile.SubagentPath(AgentRows.SessionA, "c3");
        _profile.Append(wide, $$$"""{"type":"user","isSidechain":true,"message":{"role":"user","content":"{{{new string('x', 300)}}}"}}""");
        var quiet = _profile.SubagentPath(AgentRows.SessionA, "d4");
        _profile.Append(quiet, Lines.Assistant("s4", input: 1));
        var transcripts = new SessionTranscripts(_profile.Profile, AgentRows.SessionA);

        var subagents = transcripts.Refresh().Subagents.ShouldNotBeNull();
        subagents.Where(s => s.Id is "a1" or "b2").Select(s => (s.Id, s.Type, s.Description, s.Prompt, s.ToolUseId, s.Working)).ShouldBe([
            ("a1", "Explore", "Find the clock", "Find the clock", "toolu_1", false),
            ("b2", "general-purpose", "Review it", "Review the diff", "toolu_2", true),
        ], ignoreOrder: true);
        subagents.Single(s => s.Id == "c3").Prompt.ShouldBe(new string('x', 120));
        subagents.Single(s => s.Id == "d4").Prompt.ShouldBeNull();

        // Its hand-back is in its transcript before its record says so.
        _profile.Append(review, """{"type":"assistant","isSidechain":true,"message":{"role":"assistant","content":[{"type":"tool_use","id":"t9","name":"SubagentHandback","input":{"report":"done"}}]}}""");
        transcripts.Refresh().Subagents.ShouldNotBeNull().Single(s => s.Id == "b2").Working.ShouldBeFalse();
    }
}
