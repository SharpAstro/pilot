using System.Diagnostics;
using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A merger that answers what the test set, and keeps what it was asked.</summary>
internal sealed class FakeIdeaMerger : IIdeaMerger
{
    /// <summary>What the next merges answer; null to join the two bodies.</summary>
    public IdeaMerge? Answer { get; set; }

    public List<(IdeaText Mine, IdeaText GitHub)> Asked { get; } = [];

    public Task<IdeaMerge> MergeAsync(IdeaText mine, IdeaText github, CancellationToken cancellationToken)
    {
        Asked.Add((mine, github));
        return Task.FromResult(Answer ?? new IdeaMerge(new IdeaText(mine.Title, $"{github.Body} + {mine.Body}"), false));
    }
}

/// <summary>Editing, planning and dropping an idea keep it up to date with GitHub, over FakeGitHub and a fake merger.</summary>
public sealed class IdeaEditTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2e-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly FakeIdeaMerger _merger = new();
    private readonly IdeaCommander _commander;

    public IdeaEditTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _commander = new IdeaCommander(_state, planes, catalog, _github, _merger, new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System);
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

    /// <summary>An idea on GitHub that the node has read, as a person starts an edit of it.</summary>
    private async Task<MilestoneIssue> NotedAsync(string title = "Ring shadows", string body = "on the planet")
    {
        _github.AddMilestone(Repo, IdeaIds.Milestone);
        var issue = _github.AddIssue(Repo, title, IdeaIds.Milestone, body: body);
        _state.PutIdeas("pilot", IdeaRows.Of("pilot", await _github.MilestoneIssuesAsync(Repo, IdeaIds.Milestone, Ct), [], DateTimeOffset.UtcNow));
        return issue;
    }

    private Task<CommandResult> Edit(MilestoneIssue from, string text) =>
        _commander.RunAsync(new CommandRequest
        {
            Verb = Verbs.EditIdea,
            Target = IdeaIds.Target("pilot", from.Number),
            Args = [from.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), text],
            Who = "tests",
        }, Ct);

    private MilestoneIssue OnGitHub(int number) => _github.MilestoneIssuesOf[Path.GetFullPath(Repo)].Single(i => i.Number == number);

    [Fact]
    public async Task AnEditOverAnIdeaGitHubHasNotChangedIsSavedVerbatimAndNeverMerged()
    {
        var issue = await NotedAsync();

        var edited = await Edit(issue, "Ring shadows, longer\nreally  on the\nplanet");

        edited.Ok.ShouldBeTrue(edited.Error);
        edited.Way.ShouldBe(IdeaEditWays.Saved);
        edited.Made.ShouldBe("I1");
        _github.Edits.ShouldBe([(1, "Ring shadows, longer", "really  on the\nplanet")]);
        _merger.Asked.ShouldBeEmpty();
        _state.Ideas().Single().Title.ShouldBe("Ring shadows, longer");
    }

    [Fact]
    public async Task AnEditOverAnIdeaChangedElsewhereSavesTheMergeOfBothAndNotEitherAlone()
    {
        var issue = await NotedAsync();
        _github.ChangeElsewhere(Repo, 1, i => i with { Body = "on the planet, and its moons" });

        var edited = await Edit(issue, "Ring shadows\nin the rings");

        edited.Ok.ShouldBeTrue(edited.Error);
        edited.Way.ShouldBe(IdeaEditWays.Merged);
        _merger.Asked.ShouldHaveSingleItem().ShouldBe((new IdeaText("Ring shadows", "in the rings"), new IdeaText("Ring shadows", "on the planet, and its moons")));
        _github.Edits.ShouldBe([(1, "Ring shadows", "on the planet, and its moons + in the rings")]);
    }

    [Fact]
    public async Task AnIdeaChangedElsewhereOnlyInWhatTheEditDidNotTouchIsSavedAsIsWithNoMerge()
    {
        var issue = await NotedAsync();
        // A comment, a label: updated_at moves, the text does not.
        _github.ChangeElsewhere(Repo, 1, i => i);

        var edited = await Edit(issue, "Ring shadows\nin the rings");

        edited.Way.ShouldBe(IdeaEditWays.Saved);
        _merger.Asked.ShouldBeEmpty();
        _github.Edits.ShouldBe([(1, "Ring shadows", "in the rings")]);
    }

    [Fact]
    public async Task AContradictionRaisesACardWithBothVersionsAndTheMergeAsDefaultAndWritesNothingUntilItIsAnswered()
    {
        var issue = await NotedAsync();
        _github.ChangeElsewhere(Repo, 1, i => i with { Body = "not on the planet" });
        _merger.Answer = new IdeaMerge(new IdeaText("Ring shadows", "on the planet, not"), Contradicts: true);

        var asked = await Edit(issue, "Ring shadows\non the planet, always");

        asked.Ok.ShouldBeTrue(asked.Error);
        asked.Way.ShouldBe(IdeaEditWays.Asked);
        _github.Edits.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Single();
        asked.Made.ShouldBe(card.Id);
        card.Kind.ShouldBe(CardKinds.IdeaConflict);
        card.Title.ShouldStartWith("I1: Ring shadows");
        card.Detail.ShouldNotBeNull().ShouldContain("on the planet, always");
        card.Detail.ShouldContain("not on the planet");
        card.Detail.ShouldContain("on the planet, not");
        card.Choices.Select(c => c.Label).ShouldBe(["Keep the merge", "Keep mine", "Keep GitHub's"]);

        // Keep mine: the choice saves through the same guarded path, from what GitHub holds now.
        var mine = card.Choices.Single(c => c.Label == "Keep mine");
        var answered = await _commander.RunAsync(new CommandRequest { Verb = mine.Verb, Target = mine.Target, Args = mine.Args, Who = "tests" }, Ct);

        answered.Way.ShouldBe(IdeaEditWays.Saved);
        _github.Edits.ShouldBe([(1, "Ring shadows", "on the planet, always")]);
        _state.Cards(openOnly: true).ShouldBeEmpty();
    }

    [Fact]
    public async Task KeepTheMergeSavesTheProposedMergeAsIs()
    {
        var issue = await NotedAsync();
        _github.ChangeElsewhere(Repo, 1, i => i with { Body = "not on the planet" });
        _merger.Answer = new IdeaMerge(new IdeaText("Ring shadows", "sometimes on the planet"), Contradicts: true);
        await Edit(issue, "Ring shadows\non the planet, always");

        var merge = _state.Cards(openOnly: true).Single().Choices[0];
        var answered = await _commander.RunAsync(new CommandRequest { Verb = merge.Verb, Target = merge.Target, Args = merge.Args, Who = "tests" }, Ct);

        answered.Ok.ShouldBeTrue(answered.Error);
        _github.Edits.ShouldBe([(1, "Ring shadows", "sometimes on the planet")]);
    }

    [Fact]
    public async Task AChangeLandingBetweenTheReadAndTheSaveRunsTheCheckAgain()
    {
        var issue = await NotedAsync();
        _github.BeforeNextWrite = () => _github.ChangeElsewhere(Repo, 1, i => i with { Body = "landed between" });

        var edited = await Edit(issue, "Ring shadows\nmine");

        edited.Ok.ShouldBeTrue(edited.Error);
        // The first pass saw it unchanged and tried to save as is; the write found it moved, and the second pass merged.
        edited.Way.ShouldBe(IdeaEditWays.Merged);
        _merger.Asked.ShouldHaveSingleItem().GitHub.Body.ShouldBe("landed between");
        _github.Edits.ShouldBe([(1, "Ring shadows", "landed between + mine")]);
    }

    [Fact]
    public async Task APlanOrADropOverAnIdeaThatMovedBetweenReadAndWriteReadsAgainAndStillWrites()
    {
        var issue = await NotedAsync();
        _github.AddMilestone(Repo, "v1");
        _github.BeforeNextWrite = () => _github.ChangeElsewhere(Repo, 1, i => i with { Body = "edited meanwhile" });

        var dropped = await _commander.RunAsync(new CommandRequest { Verb = Verbs.DropIdea, Target = IdeaIds.Target("pilot", issue.Number), Args = ["not now"], Who = "tests" }, Ct);

        dropped.Ok.ShouldBeTrue(dropped.Error);
        _github.ClosedNotPlanned.ShouldBe([(1, "not now")]);
        OnGitHub(1).Open.ShouldBeFalse();
        OnGitHub(1).Body.ShouldBe("edited meanwhile");
    }

    [Fact]
    public async Task AnIdeaThatKeepsChangingIsGivenUpOnWithNothingWritten()
    {
        var issue = await NotedAsync();
        var runs = 0;
        _github.BeforeNextWrite = Again;
        void Again()
        {
            runs++;
            _github.ChangeElsewhere(Repo, 1, i => i with { Body = $"change {runs}" });
            _github.BeforeNextWrite = Again;
        }

        var edited = await Edit(issue, "Ring shadows\nmine");

        edited.Ok.ShouldBeFalse();
        edited.Error.ShouldNotBeNull().ShouldContain("kept changing");
        _github.Edits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a time")]
    public async Task AnEditWithoutItsStartingTimeOrTextIsRefused(string updatedAt)
    {
        var issue = await NotedAsync();

        var refused = await _commander.RunAsync(new CommandRequest { Verb = Verbs.EditIdea, Target = IdeaIds.Target("pilot", issue.Number), Args = [updatedAt], Who = "tests" }, Ct);

        refused.Error.ShouldNotBeNull().ShouldContain("updated_at");
        _github.Edits.ShouldBeEmpty();
    }

    [Fact]
    public void ClaudesAnswerIsReadFromItsJsonEvenInAFenceAndNothingElseIsAMerge()
    {
        ClaudeIdeaMerger.Read("```json\n{\"verdict\":\"merged\",\"title\":\"T\",\"body\":\"B\"}\n```").ShouldBe(new IdeaMerge(new IdeaText("T", "B"), false));
        ClaudeIdeaMerger.Read("{\"verdict\":\"contradicts\",\"title\":\"T\",\"body\":\"\"}").ShouldBe(new IdeaMerge(new IdeaText("T", ""), true));
        ClaudeIdeaMerger.Read("I cannot").ShouldBeNull();
        ClaudeIdeaMerger.Read("{\"verdict\":\"maybe\",\"title\":\"T\"}").ShouldBeNull();
        ClaudeIdeaMerger.Read("{\"verdict\":\"merged\",\"body\":\"B\"}").ShouldBeNull();
    }
}
