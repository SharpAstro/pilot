using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A gate override (PIL702: an accepted leg finish, create-flight --anyway, a review pass by anyone but the reviewer) is refused
/// from a session that gives no reason of its own, and the black box keeps the reason it was given. A card choice's canned
/// words never count; the person and the node are not refused.
/// </summary>
public sealed class OverrideReasonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Assistant = "du1-helper";
    private const string Reviewer = "du1-tia1-review";

    private static string Watchtower(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static async Task<CommandResult> Send(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull();

    private static void Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = TestEnvironment.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', args)}: {error}");
    }

    // A failed leg in a plane that is a real (empty) repository, as finishing a leg looks at its plane.
    private static void FailedLeg(NodeHarness node)
    {
        var plane = Path.Combine(node.Folder, "plane");
        Directory.CreateDirectory(plane);
        Git(plane, "init", "-q");
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = plane, Issue = 1, Branch = "flight/tia1", Worktree = plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Title = "Sky map",
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Failed, Worker = "du1-tia1-parser", Report = "no ephemeris" });
    }

    private static CommandRequest Accept(string who, bool agent, string? why = null, params string[] words) => new()
    {
        Verb = Verbs.FinishLeg, Target = Verbs.LegTarget("TIA1", "parser"), Args = ["accepted", .. words], Who = who, Agent = agent, Why = why,
    };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAcceptFromTheWatchtowerOrAnAssistantWithoutAReasonIsRefusedAndTheLegStaysFailed()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        FailedLeg(node);

        foreach (var who in new[] { Watchtower(node), Assistant })
        {
            var result = await Send(node, Accept(who, agent: true));

            result.Ok.ShouldBeFalse();
            var error = result.Error.ShouldNotBeNull();
            error.ShouldContain("--why");
            error.ShouldContain("job id");
            node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Failed);
        }
        node.State.Decisions().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAcceptFromAnAssistantWithAWhyNamingAJobIsDoneAndRecordedWithThatReason()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        FailedLeg(node);

        var result = await Send(node, Accept(Assistant, agent: true, why: "j962 shows the ephemeris is not needed for the parser"));

        result.Error.ShouldBeNull();
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Done);
        node.State.Legs("TIA1").Single().Report.ShouldNotBeNull().ShouldContain("j962 shows the ephemeris is not needed");
        var decision = node.State.Decisions().Single(d => d.Kind == DecisionKinds.AcceptLeg);
        decision.Reason.ShouldBe("j962 shows the ephemeris is not needed for the parser");
        decision.Reason.ShouldNotContain("no reason given");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAcceptClosesTheLegsCardBySessionWithItsReasonAndDecisionsShowsIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        FailedLeg(node);
        var card = node.State.RaiseCard(CardBin.Ring, "leg-failed", Verbs.LegTarget("TIA1", "parser"), "TIA1/parser failed", "x");
        const string Reason = "j962 shows the ephemeris is not needed";

        var result = await Send(node, Accept(Assistant, agent: true, why: Reason));

        result.Error.ShouldBeNull();
        var closed = node.State.Cards().Single(c => c.Id == card.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.ClosedBy.ShouldBe(Assistant);
        closed.Outcome.ShouldNotBeNull().ShouldContain(Reason);

        var snapshot = new SnapshotEvent { Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [], Cards = [closed] };
        var line = PilotCommand.Cards(snapshot, all: true);
        line.ShouldContain($"[closed: ");
        line.ShouldContain(Reason);
        line.ShouldContain($", by {Assistant}]");

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "decisions", "--kind", "accept-leg"], node.Folder, output, error, Ct);
        exit.ShouldBe(0, error.ToString() + output);
        output.ToString().ShouldContain(Reason);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAcceptWithItsReasonGivenStraightAsWordsAfterTheOutcomeCounts()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        FailedLeg(node);

        var result = await Send(node, Accept(Assistant, agent: true, why: null, "j962", "shows", "it", "is", "fine"));

        result.Error.ShouldBeNull();
        node.State.Decisions().Single(d => d.Kind == DecisionKinds.AcceptLeg).Reason.ShouldBe("j962 shows it is fine");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAcceptFromThePersonWithoutWordsStillRuns()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        FailedLeg(node);

        var result = await Send(node, Accept("sebastian", agent: false));

        result.Error.ShouldBeNull();
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Done);
        node.State.Decisions().Single(d => d.Kind == DecisionKinds.AcceptLeg).Reason.ShouldNotBe("no reason given");
    }

    [Fact]
    public async Task ACreateAnywayFromASessionWithoutAWhyIsRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var result = await Send(node, new CommandRequest
        {
            Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(node.Folder), Args = ["41", Verbs.AnywayFlag], Who = Assistant, Agent = true,
        });

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("--why");
        node.State.Flights().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACreateAnywayWithAWhyIsMadeAndRecordedAsADecision()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var repo = Path.Combine(node.Folder, "tianwen");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "hello\n", Ct);
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var bare = Path.Combine(node.Folder, "origin.git");
        Git(node.Folder, "clone", "-q", "--bare", repo, bare);
        Git(repo, "remote", "add", "origin", bare);
        Git(repo, "fetch", "-q", "origin");
        Git(repo, "remote", "set-head", "origin", "main");

        var result = await Send(node, new CommandRequest
        {
            Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["41", Verbs.AnywayFlag, "Over", "there"], Who = Assistant, Agent = true,
            Why = "the label on #41 is stale, du1's j962 shows nothing flies it",
        });

        result.Error.ShouldBeNull();
        var decision = node.State.Decisions().Single(d => d.Kind == DecisionKinds.CreateAnyway);
        decision.Reason.ShouldBe("the label on #41 is stale, du1's j962 shows nothing flies it");
        decision.By.ShouldBe(Assistant);
    }

    // A flight whose review asked for changes, with the card whose choices pass it over them.
    private static CardRow ReviewCard(NodeHarness node)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = node.Folder, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Title = "Sky map",
            Review = new ReviewRow { State = ReviewState.Changes, Reviewer = Reviewer, Report = "## Blocking\n- x", StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow },
        });
        return node.State.RaiseCard(CardBin.ForYou, "review-changes", "flight/TIA1", "TIA1's review asks for changes", "x", [
            new CardChoice { Label = "Pass with notes", Verb = Verbs.Review, Target = Verbs.FlightTarget("TIA1"), Args = ["pass", "passed with the findings noted: repeated or not blocking"] },
            new CardChoice { Label = "Ready anyway", Verb = Verbs.Review, Target = Verbs.FlightTarget("TIA1"), Args = ["pass", "ready anyway"] },
        ]);
    }

    [Theory]
    [InlineData("Pass with notes")]
    [InlineData("Ready anyway")]
    public async Task PressingAPassChoiceFromASessionWithoutAWhyIsRefusedAndTheCardStaysOpen(string choice)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = ReviewCard(node);

        var result = await Send(node, new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = Assistant, Agent = true });

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("--why");
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Open);
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Changes);
        node.State.Decisions().ShouldBeEmpty();
    }

    [Fact]
    public async Task ThePersonsPressWithoutWordsStillRunsAndIsRecordedAsPressedByThem()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = ReviewCard(node);

        var result = await Send(node, new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Ready anyway"], Who = "sebastian" });

        result.Error.ShouldBeNull();
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Passed);
        node.State.Decisions().Single().Reason.ShouldBe($"pressed by sebastian on card {card.Id}");
    }

    [Fact]
    public async Task APressFromAnAssistantWithAWhyRunsAndRecordsItsReason()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = ReviewCard(node);

        var result = await Send(node, new CommandRequest
        {
            Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Pass with notes"], Who = Assistant, Agent = true, Why = "j962: the findings are repeats of #688",
        });

        result.Error.ShouldBeNull();
        node.State.Decisions().Single().Reason.ShouldBe("j962: the findings are repeats of #688");
    }
}
