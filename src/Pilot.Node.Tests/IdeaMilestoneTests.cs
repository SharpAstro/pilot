using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A milestoner that answers what the test set, or fails, and keeps what it was asked.</summary>
internal sealed class FakeIdeaMilestoner : IIdeaMilestoner
{
    public MilestoneAnswer? Answer { get; set; }

    public string? Fails { get; set; }

    public List<MilestoneContext[]> Asked { get; } = [];

    public Task<MilestoneAnswer> ProposeAsync(IdeaText idea, MilestoneContext[] open, string repoPath, CancellationToken cancellationToken)
    {
        Asked.Add(open);
        return Fails is { } why ? throw new FlightException(why) : Task.FromResult(Answer ?? new MilestoneAnswer(open[0].Milestone.Title, null, null, "fits"));
    }
}

/// <summary>The milestone an idea is planned into: Claude's proposal from the open ones, and plan-idea into a named or a new one only.</summary>
public sealed class IdeaMilestoneTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2m-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly FakeIdeaMilestoner _milestoner = new();
    private readonly IdeaCommander _commander;

    public IdeaMilestoneTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _commander = new IdeaCommander(_state, planes, catalog, _github, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System, _milestoner);
        _github.AddMilestone(Repo, IdeaIds.Milestone);
        _github.AddMilestone(Repo, "v1", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        _github.AddMilestone(Repo, "v2");
        _github.AddIssue(Repo, "Ring shadows", IdeaIds.Milestone, body: "on the planet");
        _github.AddIssue(Repo, "Fix the GPU crash", "v1");
    }

    private string Repo => Path.Combine(_root, "pilot");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private Task<CommandResult> Run(string verb, params string[] args) =>
        _commander.RunAsync(new CommandRequest { Verb = verb, Target = IdeaIds.Target(IdeaIds.DefaultRepo, 1), Args = args, Who = "tests" }, Ct);

    [Fact]
    public async Task AProposalOfAnOpenMilestoneComesWithTheListAndHasSeenTheMilestonesOpenIssues()
    {
        _milestoner.Answer = new MilestoneAnswer("v1", null, null, "it is about the GPU");

        var result = await Run(Verbs.ProposeMilestone);

        result.Ok.ShouldBeTrue(result.Error);
        var proposal = result.Milestones.ShouldNotBeNull();
        proposal.Open.Select(m => m.Title).ShouldBe(["v1", "v2"]);
        (proposal.Title, proposal.Reason, proposal.Note).ShouldBe(("v1", "it is about the GPU", null));
        proposal.Open[0].DueOn.ShouldNotBeNull();
        _milestoner.Asked.Single()[0].IssueTitles.ShouldBe(["Fix the GPU crash"]);
        _github.Moves.ShouldBeEmpty();
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASuggestedNewMilestoneCarriesItsTitleAndDescriptionAndMakesNothing()
    {
        _milestoner.Answer = new MilestoneAnswer(null, "v3", "Rings", "none fits");

        var proposal = (await Run(Verbs.ProposeMilestone)).Milestones.ShouldNotBeNull();

        (proposal.Title, proposal.NewTitle, proposal.NewDescription, proposal.Reason).ShouldBe((null, "v3", "Rings", "none fits"));
        _github.MilestonesMade.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaudeFailingGivesTheListWithNoProposalAndWhy()
    {
        _milestoner.Fails = "it timed out";

        var result = await Run(Verbs.ProposeMilestone);

        result.Ok.ShouldBeTrue(result.Error);
        var proposal = result.Milestones.ShouldNotBeNull();
        proposal.Open.Length.ShouldBe(2);
        (proposal.Title, proposal.NewTitle).ShouldBe((null, null));
        proposal.Note.ShouldNotBeNull().ShouldContain("it timed out");
    }

    [Fact]
    public void ReadRefusesAMilestoneThatIsNotOpenAndAnswersThatAreNoProposal()
    {
        string[] open = ["v1", "v2"];

        ClaudeIdeaMilestoner.Read("""{"milestone": "v9", "reason": "x"}""", open).ShouldBeNull();
        ClaudeIdeaMilestoner.Read("not json at all", open).ShouldBeNull();
        ClaudeIdeaMilestoner.Read("{ broken", open).ShouldBeNull();
        ClaudeIdeaMilestoner.Read("""{"newMilestone": {"description": "no title"}}""", open).ShouldBeNull();
        ClaudeIdeaMilestoner.Read("""{"reason": "nothing proposed"}""", open).ShouldBeNull();

        ClaudeIdeaMilestoner.Read("```json\n{\"milestone\": \"V2\", \"reason\": \"fits\"}\n```", open).ShouldBe(new MilestoneAnswer("v2", null, null, "fits"));
        ClaudeIdeaMilestoner.Read("""{"newMilestone": {"title": "v3", "description": "Rings"}, "reason": "r"}""", open).ShouldBe(new MilestoneAnswer(null, "v3", "Rings", "r"));
    }

    [Fact]
    public async Task PlanningIntoANewMilestoneMakesItAfterTheChecksAndPlansIntoIt()
    {
        var planned = await Run(Verbs.PlanIdea, Verbs.NewMilestoneArg, "v3", "Rings", "and", "tilt");

        planned.Ok.ShouldBeTrue(planned.Error);
        _github.MilestonesMade.ShouldBe([(Path.GetFullPath(Repo), "v3")]);
        _github.MilestonesOf[Path.GetFullPath(Repo)].Single(m => m.Title == "v3").Description.ShouldBe("Rings and tilt");
        _github.Moves.ShouldBe([(1, "v3")]);
        _state.Flights().Single().FromIdea.ShouldBe("I1");
    }

    [Fact]
    public async Task PlanningIntoAnExistingMilestoneMakesNoneEvenWhenAskedAsNew()
    {
        (await Run(Verbs.PlanIdea, "v2")).Ok.ShouldBeTrue();
        _github.MilestonesMade.ShouldBeEmpty();
        _github.Moves.ShouldBe([(1, "v2")]);
    }

    [Fact]
    public async Task ANewMilestoneIsNotMadeWhenAnotherCheckRefusesThePlan()
    {
        var ideas = await Run(Verbs.PlanIdea, Verbs.NewMilestoneArg, IdeaIds.Milestone);
        var untitled = await Run(Verbs.PlanIdea, Verbs.NewMilestoneArg);

        ideas.Error.ShouldNotBeNull().ShouldContain("in Ideas already");
        untitled.Error.ShouldNotBeNull().ShouldContain("needs its title");
        _github.MilestonesMade.ShouldBeEmpty();
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanningWithNoMilestoneIsRefusedNamingTheOpenOnesAndPlansNothing()
    {
        var planned = await Run(Verbs.PlanIdea);

        planned.Ok.ShouldBeFalse();
        planned.Error.ShouldNotBeNull().ShouldContain("v1, v2");
        _github.Moves.ShouldBeEmpty();
        _github.MilestonesMade.ShouldBeEmpty();
        _state.Flights().ShouldBeEmpty();
    }
}
