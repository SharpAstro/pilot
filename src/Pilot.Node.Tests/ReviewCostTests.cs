using System.Globalization;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>review-cost</c> (#433): per landed flight, its reviewer sessions and their tokens as the transcripts hold them. The fixture is two
/// landed flights with three reviewer sessions, a subagent's file, a message the transcript repeats, and rows that must not count.
/// </summary>
public sealed class ReviewCostTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Landed = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2rc-" + Guid.NewGuid().ToString("N")[..8]);

    private ClaudeProfile Profile => new(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SessionsAreSummedPerFlightFromTheirTranscriptsAndSubagentsWithEachMessageOnce()
    {
        Fill();

        var report = ReviewCost.Compute(Flights(), Sessions(), Profile, since: Landed.AddDays(-1));

        report.Flights.Select(f => f.Code).ShouldBe(["PIL1", "TIA2"]);
        var pil1 = report.Flights[0];
        pil1.Sessions.Select(s => (s.Round, s.Session, s.Model, s.Head)).ShouldBe([(1, "de1-pil1-review", "claude-opus-5-5", "aaaa1111"), (2, "de1-pil1-review", "claude-sonnet-5-5", "bbbb2222")]);

        // Round 1: m1 (once, though written twice), m2 (also in the subagent's file) and the subagent's m3.
        var first = pil1.Sessions[0];
        first.Usage.ShouldBe(new MessageUsage(31, 102, 3003, 19));
        first.Weighted.ShouldBe((31 * 20 + 102 * 25 + 3003 * 2 + 19 * 100) / 20);
        first.FromTranscript.ShouldBeTrue();
        pil1.Sessions[1].Usage.ShouldBe(new MessageUsage(5, 0, 100, 50));
        (pil1.Input, pil1.CacheCreation, pil1.CacheRead, pil1.Output, pil1.Weighted).ShouldBe((36, 102, 3103, 69, 553 + 265));

        var tia2 = report.Flights[1];
        tia2.Sessions.Single().Usage.ShouldBe(new MessageUsage(1, 1, 1, 1));
        tia2.Sessions.Single().Head.ShouldBeNull();

        (report.Sessions, report.Reviewed, report.Weighted).ShouldBe((3, 2, 553 + 265 + 7));
    }

    [Fact]
    public void ALandedFlightOutsideTheWindowOrARepoAndAFlightStillFlyingAreLeftOut()
    {
        Fill();

        ReviewCost.Compute(Flights(), Sessions(), Profile, since: Landed.AddDays(-1)).Flights.Select(f => f.Code).ShouldBe(["PIL1", "TIA2"]);
        ReviewCost.Compute(Flights(), Sessions(), Profile).Flights.Select(f => f.Code).ShouldBe(["PIL4", "PIL1", "TIA2"]);
        ReviewCost.Compute(Flights(), Sessions(), Profile, until: Landed.AddDays(1)).Flights.Select(f => f.Code).ShouldBe(["PIL4", "PIL1"]);
        ReviewCost.Compute(Flights(), Sessions(), Profile, repo: "tianwen").Flights.Select(f => f.Code).ShouldBe(["TIA2"]);
    }

    [Fact]
    public void TheAverageIsOverFlightsThatHadAReviewerAndTextNamesEveryFigure()
    {
        Fill();

        var text = ReviewCost.Compute(Flights(), Sessions(), Profile, since: Landed.AddDays(-1)).Text();

        text.ShouldContain("2 flights, 3 reviewer sessions");
        text.ShouldContain("PIL1  pilot #11");
        text.ShouldContain("round 1  claude-opus-5-5  head aaaa1111  in 31  cache-write 102  cache-read 3,003  out 19  weighted 553");
        text.ShouldContain("round 2  claude-sonnet-5-5  head bbbb2222");
        text.ShouldContain("total  in 37  cache-write 103  cache-read 3,104  out 70  weighted 825");
        text.ShouldContain("average per reviewed flight (2)  in 18  cache-write 51  cache-read 1,552  out 35  weighted 412  1.5 sessions");
    }

    [Fact]
    public void ASessionWithNoTranscriptCountsItsRowsSpendAndSaysSo()
    {
        var flight = Flight("PIL7", "pilot", Landed);

        var report = ReviewCost.Compute([flight], [Reviewer("PIL7", "gone0000-0000-0000-0000-000000000000", Landed.AddHours(-1), spend: 1234)], Profile);

        var session = report.Flights.Single().Sessions.Single();
        (session.FromTranscript, session.Weighted).ShouldBe((false, 1234));
        report.Text().ShouldContain("(no transcript: the row's spend only)");
    }

    [Fact]
    public void ATranscriptTitledAsAReviewerInTheFlightsWorktreeFolderCountsWhenTheNodeKeepsNoRow()
    {
        var flight = Flight("PIL5", "pilot", Landed) with { Worktree = "/r/.claude/worktrees/pil5" };
        var project = Path.Combine(_folder, "projects", "-r--claude-worktrees-pil5");
        Directory.CreateDirectory(project);
        File.WriteAllLines(Path.Combine(project, "aa000001-0000-0000-0000-000000000000.jsonl"),
        [
            """{"type":"custom-title","customTitle":"de1-pil5-review","sessionId":"aa000001"}""",
            """{"type":"user","timestamp":"2026-10-25T10:00:00.000Z","message":{"content":"You review flight PIL5"}}""",
            Assistant("m1", 3, 0, 40, 2),
        ]);
        File.WriteAllLines(Path.Combine(project, "aa000002-0000-0000-0000-000000000000.jsonl"),
        [
            """{"type":"custom-title","customTitle":"de1-pil5-leg","sessionId":"aa000002"}""",
            Assistant("m9", 9, 9, 9, 9),
        ]);

        var report = ReviewCost.Compute([flight], [], Profile);

        var only = report.Flights.Single().Sessions.Single();
        (only.Round, only.Session, only.Usage).ShouldBe((1, "de1-pil5-review", new MessageUsage(3, 0, 40, 2)));
        only.StartedAt.ShouldBe(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ItsArgsTakeDatesAndARepoAndRefuseAnythingElse()
    {
        ReviewCost.TryParse(["--since", "2026-10-22", "--until", "2026-10-29", "--repo", "pilot"], out var since, out var until, out var repo, out var error).ShouldBeTrue(error);
        (since, until, repo).ShouldBe((new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), "pilot"));

        ReviewCost.TryParse(["--since", "soon"], out _, out _, out _, out error).ShouldBeFalse();
        error.ShouldBe("--since takes a date (2026-10-22)");
        ReviewCost.TryParse(["--week"], out _, out _, out _, out error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("not --week");
    }

    [Fact]
    public async Task TheNodeAnswersTheVerbFromItsStateAndRefusesAnotherTarget()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight("PIL1", "pilot", DateTimeOffset.UtcNow));
        node.State.PutSession(Reviewer("PIL1", "cc000001-0000-0000-0000-000000000000", DateTimeOffset.UtcNow.AddHours(-1), spend: 4321));

        var sent = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.ReviewCost, Target = Verbs.NodeTarget, Args = ["--repo", "pilot"], Who = "tests" }, Ct);
        sent.Response!.Ok.ShouldBeTrue(sent.Response.Error);
        sent.Response.Made.ShouldNotBeNull().ShouldContain("1 flight, 1 reviewer session");
        sent.Response.Made.ShouldContain("weighted 4,321");

        var refused = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.ReviewCost, Target = Verbs.FlightTarget("PIL1"), Who = "tests" }, Ct);
        refused.Response!.Ok.ShouldBeFalse();
        var bad = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.ReviewCost, Target = Verbs.NodeTarget, Args = ["--since", "soon"], Who = "tests" }, Ct);
        bad.Response!.Error.ShouldBe("--since takes a date (2026-10-22)");
    }

    [Fact]
    public void TheCliAndTheMcpServerCarryTheCommand()
    {
        var root = PilotCommand.Build(_folder, TextWriter.Null, TextWriter.Null, "tests", agent: true);

        root.Subcommands.Select(c => c.Name).ShouldContain("review-cost");
        McpServer.Tools(root).Keys.ShouldContain(k => k.Contains("review-cost", StringComparison.Ordinal));
    }

    // PIL1: two reviewer sessions (round 1 on opus, round 2 on sonnet) and a worker that is not one; TIA2: one; PIL4 landed too early; PIL3 flying.
    private void Fill()
    {
        Write("aa000001", [Assistant("m1", 10, 100, 1000, 5), Assistant("m1", 10, 100, 1000, 5), Assistant("m2", 20, 0, 2000, 10)]);
        Subagent("aa000001", [Assistant("m2", 20, 0, 2000, 10), Assistant("m3", 1, 2, 3, 4)]);
        Write("aa000002", [Assistant("m4", 5, 0, 100, 50)]);
        Write("bb000001", [Assistant("m5", 1, 1, 1, 1)]);
        Write("cc000001", [Assistant("m6", 7, 7, 7, 7)]);
        Write("dd000001", [Assistant("m7", 8, 8, 8, 8)]);
    }

    private static FlightRow[] Flights() =>
    [
        Flight("PIL1", "pilot", Landed) with
        {
            PullRequest = 11,
            ReviewRounds = [Round(1, "aaaa1111cccc"), Round(2, "bbbb2222dddd")],
        },
        Flight("TIA2", "tianwen", Landed.AddDays(1)),
        Flight("PIL3", "pilot", null),
        Flight("PIL4", "pilot", Landed.AddDays(-5)),
    ];

    private static SessionRow[] Sessions() =>
    [
        Reviewer("PIL1", "aa000002-0000-0000-0000-000000000000", Landed.AddHours(-2), model: "claude-sonnet-5-5"),
        Reviewer("PIL1", "aa000001-0000-0000-0000-000000000000", Landed.AddHours(-5), model: "claude-opus-5-5"),
        Reviewer("TIA2", "bb000001-0000-0000-0000-000000000000", Landed.AddHours(20), model: "claude-sonnet-5-5"),
        Reviewer("PIL4", "dd000001-0000-0000-0000-000000000000", Landed.AddDays(-6), model: "claude-sonnet-5-5"),
        Reviewer("PIL3", "ee000001-0000-0000-0000-000000000000", Landed, model: "claude-sonnet-5-5") with { Role = SessionRole.Worker, Name = "PIL1 leg" },
        Reviewer("PIL1", "cc000001-0000-0000-0000-000000000000", Landed, model: "claude-sonnet-5-5") with { Role = SessionRole.Worker, Name = "PIL1 parser" },
    ];

    private static ReviewRound Round(int number, string head) => new() { Round = number, Head = head, Verdict = ReviewState.Changes, Findings = "f" };

    private static FlightRow Flight(string code, string repo, DateTimeOffset? landed) => new()
    {
        Code = code, Repo = repo, RepoPath = "/r", Issue = 1, Title = "t", Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"/r/.claude/worktrees/{code.ToLowerInvariant()}",
        Base = "origin/main", State = landed is null ? FlightState.Flying : FlightState.Landed, CreatedAt = Landed.AddDays(-9), LandedAt = landed,
    };

    private static SessionRow Reviewer(string flight, string sessionId, DateTimeOffset started, string? model = null, long spend = 0) => new()
    {
        Id = sessionId[..8], SessionId = sessionId, Name = $"de1-{flight.ToLowerInvariant()}-review", Kind = SessionKind.Background, Cwd = "/r", Role = SessionRole.Reviewer,
        Flight = flight, Model = model, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Ended, SpendWeighted = spend, StartedAt = started,
    };

    private static string Assistant(string id, long input, long creation, long read, long output) =>
        string.Create(CultureInfo.InvariantCulture,
            $$$"""{"type":"assistant","timestamp":"2026-10-25T10:00:01.000Z","message":{"id":"{{{id}}}","model":"claude-sonnet-5-5","usage":{"input_tokens":{{{input}}},"cache_creation_input_tokens":{{{creation}}},"cache_read_input_tokens":{{{read}}},"output_tokens":{{{output}}}}}}""");

    private void Write(string sessionPrefix, string[] lines)
    {
        var project = Path.Combine(_folder, "projects", "p");
        Directory.CreateDirectory(project);
        File.WriteAllLines(Path.Combine(project, sessionPrefix + "-0000-0000-0000-000000000000.jsonl"), lines);
    }

    private void Subagent(string sessionPrefix, string[] lines)
    {
        var folder = Path.Combine(_folder, "projects", "p", sessionPrefix + "-0000-0000-0000-000000000000", "subagents");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "agent-a1.jsonl"), lines);
    }
}
