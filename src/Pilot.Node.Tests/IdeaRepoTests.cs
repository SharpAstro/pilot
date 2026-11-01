using System.Diagnostics;
using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A chooser that answers what the test set, and keeps what it was asked.</summary>
internal sealed class FakeIdeaRepoChooser : IIdeaRepoChooser
{
    public string[] Answer { get; set; } = ["pilot"];

    public List<(IdeaText Text, RepoOption[] Repos, string? Context)> Asked { get; } = [];

    public Task<string[]> ChooseAsync(IdeaText text, IReadOnlyList<RepoOption> repos, string? context, CancellationToken cancellationToken)
    {
        Asked.Add((text, [.. repos], context));
        return Task.FromResult(Answer);
    }
}

/// <summary>Where an idea is filed: chosen by Claude, overridden by --repo, asked when two fit, and moved with the old id still resolving.</summary>
public sealed class IdeaRepoTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly FakeIdeaRepoChooser _chooser = new();
    private readonly IdeaCommander _commander;

    public IdeaRepoTests()
    {
        foreach (var name in new[] { "pilot", "tianwen", "DIR.Lib" })
        {
            var repo = Path.Combine(_root, name);
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
            File.WriteAllText(Path.Combine(repo, name + ".slnx"), "<Solution />\n");
            // A repo pilot works in, so the idea's repo choice offers it (RepoCatalog.Tracked).
            File.WriteAllText(Path.Combine(repo, RepoCatalog.MarkerFile), "# PILOT.md\n");
            Git(repo, "remote", "add", "origin", $"https://github.com/SharpAstro/{name}.git");
            Git(repo, "add", ".");
            Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        }
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _commander = new IdeaCommander(_state, planes, catalog, _github, new FakeIdeaMerger(), new FakeIdeaTidier(), _chooser, flights, TimeProvider.System);
    }

    private string Path_(string name) => Path.Combine(_root, name);

    public void Dispose()
    {
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

    private Task<CommandResult> Run(string verb, string target, params string[] args) =>
        _commander.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    [Fact]
    public async Task WithoutARepoClaudeChoosesAmongTheNodesReposAndTheIdeaIsFiledThereWithTheWordsAsGiven()
    {
        _chooser.Answer = ["tianwen"];

        var result = await Run(Verbs.NoteIdea, Verbs.RepoTarget(Verbs.AnyRepo), "Ring shadows\nnot  on the\nplanet", "page: flight TIA12");

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("tianwen I1");
        var (text, options, context) = _chooser.Asked.ShouldHaveSingleItem();
        text.ShouldBe(new IdeaText("Ring shadows", "not  on the\nplanet"));
        context.ShouldBe("page: flight TIA12");
        options.Select(o => o.Name).Order().ShouldBe(["DIR.Lib", "pilot", "tianwen"]);
        options.ShouldAllBe(o => o.Type == ".NET" && o.Path.Length > 0);
        var created = _github.IssuesCreated.ShouldHaveSingleItem();
        Path.GetFileName(created.Repo).ShouldBe("tianwen");
        (created.Title, created.Body).ShouldBe(("Ring shadows", "not  on the\nplanet"));
        _state.Ideas().ShouldHaveSingleItem().Qualified.ShouldBe("tianwen I1");
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ARepoGivenFilesThereWithNoCallToClaude()
    {
        var result = await Run(Verbs.NoteIdea, Verbs.RepoTarget("DIR.Lib"), "A multi-line editor");

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("DIR.Lib I1");
        _chooser.Asked.ShouldBeEmpty();
        Path.GetFileName(_github.IssuesCreated.ShouldHaveSingleItem().Repo).ShouldBe("DIR.Lib");
    }

    [Fact]
    public async Task WhenTwoReposFitEquallyACardAsksAndNothingIsCreatedUntilItIsAnswered()
    {
        _chooser.Answer = ["tianwen", "DIR.Lib"];

        var asked = await Run(Verbs.NoteIdea, Verbs.RepoTarget(Verbs.AnyRepo), "Scroll the list with the wheel");

        asked.Ok.ShouldBeTrue(asked.Error);
        asked.Way.ShouldBe(IdeaEditWays.Asked);
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        asked.Made.ShouldBe(card.Id);
        card.Kind.ShouldBe(CardKinds.IdeaRepo);
        card.Choices.Select(c => c.Label).ShouldBe(["File in tianwen", "File in DIR.Lib"]);
        _github.IssuesCreated.ShouldBeEmpty();
        _github.MilestonesMade.ShouldBeEmpty();
        _state.Ideas().ShouldBeEmpty();

        // The person's answer is a note into the repo named: no second call to Claude, and the card is done.
        var choice = card.Choices[1];
        var filed = await Run(choice.Verb, choice.Target, choice.Args);

        filed.Ok.ShouldBeTrue(filed.Error);
        filed.Made.ShouldBe("DIR.Lib I1");
        _chooser.Asked.ShouldHaveSingleItem();
        (Path.GetFileName(_github.IssuesCreated.ShouldHaveSingleItem().Repo), _github.IssuesCreated[0].Title).ShouldBe(("DIR.Lib", "Scroll the list with the wheel"));
        _state.Cards(openOnly: true).ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaudeNamingNoRepoFilesNothing()
    {
        _chooser.Answer = [];

        var result = await Run(Verbs.NoteIdea, Verbs.RepoTarget(Verbs.AnyRepo), "Something vague");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("--repo");
        _github.IssuesCreated.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task MovingAnIdeaTransfersItIntoTheNewReposIdeasAndTheOldIdStillFindsIt()
    {
        var noted = await Run(Verbs.NoteIdea, Verbs.RepoTarget("pilot"), "Misfiled idea\nabout the sky");
        noted.Made.ShouldBe("pilot I1");

        var moved = await Run(Verbs.MoveIdea, IdeaIds.Target("pilot", 1), "tianwen");

        moved.Ok.ShouldBeTrue(moved.Error);
        moved.Made.ShouldBe("tianwen I2");
        _github.Transfers.ShouldBe([(1, "SharpAstro/tianwen", 2)]);
        _github.MilestonesMade.Select(m => Path.GetFileName(m.Repo)).ShouldBe(["pilot", "tianwen"]);
        var ideas = _state.Ideas().ShouldHaveSingleItem();
        (ideas.Repo, ideas.Id, ideas.State, ideas.Title, ideas.Body).ShouldBe(("tianwen", "I2", IdeaState.Noted, "Misfiled idea", "about the sky"));
        _github.MilestoneIssuesOf[Path_("tianwen")].ShouldHaveSingleItem().Milestone.ShouldBe(IdeaIds.Milestone);
        _state.IdeaMoves().ShouldBe([new IdeaMoveRow("pilot", "I1", "tianwen", "I2")]);

        // The old id, in the old repo, is the new idea: an edit through it saves there.
        var edited = await Run(Verbs.EditIdea, IdeaIds.Target("pilot", 1), ideas.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), "Misfiled idea, now filed right");

        edited.Ok.ShouldBeTrue(edited.Error);
        edited.Made.ShouldBe("I2");
        _github.Edits.ShouldHaveSingleItem().Title.ShouldBe("Misfiled idea, now filed right");
    }

    [Fact]
    public async Task MovingToTheRepoItIsInAlreadyIsRefusedAndChangesNothing()
    {
        await Run(Verbs.NoteIdea, Verbs.RepoTarget("pilot"), "Stays");

        var result = await Run(Verbs.MoveIdea, IdeaIds.Target("pilot", 1), "pilot");

        result.Ok.ShouldBeFalse();
        _github.Transfers.ShouldBeEmpty();
        _state.IdeaMoves().ShouldBeEmpty();
    }

    [Fact]
    public void AMoveChainIsFollowedToTheEndAndAnIdeaThatNeverMovedStaysWhereItIs()
    {
        IdeaMoveRow[] moves = [new("pilot", "I1", "tianwen", "I2"), new("tianwen", "I2", "DIR.Lib", "I9")];

        IdeaIds.Follow(moves, "pilot", 1).ShouldBe(("DIR.Lib", 9));
        IdeaIds.Follow(moves, "tianwen", 2).ShouldBe(("DIR.Lib", 9));
        IdeaIds.Follow(moves, "pilot", 7).ShouldBe(("pilot", 7));
        // A cycle cannot come of real moves; it must still end.
        IdeaIds.Follow([new("a", "I1", "b", "I1"), new("b", "I1", "a", "I1")], "a", 1).Number.ShouldBe(1);
    }

    [Fact]
    public void ClaudesAnswerNamesOnlyListedReposAndTheWordsAreNeverPartOfIt()
    {
        RepoOption[] repos = [new("tianwen", ".NET", "/r/tianwen"), new("DIR.Lib", ".NET", "/r/DIR.Lib")];

        ClaudeIdeaRepoChooser.Read("```json\n{\"repos\": [\"Tianwen\", \"nowhere\", \"DIR.Lib\", \"tianwen\"]}\n```", repos).ShouldBe(["tianwen", "DIR.Lib"]);
        ClaudeIdeaRepoChooser.Read("{\"repos\": []}", repos).ShouldBeEmpty();
        ClaudeIdeaRepoChooser.Read("no idea", repos).ShouldBeNull();
        ClaudeIdeaRepoChooser.Read("{\"repo\": \"tianwen\"}", repos).ShouldBeNull();

        var prompt = ClaudeIdeaRepoChooser.Prompt(new IdeaText("Ring shadows", "on the planet"), repos, "flight TIA12");
        prompt.ShouldContain("- tianwen (.NET) /r/tianwen");
        prompt.ShouldContain("flight TIA12");
        prompt.ShouldContain("Ring shadows\non the planet");
        prompt.ShouldNotContain("image");
    }

    [Fact]
    public void ThePromptNamesTheImagesAndTheLocalCopiesAtNoteTime()
    {
        RepoOption[] repos = [new("tianwen", ".NET", "/r/tianwen")];
        var root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var attachments = new Attachments(root);
            var id = attachments.AddIdeaImage([1, 2, 3], "png");
            var body = $"on the planet\n![shot](pilot-attachment:{id}.png)";
            var copies = IdeaImagePrompt.Copies(body, attachments);

            var prompt = ClaudeIdeaRepoChooser.Prompt(new IdeaText("Ring shadows", body), repos, null, copies);

            copies.ShouldHaveSingleItem();
            prompt.ShouldContain("holds 1 image");
            prompt.ShouldContain($"- {copies[0]}");
            prompt.ShouldContain($"![shot](pilot-attachment:{id}.png)");
            prompt.ShouldContain("never changed");
            IdeaImagePrompt.Args(ClaudeIdeaRepoChooser.Model, copies, attachments).ShouldContain(attachments.IdeaFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
