using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1083: <c>pilot flight archive</c> (a code, <c>--landed</c>, <c>--undo</c>) and the reads of the archive, <c>pilot status --archived</c> and
/// <c>pilot status &lt;code&gt;</c> on an archived code, against an in-memory node.
/// </summary>
public sealed class ArchivedFlightsCliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct, git: new BranchGone());

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    /// <summary>Every branch is gone, so a landed flight with nothing open can be archived.</summary>
    private sealed class BranchGone : IGit
    {
        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) => Task.FromResult(new GitRun(1, "", ""));
    }

    private static FlightRow Flight(string code, FlightState state = FlightState.Landed, int hour = 0) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Title = $"Title of {code}", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/nowhere/{code}", Base = "main", State = state, CreatedAt = Start, PullRequest = int.Parse(code[3..]) + 1, PullRequestUrl = $"https://example.test/pull/{int.Parse(code[3..]) + 1}",
        LandedAt = state == FlightState.Landed ? Start.AddHours(hour) : null,
        Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "reviewer", StartedAt = Start },
        ReviewRounds = [],
    };

    private void Land(string code, int hour = 0) => _node.State.PutFlight(Flight(code, hour: hour));

    private async Task<(int Exit, string Output)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString() + error.ToString());
    }

    [Fact]
    public async Task ArchivingALandedFlightTakesItOutOfStatusAll_AndUndoBringsItBack()
    {
        Land("TIA1001");

        var (exit, output) = await Pilot("flight", "archive", "TIA1001");
        exit.ShouldBe(0, output);
        output.ShouldContain("ok: archive-flight");
        (await Pilot("status", "--all", "--output-format", "plain")).Output.ShouldNotContain("TIA1001");

        var (undoExit, undone) = await Pilot("flight", "archive", "TIA1001", "--undo");
        undoExit.ShouldBe(0, undone);
        undone.ShouldContain("is back in the flights");
        (await Pilot("status", "--all", "--output-format", "plain")).Output.ShouldContain("TIA1001");
    }

    [Fact]
    public async Task LandedArchivesEveryEligibleFlightAndNamesTheOnesItLeftAndWhy()
    {
        Land("TIA1001", 1);
        Land("TIA1002", 2);
        Land("TIA1003", 3);
        _node.State.PutFlight(Flight("TIA1004", FlightState.Flying));
        _node.State.RaiseCard(CardBin.Ring, "needs-you", Verbs.FlightTarget("TIA1003"), "TIA1003 needs you", "look");

        var (exit, output) = await Pilot("flight", "archive", "--landed");

        exit.ShouldBe(0, output);
        output.ShouldContain("archived TIA1001");
        output.ShouldContain("archived TIA1002");
        output.ShouldContain("left TIA1003:");
        output.ShouldContain("open about it");
        output.ShouldNotContain("TIA1004");
        output.ShouldContain("2 of 3 landed flights archived");
        var status = (await Pilot("status", "--all", "--output-format", "plain")).Output;
        status.ShouldContain("TIA1003");
        status.ShouldContain("TIA1004");
        status.ShouldNotContain("TIA1001");
    }

    [Fact]
    public async Task ArchiveWithNeitherACodeNorLandedIsAUsageError()
    {
        var (exit, output) = await Pilot("flight", "archive");
        exit.ShouldNotBe(0);
        output.ShouldContain("--landed");
    }

    [Fact]
    public async Task StatusArchivedListsThemPagedWithCodeTitlePrAndLandedDateAndHowMany()
    {
        for (var i = 0; i < 22; i++)
        {
            Land($"TIA{1001 + i}", i);
        }
        (await Pilot("flight", "archive", "--landed")).Output.ShouldContain("22 of 22 landed flights archived");

        var (_, first) = await Pilot("status", "--archived", "--output-format", "plain");
        first.ShouldContain("TIA1022  Title of TIA1022  (tianwen, PR #1023, landed 2026-11-02)");
        first.ShouldContain("22 archived flights, 1 to 20; next page: pilot status --archived --page 2");
        first.ShouldNotContain("TIA1001  ");
        first.IndexOf("TIA1022", StringComparison.Ordinal).ShouldBeLessThan(first.IndexOf("TIA1021", StringComparison.Ordinal));

        var (_, second) = await Pilot("status", "--archived", "--page", "2", "--output-format", "plain");
        second.ShouldContain("TIA1001  Title of TIA1001");
        second.ShouldContain("22 archived flights, 21 to 22");
        second.ShouldNotContain("next page");

        var (_, markdown) = await Pilot("status", "--archived", "--output-format", "markdown");
        markdown.ShouldContain("| Code | Title | PR | Landed |");
        markdown.ShouldContain("| TIA1022 | Title of TIA1022 | [#1023](https://example.test/pull/1023) | 2026-11-02 |");
        markdown.ShouldContain("22 archived flights, 1 to 20");
    }

    [Fact]
    public async Task StatusArchivedWithNoneSaysSo()
    {
        (await Pilot("status", "--archived", "--output-format", "plain")).Output.ShouldContain("No archived flights.");
    }

    [Fact]
    public async Task StatusOfAnArchivedCodePrintsTheFlightMarkedArchived()
    {
        Land("TIA1001");
        _node.State.PutLeg(new LegRow { Flight = "TIA1001", Name = "only", Order = 1, Goal = "goal", DoneWhen = "done", State = LegState.Done });
        await Pilot("flight", "archive", "TIA1001");

        var (exit, plain) = await Pilot("status", "TIA1001", "--output-format", "plain");
        exit.ShouldBe(0, plain);
        plain.ShouldContain("archived  archived ");
        plain.ShouldContain("TIA1001  Title of TIA1001");
        plain.ShouldContain("1. only");
        plain.ShouldContain("--undo brings it back");

        var (_, markdown) = await Pilot("status", "tia1001", "--output-format", "markdown");
        markdown.ShouldContain("_archived: archived ");
        markdown.ShouldContain("### TIA1001");

        var (_, live) = await Pilot("status", "TIA9999", "--output-format", "plain");
        live.ShouldNotContain("archived  archived");
    }

    [Fact]
    public async Task TheLandedHiddenLineCountsOnlyFlightsStillInTheState()
    {
        Land("TIA1001", 1);
        Land("TIA1002", 2);
        (await Pilot("status", "--output-format", "plain")).Output.ShouldContain("2 landed flights not shown: pilot status --all");

        await Pilot("flight", "archive", "TIA1001");

        var (_, output) = await Pilot("status", "--output-format", "plain");
        output.ShouldContain("1 landed flight not shown: pilot status --all");
        output.ShouldNotContain("TIA1001");
        _node.State.Flights().Count(f => f.State == FlightState.Landed).ShouldBe(1);
    }
}
