using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1229 (pilot #1229, Decision 3): an override (Pass with notes, Ready anyway, Mark done anyway, merge as it is) is the person's.
/// The node refuses it from the watchtower on its own, whatever --why it gives, with one line saying whose it is; the card stays
/// open. The person's word, the person's own press and the node's own presses still run, and so does Allow one more round.
/// </summary>
public sealed class WatchtowerOverrideTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Reviewer = "du1-tia1-review";
    private const string Why = "j962 shows the leftover points are repeats of #688";
    private const string Words = "mark it done anyway, the ephemeris is not needed";

    private static string Tower(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static async Task<CommandResult> Send(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull();

    private static Task<CommandResult> Answer(NodeHarness node, CardRow card, string choice, string who, bool agent, string? why = null) =>
        Send(node, new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = who, Agent = agent, Why = why });

    // Round 3 asked for changes: the loop card, as RaiseReviewLoop raises it (ring bin, both choices), on a flight whose review is at Changes.
    private static CardRow LoopCard(NodeHarness node)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = node.Folder, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Title = "Sky map",
            Review = new ReviewRow { State = ReviewState.Changes, Reviewer = Reviewer, Report = "## Blocking\n- x", StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow },
        });
        var target = Verbs.FlightTarget("TIA1");
        return node.State.RaiseCard(CardBin.Ring, LegCommander.ReviewLoopKind, target, "TIA1's review is in a loop: 3 rounds in all, 3 with changes", "Round 3 (changes): x", [
            new CardChoice { Label = "Pass with notes", Verb = Verbs.Review, Target = target, Args = ["pass", "passed with the findings noted: repeated or not blocking"] },
            new CardChoice { Label = "Ready anyway", Verb = Verbs.Review, Target = target, Args = ["pass", "ready anyway"] },
            new CardChoice { Label = "Allow one more round", Verb = Verbs.AllowRound, Target = target, Args = ["the watchtower allowed it"] },
        ]);
    }

    private static CardRow FailedLeg(NodeHarness node, SeededRepos repos)
    {
        var plane = repos.Seed("plane");
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = plane, Issue = 1, Branch = "flight/tia1", Worktree = plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Title = "Sky map",
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Failed, Worker = "du1-tia1-parser", Report = "no ephemeris" });
        return node.State.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "parser failed", "no ephemeris",
            [new CardChoice { Label = "Mark done anyway", Verb = Verbs.FinishLeg, Target = Verbs.LegTarget("TIA1", "parser"), Args = ["accepted"] }]);
    }

    private static SeededRepos Repos() => new(Path.Combine(Path.GetTempPath(), "p2o-" + Guid.NewGuid().ToString("N")[..8]));

    // The watchtower's transcript, with the person's word in it (as PersonsWordTests builds it).
    private static void PersonSaid(NodeHarness node, string text)
    {
        var brief = new JsonObject
        {
            ["type"] = "user",
            ["timestamp"] = "2026-10-30T08:59:00Z",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = Briefs.Watchtower(Tower(node), [], [], [], "pilot") },
            ["origin"] = new JsonObject { ["kind"] = "human" },
            ["promptSource"] = "typed",
            ["turnOrigin"] = "human",
        }.ToJsonString();
        var said = $$$"""{"type":"user","timestamp":"2026-10-30T09:00:00Z","message":{"role":"user","content":"{{{text}}}"}}""";
        var replied = """{"type":"assistant","timestamp":"2026-10-30T09:00:01Z","message":{"role":"assistant","content":[{"type":"text","text":"on it"}]}}""";
        var folder = Path.Combine(node.Folder, "claude", "projects", "proj-a");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, AgentRows.SessionA + ".jsonl"), [brief, said, replied]);
    }

    private static async Task TowerBusy(NodeHarness node)
    {
        var name = Tower(node);
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, name, status: "busy"));
        for (var i = 0; i < 100 && !node.State.Sessions().Any(s => s.Name == name && s.State == SessionState.Working); i++)
        {
            await Task.Delay(50, Ct);
        }
        node.State.Sessions().ShouldContain(s => s.Name == name && s.State == SessionState.Working);
    }

    [Theory]
    [InlineData("Pass with notes")]
    [InlineData("Ready anyway")]
    public async Task TheWatchtowersPassOnAHeldLoopCardIsRefusedWithAWhyAndTheCardStaysOpenInTheRingBin(string choice)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = LoopCard(node);

        var result = await Answer(node, card, choice, Tower(node), agent: true, Why);

        result.Ok.ShouldBeFalse();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("is the person's call");
        error.ShouldNotContain("--why");
        var open = node.State.Cards().Single(c => c.Id == card.Id);
        open.State.ShouldBe(CardState.Open);
        open.Bin.ShouldBe(CardBin.Ring);
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Changes);
        node.State.Decisions().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWatchtowersPassGivenStraightIsRefusedToo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        LoopCard(node);

        var result = await Send(node, new CommandRequest
        {
            Verb = Verbs.Review, Target = Verbs.FlightTarget("TIA1"), Args = ["pass", "notes"], Who = Tower(node), Agent = true, Why = Why,
        });

        result.Error.ShouldNotBeNull().ShouldContain("Pass with notes over flight/TIA1 is the person's call");
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    public async Task TheWatchtowersMarkDoneAnywayIsRefusedWithAWhyAndTheLegStaysFailed()
    {
        using var repos = Repos();
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        var card = FailedLeg(node, repos);

        var answered = await Answer(node, card, "Mark done anyway", Tower(node), agent: true, Why);
        var straight = await Send(node, new CommandRequest
        {
            Verb = Verbs.FinishLeg, Target = Verbs.LegTarget("TIA1", "parser"), Args = ["accepted", "fine"], Who = Tower(node), Agent = true, Why = Why,
        });

        answered.Error.ShouldNotBeNull().ShouldContain("is the person's call");
        straight.Error.ShouldNotBeNull().ShouldContain("Mark done anyway over flight/TIA1/parser is the person's call");
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Failed);
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task TheWatchtowersMergeAsItIsIsRefusedWithAWhy()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        LoopCard(node);

        var result = await Send(node, new CommandRequest
        {
            Verb = Verbs.Merge, Target = Verbs.FlightTarget("TIA1"), Args = ["abc123", "squash", LegCommander.MergeAsIs], Who = Tower(node), Agent = true, Why = Why,
        });

        result.Error.ShouldNotBeNull().ShouldContain("Merge as it is over flight/TIA1 is the person's call");
    }

    [Fact]
    public async Task ThePersonsOwnPressesStillRun()
    {
        using var repos = Repos();
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        var card = FailedLeg(node, repos);

        (await Answer(node, card, "Mark done anyway", "sebastian", agent: false)).Error.ShouldBeNull();
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Done);

        var loop = LoopCard(node);
        (await Answer(node, loop, "Pass with notes", "sebastian", agent: false)).Error.ShouldBeNull();
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    public async Task TheWatchtowerPressesAnOverrideAtThePersonsWordInItsChat()
    {
        using var repos = Repos();
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        PersonSaid(node, Words);
        await TowerBusy(node);
        var card = FailedLeg(node, repos);

        var result = await Answer(node, card, "Mark done anyway", Tower(node), agent: true, "mark it done ANYWAY, the ephemeris is not needed");

        result.Error.ShouldBeNull();
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Done);
    }

    [Fact]
    public async Task TheWatchtowersAllowOneMoreRoundIsNotFencedAsAnOverride()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = LoopCard(node);

        var result = await Answer(node, card, "Allow one more round", Tower(node), agent: true, "the points left are small");

        // Not held here (no rounds stored), so the command refuses on its own ground; the node's override rule is not the reason.
        result.Error.ShouldNotBeNull().ShouldNotContain("is the person's call");
        // The press ran (#1241 R1.5): its refusal is the round command's own, which only a press that reached it gives.
        result.Error.ShouldContain("is not held: nothing to allow");
    }

    [Fact]
    public async Task TheNodesOwnPressesAreUnchanged()
    {
        using var repos = Repos();
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        FailedLeg(node, repos);

        var result = await Send(node, new CommandRequest
        {
            Verb = Verbs.FinishLeg, Target = Verbs.LegTarget("TIA1", "parser"), Args = ["accepted", "the node's rule"], Who = "node",
        });

        result.Error.ShouldBeNull();
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Done);
    }
}
