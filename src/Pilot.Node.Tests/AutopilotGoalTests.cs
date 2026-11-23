using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1285, the standing goal: with autopilot on the watchtower's rules carry the goal section and the fingerprint says so, and the
/// lookout runs create-issue, create-flight, ci-budget, go-around and rerun-checks on a --why that names evidence, each a Handled line
/// and a decision; with it off nothing changes. Grants of CI budget are bounded per flight per local day.
/// </summary>
public sealed class AutopilotGoalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheGoalSectionIsInTheRulesOnlyWhileAutopilotIsOn()
    {
        Briefs.WatchtowerRules("w", "pilot").ShouldNotContain(Briefs.AutopilotGoalHeading);
        var on = Briefs.WatchtowerRules("w", "pilot", autopilot: true);
        on.ShouldContain("## Your standing goal (autopilot)");
        on.ShouldContain("autopilot-ci-grants");
        on.ShouldContain("last resort");
        on.ShouldContain("go-around");
    }

    [Fact]
    public void TheFingerprintDiffersOnAndOffSoAToggleReplacesTheWatchtower()
    {
        var off = Briefs.WatchtowerFingerprint("w", "pilot");
        var on = Briefs.WatchtowerFingerprint("w", "pilot", autopilot: true);
        on.ShouldNotBe(off);
        Briefs.WatchtowerFingerprint("w", "pilot", autopilot: false).ShouldBe(off);
        Briefs.WatchtowerFingerprint("w", "pilot", autopilot: true).ShouldBe(on);
    }

    [Theory]
    [InlineData("job j962 shows the build is green", true)]
    [InlineData("card c2737 is settled by commit cafe0123", true)]
    [InlineData("run 25123456789 passed", true)]
    [InlineData("see #1285", true)]
    [InlineData("it looks fine to me", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AWhyNamesEvidenceOnlyWhenItHoldsAJobCardCommitOrRun(string? why, bool names) =>
        Standing.NamesEvidence(why).ShouldBe(names);

    [Fact]
    public void TheGrantBoundIsASettingWithOneADayAsItsDefault()
    {
        new RoleModels(new NodeSettings()).AutopilotCiGrants.ShouldBe(1);
        var set = NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotCiGrantsKey, ["2"]).Settings!;
        new RoleModels(set).AutopilotCiGrants.ShouldBe(2);
        NodeSettings.With(set, NodeSettings.AutopilotCiGrantsKey, ["default"]).Settings!.AutopilotCiGrants.ShouldBeNull();
        NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotCiGrantsKey, ["x"]).Error.ShouldNotBeNull();
    }

    private static string Lookout(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static async Task<CommandResult> Send(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull();

    private static Task<CommandResult> AutopilotAsync(NodeHarness node, string value) =>
        Send(node, new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["autopilot", value], Who = "gui" });

    private static CommandRequest Issue(NodeHarness node, string? why) =>
        new() { Verb = Verbs.CreateIssue, Target = Verbs.RepoTarget("tianwen"), Args = ["Fix the thing", "body"], Who = Lookout(node), Agent = true, Why = why };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithAutopilotOffTheLookoutIsRefusedCreateIssueAsEver()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var refused = await Send(node, Issue(node, "card c2737 shows it"));

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("controller's");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithAutopilotOnTheLookoutNeedsEvidenceInItsWhyAndThenIsNotRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        (await AutopilotAsync(node, "on")).Error.ShouldBeNull();

        var bare = await Send(node, Issue(node, "it seems wise"));
        bare.Ok.ShouldBeFalse();
        bare.Error.ShouldNotBeNull().ShouldContain("naming the evidence");

        var named = await Send(node, Issue(node, "card c2737 repeats and no issue covers it"));
        named.Error.ShouldNotBeNull().ShouldNotContain("controller's");
        named.Error.ShouldNotContain("naming the evidence");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithAutopilotOnAnOverrideStaysThePersonsAndAnAskIsNotTheLookoutsToAnswer()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        (await AutopilotAsync(node, "on")).Error.ShouldBeNull();

        var refused = await Send(node, new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["autopilot", "off"], Who = Lookout(node), Agent = true, Why = "job j1 says so" });

        refused.Error.ShouldNotBeNull().ShouldContain("person's");
    }
}

/// <summary>The per-flight, per-local-day bound on the watchtower's own grants of CI budget (PIL1285).</summary>
[Trait("Category", "Slow")]
public sealed class AutopilotGoalTestsCiGrant : ReviewTestsBase
{
    private const string Watchtower = "du1-watchtower";

    private async Task<FlightRow> Capped(string issue)
    {
        var (flight, _) = await Passed(issue);
        _state.PutFlight(F(flight.Code) with { Ci = new FlightCi { CappedWhy = "5 runner-hours, past the budget of 4", CappedAt = DateTimeOffset.UtcNow, CountedAt = DateTimeOffset.UtcNow } });
        return F(flight.Code);
    }

    private Task<CommandResult> Grant(FlightRow flight, string who, bool autopilot) =>
        _legs.RunAsync(new CommandRequest { Verb = Verbs.CiBudget, Target = Verbs.FlightTarget(flight.Code), Args = [Verbs.CiContinue], Who = who, Autopilot = autopilot, Why = "job j962 is green" }, Ct);

    private void Cap(FlightRow flight) =>
        _state.PutFlight(F(flight.Code) with { Ci = F(flight.Code).Ci! with { CappedWhy = "again, past the budget of 4", CappedAt = DateTimeOffset.UtcNow } });

    [Fact]
    public async Task TheWatchtowerWithoutTheNodesAutopilotMarkIsRefusedAsEver()
    {
        var flight = await Capped("71");

        var refused = await Grant(flight, Watchtower, autopilot: false);

        refused.Error.ShouldNotBeNull().ShouldContain("only the person");
        F(flight.Code).Ci!.Extra.ShouldBe(0);
    }

    [Fact]
    public async Task OneGrantADayIsGivenAndASecondTheSameDayIsTheirsThenTheNextDayOneMoreIsGiven()
    {
        var flight = await Capped("72");

        (await Grant(flight, Watchtower, autopilot: true)).Error.ShouldBeNull();
        F(flight.Code).Ci!.Extra.ShouldBe(1);
        Cap(flight);

        var second = await Grant(flight, Watchtower, autopilot: true);
        second.Error.ShouldNotBeNull().ShouldContain("the person's");
        F(flight.Code).Ci!.Extra.ShouldBe(1);

        _clock.Advance(TimeSpan.FromHours(25));
        (await Grant(flight, Watchtower, autopilot: true)).Error.ShouldBeNull();
        F(flight.Code).Ci!.Extra.ShouldBe(2);
    }

    [Fact]
    public async Task ThePersonIsNotBoundByTheDay()
    {
        var flight = await Capped("73");
        (await Grant(flight, Watchtower, autopilot: true)).Error.ShouldBeNull();
        Cap(flight);

        (await Grant(flight, "controller", autopilot: false)).Error.ShouldBeNull();

        F(flight.Code).Ci!.Extra.ShouldBe(2);
    }

    [Fact]
    public async Task AZeroBoundLeavesEveryGrantToThePerson()
    {
        var flight = await Capped("74");
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotCiGrantsKey, ["0"]).Settings!);

        (await Grant(flight, Watchtower, autopilot: true)).Error.ShouldNotBeNull().ShouldContain("the person's");
    }
}

/// <summary>
/// PIL1285, the standing goal through the node's socket (in-memory git, fake GitHub): each goal verb is refused with no evidence in its
/// --why, runs with it as the watchtower or as an investigator, and leaves a Handled line and a decision whose reason is the why; the
/// person's overrides and an assistant's ask stay the person's; rerun-checks needs no why from the watchtower, autopilot on or off.
/// </summary>
[Trait("Category", "Slow")] // a real node on a socket of its own
public sealed class AutopilotGoalVerbsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    private string Repo => Path.Combine(_node.Folder, "tianwen");

    private string Lookout => $"{_node.Options.Machine.ToLowerInvariant()}-watchtower";

    private string Investigator => $"{_node.Options.Machine.ToLowerInvariant()}-investigate-tia7";

    public async ValueTask InitializeAsync()
    {
        _node = await NodeHarness.StartOnModelAsync(Ct);
        SeedRepo.MakeOn(_node.Repos!, _node.Folder);
        (await Send("gui", Verbs.Set, Verbs.NodeTarget, ["autopilot", "on"], null, agent: false)).Error.ShouldBeNull();
        (await Send("gui", Verbs.CreateFlight, Verbs.RepoTarget(Repo), ["7", "Parser"], null, agent: false)).Ok.ShouldBeTrue();
        await _node.PlannedAsync();
        _node.State.PutLeg(new LegRow { Flight = "TIA7", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = "du1-tia7-parser" });
    }

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private async Task<CommandResult> Send(string who, string verb, string target, string[] args, string? why, bool agent = true) =>
        (await _node.Client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who, Agent = agent, Why = why }, Ct)).Response.ShouldNotBeNull();

    private void LeftALineAndADecision(string who, string why)
    {
        _node.State.Cards().ShouldContain(c => c.Kind == CardKinds.NodeRan && c.AskedBy == who && c.Detail!.Contains(why));
        _node.State.Decisions().ShouldContain(d => d.By == who && d.Reason == why);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateIssueIsRefusedWithoutEvidenceAndRunsWithItLeavingALineAndADecision(bool investigator)
    {
        var who = investigator ? Investigator : Lookout;

        var bare = await Send(who, Verbs.CreateIssue, Verbs.RepoTarget(Repo), ["Fix it"], "it seems wise");
        bare.Ok.ShouldBeFalse();
        bare.Error.ShouldNotBeNull().ShouldContain("evidence");
        _node.GitHub.IssuesCreated.ShouldBeEmpty();

        var why = "card c2737 repeats and no issue covers it";
        var made = await Send(who, Verbs.CreateIssue, Verbs.RepoTarget(Repo), ["Fix it"], why);

        made.Ok.ShouldBeTrue(made.Error);
        _node.GitHub.IssuesCreated.ShouldHaveSingleItem().Title.ShouldBe("Fix it");
        LeftALineAndADecision(who, why);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateFlightIsRefusedWithoutEvidenceAndRunsWithItLeavingALineAndADecision(bool investigator)
    {
        var who = investigator ? Investigator : Lookout;

        (await Send(who, Verbs.CreateFlight, Verbs.RepoTarget(Repo), ["21", "Cause"], "just because")).Error.ShouldNotBeNull().ShouldContain("evidence");
        _node.State.Flights().ShouldNotContain(f => f.Code == "TIA21");

        var why = "issue #21 carries the cause found in job j970";
        var made = await Send(who, Verbs.CreateFlight, Verbs.RepoTarget(Repo), ["21", "Cause"], why);

        made.Ok.ShouldBeTrue(made.Error);
        _node.State.Flights().ShouldContain(f => f.Code == "TIA21");
        LeftALineAndADecision(who, why);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoAroundIsRefusedWithoutEvidenceAndRunsWithItLeavingALineAndADecision(bool investigator)
    {
        var who = investigator ? Investigator : Lookout;
        var target = Verbs.LegTarget("TIA7", "parser");

        (await Send(who, Verbs.GoAround, target, ["hung"], "it looks stuck")).Error.ShouldNotBeNull().ShouldContain("evidence");
        _node.State.Legs().Single(l => l.Flight == "TIA7").GoArounds.ShouldBe(0);

        var why = "job j962 shows the worker idle on a prompt";
        var made = await Send(who, Verbs.GoAround, target, ["hung on a prompt"], why);

        made.Ok.ShouldBeTrue(made.Error);
        _node.State.Legs().Single(l => l.Flight == "TIA7").GoArounds.ShouldBe(1);
        LeftALineAndADecision(who, why);
    }

    [Fact]
    public async Task CiBudgetFromTheWatchtowerNeedsEvidenceAndIsThenNotRefusedForWhoItIsFrom()
    {
        var target = Verbs.FlightTarget("TIA7");

        (await Send(Lookout, Verbs.CiBudget, target, [Verbs.CiContinue], "it is fine")).Error.ShouldNotBeNull().ShouldContain("evidence");

        // The flight has no spend to answer: the refusal is the budget's own, not the standing's.
        var named = await Send(Lookout, Verbs.CiBudget, target, [Verbs.CiContinue], "job j962 is green");
        named.Error.ShouldNotBeNull().ShouldNotContain("controller's");
        named.Error.ShouldNotContain("evidence");
    }

    [Fact]
    public async Task AnInvestigatorsCiBudgetIsRefusedAsNotItsOwn()
    {
        var refused = await Send(Investigator, Verbs.CiBudget, Verbs.FlightTarget("TIA7"), [Verbs.CiContinue], "job j962 is green");

        refused.Error.ShouldNotBeNull().ShouldContain("not an investigator's");
    }

    [Fact]
    public async Task RerunChecksNeedsNoWhyFromTheWatchtowerWhateverTheSwitchSays()
    {
        var on = await Send(Lookout, Verbs.RerunChecks, Verbs.FlightTarget("TIA7"), [], null);
        on.Error.ShouldNotBeNull().ShouldNotContain("evidence");
        on.Error.ShouldNotContain("controller's");

        (await Send("gui", Verbs.Set, Verbs.NodeTarget, ["autopilot", "off"], null, agent: false)).Error.ShouldBeNull();
        var off = await Send(Lookout, Verbs.RerunChecks, Verbs.FlightTarget("TIA7"), [], null);
        off.Error.ShouldNotBeNull().ShouldNotContain("evidence");
        off.Error.ShouldNotContain("controller's");
    }

    [Fact]
    public async Task AnOverrideStaysThePersonsForAnInvestigatorWhateverItsWhy()
    {
        foreach (var who in new[] { Investigator })
        {
            var refused = await Send(who, Verbs.CreateFlight, Verbs.RepoTarget(Repo), ["22", "Again", Verbs.AnywayFlag], "issue #22 and job j970 say so");

            refused.Error.ShouldNotBeNull().ShouldContain("person");
            _node.State.Flights().ShouldNotContain(f => f.Code == "TIA22");
        }
    }

    [Fact]
    public async Task TheCliTakesWhyOnGoAroundAndCiBudgetAndTheRequestCarriesIt()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var round = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, "leg", "go-around", "--leg", "TIA7/parser", "--why", "job j962 shows the worker idle", "hung", "on", "a", "prompt"], _node.Folder, output, error, Ct);

        round.ShouldBe(0, output + error.ToString());
        _node.State.Legs().Single(l => l.Flight == "TIA7").GoArounds.ShouldBe(1);
        _node.State.Decisions().ShouldContain(d => d.Reason.Contains("job j962"));

        // The budget has nothing to answer on this flight, but the option parses and reaches the node: no parse error, and the refusal is the budget's.
        var budgetError = new StringWriter();
        await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, "flight", "ci-budget", "TIA7", "continue", "--why", "job j962 is green"], _node.Folder, new StringWriter(), budgetError, Ct);
        budgetError.ToString().ShouldNotContain("Unrecognized");
        budgetError.ToString().ShouldNotContain("--why");
    }

    [Fact]
    public async Task AnAssistantsAskIsNotTheWatchtowersOrAnInvestigatorsToAnswer()
    {
        var ask = _node.State.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, "flight/TIA7", "an assistant asks to merge", null,
            [new CardChoice { Label = "Allow", Verb = Verbs.Merge, Target = "flight/TIA7" }], askedBy: "de1-term");

        foreach (var who in new[] { Lookout, Investigator })
        {
            var refused = await Send(who, Verbs.Answer, Verbs.CardTarget(ask.Id), ["Allow"], "job j962 says it is fine");
            refused.Ok.ShouldBeFalse();
        }
        _node.State.Cards().Single(c => c.Id == ask.Id).State.ShouldBe(CardState.Open);
    }
}
