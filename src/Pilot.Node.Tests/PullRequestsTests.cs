using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The Pull requests page's data (#252): GitHub's search answer read per state, the one merge-state reader, the grouping and
/// ordering and the Needs me filter every client shares, and the node's watch that keeps the row current.
/// </summary>
public sealed class PullRequestsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2p-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    // A recorded answer of GitHub's search: Fixtures/PullRequests/<name>.json.
    private static string Recorded(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PullRequests", name + ".json"));

    private static PullRequestRow Read(string name) => PullRequestSearch.Parse(Recorded(name)).PullRequests.Single();

    // ---- the answer, one per state

    [Theory]
    [InlineData("draft", PullRequestKind.Draft, "Draft", false)]
    [InlineData("checks-running", PullRequestKind.ChecksRunning, "Checks running 4/7", false)]
    [InlineData("checks-failed", PullRequestKind.ChecksFailed, "Checks failed: test (ubuntu)", false)]
    [InlineData("ready", PullRequestKind.Ready, "Ready", true)]
    [InlineData("in-merge-queue", PullRequestKind.InMergeQueue, "In merge queue #2", false)]
    [InlineData("conflicts", PullRequestKind.Conflicts, "Conflicts", true)]
    [InlineData("behind", PullRequestKind.Behind, "Behind", false)]
    [InlineData("changes-requested", PullRequestKind.ChangesRequested, "Changes requested", true)]
    [InlineData("approved", PullRequestKind.Approved, "Approved", false)]
    public void PullRequestsAreReadFromTheRecordedAnswerOfEachStateWithOneLabelAndTheNeedsMeFlag(string answer, PullRequestKind kind, string label, bool needsMe)
    {
        var pr = Read(answer);

        (pr.Kind, pr.Label, pr.NeedsMe).ShouldBe((kind, label, needsMe));
    }

    [Fact]
    public void ARecordedPullRequestCarriesItsFieldsItsCheckCountAndPilotsReviewWithoutThePilotReviewCountingAsACheck()
    {
        var pr = Read("ready");

        (pr.Repo, pr.Number, pr.Title, pr.Author, pr.Head, pr.Base, pr.Draft).ShouldBe(("SharpAstro/demo", 104, "Ready to merge", "sebgod", "feature-104", "main", false));
        pr.Url.ShouldBe("https://github.com/SharpAstro/demo/pull/104");
        pr.CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 27, 19, 56, 26, TimeSpan.Zero));
        pr.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 10, 28, 6, 0, 0, TimeSpan.Zero));
        // build, test and a skipped publish: the pilot/review status is the review's word, not a check.
        (pr.ChecksDone, pr.ChecksTotal, pr.FailedCheck).ShouldBe((3, 3, null));
        (pr.Review, pr.ReviewUrl).ShouldBe((PullRequestReview.Passed, "https://github.com/SharpAstro/demo/pull/104#issuecomment-9"));
    }

    [Theory]
    [InlineData("draft", PullRequestReview.Reviewing)]
    [InlineData("changes-requested", PullRequestReview.Changes)]
    [InlineData("conflicts", null)]
    public void PilotsReviewIsReadFromItsStatusOnTheLastCommit(string answer, string? review) => Read(answer).Review.ShouldBe(review);

    [Fact]
    public void AFailedCheckIsNamedAndLinkedToItsRunWhileOthersStillRun()
    {
        var pr = Read("checks-failed");

        pr.FailedCheck.ShouldBe("test (ubuntu)");
        pr.FailedUrl.ShouldStartWith("https://github.com/SharpAstro/demo/actions/runs/");
        // The cancelled run after it is a failure too, but the first named is the one shown.
        (pr.ChecksDone, pr.ChecksTotal).ShouldBe((3, 4));
    }

    [Fact]
    public void ASearchPageSaysWhereTheNextOneStartsAndTheLastSaysNone()
    {
        var first = PullRequestSearch.Parse(Recorded("page-1"));
        var second = PullRequestSearch.Parse(Recorded("page-2"));

        (first.PullRequests.Single().Number, first.Next).ShouldBe((110, "Y3Vyc29yOjE="));
        second.PullRequests.Single().Number.ShouldBe(111);
        second.Next.ShouldBeNull();
    }

    [Fact]
    public void ANodeThatIsNoPullRequestIsSkippedAndAnAnswerThatIsNoSearchIsRefusedInGitHubsWords()
    {
        PullRequestSearch.Parse(Recorded("empty-node")).PullRequests.Select(p => p.Number).ShouldBe([112]);
        Should.Throw<FlightException>(() => PullRequestSearch.Parse(Recorded("errors"))).Message.ShouldContain("API rate limit exceeded");
        Should.Throw<FlightException>(() => PullRequestSearch.Parse("<html>")).Message.ShouldContain("no JSON");
    }

    [Fact]
    public void TheLiveAnswerForAUserWithFivePullRequestsReadsAsGitHubShowsThem()
    {
        var read = PullRequestSearch.Parse(Recorded("recorded-sebgod")).PullRequests;

        read.Length.ShouldBe(5);
        read.Select(p => (p.Repo, p.Label)).ShouldBe(
        [
            ("sebgod/azure-docs", "Ready"),
            ("sebgod/servian-bench-aks-tf-demo", "Checking merge state"),
            ("sebgod/mercury", "Ready"),
            ("sebgod/node-opcua", "Checking merge state"),
            ("sebgod/nlpviz", "Conflicts"),
        ]);
        // No CI on these: nothing running, nothing failed.
        read.ShouldAllBe(p => p.ChecksTotal == 0 && p.FailedCheck == null);
    }

    [Fact]
    public void TheMergeStateReaderTakesTheFirstThatHoldInTheOrderItDocuments()
    {
        static PullRequestMergeState Of(bool draft = false, string? mergeable = "MERGEABLE", string? status = "CLEAN", string? decision = null,
            bool queued = false, int? position = null, int done = 2, int total = 2, string? failed = null) =>
            PullRequestStates.Read(new PullRequestFacts(draft, mergeable, status, decision, queued, position, done, total, failed));

        // A draft is a draft whatever else is true; a PR in the queue is in it.
        Of(draft: true, mergeable: "CONFLICTING", failed: "build").Label.ShouldBe("Draft");
        Of(queued: true, position: 1, mergeable: "CONFLICTING").Label.ShouldBe("In merge queue #1");
        Of(queued: true).Label.ShouldBe("In merge queue");
        // Conflicts, then changes requested, then the checks failed before running.
        Of(mergeable: "CONFLICTING", decision: "CHANGES_REQUESTED").Label.ShouldBe("Conflicts");
        Of(decision: "CHANGES_REQUESTED", failed: "build").Label.ShouldBe("Changes requested");
        Of(failed: "build", done: 1, total: 3).Label.ShouldBe("Checks failed: build");
        Of(done: 1, total: 3, status: "BEHIND").Label.ShouldBe("Checks running 1/3");
        Of(status: "BEHIND").Label.ShouldBe("Behind");
        // What is left is ready; approved when GitHub still holds the merge; a review wanted when the repo requires one.
        Of(decision: "APPROVED").Label.ShouldBe("Ready");
        Of(decision: "APPROVED", status: "BLOCKED").Label.ShouldBe("Approved");
        Of(decision: "REVIEW_REQUIRED", status: "BLOCKED").Label.ShouldBe("Review required");
        Of(mergeable: "UNKNOWN", status: "UNKNOWN").Label.ShouldBe("Checking merge state");
        // Only ready, changes requested and conflicts wait on the person.
        Of().NeedsMe.ShouldBeTrue();
        Of(decision: "CHANGES_REQUESTED").NeedsMe.ShouldBeTrue();
        Of(mergeable: "CONFLICTING").NeedsMe.ShouldBeTrue();
        new[] { Of(draft: true), Of(queued: true), Of(failed: "build"), Of(done: 0, total: 2), Of(status: "BEHIND"), Of(decision: "APPROVED", status: "BLOCKED"),
            Of(decision: "REVIEW_REQUIRED", status: "BLOCKED"), Of(mergeable: "UNKNOWN") }.ShouldAllBe(s => !s.NeedsMe);
    }

    // ---- grouping, ordering, Needs me

    private static PullRequestRow Row(string repo, int number, PullRequestKind kind, int updatedHoursAgo = 1) => new()
    {
        Repo = repo, Number = number, Title = $"PR {number}", Url = $"https://github.com/{repo}/pull/{number}", Kind = kind, Label = kind.ToString(),
        NeedsMe = kind is PullRequestKind.Ready or PullRequestKind.ChangesRequested or PullRequestKind.Conflicts,
        UpdatedAt = Noon - TimeSpan.FromHours(updatedHoursAgo),
    };

    private static PullRequestsRow RowOf(params PullRequestRow[] prs) => new() { Orgs = ["SharpAstro"], PullRequests = prs, ReadAt = Noon };

    [Fact]
    public void PullRequestsAreGroupedByRepoAndTheReposOrderedByThePullRequestThatMostNeedsThePerson()
    {
        var row = RowOf(
            Row("SharpAstro/draftsonly", 1, PullRequestKind.Draft, 1),
            Row("SharpAstro/running", 2, PullRequestKind.ChecksRunning, 1),
            Row("SharpAstro/mixed", 3, PullRequestKind.Conflicts, 5),
            Row("SharpAstro/mixed", 4, PullRequestKind.Draft, 1),
            Row("SharpAstro/mixed", 5, PullRequestKind.ChangesRequested, 9),
            Row("SharpAstro/ready", 6, PullRequestKind.Ready, 30),
            Row("sharpastro/READY", 7, PullRequestKind.Behind, 2));

        var groups = row.Groups();

        // Ready first (0), then changes requested (1, in "mixed"), then running (7), then drafts (9).
        groups.Select(g => g.Repo).ShouldBe(["SharpAstro/ready", "SharpAstro/mixed", "SharpAstro/running", "SharpAstro/draftsonly"]);
        // A repo is one group however GitHub cases it, and its PRs go the same way: ready, then behind.
        groups[0].PullRequests.Select(p => p.Number).ShouldBe([6, 7]);
        groups[1].PullRequests.Select(p => p.Number).ShouldBe([5, 3, 4]);
    }

    [Fact]
    public void ReposThatRankAlikeGoTheMostRecentlyUpdatedFirstThenByName()
    {
        var row = RowOf(
            Row("SharpAstro/b", 1, PullRequestKind.Ready, 5),
            Row("SharpAstro/a", 2, PullRequestKind.Ready, 5),
            Row("SharpAstro/c", 3, PullRequestKind.Ready, 1));

        row.Groups().Select(g => g.Repo).ShouldBe(["SharpAstro/c", "SharpAstro/a", "SharpAstro/b"]);
    }

    [Fact]
    public void TheNeedsMeFilterKeepsReadyChangesRequestedAndConflictsAndDropsTheReposLeftEmpty()
    {
        var row = RowOf(
            Row("SharpAstro/a", 1, PullRequestKind.Ready),
            Row("SharpAstro/a", 2, PullRequestKind.ChecksRunning),
            Row("SharpAstro/b", 3, PullRequestKind.Draft),
            Row("SharpAstro/c", 4, PullRequestKind.Conflicts),
            Row("SharpAstro/c", 5, PullRequestKind.ChangesRequested),
            Row("SharpAstro/d", 6, PullRequestKind.Approved));

        var groups = row.Groups(needsMeOnly: true);

        groups.Select(g => g.Repo).ShouldBe(["SharpAstro/a", "SharpAstro/c"]);
        groups.SelectMany(g => g.PullRequests).Select(p => p.Number).ShouldBe([1, 5, 4]);
        RowOf().Groups().ShouldBeEmpty();
    }

    [Fact]
    public void RowsOfSeveralNodesMergeIntoOneWithTheNewestReadOfEachPullRequestAndTheFlightEitherKnew()
    {
        var mine = new PullRequestsRow
        {
            Orgs = ["SharpAstro"], ReadAt = Noon - TimeSpan.FromMinutes(1),
            PullRequests = [Row("SharpAstro/pilot", 1, PullRequestKind.ChecksRunning) with { Flight = "PIL1" }, Row("SharpAstro/pilot", 2, PullRequestKind.Draft)],
        };
        var theirs = new PullRequestsRow
        {
            Orgs = ["sharpastro", "sebgod"], ReadAt = Noon,
            PullRequests = [Row("SharpAstro/pilot", 1, PullRequestKind.Ready), Row("sebgod/mercury", 3, PullRequestKind.Ready)],
            Errors = [new PullRequestsError { Org = "sebgod", Message = "offline" }],
        };

        var merged = PullRequestsRow.Merge([mine, null, theirs])!;

        merged.PullRequests.Length.ShouldBe(3);
        // The later read says ready; the flight is the one only this node knew.
        merged.PullRequests.Single(p => p.Number == 1).ShouldSatisfyAllConditions(p => p.Kind.ShouldBe(PullRequestKind.Ready), p => p.Flight.ShouldBe("PIL1"));
        // The orgs as the newest read spelled them, once however they are cased.
        merged.Orgs.ShouldBe(["sharpastro", "sebgod"]);
        merged.Errors.Single().Message.ShouldBe("offline");
        // As old as the oldest read, so a node that stopped reading shows as stale.
        merged.ReadAt.ShouldBe(Noon - TimeSpan.FromMinutes(1));
        PullRequestsRow.Merge([null, null]).ShouldBeNull();
        PullRequestsRow.Merge([mine]).ShouldBeSameAs(mine);
    }

    // ---- the row on the wire

    [Fact]
    public void TheRowTravelsTheStreamInTheSnapshotAndAsADeltaAndRoundTripsThroughTheContractsJson()
    {
        var row = RowOf(Row("SharpAstro/a", 1, PullRequestKind.InMergeQueue) with { Review = PullRequestReview.Passed, Flight = "A1" });
        var mirror = new NodeMirror();

        mirror.Apply(new SnapshotEvent { Seq = 4, At = Noon, Stream = "s", Sessions = [], Notes = [], PullRequests = row });
        mirror.PullRequests.ShouldBe(row);

        var later = row with { ReadAt = Noon.AddMinutes(1), PullRequests = [] };
        var delta = JsonSerializer.Deserialize(JsonSerializer.Serialize<NodeEvent>(new PullRequestsChangedEvent { Seq = 5, At = Noon, Row = later }, PilotJsonContext.Default.NodeEvent), PilotJsonContext.Default.NodeEvent)!;
        mirror.Apply(delta);
        mirror.PullRequests!.ReadAt.ShouldBe(later.ReadAt);

        var json = JsonSerializer.Serialize(row, PilotJsonContext.Default.PullRequestsRow);
        json.ShouldContain("\"kind\":\"InMergeQueue\"");
        JsonSerializer.Deserialize(json, PilotJsonContext.Default.PullRequestsRow)!.PullRequests.Single().ShouldBe(row.PullRequests.Single());
    }

    [Fact]
    public void TheNodePutsTheRowOnTheStreamWhenItChangesAndRenewsItsReadTimeWellBeforeItGoesStale()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var row = RowOf(Row("SharpAstro/a", 1, PullRequestKind.Ready));

        state.PutPullRequests(row);
        var seq = state.Seq;
        // Read again a minute on with nothing new: not a delta each minute.
        state.PutPullRequests(row with { ReadAt = row.ReadAt.AddMinutes(1) });
        state.Seq.ShouldBe(seq);
        // The same PRs two minutes on: the read time is renewed, as a client marks a row over five minutes old stale.
        state.PutPullRequests(row with { ReadAt = row.ReadAt.AddMinutes(2) });
        state.Seq.ShouldBe(seq + 1);
        // A changed PR is a delta at once.
        state.PutPullRequests(row with { ReadAt = row.ReadAt.AddMinutes(2), PullRequests = [Row("SharpAstro/a", 1, PullRequestKind.Behind)] });
        state.Seq.ShouldBe(seq + 2);
        state.Snapshot().PullRequests!.PullRequests.Single().Kind.ShouldBe(PullRequestKind.Behind);
    }

    // ---- the node's watch

    // A folder that looks like a clone: a .git folder, with an origin on GitHub.
    private string Clone(string owner, string name)
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, name, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = https://github.com/{owner}/{name}.git\n");
        return Path.Combine(_root, name);
    }

    private PullRequestWatch Watch(NodeState state, FakeGitHub github, TimeProvider? time = null) =>
        new(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), github, time ?? TimeProvider.System);

    [Fact]
    public async Task TheWatchSearchesOncePerDistinctOwnerOfTheClonesOriginsAndMapsAFlightBranchToItsFlight()
    {
        var pilot = Clone("SharpAstro", "pilot");
        Clone("SharpAstro", "tianwen");
        Clone("sebgod", "mercury");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "elsewhere", ".git")).FullName, "config"), "[remote \"origin\"]\n\turl = git@example.com:x/y.git\n");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(new FlightRow
        {
            Code = "PIL1", Repo = "pilot", RepoPath = pilot, Issue = 1, Branch = "flight/pil1", Worktree = Path.Combine(pilot, ".claude", "worktrees", "pil1"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        var github = new FakeGitHub();
        github.OrgPrs["SharpAstro"] = [Row("SharpAstro/pilot", 7, PullRequestKind.Ready) with { Head = "flight/pil1" }, Row("SharpAstro/tianwen", 8, PullRequestKind.Draft) with { Head = "flight/pil1" }];
        github.OrgPrs["sebgod"] = [Row("sebgod/mercury", 9, PullRequestKind.Behind)];

        var row = (await Watch(state, github).RefreshAsync(Ct))!;

        github.OrgSearches.ShouldBe(["SharpAstro", "sebgod"], ignoreOrder: true);
        row.Orgs.ShouldBe(["sebgod", "SharpAstro"]);
        row.PullRequests.Select(p => (p.Number, p.Flight)).ShouldBe([(7, "PIL1"), (8, null), (9, null)], ignoreOrder: true);
        state.PullRequests().ShouldBe(row);
        // And the same row is the stream's snapshot.
        state.Snapshot().PullRequests.ShouldBe(row);
        // It says who gh reads as, which the Pull requests page's Mine pill goes by.
        row.Viewer.ShouldBe("controller");
    }

    [Fact]
    public async Task TheRowSaysNoViewerWhenGhCannotSayWhoItReadsAsAndMineIsThenWhatIsNoFlights()
    {
        Clone("SharpAstro", "pilot");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub { Login = null };
        github.OrgPrs["SharpAstro"] = [Row("SharpAstro/pilot", 7, PullRequestKind.Ready) with { Author = "sebgod" }];

        var row = (await Watch(state, github).RefreshAsync(Ct))!;

        row.Viewer.ShouldBeNull();
        var own = row.PullRequests[0];
        row.IsMine(own).ShouldBeTrue();
        row.IsMine(own with { Flight = "PIL1" }).ShouldBeFalse();
        // With one, it is the author's login whatever the case, flight or none.
        (row with { Viewer = "SebGod" }).IsMine(own with { Flight = "PIL1" }).ShouldBeTrue();
        (row with { Viewer = "ada" }).IsMine(own).ShouldBeFalse();
    }

    [Fact]
    public void TheCountsAndTheAgeAreReadOneWayForEveryClient()
    {
        var row = new PullRequestsRow
        {
            ReadAt = Noon,
            PullRequests = [Row("a/b", 1, PullRequestKind.Ready), Row("a/b", 2, PullRequestKind.Ready), Row("a/b", 3, PullRequestKind.ChecksRunning), Row("a/b", 4, PullRequestKind.Conflicts), Row("a/b", 5, PullRequestKind.Draft)],
        };

        row.Summary().ShouldBe("5 open · 2 ready · 1 checks running · 1 conflicts");
        PullRequestsRow.Age(Noon - TimeSpan.FromSeconds(20), Noon).ShouldBe("just now");
        PullRequestsRow.Age(Noon - TimeSpan.FromMinutes(7), Noon).ShouldBe("7m ago");
        PullRequestsRow.Age(Noon - TimeSpan.FromHours(3.5), Noon).ShouldBe("3h ago");
        PullRequestsRow.Age(Noon - TimeSpan.FromDays(2), Noon).ShouldBe("2d ago");
        PullRequestsRow.Age(null, Noon).ShouldBe("");
        // The newest read's viewer, whichever node knew it.
        PullRequestsRow.Merge([row with { Viewer = "sebgod", ReadAt = Noon - TimeSpan.FromMinutes(2) }, row with { Viewer = null }])!.Viewer.ShouldBe("sebgod");
    }

    [Fact]
    public async Task AnOrgThatCannotBeSearchedIsNamedInTheRowAndKeepsTheLastReadOfItsPullRequests()
    {
        Clone("SharpAstro", "pilot");
        Clone("sebgod", "mercury");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.OrgPrs["SharpAstro"] = [Row("SharpAstro/pilot", 1, PullRequestKind.Ready)];
        github.OrgPrs["sebgod"] = [Row("sebgod/mercury", 2, PullRequestKind.Ready)];
        var watch = Watch(state, github);
        await watch.RefreshAsync(Ct);

        github.OrgPrsFail.Add("sebgod");
        github.OrgPrs["SharpAstro"] = [Row("SharpAstro/pilot", 1, PullRequestKind.Behind)];
        var row = (await watch.RefreshAsync(Ct))!;

        var error = row.Errors.Single();
        (error.Org, error.Message).ShouldBe(("sebgod", "gh could not search sebgod's PRs: offline"));
        // The other org is read afresh; the failed one stands as it was read rather than vanishing.
        row.PullRequests.Select(p => (p.Number, p.Kind)).ShouldBe([(1, PullRequestKind.Behind), (2, PullRequestKind.Ready)], ignoreOrder: true);
    }

    [Fact]
    public async Task ANodeWhoseClonesAreNotOnGitHubSaysNothing()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();

        (await Watch(state, github).RefreshAsync(Ct)).ShouldBeNull();

        state.PullRequests().ShouldBeNull();
        github.OrgSearches.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWatchReadsAtOnceAgainEveryMinuteAndNotBeforeAndARefreshReadsWithoutWaitingForTheMinute()
    {
        Clone("SharpAstro", "pilot");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.OrgPrs["SharpAstro"] = [Row("SharpAstro/pilot", 1, PullRequestKind.Ready)];
        var time = new ManualTime(Noon);
        var watch = Watch(state, github, time);
        watch.Interval.ShouldBe(TimeSpan.FromSeconds(60));

        await watch.StartAsync(Ct);
        try
        {
            await Until(() => github.OrgSearches.Count == 1 && time.Waiting == 1);
            state.PullRequests()!.ReadAt.ShouldBe(Noon);

            time.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(50, Ct);
            github.OrgSearches.Count.ShouldBe(1);

            github.OrgPrs["SharpAstro"] = [Row("SharpAstro/pilot", 1, PullRequestKind.Behind)];
            time.Advance(TimeSpan.FromSeconds(1));
            await Until(() => github.OrgSearches.Count == 2 && time.Waiting == 1);
            state.PullRequests()!.PullRequests.Single().Kind.ShouldBe(PullRequestKind.Behind);
            state.PullRequests()!.ReadAt.ShouldBe(Noon.AddSeconds(60));

            // Refresh: now, with no time passed.
            github.OrgPrs["SharpAstro"] = [Row("SharpAstro/pilot", 1, PullRequestKind.Ready), Row("SharpAstro/pilot", 2, PullRequestKind.Draft)];
            var row = (await watch.RefreshAsync(Ct))!;
            github.OrgSearches.Count.ShouldBe(3);
            row.PullRequests.Length.ShouldBe(2);
            state.PullRequests()!.PullRequests.Length.ShouldBe(2);
        }
        finally
        {
            await watch.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ARefreshAskedWhileAnotherRunsGetsThatOnesAnswer()
    {
        Clone("SharpAstro", "pilot");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        var held = github.HoldSearches();
        var watch = Watch(state, github);

        var first = watch.RefreshAsync(Ct);
        var second = watch.RefreshAsync(Ct);
        held.SetResult();

        (await first).ShouldBeSameAs(await second);
        github.OrgSearches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ThePullRequestsRefreshCommandReadsThroughARealNodeAndPilotPrsPrintsTheRowGroupedByRepo()
    {
        var pilot = Clone("SharpAstro", "pilot");
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(new FlightRow
        {
            Code = "PIL1", Repo = "pilot", RepoPath = pilot, Issue = 1, Branch = "flight/pil1", Worktree = Path.Combine(pilot, ".claude", "worktrees", "pil1"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        node.GitHub.OrgPrs["SharpAstro"] =
        [
            Row("SharpAstro/pilot", 7, PullRequestKind.Ready) with { Head = "flight/pil1", Author = "sebgod", Review = PullRequestReview.Passed },
            Row("SharpAstro/pilot", 8, PullRequestKind.Draft),
        ];

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.RefreshPullRequests, Target = Verbs.NodeTarget, Who = "test" }, Ct)).Response!;

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("2");
        var output = new StringWriter();
        (await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "prs", "--output-format", "plain"], _root, output, new StringWriter(), Ct)).ShouldBe(0);
        output.ToString().ShouldContain("2 open · 1 ready");
        output.ToString().ShouldContain("SharpAstro/pilot  2");
        output.ToString().ShouldContain("#7 PR 7  Ready  PIL1  sebgod  review passed");

        var needsMe = new StringWriter();
        (await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "prs", "--needs-me", "--output-format", "plain"], _root, needsMe, new StringWriter(), Ct)).ShouldBe(0);
        needsMe.ToString().ShouldContain("#7 PR 7");
        needsMe.ToString().ShouldNotContain("#8 PR 8");
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the watch did not get there");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>A clock the test moves: <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on it ends when <see cref="Advance"/> passes its time.</summary>
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;

        /// <summary>The timers set and not yet fired or disposed.</summary>
        public int Waiting
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count(t => t.Due is not null);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due <= _now)];
            }
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                }
                return true;
            }

            public void Fire()
            {
                lock (owner._gate)
                {
                    Due = null;
                }
                callback(state);
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
