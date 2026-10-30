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
    [InlineData("checks-running", PullRequestKind.ChecksRunning, "Checks running 4/8", false)]
    [InlineData("checks-failed", PullRequestKind.ChecksFailed, "Checks failed: test (ubuntu)", false)]
    [InlineData("ready", PullRequestKind.Ready, "Ready", true)]
    [InlineData("in-merge-queue", PullRequestKind.InMergeQueue, "In merge queue #2", false)]
    [InlineData("conflicts", PullRequestKind.Conflicts, "Conflicts", true)]
    [InlineData("behind", PullRequestKind.Behind, "Behind", false)]
    [InlineData("changes-requested", PullRequestKind.ChangesRequested, "Changes requested", true)]
    // A failing pilot/review status is a failed check to the card's reader, which comes before a review's request for changes.
    [InlineData("review-failed", PullRequestKind.ChecksFailed, "Checks failed: pilot/review", false)]
    [InlineData("approved", PullRequestKind.Ready, "Ready", true)]
    // Approved, but a required check or rule still holds it: waiting, not ready.
    [InlineData("approved-held", PullRequestKind.AwaitingCheck, "Waiting for a required check", false)]
    [InlineData("blocked", PullRequestKind.AwaitingCheck, "Waiting for a required check", false)]
    [InlineData("armed", PullRequestKind.Armed, "Auto-merge armed", false)]
    public void PullRequestsAreReadFromTheRecordedAnswerOfEachStateWithOneLabelAndTheNeedsMeFlag(string answer, PullRequestKind kind, string label, bool needsMe)
    {
        var pr = Read(answer);

        (pr.Kind, pr.Label, pr.NeedsMe).ShouldBe((kind, label, needsMe));
    }

    [Fact]
    public void ARecordedPullRequestCarriesItsFieldsItsCheckCountAndPilotsReviewWhichIsACheckToo()
    {
        var pr = Read("ready");

        (pr.Repo, pr.Number, pr.Title, pr.Author, pr.Head, pr.Base, pr.Draft).ShouldBe(("SharpAstro/demo", 104, "Ready to merge", "sebgod", "feature-104", "main", false));
        pr.Url.ShouldBe("https://github.com/SharpAstro/demo/pull/104");
        pr.CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 27, 19, 56, 26, TimeSpan.Zero));
        pr.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 10, 28, 6, 0, 0, TimeSpan.Zero));
        // build, test, a skipped publish and the pilot/review status: a card counts that status among its checks, so the page does.
        (pr.ChecksDone, pr.ChecksTotal, pr.FailedCheck).ShouldBe((4, 4, null));
        (pr.Review, pr.ReviewUrl).ShouldBe((PullRequestReview.Passed, "https://github.com/SharpAstro/demo/pull/104#issuecomment-9"));
    }

    [Theory]
    [InlineData("draft", PullRequestReview.Reviewing)]
    [InlineData("review-failed", PullRequestReview.Changes)]
    [InlineData("changes-requested", null)]
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
    public void ACheckThatFailedAndPassedOnAReRunCountsOnceAsPassedLikeTheCardReadsIt()
    {
        // The rollup lists the failed run and its re-run as two entries of one name (the failed one listed last here): only the one
        // that started last counts, as GhCli.RollupChecks reads it, so the page says Ready where the flight's card does.
        var pr = Read("rerun-passed");

        (pr.Kind, pr.Label, pr.NeedsMe).ShouldBe((PullRequestKind.Ready, "Ready", true));
        (pr.ChecksDone, pr.ChecksTotal, pr.FailedCheck).ShouldBe((4, 4, null));
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

    // The one reader: the page's kind and label are what a flight's card reads from the same GitHub facts.
    private static (MergeState? Card, PullRequestMergeState Page) Of(bool draft = false, string? mergeable = "MERGEABLE", string? status = "CLEAN", string? decision = null,
        bool queued = false, int? position = null, bool armed = false, params (string Bucket, string? Name, string? Link)[] checks)
    {
        var merge = GhCli.ReadMergeState(new MergeFacts("OPEN", draft, status, mergeable, decision, armed, checks), queued ? new MergeQueueState("QUEUED", "", true, position) : null);
        return (merge, PullRequestSearch.Say(merge, draft));
    }

    private static (string, string?, string?) Pass(string name = "build") => ("pass", name, null);

    [Fact]
    public void TheMergeStateReaderTakesTheFirstThatHoldInTheOrderTheCardDocuments()
    {
        // A draft is a draft on the page whatever else is true; a PR in the queue is in it.
        Of(draft: true, mergeable: "CONFLICTING", checks: [("fail", "build", null)]).Page.Label.ShouldBe("Draft");
        Of(queued: true, position: 1, mergeable: "CONFLICTING", checks: [Pass()]).Page.Label.ShouldBe("In merge queue #1");
        Of(queued: true, checks: [Pass()]).Page.Label.ShouldBe("In merge queue");
        // The card's order: conflicts, behind, a failed check, then changes requested, then what waits.
        Of(mergeable: "CONFLICTING", decision: "CHANGES_REQUESTED", checks: [Pass()]).Page.Label.ShouldBe("Conflicts");
        Of(status: "BEHIND", decision: "CHANGES_REQUESTED", checks: [Pass()]).Page.Label.ShouldBe("Behind");
        Of(status: "BEHIND", checks: [("fail", "build", null)]).Page.Label.ShouldBe("Behind");
        Of(decision: "CHANGES_REQUESTED", checks: [("fail", "build", null)]).Page.Label.ShouldBe("Checks failed: build");
        Of(decision: "CHANGES_REQUESTED", checks: [Pass()]).Page.Label.ShouldBe("Changes requested");
        Of(checks: [("fail", "build", null), ("pending", "test", null), Pass("lint")]).Page.Label.ShouldBe("Checks failed: build");
        Of(checks: [("pending", "build", null), Pass("test"), Pass("lint")]).Page.Label.ShouldBe("Checks running 2/3");
        // Auto-merge armed says so, whatever the merge box says of the rest; a failed check still blocks it.
        Of(armed: true, status: "BLOCKED", checks: [Pass()]).Page.Label.ShouldBe("Auto-merge armed");
        Of(armed: true, checks: [("pending", "build", null)]).Page.Label.ShouldBe("Auto-merge armed");
        Of(armed: true, checks: [("fail", "build", null)]).Page.Label.ShouldBe("Checks failed: build");
        // What is left is ready, a review wanted when the repo requires one, or a required check GitHub has yet to create.
        Of(checks: [Pass()]).Page.Label.ShouldBe("Ready");
        Of(decision: "APPROVED", checks: [Pass()]).Page.Label.ShouldBe("Ready");
        Of(decision: "REVIEW_REQUIRED", status: "BLOCKED", checks: [Pass()]).Page.Label.ShouldBe("Review required");
        Of(mergeable: "UNKNOWN", status: "UNKNOWN", checks: [Pass()]).Page.Label.ShouldBe("Checking merge state");
        // Only ready, changes requested and conflicts wait on the person.
        Of(checks: [Pass()]).Page.NeedsMe.ShouldBeTrue();
        Of(decision: "CHANGES_REQUESTED", checks: [Pass()]).Page.NeedsMe.ShouldBeTrue();
        Of(mergeable: "CONFLICTING", checks: [Pass()]).Page.NeedsMe.ShouldBeTrue();
        new[] { Of(draft: true), Of(queued: true), Of(checks: [("fail", "build", null)]), Of(checks: [("pending", "build", null)]), Of(status: "BEHIND"), Of(armed: true, checks: [Pass()]),
            Of(decision: "APPROVED", status: "BLOCKED", checks: [Pass()]), Of(status: "BLOCKED", checks: [Pass()]),
            Of(decision: "REVIEW_REQUIRED", status: "BLOCKED", checks: [Pass()]), Of(mergeable: "UNKNOWN", status: "UNKNOWN", checks: [Pass()]) }.ShouldAllBe(s => !s.Page.NeedsMe);
    }

    [Fact]
    public void APullRequestGitHubHoldsAsBlockedWithNoReviewInTheWayWaitsForItsRequiredCheckAndIsNeitherReadyNorNeedsMe()
    {
        // Recorded: BLOCKED, no review decision, every check green: the window before the required gate check is created.
        var pr = Read("blocked");

        (pr.Kind, pr.Label, pr.NeedsMe).ShouldBe((PullRequestKind.AwaitingCheck, "Waiting for a required check", false));
        new PullRequestsRow { PullRequests = [pr], ReadAt = Noon }.Groups(needsMeOnly: true).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("blocked")]
    [InlineData("armed")]
    [InlineData("approved")]
    [InlineData("approved-held")]
    [InlineData("checks-running")]
    [InlineData("checks-failed")]
    [InlineData("conflicts")]
    [InlineData("behind")]
    [InlineData("changes-requested")]
    [InlineData("review-failed")]
    [InlineData("in-merge-queue")]
    public void APullRequestOnThePageAndItsCardReadTheSameMergeStateFromTheSameFacts(string answer)
    {
        // The card's reader and the page's are one: the page's row is the card's merge state said in the page's words.
        var pr = Read(answer);
        var json = JsonDocument.Parse(Recorded(answer)).RootElement.GetProperty("data").GetProperty("search").GetProperty("nodes")[0];
        var contexts = json.GetProperty("commits").GetProperty("nodes")[0].GetProperty("commit").GetProperty("statusCheckRollup").GetProperty("contexts").GetProperty("nodes");
        var checks = contexts.EnumerateArray().Select(c => c.GetProperty("__typename").GetString() == "CheckRun"
            ? (GhCli.CheckBucket(Str(c, "conclusion"), Str(c, "status")), Str(c, "name"), Str(c, "detailsUrl"))
            : (GhCli.CheckBucket(Str(c, "state"), null), Str(c, "context"), Str(c, "targetUrl"))).ToList();
        var card = GhCli.ReadMergeState(
            new MergeFacts("OPEN", json.GetProperty("isDraft").GetBoolean(), Str(json, "mergeStateStatus"), Str(json, "mergeable"), Str(json, "reviewDecision"),
                json.GetProperty("autoMergeRequest").ValueKind == JsonValueKind.Object, checks),
            json.GetProperty("isInMergeQueue").GetBoolean() ? new MergeQueueState("QUEUED", "", true, json.GetProperty("mergeQueueEntry").GetProperty("position").GetInt32()) : null);

        PullRequestSearch.Say(card, pr.Draft).ShouldBe(new PullRequestMergeState(pr.Kind, pr.Label, pr.NeedsMe));
    }

    private static string? Str(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
            Row("SharpAstro/d", 6, PullRequestKind.ReviewRequired));

        var groups = row.Groups(needsMeOnly: true);

        groups.Select(g => g.Repo).ShouldBe(["SharpAstro/a", "SharpAstro/c"]);
        groups.SelectMany(g => g.PullRequests).Select(p => p.Number).ShouldBe([1, 5, 4]);
        RowOf().Groups().ShouldBeEmpty();
    }

    [Fact]
    public void RepoGroupsAreGroupedOnceMoreByOrgTheOrgWithThePullRequestThatMostNeedsThePersonFirstAndEachWithItsCounts()
    {
        var row = new PullRequestsRow
        {
            Orgs = ["sharpastro", "sebgod", "acme"], ReadAt = Noon,
            PullRequests =
            [
                Row("sebgod/a", 1, PullRequestKind.Conflicts, 2),
                Row("SharpAstro/draft", 2, PullRequestKind.Draft, 1),
                Row("SharpAstro/ready", 3, PullRequestKind.Ready, 8),
                Row("sharpastro/READY", 4, PullRequestKind.ChecksRunning, 1),
                Row("sebgod/b", 5, PullRequestKind.Conflicts, 1),
                Row("acme/x", 6, PullRequestKind.Ready, 20),
            ],
        };

        var orgs = row.OrgGroups();

        // Ready in acme (older) and SharpAstro (newer) tie on rank: the more recently updated first, then the conflicts.
        orgs.Select(o => o.Org).ShouldBe(["SharpAstro", "acme", "sebgod"]);
        // Each org's repos are Groups' order, the repos of an org as Groups has them.
        orgs[0].Repos.Select(g => g.Repo).ShouldBe(["SharpAstro/ready", "SharpAstro/draft"]);
        orgs[2].Repos.Select(g => g.Repo).ShouldBe(["sebgod/b", "sebgod/a"]);
        orgs.SelectMany(o => o.Repos).Select(g => g.Repo).ShouldBe(row.Groups().Select(g => g.Repo), ignoreOrder: true);
        // The words of the summary, over the org's PRs, so header and summary read alike.
        orgs[0].Counts.ShouldBe("3 open · 1 ready · 1 checks running");
        orgs[2].Counts.ShouldBe("2 open · 2 conflicts");
        row.Summary().ShouldBe(PullRequestsRow.Counts(row.PullRequests));
        orgs.All(o => o.Error is null).ShouldBeTrue();
    }

    [Fact]
    public void AnOrgWithNoPullRequestOrWhoseReadFailedIsStillListedAfterTheOnesWithPullRequestsAndAFailedOneKeepsItsLastReadPullRequests()
    {
        var row = new PullRequestsRow
        {
            Orgs = ["zeta", "SharpAstro", "alpha", "sebgod"], ReadAt = Noon,
            PullRequests = [Row("SharpAstro/a", 1, PullRequestKind.Draft), Row("sebgod/m", 2, PullRequestKind.Ready)],
            Errors = [new PullRequestsError { Org = "sebgod", Message = "gh offline\nretry" }, new PullRequestsError { Org = "Sebgod", Message = "rate limited" }, new PullRequestsError { Org = "elsewhere", Message = "no access" }],
        };

        var orgs = row.OrgGroups();

        orgs.Select(o => o.Org).ShouldBe(["sebgod", "SharpAstro", "alpha", "elsewhere", "zeta"]);
        // A failed org keeps what was read of it, and says why (once however it is cased, on one line).
        orgs[0].ShouldSatisfyAllConditions(o => o.Error.ShouldBe("gh offline retry rate limited"), o => o.Repos.Single().PullRequests.Single().Number.ShouldBe(2), o => o.Counts.ShouldBe("1 open · 1 ready"));
        orgs[1].Error.ShouldBeNull();
        // No PR: nothing to show under it, and it counts none.
        orgs[2].ShouldSatisfyAllConditions(o => o.Repos.ShouldBeEmpty(), o => o.Counts.ShouldBe("0 open"), o => o.Error.ShouldBeNull());
        // An error naming an org that is not among Orgs is one too.
        orgs[3].Error.ShouldBe("no access");
    }

    [Fact]
    public void TheNeedsMeOrgGroupsKeepOnlyTheOrgsWithAPullRequestThatNeedsThePersonOrWhoseReadFailed()
    {
        var row = new PullRequestsRow
        {
            Orgs = ["SharpAstro", "sebgod", "quiet", "down"], ReadAt = Noon,
            PullRequests = [Row("SharpAstro/a", 1, PullRequestKind.Ready), Row("SharpAstro/a", 2, PullRequestKind.ChecksRunning), Row("sebgod/m", 3, PullRequestKind.Draft)],
            Errors = [new PullRequestsError { Org = "down", Message = "offline" }],
        };

        var orgs = row.OrgGroups(needsMeOnly: true);

        orgs.Select(o => o.Org).ShouldBe(["SharpAstro", "down"]);
        // The shown repos are the ones that need the person; the counts are still of everything the org has open.
        orgs[0].Repos.Single().PullRequests.Select(p => p.Number).ShouldBe([1]);
        orgs[0].Counts.ShouldBe("2 open · 1 ready · 1 checks running");
        orgs[1].Repos.ShouldBeEmpty();
        RowOf().OrgGroups().Single().ShouldSatisfyAllConditions(o => o.Org.ShouldBe("SharpAstro"), o => o.Counts.ShouldBe("0 open"), o => o.Repos.ShouldBeEmpty());
        RowOf().OrgGroups(needsMeOnly: true).ShouldBeEmpty();
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
        // The per-org errors and the rate-limit note of every node stand, an error once however its org is cased.
        var limited = PullRequestsRow.Merge([mine with { Note = "Rate-limited by GitHub until 14:05; reading again then" }, theirs with { Errors = [.. theirs.Errors, new PullRequestsError { Org = "SEBGOD", Message = "offline" }, new PullRequestsError { Org = "acme", Message = "no access" }] }])!;
        limited.Errors.Select(e => e.Message).ShouldBe(["offline", "no access"]);
        limited.Note.ShouldBe("Rate-limited by GitHub until 14:05; reading again then");
        limited.OrgGroups().Single(o => o.Org == "sebgod").Error.ShouldBe("offline");
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
        // Records compare an array by reference, so the labels are compared on their own.
        var back = JsonSerializer.Deserialize(json, PilotJsonContext.Default.PullRequestsRow)!.PullRequests.Single();
        back.Labels.ShouldBe(row.PullRequests.Single().Labels);
        (back with { Labels = row.PullRequests.Single().Labels }).ShouldBe(row.PullRequests.Single());
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
    public async Task TheWatchSearchesTheTrackedReposInOneBatchAndMapsAFlightBranchToItsFlight()
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
        github.PullRequestsOnGitHub.AddRange([Row("SharpAstro/pilot", 7, PullRequestKind.Ready) with { Head = "flight/pil1" }, Row("SharpAstro/tianwen", 8, PullRequestKind.Draft) with { Head = "flight/pil1" }, Row("sebgod/mercury", 9, PullRequestKind.Behind)]);

        var row = (await Watch(state, github).RefreshAsync(Ct))!;

        // One search for the three repos with a GitHub origin; the clone whose origin is elsewhere is not in it.
        github.Searches.Single().ShouldBe(["sebgod/mercury", "SharpAstro/pilot", "SharpAstro/tianwen"]);
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
        github.PullRequestsOnGitHub.Add(Row("SharpAstro/pilot", 7, PullRequestKind.Ready) with { Author = "sebgod" });

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
    public async Task EveryOrgOfABatchThatCannotBeSearchedIsNamedInTheRowAndKeepsTheLastReadOfItsPullRequestsWhileTheOtherBatchesAreReadAfresh()
    {
        // Twelve repos of two orgs, named long enough that they take more than one search.
        var long1 = Enumerable.Range(0, 6).Select(i => $"SharpAstro/repo-with-a-rather-long-name-{i}").ToArray();
        var long2 = Enumerable.Range(0, 6).Select(i => $"sebgod/another-quite-long-repository-name-{i}").ToArray();
        foreach (var repo in long1.Concat(long2))
        {
            Clone(repo.Split('/')[0], repo.Split('/')[1]);
        }
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        for (var i = 0; i < 6; i++)
        {
            github.PullRequestsOnGitHub.Add(Row(long1[i], i + 1, PullRequestKind.Ready));
            github.PullRequestsOnGitHub.Add(Row(long2[i], i + 101, PullRequestKind.Ready));
        }
        var watch = Watch(state, github);
        await watch.RefreshAsync(Ct);
        github.Searches.Count.ShouldBeGreaterThan(1);
        var first = github.Searches.Count;

        // One repo of the batch the first of the sorted repos is in fails it; every other batch is read again.
        var failing = github.Searches[0][0];
        github.SearchFail.Add(failing);
        github.PullRequestsOnGitHub.RemoveAll(p => p.Number is 2);
        var row = (await watch.RefreshAsync(Ct))!;

        var batch = github.Searches[first];
        var failedOrgs = batch.Select(r => r.Split('/')[0]).Distinct().Order().ToArray();
        row.Errors.Select(e => e.Org).Order().ToArray().ShouldBe(failedOrgs);
        row.Errors.ShouldAllBe(e => e.Message == $"gh could not search the PRs of {batch.Length} repos: offline");
        // The failed batch's PRs stand as they were read, PR 2 included (it is closed now, but this read could not say); the rest are read afresh.
        row.PullRequests.Count(p => batch.Contains(p.Repo)).ShouldBe(batch.Length);
        row.PullRequests.Any(p => p.Number == 2).ShouldBe(batch.Contains(long1[1]));
    }

    [Fact]
    public async Task ANodeWhoseClonesAreNotOnGitHubSaysNothing()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();

        (await Watch(state, github).RefreshAsync(Ct)).ShouldBeNull();

        state.PullRequests().ShouldBeNull();
        github.Searches.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWatchReadsAtOnceAgainEveryMinuteAndNotBeforeAndARefreshReadsWithoutWaitingForTheMinute()
    {
        Clone("SharpAstro", "pilot");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.PullRequestsOnGitHub.Add(Row("SharpAstro/pilot", 1, PullRequestKind.Ready));
        var time = new ManualTime(Noon);
        var watch = Watch(state, github, time);
        watch.Interval.ShouldBe(TimeSpan.FromSeconds(60));

        await watch.StartAsync(Ct);
        try
        {
            await Until(() => github.Searches.Count == 1 && time.Waiting == 1);
            state.PullRequests()!.ReadAt.ShouldBe(Noon);

            time.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(50, Ct);
            github.Searches.Count.ShouldBe(1);

            github.PullRequestsOnGitHub.Clear(); github.PullRequestsOnGitHub.Add(Row("SharpAstro/pilot", 1, PullRequestKind.Behind));
            time.Advance(TimeSpan.FromSeconds(1));
            await Until(() => github.Searches.Count == 2 && time.Waiting == 1);
            state.PullRequests()!.PullRequests.Single().Kind.ShouldBe(PullRequestKind.Behind);
            state.PullRequests()!.ReadAt.ShouldBe(Noon.AddSeconds(60));

            // Refresh: now, with no time passed.
            github.PullRequestsOnGitHub.Clear(); github.PullRequestsOnGitHub.AddRange([Row("SharpAstro/pilot", 1, PullRequestKind.Ready), Row("SharpAstro/pilot", 2, PullRequestKind.Draft)]);
            var row = (await watch.RefreshAsync(Ct))!;
            github.Searches.Count.ShouldBe(3);
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
        github.Searches.Count.ShouldBe(1);
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
        node.GitHub.PullRequestsOnGitHub.AddRange(
        [
            Row("SharpAstro/pilot", 7, PullRequestKind.Ready) with { Head = "flight/pil1", Author = "sebgod", Review = PullRequestReview.Passed },
            Row("SharpAstro/pilot", 8, PullRequestKind.Draft),
        ]);

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

    // ---- scope: only the repos pilot works in (#429)

    private static PrReviewRow Review(string repo, int number) => new()
    {
        Repo = repo, Number = number, Url = $"https://github.com/{repo}/pull/{number}", Base = "main", Head = "4f2c9a01d6e8",
        ClonePath = "/r", Worktree = $"/r/.claude/worktrees/review-{number}", Reviewer = $"review-{number}",
        State = PrReviewState.Reviewing, StartedAt = Noon,
    };

    [Fact]
    public async Task OnlyTheTrackedReposAreSearchedTheClonesAndTheReposOfFlightsAndPrReviewsAndNeverAnOrg()
    {
        var pilot = Clone("SharpAstro", "pilot");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        // A review of a PR in a repo that is not cloned here: tracked all the same.
        state.PutPrReview(Review("acme/widget", 7));
        var github = new FakeGitHub();
        // GitHub holds PRs of other repos of the same orgs, which an org-wide search would have read.
        github.PullRequestsOnGitHub.AddRange([Row("SharpAstro/pilot", 1, PullRequestKind.Ready), Row("acme/widget", 7, PullRequestKind.Ready), Row("SharpAstro/never-cloned", 2, PullRequestKind.Ready), Row("acme/other", 3, PullRequestKind.Ready)]);

        var row = (await Watch(state, github).RefreshAsync(Ct))!;

        var search = github.Searches.Single();
        search.ShouldBe(["SharpAstro/pilot", "acme/widget"], ignoreOrder: true);
        PullRequestSearch.QueryFor(search).ShouldNotContain("org:");
        PullRequestSearch.QueryFor(search).ShouldStartWith("is:pr is:open repo:");
        row.PullRequests.Select(p => p.Repo).Order().ToArray().ShouldBe(new[] { "SharpAstro/pilot", "acme/widget" }.Order().ToArray());
        row.Orgs.ShouldBe(["acme", "SharpAstro"]);

        // A flight's repo is tracked whether or not it is in the catalog, and a repo is tracked once however it came to be.
        var flight = new FlightRow
        {
            Code = "PIL1", Repo = "pilot", RepoPath = pilot, Issue = 1, Branch = "flight/pil1", Worktree = Path.Combine(pilot, ".claude", "worktrees", "pil1"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        PullRequestWatch.TrackedRepos([], [flight], [Review("sharpastro/PILOT", 3), Review("acme/widget", 7)])
            .ShouldBe(["acme/widget", "SharpAstro/pilot"]);
        PullRequestWatch.TrackedRepos([], [flight], []).ShouldBe(["SharpAstro/pilot"]);
    }

    [Fact]
    public void ReposAreBatchedIntoRepoQualifiersAndSplitWhereAQueryWouldPassTheLimit()
    {
        PullRequestSearch.MaxQueryLength.ShouldBe(256);
        PullRequestSearch.QueryFor(["a/b", "c/d"]).ShouldBe("is:pr is:open repo:a/b repo:c/d");
        PullRequestSearch.Batches([]).ShouldBeEmpty();
        Show(PullRequestSearch.Batches(["a/b", "c/d", "A/B"])).ShouldBe("a/b,c/d");

        var repos = Enumerable.Range(0, 40).Select(i => $"SharpAstro/repo-number-{i:00}").ToArray();
        var batches = PullRequestSearch.Batches(repos);

        batches.Length.ShouldBeGreaterThan(1);
        // Every repo is in exactly one batch, in order, and no batch makes a query past the limit.
        batches.SelectMany(b => b).ShouldBe(repos);
        batches.ShouldAllBe(b => PullRequestSearch.QueryFor(b).Length <= PullRequestSearch.MaxQueryLength);
        // As full as can be: the next batch's first repo would not have fitted in the batch before it.
        for (var i = 1; i < batches.Length; i++)
        {
            PullRequestSearch.QueryFor(batches[i - 1].Append(batches[i][0])).Length.ShouldBeGreaterThan(PullRequestSearch.MaxQueryLength);
        }

        // A query of exactly 256 characters fits; one more does not.
        var head = PullRequestSearch.QueryFor([]).Length;
        string Of(int length) => "o/" + new string('x', length - 2);
        var room = PullRequestSearch.MaxQueryLength - head - 2 * " repo:".Length;
        PullRequestSearch.Batches([Of(100), Of(room - 100)]).Length.ShouldBe(1);
        PullRequestSearch.QueryFor([Of(100), Of(room - 100)]).Length.ShouldBe(256);
        PullRequestSearch.Batches([Of(100), Of(room - 100 + 1)]).Length.ShouldBe(2);
        // A repo too long for a query alone is still searched, alone.
        Show(PullRequestSearch.Batches([Of(300), "a/b"])).ShouldBe($"{Of(300)} {"a/b"}");
    }

    private static string Show(string[][] batches) => string.Join(" ", batches.Select(b => string.Join(",", b)));

    [Fact]
    public async Task TheWatchSplitsManyReposIntoSearchesThatEachStayWithinTheQueryLimit()
    {
        var repos = Enumerable.Range(0, 23).Select(i => $"SharpAstro/clone-{i:00}").ToArray();
        foreach (var repo in repos)
        {
            Clone("SharpAstro", repo.Split('/')[1]);
        }
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.PullRequestsOnGitHub.AddRange(repos.Select((r, i) => Row(r, i + 1, PullRequestKind.Ready)));

        var row = (await Watch(state, github).RefreshAsync(Ct))!;

        github.Searches.Count.ShouldBeGreaterThan(1);
        github.Searches.ShouldAllBe(s => PullRequestSearch.QueryFor(s).Length <= PullRequestSearch.MaxQueryLength);
        github.Searches.SelectMany(s => s).Order().ToArray().ShouldBe(repos);
        row.PullRequests.Length.ShouldBe(23);
    }

    [Fact]
    public async Task ARepoThatIsNoLongerTrackedLeavesTheRowEvenWhenItsSearchFailsAndItsHeldPullRequestsAreNotKept()
    {
        Clone("SharpAstro", "pilot");
        var mercury = Clone("sebgod", "mercury");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.PullRequestsOnGitHub.AddRange([Row("SharpAstro/pilot", 1, PullRequestKind.Ready), Row("sebgod/mercury", 2, PullRequestKind.Ready)]);
        var watch = Watch(state, github);
        (await watch.RefreshAsync(Ct))!.PullRequests.Length.ShouldBe(2);

        // The clone goes: its repo is tracked no more, and the PR GitHub still holds is not in the row.
        foreach (var file in Directory.EnumerateFiles(mercury, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(mercury, recursive: true);
        var row = (await watch.RefreshAsync(Ct))!;

        row.PullRequests.Select(p => p.Repo).ShouldBe(["SharpAstro/pilot"]);
        row.Orgs.ShouldBe(["SharpAstro"]);
        // Nor are its held PRs kept when what is left cannot be read: only the tracked repo's stand.
        github.PullRequestsOnGitHub.Add(Row("sebgod/mercury", 2, PullRequestKind.Ready));
        github.SearchFail.Add("SharpAstro/pilot");
        var failed = (await watch.RefreshAsync(Ct))!;
        failed.PullRequests.Select(p => p.Repo).ShouldBe(["SharpAstro/pilot"]);
        failed.Errors.Single().Org.ShouldBe("SharpAstro");
    }

    [Fact]
    public async Task TheRowCarriesTheGraphQlPointsTheReadCostAndWhatIsLeftAndTheLogLineSaysSo()
    {
        Clone("SharpAstro", "pilot");
        Clone("sebgod", "mercury");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub { SearchCost = 4, SearchRemaining = 4321 };
        github.PullRequestsOnGitHub.Add(Row("SharpAstro/pilot", 1, PullRequestKind.Ready));
        var log = new StringWriter();
        var watch = Watch(state, github);
        watch.Error = log;

        var row = (await watch.RefreshAsync(Ct))!;

        // One search of both repos: its cost, and the points GitHub says are left.
        (row.Cost, row.RateRemaining, row.Note).ShouldBe((4, 4321, null));
        log.ToString().ShouldContain("1 open PRs of 2 repos in 1 searches, 4 GraphQL points, 4321 left");
        // The cost of several searches is summed.
        github.SearchCost = 5;
        foreach (var i in Enumerable.Range(0, 20))
        {
            Clone("SharpAstro", $"another-clone-{i:00}");
        }
        var more = (await watch.RefreshAsync(Ct))!;
        github.Searches.Count.ShouldBeGreaterThan(2);
        more.Cost.ShouldBe(5 * (github.Searches.Count - 1));
        // And merging several nodes' rows adds their reads up.
        PullRequestsRow.Merge([row, more with { ReadAt = row.ReadAt.AddSeconds(1) }])!.Cost.ShouldBe(row.Cost + more.Cost);
    }

    [Fact]
    public async Task ARateLimitedReadKeepsTheHeldPullRequestsSaysSoOnTheRowAndTheNextReadsWaitForTheReset()
    {
        Clone("SharpAstro", "pilot");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.PullRequestsOnGitHub.Add(Row("SharpAstro/pilot", 1, PullRequestKind.Ready));
        var time = new ManualTime(Noon);
        var watch = Watch(state, github, time);
        (await watch.RefreshAsync(Ct))!.Note.ShouldBeNull();

        var reset = Noon.AddMinutes(30);
        github.RateLimited = (true, reset);
        github.PullRequestsOnGitHub.Clear();
        time.Advance(TimeSpan.FromMinutes(1));
        var limited = (await watch.RefreshAsync(Ct))!;

        github.Searches.Count.ShouldBe(2);
        limited.Note.ShouldBe("Rate-limited by GitHub until 12:30 UTC: showing what was last read, reading again then");
        limited.PullRequests.Select(p => p.Number).ShouldBe([1]);
        limited.RateRemaining.ShouldBe(0);
        limited.Errors.ShouldBeEmpty();
        state.PullRequests()!.Note.ShouldBe(limited.Note);

        // Until the reset, a read (or a refresh asked for) asks GitHub nothing and shows the same PRs, as old as they were.
        github.RateLimited = (false, null);
        time.Advance(TimeSpan.FromMinutes(20));
        var waiting = (await watch.RefreshAsync(Ct))!;
        github.Searches.Count.ShouldBe(2);
        (waiting.Note, waiting.Cost, waiting.ReadAt).ShouldBe((limited.Note, 0, limited.ReadAt));
        waiting.PullRequests.Select(p => p.Number).ShouldBe([1]);

        // At the reset it reads again, and the note goes.
        time.Advance(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(59));
        (await watch.RefreshAsync(Ct))!.Note.ShouldNotBeNull();
        github.Searches.Count.ShouldBe(2);
        time.Advance(TimeSpan.FromSeconds(1));
        var again = (await watch.RefreshAsync(Ct))!;
        github.Searches.Count.ShouldBe(3);
        again.Note.ShouldBeNull();
        again.PullRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARateLimitStopsTheLoopReadingEveryMinuteUntilTheResetAndAnUnknownResetIsWaitedOutForAWhile()
    {
        Clone("SharpAstro", "pilot");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.PullRequestsOnGitHub.Add(Row("SharpAstro/pilot", 1, PullRequestKind.Ready));
        var time = new ManualTime(Noon);
        var watch = Watch(state, github, time);

        await watch.StartAsync(Ct);
        try
        {
            await Until(() => github.Searches.Count == 1 && time.Waiting == 1);
            github.RateLimited = (true, Noon.AddMinutes(31));
            time.Advance(TimeSpan.FromSeconds(60));
            await Until(() => github.Searches.Count == 2 && time.Waiting == 1);
            github.RateLimited = (false, null);

            // Not every minute: nothing is read for the half hour the limit holds.
            time.Advance(TimeSpan.FromMinutes(29));
            await Task.Delay(50, Ct);
            github.Searches.Count.ShouldBe(2);
            time.Advance(TimeSpan.FromMinutes(1));
            await Until(() => github.Searches.Count == 3 && time.Waiting == 1);
            state.PullRequests()!.Note.ShouldBeNull();
        }
        finally
        {
            await watch.StopAsync(CancellationToken.None);
        }

        // gh could not say when: the watch waits a fixed while, not a minute.
        var unknown = new FakeGitHub { RateLimited = (true, null) };
        var state2 = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var time2 = new ManualTime(Noon);
        var limited = (await Watch(state2, unknown, time2).RefreshAsync(Ct))!;
        limited.Note.ShouldBe($"Rate-limited by GitHub until {Noon.Add(PullRequestWatch.UnknownReset):HH:mm} UTC: showing what was last read, reading again then");
    }

    [Fact]
    public void TheCostAndTheRateLimitAreReadFromGitHubsAnswerAndAnErrorOfTheRateLimitSaysSo()
    {
        const string answer = """
            {"data":{"rateLimit":{"cost":3,"remaining":4990,"resetAt":"2026-10-28T13:00:00Z"},
            "search":{"issueCount":0,"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}
            """;

        var page = PullRequestSearch.Parse(answer);

        (page.Cost, page.Remaining, page.ResetAt).ShouldBe((3, 4990, new DateTimeOffset(2026, 10, 28, 13, 0, 0, TimeSpan.Zero)));
        PullRequestSearch.Query.ShouldContain("rateLimit{cost remaining resetAt}");
        // An answer with none says nothing of it.
        PullRequestSearch.Parse(Recorded("empty-node")).Cost.ShouldBe(0);
        Should.Throw<RateLimitedException>(() => PullRequestSearch.Parse(Recorded("errors"))).Message.ShouldContain("API rate limit exceeded");
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
