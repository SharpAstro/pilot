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
}
