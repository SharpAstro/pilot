using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A merged flight's placed design files are linked at the merge commit (PIL1208): the issue's pictures, the PR's Designs section and the close comment name the full commit and
/// the folder, every link is asked for, and only then does the branch design/&lt;n&gt; go. The fast tests run on FakeGitHub; the Slow one reads a real git repo.
/// </summary>
public sealed class DesignLandLinksTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2dll-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TimerTime _time = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly string _repo;
    private readonly DesignStore _store;
    private readonly NodeState _state;
    private readonly RepoCatalog _catalog;
    private readonly DesignSync _sync;
    private readonly DesignRecord _record;

    public DesignLandLinksTests()
    {
        _repo = Path.Combine(_root, "repos", "pilot");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        File.WriteAllText(Path.Combine(_repo, "PILOT.md"), "rules");
        var data = Path.Combine(_root, "data");
        var options = new NodeOptions(Path.Combine(_root, "n.sock"), data, RepoRootsGiven: [Path.Combine(_root, "repos")]);
        _store = new DesignStore(data, _time);
        _state = new NodeState(new InMemoryJournal(), _time);
        _catalog = new RepoCatalog(_state, options);
        _record = new DesignRecord(_store, _catalog, _state, _github);
        _sync = new DesignSync(_store, _catalog, _github, _time, "DE1") { Record = _record };
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private void Published(string id, int number)
    {
        var folder = Path.Combine(_store.FolderOf(id), "versions", "v" + number);
        Directory.CreateDirectory(Path.Combine(folder, "screens"));
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        File.WriteAllText(Path.Combine(folder, "DESIGN.md"), $"# Disk alert\n\n**Objective:** Version {number}.\n\n## Threshold\n\n**Objective:** Shows it.\n\n```screen id=home\nThe dialog\n```\n");
        File.WriteAllText(Path.Combine(folder, "prototype.json"), "{\"width\":1280,\"height\":720,\"entry\":\"home\",\"screens\":[]}");
        File.WriteAllText(Path.Combine(folder, "screens", "home.json"), "{\"id\":\"home\"}");
        File.WriteAllBytes(Path.Combine(folder, "renders", "home.png"), [0x89, 0x50, (byte)number]);
        _store.Update(id, r => r with
        {
            Current = number,
            Versions = number,
            Why = "v" + number,
            History = [.. r.History, new DesignVersionRow { Number = number, Why = "v" + number, At = new DateTimeOffset(2026, 11, 20, 9, number, 0, TimeSpan.Zero) }],
        });
    }

    /// <summary>A design published up to <paramref name="version"/>, its issue made and its branch written; the id and the issue's number.</summary>
    private async Task<(string Id, int Issue)> DesignAsync(int version = 1)
    {
        var design = _store.New("pilot", "Disk alert", "brief");
        for (var v = 1; v <= version; v++)
        {
            Published(design.Id, v);
        }
        await _sync.SyncAsync(design.Id, Ct);
        _github.IssueComments.Clear();
        return (design.Id, _store.Find(design.Id)!.IssueNumber!.Value);
    }

    private FlightRow Flight(string id, int version, string code = "PIL9", int pr = 77) => new()
    {
        Code = code, Repo = "pilot", RepoPath = "/r", Issue = 5, Title = "Build it", Branch = $"flight/{code.ToLowerInvariant()}", Worktree = "/nowhere", Base = "main",
        State = FlightState.Landed, CreatedAt = DateTimeOffset.UnixEpoch, PullRequest = pr, PullRequestUrl = $"https://github.com/SharpAstro/pilot/pull/{pr}",
        Designs = [new FlightDesignRow { Id = id, Version = version, Sha256 = "ab" + version + "cd" }],
    };

    /// <summary>The state after the node placed the files in the plane (leg place-design-files): the row names the folder.</summary>
    private void Placed(string id, int issue, FlightRow flight)
    {
        _store.Update(id, r => r with { Record = [.. r.Record, new DesignRecordRow { Flight = flight.Code, Version = flight.Designs[0].Version, Folder = DesignSyncText.RepoFolder(issue) }] });
        _state.PutFlight(flight);
        _github.PrBodies[flight.PullRequest!.Value] = LegCommander.PrBody(flight, [], _record.Section(flight));
    }

    private async Task<string> IssueBodyAsync(int issue) => (await _github.IssueNowAsync(_repo, issue, Ct))!.Body;

    [Fact]
    public async Task AfterTheLandTheIssuePicturesNameTheFullMergeCommitAndTheFolderAndNotTheBranch()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);
        (await IssueBodyAsync(issue)).ShouldContain($"/blob/design/{issue}/renders/home.png?raw=true");

        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);

        var body = await IssueBodyAsync(issue);
        body.ShouldContain($"https://github.com/SharpAstro/pilot/blob/{Sha}/docs/designs/{issue}/renders/home.png?raw=true");
        body.ShouldNotContain($"design/{issue}");
        var meta = DesignIssueDocument.Meta(body)!;
        meta.Commit.ShouldBe(Sha);
        meta.Folder.ShouldBe($"docs/designs/{issue}");
        meta.Node.ShouldBe("DE1");
        var row = _store.Find(id)!.Record.Single();
        row.MergeCommit.ShouldBe(Sha);
        row.MergedAt.ShouldBe("0123456");
        row.LinksWritten.ShouldBe(true);
    }

    [Fact]
    public async Task ReadingTheIssueBackAfterTheLandFindsNoChangeMadeOnGitHub()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);
        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);
        var before = await IssueBodyAsync(issue);
        var edits = _github.Edits.Count;
        var locks = new DesignLock(_store, _catalog, _github, _time, "DE1", _ => null);

        await locks.ReadBackAsync(id, Ct);

        _store.Find(id)!.GitHubIgnored.ShouldBeEmpty();
        _store.Find(id)!.GitHubEdits.ShouldBeEmpty();
        (await IssueBodyAsync(issue)).ShouldBe(before);
        _github.Edits.Count.ShouldBe(edits);
    }

    [Fact]
    public void AskingForAFolderAtACommitSendsSilentAndNoJqSoADirectoryAnswersZero()
    {
        var args = GhCli.FileAtArgs(Sha, "docs/designs/12");

        args.ShouldContain("--silent");
        args.ShouldNotContain("--jq");
        args[1].ShouldBe($"repos/{{owner}}/{{repo}}/contents/docs/designs/12?ref={Sha}");
    }

    [Fact]
    public void AFileAtAnswerReadsExitZeroAsFoundA404AsMissingAndAnyOtherRefusalThrows()
    {
        GhCli.FileAtAnswer(0, "", Sha, "docs/designs/12").ShouldBeTrue();
        GhCli.FileAtAnswer(1, "gh: Not Found (HTTP 404)", Sha, "docs/designs/12").ShouldBeFalse();
        Should.Throw<FlightException>(() => GhCli.FileAtAnswer(1, "gh: Forbidden (HTTP 403)", Sha, "docs/designs/12"));
    }

    [Fact]
    public async Task ThePrDesignsSectionAndTheCloseCommentLinkTheFolderAtTheCommit()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);
        var link = $"https://github.com/SharpAstro/pilot/tree/{Sha}/docs/designs/{issue}";

        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);

        _github.PrBodies[77].ShouldContain($"files in [docs/designs/{issue}]({link})");
        _github.PrBodies[77].ShouldNotContain($"files in docs/designs/{issue},");
        _github.ClosedIssues.Single().Comment.ShouldBe($"Built as v1 by PIL9 in #77, merged at 0123456; files: {link}");
    }

    [Fact]
    public async Task TheBranchIsDeletedOnlyAfterEveryLinkAnsweredAndTheRowSaysSo()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);

        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);

        var calls = _github.LandCalls.ToArray();
        calls.Last().ShouldBe($"delete design/{issue}");
        calls.Count(c => c.StartsWith("delete", StringComparison.Ordinal)).ShouldBe(1);
        calls.Take(calls.Length - 1).ShouldAllBe(c => c.StartsWith("fileat " + Sha, StringComparison.Ordinal));
        calls.ShouldContain($"fileat {Sha} docs/designs/{issue}");
        calls.ShouldContain($"fileat {Sha} docs/designs/{issue}/DESIGN.md");
        calls.ShouldContain($"fileat {Sha} docs/designs/{issue}/renders/home.png");
        _github.Branches.ContainsKey((Path.GetFullPath(_repo), $"design/{issue}")).ShouldBeFalse();
        _store.Find(id)!.Record.Single().BranchGone.ShouldBe(true);
    }

    [Fact]
    public async Task ADesignWhoseCurrentIsPastTheBuiltVersionKeepsItsBranchAndItsBlockLinksStayOnTheBranch()
    {
        var (id, issue) = await DesignAsync(3);
        Published(id, 4);
        var flight = Flight(id, 3);
        Placed(id, issue, flight);
        var before = await IssueBodyAsync(issue);

        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);

        _github.LandCalls.ShouldBeEmpty();
        _github.BranchesDeleted.ShouldBeEmpty();
        (await IssueBodyAsync(issue)).ShouldBe(before);
        (await IssueBodyAsync(issue)).ShouldContain($"/blob/design/{issue}/");
        _github.PrBodyEdits.ShouldBeEmpty();
        _store.Find(id)!.Record.Single().BranchGone.ShouldBeNull();
    }

    [Fact]
    public async Task ALinkThatDoesNotAnswerKeepsTheBranchAndTheSweepRetriesWithoutDoublingAnythingMade()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);
        _github.FileAt = (_, _, path) => !path.EndsWith("home.png", StringComparison.Ordinal);

        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);

        _github.BranchesDeleted.ShouldBeEmpty();
        _store.Find(id)!.Record.Single().BranchGone.ShouldBeNull();
        _github.Branches.ContainsKey((Path.GetFullPath(_repo), $"design/{issue}")).ShouldBeTrue();
        var edits = _github.Edits.Count;
        var prEdits = _github.PrBodyEdits.Count;

        _github.FileAt = null;
        await _record.SweepAsync(Ct);
        await _record.SweepAsync(Ct);

        _github.BranchesDeleted.Count.ShouldBe(1);
        _store.Find(id)!.Record.Single().BranchGone.ShouldBe(true);
        _github.ClosedIssues.Count.ShouldBe(1);
        _github.IssueComments.ShouldBeEmpty();
        _github.Edits.Count.ShouldBe(edits);
        _github.PrBodyEdits.Count.ShouldBe(prEdits);
    }

    [Fact]
    public async Task AFailedCloseIsRetriedAndTheLinksAreWrittenAfterItWithTheCommentOnce()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);

        _github.CloseIssueFails = "502";
        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);
        _github.ClosedIssues.ShouldBeEmpty();
        _github.LandCalls.ShouldBeEmpty();

        _github.CloseIssueFails = null;
        await _record.SweepAsync(Ct);
        await _record.SweepAsync(Ct);

        _github.ClosedIssues.Count.ShouldBe(1);
        _github.BranchesDeleted.Count.ShouldBe(1);
        (await IssueBodyAsync(issue)).ShouldContain($"/blob/{Sha}/docs/designs/{issue}/");
    }

    [Fact]
    public async Task ARowWithNoFolderKeepsTodaysBehaviourExactly()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        _state.PutFlight(flight);
        var before = await IssueBodyAsync(issue);

        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);

        _github.ClosedIssues.ToArray().ShouldBe([(issue, "Built as v1 by PIL9 in #77, merged at 0123456")]);
        (await IssueBodyAsync(issue)).ShouldBe(before);
        _github.LandCalls.ShouldBeEmpty();
        _github.PrBodyEdits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALaterPublishCommitsToTheBranchAgainAndItsBlockLinksTheBranch()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);
        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);
        _github.Branches.ContainsKey((Path.GetFullPath(_repo), $"design/{issue}")).ShouldBeFalse();

        Published(id, 2);
        await _sync.SyncAsync(id, Ct);

        _github.BranchCommits.Last().Branch.ShouldBe($"design/{issue}");
        _github.Branches.ContainsKey((Path.GetFullPath(_repo), $"design/{issue}")).ShouldBeTrue();
        var body = await IssueBodyAsync(issue);
        body.ShouldContain($"/blob/design/{issue}/renders/home.png?raw=true");
        body.ShouldNotContain(Sha);
        DesignIssueDocument.Meta(body)!.Commit.ShouldBeNull();
    }

    [Fact]
    public async Task ANodeThatHasNotMirroredTheDesignReadsItsFilesFromTheCommitOnceTheBranchIsGone()
    {
        var (id, issue) = await DesignAsync();
        var flight = Flight(id, 1);
        Placed(id, issue, flight);
        await _record.LandedAsync(flight, 77, "0123456", Ct, Sha);
        var built = _store.ReadVersion(id, 1);
        var folder = $"docs/designs/{issue}";
        _github.FolderAt = (_, commit, at) => commit == Sha && at == folder ? built : null;

        var data = Path.Combine(_root, "data-se2");
        var other = new DesignStore(data, _time);
        var options = new NodeOptions(Path.Combine(_root, "se2.sock"), data, RepoRootsGiven: [Path.Combine(_root, "repos")]);
        var second = new DesignSync(other, new RepoCatalog(new NodeState(new InMemoryJournal(), _time), options), _github, _time, "SE2");
        await second.MirrorAsync(Ct);

        var read = other.List().Single();
        read.MadeOn.ShouldBe("DE1");
        read.SyncedVersion.ShouldBe(1);
        File.ReadAllBytes(Path.Combine(other.FolderOf(read.Id), "versions", "v1", "renders", "home.png")).ShouldBe(built["renders/home.png"]);
    }

    private async Task<string> GitAsync(params string[] args)
    {
        var result = await new GitCli().RunAsync(args, _repo, Ct);
        result.Ok.ShouldBeTrue(string.Join(' ', args) + ": " + result.Why);
        return result.Output.Trim();
    }

    private async Task<string[]> FilesAtAsync(string commit, string folder) =>
        [.. (await GitAsync("ls-tree", "-r", "--name-only", commit, "--", folder)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)];

    [Fact]
    [Trait("Category", "Slow")] // it is about real git: placing in a plane, squash-merging into main, and reading the merge commit's tree
    public async Task EachSquashMergeHoldsExactlyItsBuiltVersionsFilesAndTheFirstBuildsLinksStillAnswerAfterTheSecond()
    {
        // The repo is the plane and main at once: the flight branch places the files, a squash merge puts them on main.
        foreach (var args in new[] { new[] { "init", "-q", "-b", "main" }, ["config", "user.email", "t@example.com"], ["config", "user.name", "T"], ["config", "commit.gpgsign", "false"],
            ["remote", "set-url", "origin", "https://github.com/SharpAstro/pilot.git"],["add", "-A"], ["commit", "-q", "-m", "start"] })
        {
            await GitAsync(args);
        }
        _record.Git = new GitCli();
        _github.FileAt = (_, commit, path) => new GitCli().RunAsync(["cat-file", "-e", $"{commit}:{path}"], _repo, CancellationToken.None).GetAwaiter().GetResult().Ok;

        var (id, issue) = await DesignAsync();
        var folder = DesignSyncText.RepoFolder(issue);

        async Task<(FlightRow Flight, string Commit)> BuildAndMergeAsync(string code, int version, int pr)
        {
            var flight = Flight(id, version, code, pr) with { Worktree = _repo, RepoPath = _repo, State = FlightState.Flying };
            await GitAsync("checkout", "-q", "-b", $"flight/{code.ToLowerInvariant()}");
            _state.PutFlight(flight);
            await _record.PlaceFilesAsync(flight, Ct);
            await GitAsync("checkout", "-q", "main");
            await GitAsync("merge", "--squash", $"flight/{code.ToLowerInvariant()}");
            await GitAsync("commit", "-q", "-m", $"{code} (#{pr})");
            return (flight with { State = FlightState.Landed }, await GitAsync("rev-parse", "HEAD"));
        }

        var (first, firstCommit) = await BuildAndMergeAsync("PIL9", 1, 77);
        (await FilesAtAsync(firstCommit, folder)).ShouldBe([$"{folder}/DESIGN.md", $"{folder}/prototype.json", $"{folder}/renders/home.png", $"{folder}/screens/home.json"]);
        _state.PutFlight(first);
        await _record.LandedAsync(first, 77, firstCommit[..7], Ct, firstCommit);
        _store.Find(id)!.Record.Single(r => r.Flight == "PIL9").BranchGone.ShouldBe(true);
        var firstComment = _github.ClosedIssues.Single().Comment;
        firstComment.ShouldContain($"/tree/{firstCommit}/{folder}");

        // A second build of a newer version replaces the files; the first build's commit still holds the first's.
        Published(id, 2);
        await _sync.SyncAsync(id, Ct);
        File.WriteAllBytes(Path.Combine(_store.FolderOf(id), "versions", "v2", "renders", "extra.png"), [1, 2, 3, 4]);
        var (second, secondCommit) = await BuildAndMergeAsync("PIL10", 2, 78);
        (await FilesAtAsync(secondCommit, folder)).ShouldBe([$"{folder}/DESIGN.md", $"{folder}/prototype.json", $"{folder}/renders/extra.png", $"{folder}/renders/home.png", $"{folder}/screens/home.json"]);
        (await GitAsync("show", $"{secondCommit}:{folder}/DESIGN.md")).ShouldContain("Version 2.");
        _state.PutFlight(second);
        await _record.LandedAsync(second, 78, secondCommit[..7], Ct, secondCommit);

        (await GitAsync("show", $"{firstCommit}:{folder}/DESIGN.md")).ShouldContain("Version 1.");
        (await _github.FileAtAsync(_repo, firstCommit, $"{folder}/renders/home.png", Ct)).ShouldBeTrue();
        (await _github.FileAtAsync(_repo, firstCommit, $"{folder}/renders/extra.png", Ct)).ShouldBeFalse();
        _github.ClosedIssues.First().Comment.ShouldBe(firstComment);
        _github.ClosedIssues.Last().Comment.ShouldContain($"/tree/{secondCommit}/{folder}");
        _store.Find(id)!.Record.Single(r => r.Flight == "PIL10").BranchGone.ShouldBe(true);
    }

    [Fact]
    public void TheLinkBaseIsOneFunctionForBranchAndCommit()
    {
        DesignIssueDocument.RawLink("a/b", 7, "renders/x.png").ShouldBe("https://github.com/a/b/blob/design/7/renders/x.png?raw=true");
        DesignIssueDocument.RawLink("a/b", DesignLinkBase.OfCommit(Sha, "docs/designs/7"), "renders/x.png")
            .ShouldBe($"https://github.com/a/b/blob/{Sha}/docs/designs/7/renders/x.png?raw=true");
    }
}
