using System.Diagnostics;
using System.Globalization;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A tidier that answers what the test set, and keeps what it was asked.</summary>
internal sealed class FakeIdeaTidier : IIdeaTidier
{
    public IdeaText? Answer { get; set; }

    public List<(IdeaText Current, string RepoPath)> Asked { get; } = [];

    public Task<IdeaText> TidyAsync(IdeaText current, string repoPath, CancellationToken cancellationToken)
    {
        Asked.Add((current, repoPath));
        return Task.FromResult(Answer ?? new IdeaText(current.Title, "- " + current.Body));
    }
}

/// <summary>Tidy an idea on request: a proposal that writes nothing, saved through the up-to-date path, or discarded.</summary>
public sealed class IdeaTidyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly FakeIdeaMerger _merger = new();
    private readonly FakeIdeaTidier _tidier = new();
    private readonly IdeaCommander _commander;

    public IdeaTidyTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _commander = new IdeaCommander(_state, planes, catalog, _github, _merger, _tidier, new FakeIdeaRepoChooser(), flights, TimeProvider.System);
    }

    private string Repo => Path.Combine(_root, "pilot");

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

    private async Task<MilestoneIssue> NotedAsync(string title = "Ring shadows", string body = "on the planet")
    {
        _github.AddMilestone(Repo, IdeaIds.Milestone);
        var issue = _github.AddIssue(Repo, title, IdeaIds.Milestone, body: body);
        _state.PutIdeas("pilot", IdeaRows.Of("pilot", await _github.MilestoneIssuesAsync(Repo, IdeaIds.Milestone, Ct), [], DateTimeOffset.UtcNow));
        return issue;
    }

    private Task<CommandResult> Tidy(int number) =>
        _commander.RunAsync(new CommandRequest { Verb = Verbs.TidyIdea, Target = IdeaIds.Target("pilot", number), Who = "tests" }, Ct);

    private Task<CommandResult> Save(IdeaTidyProposal proposal) =>
        _commander.RunAsync(new CommandRequest
        {
            Verb = Verbs.SaveTidyIdea,
            Target = IdeaIds.Target("pilot", 1),
            Args = [proposal.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), IdeaTidyCache.Joined(proposal.Title, proposal.Body)],
            Who = "tests",
        }, Ct);

    [Fact]
    public async Task TidyProposesTheTextAndWritesNothing()
    {
        var issue = await NotedAsync();
        _tidier.Answer = new IdeaText("Ring shadows", "- touches `Planet.Render`\n- related: #12\n- does it cast on moons?");

        var proposed = await Tidy(1);

        proposed.Ok.ShouldBeTrue(proposed.Error);
        proposed.Made.ShouldBe("I1");
        var tidy = proposed.Tidy.ShouldNotBeNull();
        tidy.Body.ShouldContain("`Planet.Render`");
        tidy.CurrentBody.ShouldBe("on the planet");
        tidy.UpdatedAt.ShouldBe(issue.UpdatedAt);
        _github.Edits.ShouldBeEmpty();
        _tidier.Asked.ShouldHaveSingleItem().RepoPath.ShouldBe(Path.GetFullPath(Repo), StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task TidyCarriesTheMilestoneProposalAndTheOpenListOrSaysThereIsNoOneToPropose()
    {
        await NotedAsync();
        _github.AddMilestone(Repo, "v1", null, "The GPU work", 3);
        var planes = new Planes(new GitCli());
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        var milestoner = new FakeIdeaMilestoner { Answer = new MilestoneAnswer("v1", null, null, "it is the GPU") };
        var proposing = new IdeaCommander(_state, planes, catalog, _github, _merger, _tidier, new FakeIdeaRepoChooser(), new FlightCommander(_state, planes, TimeProvider.System, github: _github), TimeProvider.System, milestoner);

        var withOne = (await proposing.RunAsync(new CommandRequest { Verb = Verbs.TidyIdea, Target = IdeaIds.Target("pilot", 1), Who = "tests" }, Ct)).Tidy.ShouldNotBeNull().Milestone.ShouldNotBeNull();
        var withNone = (await Tidy(1)).Tidy.ShouldNotBeNull().Milestone.ShouldNotBeNull();

        withOne.Open.Select(m => m.Title).ShouldBe(["v1"]);
        (withOne.Title, withOne.Reason).ShouldBe(("v1", "it is the GPU"));
        (withNone.Title, withNone.NewTitle).ShouldBe((null, null));
        withNone.Note.ShouldBe("no proposal: this node has no one to propose with");
    }

    [Fact]
    public async Task SavingAProposalWritesItThroughTheGuardedPathAsIsOverAnUnchangedIdea()
    {
        await NotedAsync();
        _tidier.Answer = new IdeaText("Ring shadows", "- on the planet\n- related: #12");
        var tidy = (await Tidy(1)).Tidy.ShouldNotBeNull();

        var saved = await Save(tidy);

        saved.Ok.ShouldBeTrue(saved.Error);
        saved.Way.ShouldBe(IdeaEditWays.Saved);
        _github.Edits.ShouldBe([(1, "Ring shadows", "- on the planet\n- related: #12")]);
        _merger.Asked.ShouldBeEmpty();
        _state.Ideas().Single().Body.ShouldBe("- on the planet\n- related: #12");
    }

    [Fact]
    public async Task AChangeOnGitHubSinceTheProposalGoesThroughMerge()
    {
        await NotedAsync();
        var tidy = (await Tidy(1)).Tidy.ShouldNotBeNull();
        _github.ChangeElsewhere(Repo, 1, i => i with { Body = "on the planet, and its moons" });

        var saved = await Save(tidy);

        saved.Way.ShouldBe(IdeaEditWays.Merged);
        _merger.Asked.ShouldHaveSingleItem().GitHub.ShouldBe(new IdeaText("Ring shadows", "on the planet, and its moons"));
        _github.Edits.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AContradictionSinceTheProposalAsksAndWritesNothing()
    {
        await NotedAsync();
        var tidy = (await Tidy(1)).Tidy.ShouldNotBeNull();
        _github.ChangeElsewhere(Repo, 1, i => i with { Body = "not on the planet" });
        _merger.Answer = new IdeaMerge(new IdeaText("Ring shadows", "- sometimes on the planet"), Contradicts: true);

        var saved = await Save(tidy);

        saved.Way.ShouldBe(IdeaEditWays.Asked);
        _github.Edits.ShouldBeEmpty();
        _state.Cards(openOnly: true).Single().Kind.ShouldBe(CardKinds.IdeaConflict);
    }

    [Fact]
    public async Task DiscardingWritesNothingAndTheCliForgetsTheProposal()
    {
        await NotedAsync();
        var tidy = (await Tidy(1)).Tidy.ShouldNotBeNull() with { Milestone = null };
        var target = IdeaIds.Target("pilot", 1) + "/" + Guid.NewGuid().ToString("N");
        IdeaTidyCache.Write(target, tidy);

        IdeaTidyCache.Read(target).ShouldBe(tidy);
        IdeaTidyCache.Discard(target).ShouldBeTrue();

        IdeaTidyCache.Read(target).ShouldBeNull();
        _github.Edits.ShouldBeEmpty();
    }

    [Fact]
    public void TheDiffShowsWhatChangedAndIsEmptyWhenNothingDid()
    {
        var diff = IdeaDiff.Unified("Ring shadows\non the planet", "Ring shadows\n- on the planet\n- related: #12", "I1 (GitHub)", "I1 (tidied)");

        diff.ShouldBe("--- I1 (GitHub)\n+++ I1 (tidied)\n Ring shadows\n-on the planet\n+- on the planet\n+- related: #12\n");
        IdeaDiff.Unified("same", "same", "a", "b").ShouldBe("");
    }

    [Fact]
    public void ThePromptAsksForTheShapeTheFlightSets()
    {
        var prompt = ClaudeIdeaTidier.Prompt(new IdeaText("Ring shadows", "on the planet"));

        prompt.ShouldContain("code spans", Case.Insensitive);
        prompt.ShouldContain("related issue or PR");
        prompt.ShouldContain("question");
        prompt.ShouldContain("no \"Done when\"");
        prompt.ShouldContain("Ring shadows\non the planet");
        ClaudeIdeaTidier.Read("```json\n{\"title\": \"T\", \"body\": \"- a\"}\n```").ShouldBe(new IdeaText("T", "- a"));
        ClaudeIdeaTidier.Read("no json").ShouldBeNull();
        prompt.ShouldNotContain("image");
    }

    private const string Shot = "![shot](pilot-attachment:abc123.png)";

    [Fact]
    public void ThePromptNamesTheImagesAndTheLocalCopiesAndTheRunReadsTheFolder()
    {
        var copy = Path.Combine(_root, "attachments", "ideas", "abc123.png");
        var prompt = ClaudeIdeaTidier.Prompt(new IdeaText("Ring shadows", $"on the planet\n\n{Shot}\n\nand moons"), [copy]);

        prompt.ShouldContain("holds 1 image");
        prompt.ShouldContain("verbatim");
        prompt.ShouldContain("same place");
        prompt.ShouldContain($"- {copy}");
        prompt.ShouldContain(Shot);

        var attachments = new Attachments(_root);
        IdeaImagePrompt.Args("sonnet", [copy], attachments).ShouldBe(["-p", "--model", "sonnet", "--add-dir", attachments.IdeaFolder]);
        IdeaImagePrompt.Args("sonnet", [], attachments).ShouldBe(["-p", "--model", "sonnet"]);
    }

    [Fact]
    public void TheCopiesAreTheOnesTheNodeHolds()
    {
        var attachments = new Attachments(_root);
        var id = attachments.AddIdeaImage([1, 2, 3], "png");

        var copies = IdeaImagePrompt.Copies($"a\n![x](pilot-attachment:{id}.png)\n![y](pilot-attachment:nothere.png)\n![z](https://example.com/z.png)", attachments);

        copies.ShouldBe([attachments.FindIdeaImage(id)!.Value.Path]);
        IdeaImagePrompt.Copies("no images", attachments).ShouldBeEmpty();
    }

    [Fact]
    public void AnImageTheTidyDroppedIsPutBackAfterTheParagraphItFollowed()
    {
        var current = $"on the planet\n\n{Shot}\n\nand moons";

        IdeaImagePrompt.Restore(current, "- on the planet\n\n- and moons")
            .ShouldBe($"- on the planet\n\n{Shot}\n\n- and moons");
        IdeaImagePrompt.Restore(current, "- and moons\n- on the planet\n\n- more")
            .ShouldBe($"- and moons\n- on the planet\n\n{Shot}\n\n- more");
        IdeaImagePrompt.Restore(current, "- something else").ShouldBe($"- something else\n\n{Shot}");
        IdeaImagePrompt.Restore($"a\n\n{Shot}\n![two](https://x/y.png)", "a\n\nb")
            .ShouldBe($"a\n\n{Shot}\n\n![two](https://x/y.png)\n\nb");
    }

    [Fact]
    public void AnImageTheTidyDroppedThatStoodFirstIsPutBackAtTheTop()
    {
        const string Two = "![two](https://x/y.png)";

        IdeaImagePrompt.Restore($"{Shot}\n\nmore words", "- more words").ShouldBe($"{Shot}\n\n- more words");
        IdeaImagePrompt.Restore($"{Shot}\n{Two}\n\nmore words", "- more words").ShouldBe($"{Shot}\n\n{Two}\n\n- more words");
        IdeaImagePrompt.Restore($"{Shot}\n{Two}\n\nmore words", $"{Shot}\n\n- more words").ShouldBe($"{Shot}\n\n{Two}\n\n- more words");
        IdeaImagePrompt.Restore($"{Shot}\n\nmore words", "").ShouldBe(Shot);
    }

    [Fact]
    public void AProposalThatKeptItsImagesIsLeftAsItIs()
    {
        var proposed = $"- on the planet\n\n{Shot}\n\n- and moons";

        IdeaImagePrompt.Restore($"on the planet\n\n{Shot}\n\nand moons", proposed).ShouldBe(proposed);
        IdeaImagePrompt.Restore("no images", "- tidy").ShouldBe("- tidy");
    }

    [Fact]
    public async Task ATidyThatDroppedAnImageIsOfferedWithItBack()
    {
        await NotedAsync(body: $"on the planet\n\n{Shot}");
        _tidier.Answer = new IdeaText("Ring shadows", "- on the planet\n- related: #12");

        var tidy = (await Tidy(1)).Tidy.ShouldNotBeNull();

        tidy.Body.ShouldBe($"- on the planet\n- related: #12\n\n{Shot}");
        tidy.CurrentBody.ShouldBe($"on the planet\n\n{Shot}");
    }
}
