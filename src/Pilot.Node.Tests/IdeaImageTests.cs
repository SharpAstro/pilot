using System.Diagnostics;
using System.Globalization;
using System.Net;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What an idea's text says about its images: markers, Markdown and HTML, and the rows read from them.</summary>
public sealed class IdeaImageParseTests
{
    [Fact]
    public void IdeaImagesFindsMarkersMarkdownAndImgTagsInTheOrderTheyStand()
    {
        var body = """
            first ![pasted](pilot-attachment:abc123def456.png) then <img alt="a diagram" src="https://example.com/d.png" width="40">
            and ![on github](https://github.com/SharpAstro/pilot/blob/pilot-attachments/ideas/0011aabb22cc.jpg?raw=true "title") last
            """;

        var images = IdeaImages.In(body);

        images.ShouldBe([
            new IdeaImage("pasted", "pilot-attachment:abc123def456.png", "abc123def456"),
            new IdeaImage("a diagram", "https://example.com/d.png", null),
            new IdeaImage("on github", "https://github.com/SharpAstro/pilot/blob/pilot-attachments/ideas/0011aabb22cc.jpg?raw=true", "0011aabb22cc"),
        ]);
    }

    [Fact]
    public void ABodyWithoutImagesHasNoneAndAMarkerIsWrittenAsTheClientsWriteIt()
    {
        IdeaImages.In("").ShouldBeEmpty();
        IdeaImages.In("a [link](https://x.y) and text").ShouldBeEmpty();
        IdeaImages.Marker("my [shot]", "abc", "png").ShouldBe("![my shot](pilot-attachment:abc.png)");
    }

    [Fact]
    public void AnIdeaRowListsTheImagesOfItsBody()
    {
        var at = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        var issue = new MilestoneIssue(7, "Look", "see ![shot](pilot-attachment:abc123def456.png)", true, IdeaIds.Milestone, at, at, "sebgod", "https://github.com/SharpAstro/pilot/issues/7");
        var plain = new MilestoneIssue(8, "Plain", "nothing", true, IdeaIds.Milestone, at, at, "sebgod", "https://github.com/SharpAstro/pilot/issues/8");

        var rows = IdeaRows.Of("pilot", [issue, plain], [], at);

        rows.Single(r => r.Id == "I7").Images.ShouldBe([new IdeaImage("shot", "pilot-attachment:abc123def456.png", "abc123def456")]);
        rows.Single(r => r.Id == "I8").Images.ShouldBeEmpty();
        new IdeaRow { Id = "I1", Repo = "pilot", Title = "t", State = IdeaState.Noted }.Images.ShouldBeEmpty();
    }
}

/// <summary>
/// What gh's answers say while an image is put on the pilot-attachments branch: the sha of each git data object made for the
/// branch's first image, and whether the branch, or the image under its id, is there already.
/// </summary>
public sealed class IdeaImageGitHubAnswerTests
{
    private const string Blob = """{"url":"https://api.github.com/repos/SharpAstro/pilot/git/blobs/3a0f86fb8db8eea7ccbb9a95f325ddbedfb25e15","sha":"3a0f86fb8db8eea7ccbb9a95f325ddbedfb25e15"}""";

    private const string Tree = """
        {"sha":"cd8274d15fa3ae2ab983129fb037999f264ba9a7","url":"https://api.github.com/repos/SharpAstro/pilot/git/trees/cd8274d15fa3ae2ab983129fb037999f264ba9a7",
         "tree":[{"path":"ideas/abc123def456.png","mode":"100644","type":"blob","size":12,"sha":"3a0f86fb8db8eea7ccbb9a95f325ddbedfb25e15","url":"https://api.github.com/repos/SharpAstro/pilot/git/blobs/3a0f86fb8db8eea7ccbb9a95f325ddbedfb25e15"}],
         "truncated":false}
        """;

    private const string Commit = """
        {"sha":"7638417db6d59f3c431d3e1f261cc637155684cd","node_id":"C_kwDOabc","url":"https://api.github.com/repos/SharpAstro/pilot/git/commits/7638417db6d59f3c431d3e1f261cc637155684cd",
         "html_url":"https://github.com/SharpAstro/pilot/commit/7638417db6d59f3c431d3e1f261cc637155684cd",
         "author":{"name":"sebgod","email":"s@example.com","date":"2026-10-30T09:00:00Z"},"committer":{"name":"sebgod","email":"s@example.com","date":"2026-10-30T09:00:00Z"},
         "tree":{"sha":"cd8274d15fa3ae2ab983129fb037999f264ba9a7","url":"https://api.github.com/repos/SharpAstro/pilot/git/trees/cd8274d15fa3ae2ab983129fb037999f264ba9a7"},
         "message":"Idea image abc123def456","parents":[],"verification":{"verified":false,"reason":"unsigned","signature":null,"payload":null}}
        """;

    private const string Ref = """
        {"ref":"refs/heads/pilot-attachments","node_id":"REF_kwDOabc","url":"https://api.github.com/repos/SharpAstro/pilot/git/refs/heads/pilot-attachments",
         "object":{"sha":"7638417db6d59f3c431d3e1f261cc637155684cd","type":"commit","url":"https://api.github.com/repos/SharpAstro/pilot/git/commits/7638417db6d59f3c431d3e1f261cc637155684cd"}}
        """;

    [Fact]
    public void ABlobTreeOrCommitSaysItsOwnShaAndARefTheCommitItPointsAt()
    {
        GhCli.ParseGitSha("git/blobs", Blob).ShouldBe("3a0f86fb8db8eea7ccbb9a95f325ddbedfb25e15");
        GhCli.ParseGitSha("git/trees", Tree).ShouldBe("cd8274d15fa3ae2ab983129fb037999f264ba9a7");
        GhCli.ParseGitSha("git/commits", Commit).ShouldBe("7638417db6d59f3c431d3e1f261cc637155684cd");
        GhCli.ParseGitSha("git/refs", Ref).ShouldBe("7638417db6d59f3c431d3e1f261cc637155684cd");
    }

    [Fact]
    public void AnAnswerWithoutTheShaWhereItBelongsSaysNone()
    {
        // A ref answer has no sha of its own at the top, which is what made the branch's first image fail.
        GhCli.ParseGitSha("git/commits", Ref).ShouldBeNull();
        GhCli.ParseGitSha("git/refs", Commit).ShouldBeNull();
        GhCli.ParseGitSha("git/blobs", """{"message":"Not Found","documentation_url":"https://docs.github.com/rest/git/blobs#create-a-blob","status":"404"}""").ShouldBeNull();
        GhCli.ParseGitSha("git/refs", "[]").ShouldBeNull();
        GhCli.ParseGitSha("git/blobs", "").ShouldBeNull();
    }

    [Fact]
    public void AGetThatAnsweredFoundItA404FoundNoneAndAnyOtherRefusalSaysNothing()
    {
        GhCli.ParseFound(0, "").ShouldBe(true);
        GhCli.ParseFound(1, "gh: Not Found (HTTP 404)\n").ShouldBe(false);
        GhCli.ParseFound(1, "gh: Bad credentials (HTTP 401)\n").ShouldBeNull();
        GhCli.ParseFound(1, "error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com\n").ShouldBeNull();
    }
}

/// <summary>An image pasted into an idea: uploaded to the node, served by id, and filed on GitHub with the idea.</summary>
public sealed class IdeaImageTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2im-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly Attachments _attachments;
    private readonly IdeaCommander _commander;

    public IdeaImageTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        _attachments = new Attachments(Path.Combine(_root, "data"));
        var planes = new Planes(new GitCli());
        var flights = new FlightCommander(_state, planes, TimeProvider.System, github: _github);
        var catalog = new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root]));
        _commander = new IdeaCommander(_state, planes, catalog, _github, new FakeIdeaMerger(), new FakeIdeaTidier(), new FakeIdeaRepoChooser(), flights, TimeProvider.System)
        {
            Attachments = _attachments,
        };
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

    private Task<CommandResult> Note(string text) =>
        _commander.RunAsync(new CommandRequest { Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(IdeaIds.DefaultRepo), Args = [text], Who = "tests" }, Ct);

    private Task<CommandResult> Edit(MilestoneIssue issue, string text) =>
        _commander.RunAsync(new CommandRequest
        {
            Verb = Verbs.EditIdea,
            Target = IdeaIds.Target(IdeaIds.DefaultRepo, issue.Number),
            Args = [issue.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), text],
            Who = "tests",
        }, Ct);

    private static string Url(string id, string ext = "png") => $"https://github.com/SharpAstro/pilot/blob/pilot-attachments/ideas/{id}.{ext}?raw=true";

    [Fact]
    public async Task NoteIdeaFilesAPastedImageOnceAndPutsItsUrlWhereTheMarkerWas()
    {
        var id = _attachments.AddIdeaImage(Png, "png");

        var result = await Note($"A look\n\nbefore ![shot]({IdeaImages.Scheme}{id}.png) between ![again]({IdeaImages.Scheme}{id}.png) after");

        result.Ok.ShouldBeTrue(result.Error);
        var upload = _github.ImageUploads.ShouldHaveSingleItem();
        (upload.Id, upload.Ext).ShouldBe((id, "png"));
        upload.Bytes.ShouldBe(Png);
        var created = _github.IssuesCreated.ShouldHaveSingleItem();
        created.Body.ShouldBe($"before ![shot]({Url(id)}) between ![again]({Url(id)}) after");
        _state.Ideas().Single().Images.Select(i => i.Copy).ShouldBe([id, id]);
    }

    [Fact]
    public async Task NoteIdeaTakesAMarkerThatIsTheWholeFirstLineAsBodyAndTheNextTextAsTitle()
    {
        var id = _attachments.AddIdeaImage(Png, "png");

        var result = await Note($"![screenshot]({IdeaImages.Scheme}{id}.png)\nThe real title\nmore words");

        result.Ok.ShouldBeTrue(result.Error);
        _github.ImageUploads.ShouldHaveSingleItem().Id.ShouldBe(id);
        var created = _github.IssuesCreated.ShouldHaveSingleItem();
        created.Title.ShouldBe("The real title");
        created.Body.ShouldBe($"![screenshot]({Url(id)})\nmore words");
    }

    [Fact]
    public async Task NoteIdeaTakesAMarkerInsideTheFirstLineOutOfTheTitle()
    {
        var id = _attachments.AddIdeaImage(Png, "png");

        var result = await Note($"Fix the ![screenshot]({IdeaImages.Scheme}{id}.png) glitch\nbody");

        result.Ok.ShouldBeTrue(result.Error);
        _github.ImageUploads.ShouldHaveSingleItem().Id.ShouldBe(id);
        var created = _github.IssuesCreated.ShouldHaveSingleItem();
        created.Title.ShouldBe("Fix the glitch");
        created.Body.ShouldBe($"![screenshot]({Url(id)})\nbody");
    }

    [Fact]
    public async Task NoteIdeaRefusesAMarkerWhoseCopyTheNodeDoesNotHoldAndFilesNothing()
    {
        var result = await Note("A look\n\n![gone](pilot-attachment:ffffffffffff.png)");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("ffffffffffff");
        _github.ImageUploads.ShouldBeEmpty();
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    public async Task EditIdeaKeepsAnImageAlreadyThereAndUploadsOnlyTheNewOne()
    {
        var old = Url("0011aabb22cc");
        var issue = _github.AddIssue(Repo, "A look", IdeaIds.Milestone, body: $"old ![first]({old})");
        var fresh = _attachments.AddIdeaImage(Png, "png");

        var result = await Edit(issue, $"A look\n\nold ![first]({old}) and ![second]({IdeaImages.Scheme}{fresh}.png)");

        result.Ok.ShouldBeTrue(result.Error);
        _github.ImageUploads.ShouldHaveSingleItem().Id.ShouldBe(fresh);
        _github.Edits.ShouldHaveSingleItem().Body.ShouldBe($"old ![first]({old}) and ![second]({Url(fresh)})");
    }

    [Fact]
    public async Task EditIdeaWithOnlyUrlImagesUploadsNothing()
    {
        var old = Url("0011aabb22cc");
        var issue = _github.AddIssue(Repo, "A look", IdeaIds.Milestone, body: $"![first]({old})");

        (await Edit(issue, $"A look\n\n![first]({old}) reworded")).Ok.ShouldBeTrue();

        _github.ImageUploads.ShouldBeEmpty();
        _github.Edits.ShouldHaveSingleItem().Body.ShouldBe($"![first]({old}) reworded");
    }

    [Fact]
    public async Task AnUploadIsKeptAndServedBackByIdAndAnOversizeOrWrongExtensionIsRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var uploaded = await node.Client.UploadAttachmentAsync(Png, "png", Ct);
        uploaded.Success.ShouldBeTrue(uploaded.Error);
        var id = uploaded.Response.ShouldNotBeNull();
        File.Exists(Path.Combine(node.Options.DataFolder, "attachments", Attachments.IdeaOwner, id + ".png")).ShouldBeTrue();

        var served = await node.Client.GetAttachmentAsync(id, Ct);
        (served.Status, served.ContentType).ShouldBe((HttpStatusCode.OK, "image/png"));
        served.Bytes.ShouldBe(Png);
        (await node.Client.GetAttachmentAsync("nosuchid", Ct)).Status.ShouldBe(HttpStatusCode.NotFound);

        var big = await node.Client.UploadAttachmentAsync(new byte[IdeaImages.MaxBytes + 1], "png", Ct);
        (big.Success, big.StatusCode).ShouldBe((false, 413));
        var wrong = await node.Client.UploadAttachmentAsync(Png, "exe", Ct);
        (wrong.Success, wrong.StatusCode).ShouldBe((false, 400));
        (await node.Client.UploadAttachmentAsync([], "png", Ct)).Success.ShouldBeFalse();
    }

    [Fact]
    public async Task AnImageOnlyOnTheBranchIsFetchedOnceAndKept()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var tianwen = Path.Combine(_root, "tianwen");
        Directory.CreateDirectory(Path.Combine(tianwen, ".git"));
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = tianwen, Issue = 1, Branch = "flight/tia1", Worktree = _root, Base = "origin/main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        var at = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        node.GitHub.ImageUploads.Add(("/r/tianwen", "0011aabb22cc", "png", Png));
        node.State.PutIdeas("tianwen", IdeaRows.Of("tianwen", [new MilestoneIssue(5, "Look", $"![s]({Url("0011aabb22cc")})", true, IdeaIds.Milestone, at, at, "sebgod", "https://github.com/SharpAstro/tianwen/issues/5")], [], at));

        var first = await node.Client.GetAttachmentAsync("0011aabb22cc", Ct);
        var second = await node.Client.GetAttachmentAsync("0011aabb22cc", Ct);

        first.Status.ShouldBe(HttpStatusCode.OK);
        first.Bytes.ShouldBe(Png);
        second.Bytes.ShouldBe(Png);
        node.GitHub.ImageReads.ShouldHaveSingleItem().ShouldBe(("0011aabb22cc", "png"));
        (await node.Client.GetAttachmentAsync("ffffffffffff", Ct)).Status.ShouldBe(HttpStatusCode.NotFound);
    }
}
