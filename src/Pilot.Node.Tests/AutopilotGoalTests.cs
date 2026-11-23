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
