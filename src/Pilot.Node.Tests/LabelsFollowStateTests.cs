using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The labels GitHub shows follow the node's state (PIL795, part 4 of #795): a pass, with notes too, leaves the PR with the one
/// pilot: passed; a pilot: label GitHub shows that the node did not set is set back at the next look and the black box says so.
/// </summary>
public sealed class LabelsFollowStateTests : PrMarksTestsBase
{
    private const string Notes = "Every Done-when holds.\n\n## Not blocking\n- a nit in the parser's naming";

    private readonly FileJournal _journal;

    public LabelsFollowStateTests() : this(new FileJournal(Path.Combine(Path.GetTempPath(), "p2l-" + Guid.NewGuid().ToString("N")[..8])))
    {
    }

    private LabelsFollowStateTests(FileJournal journal) : base(journal)
    {
        _journal = journal;
    }

    public override void Dispose()
    {
        var folder = Path.GetDirectoryName(_journal.BlackBoxPath)!;
        _journal.Dispose();
        base.Dispose();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private BlackBoxLine[] BlackBox()
    {
        using var box = new StreamReader(new FileStream(_journal.BlackBoxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return [.. box.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => System.Text.Json.JsonSerializer.Deserialize(l, PilotJsonContext.Default.BlackBoxLine)!)];
    }

    private Task LookAsync() => new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

    /// <summary>The fake's PR head is the one from before the push; the look would note that as a head disagreement, which is not what these tests are about.</summary>
    private void GitHubHasTheHeadThePlaneHas() => _github.Pr = _github.Pr! with { Head = Reviewed };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewThatPassesWithNotesEndsWithOnlyPilotPassedOnThePrAfterAChangesRound()
    {
        var flight = await Opened();
        GitHubHasTheHeadThePlaneHas();
        (await Verdict(flight.Code, "changes", "## Blocking\n- the null check in Parse is gone")).Error.ShouldBeNull();
        Pilot(_github.Labels[71]).ShouldBe(["pilot: changes"]);
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();

        (await Verdict(flight.Code, "pass", Notes)).Error.ShouldBeNull();

        Pilot(_github.Labels[71]).ShouldBe(["pilot: passed"]);
        F(flight.Code).Review!.Label.ShouldBe("pilot: passed");
        // The node label is another kind and stays.
        _github.IssueLabels[71].ShouldBe(["pilot node: DU1"]);

        // Looked at again, nothing disagrees and nothing is written.
        var writes = _github.LabelCalls.Count;
        await LookAsync();
        F(flight.Code).GitHubDisagrees.ShouldBeNull();
        _github.LabelCalls.Count.ShouldBe(writes);
        BlackBox().ShouldNotContain(l => l.Verb == PrMarks.LabelCorrectedVerb);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APilotChangesLabelLeftOnGitHubAfterAPassIsReplacedAtTheNextLookAndTheBlackBoxSaysSo()
    {
        var flight = await Opened();
        GitHubHasTheHeadThePlaneHas();
        (await Verdict(flight.Code, "pass", Notes)).Error.ShouldBeNull();
        // Something else wrote pilot: changes after the pass.
        _github.Labels[71].Clear();
        _github.Labels[71].Add("pilot: changes");
        var writes = _github.LabelCalls.Count;

        await LookAsync();

        Pilot(_github.Labels[71]).ShouldBe(["pilot: passed"]);
        _github.LabelCalls.Count.ShouldBe(writes + 1);
        var after = F(flight.Code);
        (after.GitHubLabel, after.GitHubDisagrees, after.Review!.Label, after.Review.MarksWhy).ShouldBe(("pilot: passed", null, "pilot: passed", null));
        var line = BlackBox().Single(l => l.Verb == PrMarks.LabelCorrectedVerb);
        (line.Who, line.Target, line.Ok).ShouldBe(("node", "flight/TIA1", true));
        line.Why.ShouldNotBeNull().ShouldBe("pilot: passed is pilot: changes on GitHub; set again");

        // Stands now: a further look finds nothing to put right.
        await LookAsync();
        _github.LabelCalls.Count.ShouldBe(writes + 1);
        BlackBox().Count(l => l.Verb == PrMarks.LabelCorrectedVerb).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALabelTakenOffOnGitHubIsSetAgainAndALabelGitHubRefusesIsACardNotALogLine()
    {
        var flight = await Opened();
        (await Verdict(flight.Code, "pass", "every Done-when holds")).Error.ShouldBeNull();
        _github.Labels[71].Clear();
        _github.LabelFails = "GitHub refused the label: rate limited";

        await LookAsync();

        // Refused: still missing, said as a disagreement and as a card; the black box has no correction to show.
        Pilot(_github.Labels[71]).ShouldBeEmpty();
        BlackBox().ShouldNotContain(l => l.Verb == PrMarks.LabelCorrectedVerb);
        _state.Cards().ShouldContain(c => c.Kind == PrMarks.RefusedKind);

        _github.LabelFails = null;
        await _legs.RetryMarksAsync(Ct);
        Pilot(_github.Labels[71]).ShouldBe(["pilot: passed"]);

        // Taken off again once the writes go through: set again, and logged.
        _github.Labels[71].Clear();
        await LookAsync();
        Pilot(_github.Labels[71]).ShouldBe(["pilot: passed"]);
        BlackBox().Single(l => l.Verb == PrMarks.LabelCorrectedVerb).Why.ShouldBe("pilot: passed was removed on GitHub; set again");
    }
}

/// <summary>A moved flight's issue and PR carry the new node's label and not the old one.</summary>
public sealed class LabelsFollowStateMoveTests : FlightMoveTestsBase
{
    private const string Old = "pilot node: SE1";
    private const string New = "pilot node: DE1";

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMovedFlightsIssueAndPrCarryTheNewNodesLabelAndNotTheOld()
    {
        Fly(LegState.Done);
        _sender.PutFlight(OnSender with { PullRequest = 41, PullRequestUrl = $"https://github.com/{GitHub}/pull/41" });
        _senderGitHub.IssueLabels[900] = [Old];
        _senderGitHub.IssueLabels[41] = [Old];

        var moved = await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.MoveFlight, Target = "flight/TIA900", Who = "tests" }, Ct);
        await _flights.WhenAdvancedAsync();

        moved.Ok.ShouldBeTrue(moved.Error);
        MovedAway();
        // The sending node took its label off the issue and the PR; the receiving node put its own on both.
        _senderGitHub.IssueLabels[900].ShouldNotContain(Old);
        _senderGitHub.IssueLabels[41].ShouldNotContain(Old);
        _github.IssueLabels[900].ShouldBe([New]);
        _github.IssueLabels[41].ShouldBe([New]);
        _sender.Cards(openOnly: false).ShouldNotContain(c => c.Kind == "node-label-refused");
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == "node-label-refused");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APrLabelGitHubRefusesOnTheSendingNodeIsTheExistingNodeLabelRefusedCard()
    {
        Fly(LegState.Done);
        _sender.PutFlight(OnSender with { PullRequest = 41, PullRequestUrl = $"https://github.com/{GitHub}/pull/41" });
        _senderGitHub.IssueLabels[41] = [Old];
        _senderGitHub.IssueLabelFails = "label refused";

        var moved = await _senderLegs.RunAsync(new CommandRequest { Verb = Verbs.MoveFlight, Target = "flight/TIA900", Who = "tests" }, Ct);
        await _flights.WhenAdvancedAsync();

        // The flight moved all the same: a label is a card, never a lost move.
        moved.Ok.ShouldBeTrue(moved.Error);
        MovedAway();
        var card = _sender.Cards(openOnly: false).First(c => c.Kind == "node-label-refused");
        card.Detail.ShouldNotBeNull().ShouldContain("label refused");
    }
}
