using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Repo setup: the Priority field, the labels the node reads, and the end of the <c>pilot: priority</c> label where the field stands.</summary>
public sealed class RepoSetupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Repo = "/r/tianwen";

    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();

    public RepoSetupTests() => _github.FieldCreateFails = null;

    private Task Setup(RepoSetup? setup = null) => (setup ?? new RepoSetup(_state)).EnsureAsync(new GitHubTracker(_github), Repo, "TIA1", Ct);

    private IEnumerable<string> Titles(string kind) => _state.Cards(openOnly: false).Where(c => c.Kind == kind).Select(c => c.Title);

    [Fact]
    public async Task SetupAgainstAnOrgWithNoFieldAndNoLabelsMakesTheFieldAndTheReadLabelsOnceAndASecondRunMakesNothing()
    {
        await Setup();

        _github.FieldsCreated.ShouldBe(1);
        _github.LabelsEnsured.Select(e => e.Label).ShouldBe([PilotMarks.Recheck, PilotMarks.Hold, PilotMarks.HumanVerification, PilotMarks.SiblingSource]);
        _github.LabelsEnsured.ShouldNotContain(e => e.Label == PilotMarks.Priority);
        Titles(RepoSetup.ReportKind).Single().ShouldContain("Priority field");

        // A second run, even by a node that has forgotten the first (a restart), finds it all there and makes nothing.
        await Setup(new RepoSetup(_state));

        _github.FieldsCreated.ShouldBe(1);
        _github.LabelsEnsured.Count.ShouldBe(4);
        _github.LabelsDeleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARunningNodeSetsUpARepoOnceAndSaysSoOnOneCard()
    {
        var setup = new RepoSetup(_state);

        await Setup(setup);
        await Setup(setup);

        Titles(RepoSetup.ReportKind).Count().ShouldBe(1);
    }

    [Fact]
    public async Task AnOrgWhoseTokenCannotMakeFieldsGetsAHandledCardAndTheLabelAndSetupFinishes()
    {
        _github.FieldCreateFails = "Resource not accessible by personal access token";

        await Setup();

        _github.FieldsCreated.ShouldBe(0);
        _github.LabelsEnsured.Select(e => e.Label).ShouldBe([PilotMarks.Priority, PilotMarks.Recheck, PilotMarks.Hold, PilotMarks.HumanVerification, PilotMarks.SiblingSource]);
        var card = _state.Cards(openOnly: false).Single(c => c.Kind == "node-label-refused");
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldContain("TIA1");
        card.Title.ShouldContain(GhCli.PriorityFieldName);
        _github.LabelsDeleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedFieldStillLetsTheFlightBeMade()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8]);
        using var repos = new SeededRepos(root);
        var repo = repos.Seed("tianwen");
        _github.FieldCreateFails = "no rights";
        var commander = new FlightCommander(_state, repos.Planes(), TimeProvider.System)
        {
            Machine = "DE1",
            Trackers = new Trackers([new GitHubTracker(_github)]),
        };

        var result = await commander.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["71", "kept"], Who = "tests" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _state.Flights().Single().State.ShouldBe(FlightState.Flying);
        _github.LabelsEnsured.Select(e => e.Label).ShouldContain(PilotMarks.Priority);
    }

    [Fact]
    public async Task WithTheFieldTheLabelIsDeletedOnlyOnceNoOpenIssueCarriesIt()
    {
        _github.HasPriorityField = true;
        _github.RepoLabels.TryAdd(PilotMarks.Priority, 0);
        _github.IssueLabels[9] = [PilotMarks.Priority];

        await Setup();

        _github.LabelsDeleted.ShouldBeEmpty();
        _github.RepoLabels.Keys.ShouldContain(PilotMarks.Priority);

        // The issue is migrated off the label; the next run (a fresh node) deletes it.
        _github.IssueLabels[9].Remove(PilotMarks.Priority);
        await Setup(new RepoSetup(_state));

        _github.LabelsDeleted.ShouldBe([PilotMarks.Priority]);
        _github.RepoLabels.Keys.ShouldNotContain(PilotMarks.Priority);
        _github.FieldsCreated.ShouldBe(0);
    }

    [Fact]
    public async Task AnExistingFieldIsNeverMadeAgainAndTheLabelIsNotMadeBesideIt()
    {
        _github.HasPriorityField = true;

        await Setup();

        _github.FieldsCreated.ShouldBe(0);
        _github.LabelsEnsured.Select(e => e.Label).ShouldBe([PilotMarks.Recheck, PilotMarks.Hold, PilotMarks.HumanVerification, PilotMarks.SiblingSource]);
    }
}

/// <summary>What <see cref="GhCli"/> sends: gh fills <c>{owner}</c> and <c>{repo}</c> only in the endpoint and in <c>-F</c> values, never inside a <c>-f</c> query.</summary>
public sealed class IssueFieldQueryTests
{
    [Fact]
    public void QueryTextsCarryNoPlaceholders()
    {
        foreach (var query in new[] { GhCli.PriorityQuery, GhCli.OwnerFieldsQuery })
        {
            query.ShouldNotContain("{owner}");
            query.ShouldNotContain("{repo}");
            query.ShouldContain("$owner: String!");
            query.ShouldContain("repository(owner: $owner, name: $name)");
        }
    }
}
