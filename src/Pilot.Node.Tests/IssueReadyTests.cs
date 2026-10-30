using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A readiness checker that answers what the test set, or fails, and keeps what it was asked.</summary>
internal sealed class FakeIssueReadinessChecker : IIssueReadinessChecker
{
    public ReadinessAnswer Answer { get; set; } = new(true, ["one goal", "tests are nameable"]);

    public string? Fails { get; set; }

    public List<IdeaText> Asked { get; } = [];

    public Task<ReadinessAnswer> CheckAsync(IdeaText issue, string repoPath, CancellationToken cancellationToken)
    {
        Asked.Add(issue);
        return Fails is { } why ? throw new FlightException(why) : Task.FromResult(Answer);
    }
}

/// <summary>A milestone suggester that answers what the test set, or fails, and keeps what it was asked.</summary>
internal sealed class FakeMilestoneSuggester : IMilestoneSuggester
{
    public Func<SuggestItem[], SuggestAnswer>? Answer { get; set; }

    public string? Fails { get; set; }

    public List<SuggestItem[]> Asked { get; } = [];

    public Task<SuggestAnswer> SuggestAsync(SuggestItem[] items, MilestoneContext[] open, string repoPath, CancellationToken cancellationToken)
    {
        Asked.Add(items);
        return Fails is { } why ? throw new FlightException(why) : Task.FromResult(Answer?.Invoke(items) ?? new SuggestAnswer([], []));
    }
}

/// <summary>A repo of the test's own with milestones and open issues on a fake GitHub, and the commander that checks and sorts them.</summary>
public abstract class IssueBoardFixture : IDisposable
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    internal readonly NodeState State = new(new InMemoryJournal(), TimeProvider.System);
    internal readonly FakeGitHub GitHub = new();
    internal readonly FakeIssueReadinessChecker Checker = new();
    internal readonly FakeMilestoneSuggester Suggester = new();
    internal readonly IdeaCommander Commander;

    protected IssueBoardFixture()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(Repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(State, planes, TimeProvider.System, github: GitHub);
        var catalog = new RepoCatalog(State, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        Commander = new IdeaCommander(State, planes, catalog, GitHub, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System, null, Checker, Suggester);
        GitHub.AddMilestone(Repo, IdeaIds.Milestone, openIssues: 3);
        GitHub.AddMilestone(Repo, "v1", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "The GPU work", openIssues: 1);
        GitHub.AddMilestone(Repo, "v2");
        GitHub.AddIssue(Repo, "Ring shadows", IdeaIds.Milestone, body: "on the planet");
        GitHub.AddIssue(Repo, "Fix the GPU crash", "v1");
        GitHub.AddIssue(Repo, "Loose end", null);
        GitHub.AddIssue(Repo, "Ring glow", IdeaIds.Milestone);
        GitHub.AddIssue(Repo, "Cache sky maps", null);
        GitHub.AddIssue(Repo, "Ring moons", IdeaIds.Milestone);
        GitHub.AddIssue(Repo, "Shipped already", "v1", open: false);
    }

    protected string Repo => Path.Combine(_root, "pilot");

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
        GC.SuppressFinalize(this);
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

    internal Task<CommandResult> Run(string verb, string target, params string[] args) =>
        Commander.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    internal string? MilestoneOf(int number) => GitHub.MilestoneIssuesOf[Path.TrimEndingDirectorySeparator(Path.GetFullPath(Repo))].Single(i => i.Number == number).Milestone;
}

/// <summary>check-ready: Claude's judgement of an issue or idea, kept on its row with the reasons and the time, through a fake checker.</summary>
public sealed class IssueReadyTests : IssueBoardFixture
{
    private static string Issue(int number) => IssueIds.Target("pilot", number);

    [Fact]
    public async Task AReadyIssueKeepsReadyWithItsReasonsAndTimeOnItsRowAndWritesNothingToGitHub()
    {
        var before = DateTimeOffset.UtcNow;

        var result = await Run(Verbs.CheckReady, Issue(2));

        result.Ok.ShouldBeTrue(result.Error);
        var answer = result.Readiness.ShouldNotBeNull();
        (answer.Note, answer.Readiness!.Ready).ShouldBe((null, true));
        answer.Readiness.Reasons.ShouldBe(["one goal", "tests are nameable"]);
        var row = State.Issues().Single(i => i.Number == 2);
        row.Readiness.ShouldBe(answer.Readiness);
        row.Readiness!.CheckedAt.ShouldBeGreaterThanOrEqualTo(before);
        Checker.Asked.Single().Title.ShouldBe("Fix the GPU crash");
        GitHub.Moves.ShouldBeEmpty();
        GitHub.Edits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ANotReadyIssueKeepsNotReadyWithTheReasonsAndAnIdeaCanBeCheckedByItsIdeaTarget()
    {
        Checker.Answer = new ReadinessAnswer(false, ["two goals in one issue", "no test can be named", "which GPU?"]);

        var result = await Run(Verbs.CheckReady, IdeaIds.Target("pilot", 1));

        result.Ok.ShouldBeTrue(result.Error);
        var row = State.Issues().Single(i => i.Number == 1);
        row.Readiness.ShouldNotBeNull().Ready.ShouldBeFalse();
        row.Readiness.Reasons.ShouldBe(["two goals in one issue", "no test can be named", "which GPU?"]);
        Checker.Asked.Single().ShouldBe(new IdeaText("Ring shadows", "on the planet"));
        State.Issues().Where(i => i.Number != 1).ShouldAllBe(i => i.Readiness == null);
    }

    [Fact]
    public async Task AnAnswerStaysOnTheRowThroughAReadOfTheRepoAndIsReplacedByTheNextCheck()
    {
        await Run(Verbs.CheckReady, Issue(3));
        var first = State.Issues().Single(i => i.Number == 3).Readiness.ShouldNotBeNull();

        // A move reads the board again: GitHub knows nothing of readiness, so the row keeps it.
        (await Run(Verbs.SetMilestone, Issue(3), "v2")).Ok.ShouldBeTrue();
        State.Issues().Single(i => i.Number == 3).Readiness.ShouldBe(first);

        Checker.Answer = new ReadinessAnswer(false, ["now there is an open question"]);
        await Run(Verbs.CheckReady, Issue(3));
        var second = State.Issues().Single(i => i.Number == 3).Readiness.ShouldNotBeNull();
        second.Ready.ShouldBeFalse();
        second.Reasons.ShouldBe(["now there is an open question"]);
        second.CheckedAt.ShouldBeGreaterThanOrEqualTo(first.CheckedAt);
    }

    [Fact]
    public async Task ClaudeFailingIsANoteAndLeavesTheRowAsItWas()
    {
        await Run(Verbs.CheckReady, Issue(2));
        var kept = State.Issues().Single(i => i.Number == 2).Readiness;
        Checker.Fails = "it timed out";

        var result = await Run(Verbs.CheckReady, Issue(2));

        result.Ok.ShouldBeTrue(result.Error);
        var answer = result.Readiness.ShouldNotBeNull();
        answer.Readiness.ShouldBeNull();
        answer.Note.ShouldNotBeNull().ShouldContain("it timed out");
        State.Issues().Single(i => i.Number == 2).Readiness.ShouldBe(kept);
    }

    [Fact]
    public async Task AClosedIssueAndAMissingOneAndAWrongTargetAreRefused()
    {
        (await Run(Verbs.CheckReady, Issue(7))).Error.ShouldNotBeNull().ShouldContain("closed");
        (await Run(Verbs.CheckReady, Issue(99))).Error.ShouldNotBeNull().ShouldContain("no issue #99");
        (await Run(Verbs.CheckReady, "flight/PIL1")).Error.ShouldNotBeNull().ShouldContain("issue target");
        Checker.Asked.ShouldBeEmpty();
    }

    [Fact]
    public void TheAnswerIsReadFromTheJsonInClaudesWords()
    {
        var fenced = ClaudeIssueReadinessChecker.Read("```json\n{\"ready\": false, \"reasons\": [\"too big\", \" \", \"no tests\"]}\n```").ShouldNotBeNull();
        fenced.Ready.ShouldBeFalse();
        fenced.Reasons.ShouldBe(["too big", "no tests"]);
        var bare = ClaudeIssueReadinessChecker.Read("""{"ready": true}""").ShouldNotBeNull();
        bare.Ready.ShouldBeTrue();
        bare.Reasons.ShouldBeEmpty();
        ClaudeIssueReadinessChecker.Read("""{"reasons": ["no verdict"]}""").ShouldBeNull();
        ClaudeIssueReadinessChecker.Read("""{"ready": "yes"}""").ShouldBeNull();
        ClaudeIssueReadinessChecker.Read("not json").ShouldBeNull();
        ClaudeIssueReadinessChecker.Read("{ broken").ShouldBeNull();
    }
}
