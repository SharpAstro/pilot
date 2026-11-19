using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// GitHub as the record of what built a design (PIL1185): the Designs section of a flight's PR, the opening comment on each design issue, and what a merge does
/// to each (close with a comment, or a comment only), against a fake IGitHub that records every call: no process, no network.
/// </summary>
public sealed class DesignRecordTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2drc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TimerTime _time = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly string _repo;
    private readonly DesignStore _store;
    private readonly NodeState _state;
    private readonly DesignSync _sync;
    private readonly DesignRecord _record;

    public DesignRecordTests()
    {
        _repo = Path.Combine(_root, "repos", "pilot");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        File.WriteAllText(Path.Combine(_repo, "PILOT.md"), "rules");
        var data = Path.Combine(_root, "data");
        var options = new NodeOptions(Path.Combine(_root, "n.sock"), data, RepoRootsGiven: [Path.Combine(_root, "repos")]);
        _store = new DesignStore(data, _time);
        _state = new NodeState(new InMemoryJournal(), _time);
        var catalog = new RepoCatalog(_state, options);
        _record = new DesignRecord(_store, catalog, _state, _github);
        _sync = new DesignSync(_store, catalog, _github, _time, "DE1") { Record = _record };
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private void Published(string id, int number)
    {
        var folder = Path.Combine(_store.FolderOf(id), "versions", "v" + number);
        Directory.CreateDirectory(Path.Combine(folder, "screens"));
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        File.WriteAllText(Path.Combine(folder, "DESIGN.md"), "# Disk alert\n\n**Objective:** Sets it.\n\n## Threshold\n\n**Objective:** Shows it.\n\n```screen id=home\nThe dialog\n```\n");
        File.WriteAllText(Path.Combine(folder, "prototype.json"), "{\"width\":1280,\"height\":720,\"entry\":\"home\",\"screens\":[]}");
        File.WriteAllText(Path.Combine(folder, "screens", "home.json"), "{\"id\":\"home\"}");
        File.WriteAllBytes(Path.Combine(folder, "renders", "home.png"), [0x89, 0x50, 1]);
        _store.Update(id, r => r with
        {
            Current = number,
            Versions = number,
            Why = "v" + number,
            History = [.. r.History, new DesignVersionRow { Number = number, Why = "v" + number, At = new DateTimeOffset(2026, 11, 19, 9, number, 0, TimeSpan.Zero) }],
        });
    }

    /// <summary>A design published at <paramref name="version"/> with its issue made; the issue's number.</summary>
    private async Task<(string Id, int Issue)> DesignAsync(string title, int version = 1)
    {
        var design = _store.New("pilot", title, "brief");
        for (var v = 1; v <= version; v++)
        {
            Published(design.Id, v);
        }
        await _sync.SyncAsync(design.Id, Ct);
        _github.IssueComments.Clear();
        return (design.Id, _store.Find(design.Id)!.IssueNumber!.Value);
    }

    private static FlightRow Flight(string code, FlightState state, params (string Id, int Version)[] designs) => new()
    {
        Code = code, Repo = "pilot", RepoPath = "/r", Issue = 5, Title = "Build it", Branch = $"flight/{code.ToLowerInvariant()}", Worktree = "/nowhere", Base = "main",
        State = state, CreatedAt = DateTimeOffset.UnixEpoch, PullRequest = 77, PullRequestUrl = "https://github.com/SharpAstro/pilot/pull/77",
        Designs = [.. designs.Select(d => new FlightDesignRow { Id = d.Id, Version = d.Version, Sha256 = "ab" + d.Version + "cd" })],
    };

    [Fact]
    public async Task ThePrBodyOfAFlightWithTwoDesignsHoldsBothWithVersionHashAndLinks()
    {
        var (a, issueA) = await DesignAsync("Disk alert");
        var (b, issueB) = await DesignAsync("Rings", 2);
        var flight = Flight("PIL9", FlightState.Flying, (a, 1), (b, 2));

        var body = LegCommander.PrBody(flight, [], _record.Section(flight));

        body.ShouldContain("## Designs");
        body.ShouldContain($"**{a}** Disk alert, built as v1, prototype sha256 `ab1cd`, design issue SharpAstro/pilot#{issueA}");
        body.ShouldContain($"**{b}** Rings, built as v2, prototype sha256 `ab2cd`, design issue SharpAstro/pilot#{issueB}");
        body.ShouldContain("Closes");
    }

    [Fact]
    public async Task ALaterPushRewritesTheSectionOnlyWhenItChanged()
    {
        var (a, _) = await DesignAsync("Disk alert");
        var flight = Flight("PIL9", FlightState.Flying, (a, 1));
        _github.PrBodies[77] = LegCommander.PrBody(flight, [], _record.Section(flight));

        await _record.OpenedAsync(flight, 77, flight.PullRequestUrl, Ct);
        _github.PrBodyEdits.ShouldBeEmpty();

        _store.Update(a, r => r with { Title = "Disk alerts" });
        await _record.OpenedAsync(flight, 77, flight.PullRequestUrl, Ct);

        _github.PrBodyEdits.Count.ShouldBe(1);
        _github.PrBodies[77].ShouldContain("Disk alerts");
        _github.PrBodies[77].ShouldContain("Closes");
    }

    [Fact]
    public async Task EachDesignIssueGetsTheOpeningCommentOnceNotOncePerPushAndARetryAfterAFailedCallDoesNotDoubleIt()
    {
        var (a, issueA) = await DesignAsync("Disk alert");
        var (b, issueB) = await DesignAsync("Rings");
        var flight = Flight("PIL9", FlightState.Flying, (a, 1), (b, 1));

        // The first call fails for both: nothing is remembered.
        _github.CommentFails = "rate limited";
        await _record.OpenedAsync(flight, 77, flight.PullRequestUrl, Ct);
        _github.IssueComments.ShouldBeEmpty();
        _store.Find(a)!.Record.ShouldBeEmpty();

        _github.CommentFails = null;
        await _record.OpenedAsync(flight, 77, flight.PullRequestUrl, Ct);
        await _record.OpenedAsync(flight, 77, flight.PullRequestUrl, Ct);
        await _record.OpenedAsync(flight, 77, flight.PullRequestUrl, Ct);

        _github.IssueComments.OrderBy(c => c.Number).ToArray().ShouldBe(
        [
            .. new[]
            {
                (issueA, "PIL9 builds v1 of this design: https://github.com/SharpAstro/pilot/pull/77"),
                (issueB, "PIL9 builds v1 of this design: https://github.com/SharpAstro/pilot/pull/77"),
            }.OrderBy(c => c.Item1),
        ]);
    }

    [Fact]
    public async Task OnMergeEachDesignIssueIsClosedWithTheCommentAndItsLockLabelsAreGone()
    {
        var (a, issueA) = await DesignAsync("Disk alert");
        _github.SetIssueLabels(issueA, "pilot: design", "pilot: editing", "pilot node: DE1");
        var flight = Flight("PIL9", FlightState.Landed, (a, 1));

        await _record.LandedAsync(flight, 77, "abc1234", Ct);

        _github.ClosedIssues.ToArray().ShouldBe([(issueA, "Built as v1 by PIL9 in #77, merged at abc1234")]);
        _github.IssueLabels[issueA].Keys.ShouldNotContain("pilot: editing");
        _github.IssueLabels[issueA].Keys.ShouldNotContain("pilot node: DE1");
        _store.Find(a)!.Record.Single().Landed.ShouldBe("closed");
    }

    [Fact]
    public async Task AFailedCloseIsRetriedByTheSweepAndNeverDoublesTheComment()
    {
        var (a, issueA) = await DesignAsync("Disk alert");
        var flight = Flight("PIL9", FlightState.Landed, (a, 1));

        _github.CloseIssueFails = "502";
        await _record.LandedAsync(flight, 77, "abc1234", Ct);
        _github.ClosedIssues.ShouldBeEmpty();
        _store.Find(a)!.Record.Single().Landed.ShouldBeNull();

        _github.CloseIssueFails = null;
        await _record.SweepAsync(Ct);
        await _record.SweepAsync(Ct);

        _github.ClosedIssues.Count.ShouldBe(1);
        _github.ClosedIssues.Single().Number.ShouldBe(issueA);
    }

    [Fact]
    public async Task ADesignCarriedByTwoFlightsClosesAfterTheSecondLands()
    {
        var (a, issueA) = await DesignAsync("Disk alert");
        var first = Flight("PIL9", FlightState.Landed, (a, 1));
        var second = Flight("PIL10", FlightState.Flying, (a, 1)) with { PullRequest = 78 };
        _state.PutFlight(first);
        _state.PutFlight(second);

        await _record.LandedAsync(first, 77, "abc1234", Ct);

        _github.ClosedIssues.ShouldBeEmpty();
        _github.IssueComments.Single().Body.ShouldContain("PIL10 still builds this design");

        _state.PutFlight(second with { State = FlightState.Landed });
        await _record.LandedAsync(second, 78, "def5678", Ct);

        _github.ClosedIssues.ToArray().ShouldBe([(issueA, "Built as v1 by PIL10 in #78, merged at def5678")]);
    }

    [Fact]
    public async Task AnOlderVersionBuiltLeavesTheIssueOpenWithACommentSayingWhereTheDesignIs()
    {
        var (a, issueA) = await DesignAsync("Disk alert", 3);
        Published(a, 4);
        var flight = Flight("PIL9", FlightState.Landed, (a, 3));

        await _record.LandedAsync(flight, 77, "abc1234", Ct);

        _github.ClosedIssues.ShouldBeEmpty();
        _github.IssueComments.Single().Body.ShouldContain("built v3; the design is at v4");
        _store.Find(a)!.Record.Single().Landed.ShouldBe("commented");
        issueA.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ANewVersionOfAClosedDesignReopensItsIssueWithTheVersionNotBuiltYet()
    {
        var (a, issueA) = await DesignAsync("Disk alert");
        var closed = (await _github.IssueNowAsync(_repo, issueA, Ct))! with { Open = false };
        _github.MilestoneIssuesOf[Path.GetFullPath(_repo)][issueA] = closed;
        Published(a, 2);

        await _sync.SyncAsync(a, Ct);

        var reopened = _github.Reopened.Single();
        reopened.Number.ShouldBe(issueA);
        reopened.Comment.ShouldContain("v2");
        reopened.Comment.ShouldContain("not built yet");
        (await _github.IssueNowAsync(_repo, issueA, Ct))!.Open.ShouldBeTrue();
    }
}
