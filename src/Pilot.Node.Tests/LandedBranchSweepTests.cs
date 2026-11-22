using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1101: the sweep that deletes the branches of flights that landed before the node did it at landing, on real git (a temp repo and a
/// bare origin): which refs are safe to delete is git's own answer, and the one-push-per-batch claim counts real git calls.
/// </summary>
[Trait("Category", "Slow")] // real git processes: a sweep of 30 branches runs about 60 git commands, each 100 to 400 ms on Windows
public sealed class LandedBranchSweepTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2lbs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _repo;
    private readonly string _origin;
    private readonly FileJournal _journal;
    private readonly NodeState _state;
    private readonly CountingGit _git = new(new GitCli());
    private readonly FakeGitHub _github = new();
    private readonly Dictionary<string, string> _heads = [];

    public LandedBranchSweepTests()
    {
        _repo = Path.Combine(_root, "repo");
        _origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_origin);
        Git(_origin, "init", "--bare", "-b", "main");
        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "t@example.com");
        Git(_repo, "config", "user.name", "t");
        Git(_repo, "config", "commit.gpgsign", "false");
        Git(_repo, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "a");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "first");
        Git(_repo, "push", "origin", "main");
        _journal = new FileJournal(Path.Combine(_root, "store"));
        _state = new NodeState(_journal, new FakeTime(() => _skew)) { Archive = new FlightArchive(_root) };
    }

    public void Dispose()
    {
        _journal.Dispose();
        TestDirs.DeleteTree(_root);
    }

    /// <summary>How far the clock has moved past <see cref="Now"/>; a test that needs a later refusal sets it.</summary>
    private TimeSpan _skew;

    private sealed class FakeTime(Func<TimeSpan> skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now + skew();
    }

    private string Git(string cwd, params string[] args)
    {
        var run = new GitCli().RunAsync(args, cwd, Ct).GetAwaiter().GetResult();
        run.Ok.ShouldBeTrue($"git {string.Join(' ', args)}: {run.Error}");
        return run.Output.Trim();
    }

    /// <summary>A branch with one commit past main, pushed unless told not to; returns the head it was merged at.</summary>
    private string MakeBranch(string code, bool push = true)
    {
        var branch = $"flight/{code.ToLowerInvariant()}";
        Git(_repo, "checkout", "-q", "-b", branch, "main");
        File.WriteAllText(Path.Combine(_repo, code + ".txt"), code);
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "work " + code);
        var head = Git(_repo, "rev-parse", "HEAD");
        if (push)
        {
            Git(_repo, "push", "origin", branch);
        }
        Git(_repo, "checkout", "-q", "main");
        _heads[code] = head;
        return head;
    }

    private void CommitOn(string code, string message, bool push)
    {
        Git(_repo, "checkout", "-q", $"flight/{code.ToLowerInvariant()}");
        File.WriteAllText(Path.Combine(_repo, message.Replace(' ', '_') + ".txt"), message);
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", message);
        if (push)
        {
            Git(_repo, "push", "origin", $"flight/{code.ToLowerInvariant()}");
        }
        Git(_repo, "checkout", "-q", "main");
    }

    private bool LocalHas(string code) => Git(_repo, "branch", "--list", $"flight/{code.ToLowerInvariant()}").Length > 0;

    private bool OriginHas(string code) => Git(_repo, "ls-remote", "--heads", "origin", $"flight/{code.ToLowerInvariant()}").Length > 0;

    private FlightRow Row(string code, string? head = null, string? branch = null) => new()
    {
        Code = code, Repo = "repo", RepoPath = _repo, Issue = int.Parse(code[3..]), Branch = branch ?? $"flight/{code.ToLowerInvariant()}",
        Worktree = Path.Combine(_root, "planes", code), Base = "origin/main", State = FlightState.Landed, CreatedAt = Now.AddDays(-30),
        LandedAt = Now.AddDays(-20), LandedHead = head, ReviewRounds = [],
    };

    private void Put(FlightRow row, bool archived = false)
    {
        _state.PutFlight(row);
        if (archived)
        {
            _state.ArchiveFlight(row.Code);
        }
    }

    private LandedBranchSweep Sweeper(IGit? git = null, int batch = 5, Func<bool>? enabled = null) =>
        new(_state, new LandedBranches(git ?? _git), _github, new FakeTime(() => _skew)) { Batch = batch, Pause = TimeSpan.Zero, StartDelay = TimeSpan.FromHours(1), Enabled = enabled ?? (() => true) };

    private IEnumerable<CardRow> Summaries() => _state.Cards().Where(c => c.Kind == LandedBranchSweep.SummaryKind);

    private FlightRow Live(string code) => _state.Flights().Single(f => f.Code == code);

    private FlightRow Archived(string code) => _state.Archive!.Read(code)!.Flight;

    [Fact]
    public async Task ThirtyLandedRowsDeleteExactlyTheSafeOnesAndRaiseOneLineWithCountsByReason()
    {
        var deleted = new List<string>();
        var kept = new List<string>();

        // PIL1 to 8: live, the merged head recorded; PIL9 to 12: archived, the same.
        for (var i = 1; i <= 12; i++)
        {
            var code = $"PIL{i}";
            Put(Row(code, MakeBranch(code)), archived: i > 8);
            deleted.Add(code);
        }
        // PIL13 (live) and PIL14 (archived): no head on the row, GitHub lists the PR as merged.
        var number = 100;
        foreach (var code in new[] { "PIL13", "PIL14" })
        {
            var head = MakeBranch(code);
            Put(Row(code) with { MergedPullRequest = number }, archived: code == "PIL14");
            _github.MergedPrs[_repo] = [.. (_github.MergedPrs.TryGetValue(_repo, out var listed) ? listed : null) ?? [], GhAnswers.MergedPullRequest(number++, $"flight/{code.ToLowerInvariant()}", head)];
            deleted.Add(code);
        }
        // PIL15: no head, GitHub does not list it.
        MakeBranch("PIL15");
        Put(Row("PIL15"));
        kept.Add("PIL15");
        // PIL16 (live) and PIL17 (archived): the local branch is a commit past the merged head.
        foreach (var code in new[] { "PIL16", "PIL17" })
        {
            Put(Row(code, MakeBranch(code)), archived: code == "PIL17");
            CommitOn(code, "local only work " + code, push: false);
            kept.Add(code);
        }
        // PIL18: origin is a commit past the merged head, the local branch is at it.
        Put(Row("PIL18", MakeBranch("PIL18")));
        CommitOn("PIL18", "origin work", push: true);
        Git(_repo, "branch", "-f", "flight/pil18", _heads["PIL18"]);
        kept.Add("PIL18");
        // PIL19 and PIL20: never pushed, so no remote branch.
        foreach (var code in new[] { "PIL19", "PIL20" })
        {
            Put(Row(code, MakeBranch(code, push: false)));
            deleted.Add(code);
        }
        // PIL21: its branch is gone everywhere already.
        Put(Row("PIL21", Git(_repo, "rev-parse", "main")));
        deleted.Add("PIL21");
        // PIL22: folded into PIL1 with a branch PIL1's merge holds; PIL23: folded into a flight that has not landed.
        var pil1 = _heads["PIL1"];
        Git(_repo, "branch", "flight/pil22", pil1);
        Git(_repo, "push", "origin", "flight/pil22");
        Put(Row("PIL22") with { FoldedInto = "PIL1" });
        deleted.Add("PIL22");
        MakeBranch("PIL23");
        Put(Row("PIL23") with { FoldedInto = "PIL99" });
        kept.Add("PIL23");
        // PIL24 and PIL25: a session of the flight is live.
        foreach (var code in new[] { "PIL24", "PIL25" })
        {
            Put(Row(code, MakeBranch(code)));
            _state.PutSession(new SessionRow
            {
                Id = "s" + code, SessionId = $"{code}-0000-4000-8000-000000000000", Name = $"de1-{code}-worker", Kind = SessionKind.Background, Cwd = "/work",
                Role = SessionRole.Worker, Flight = code, ContextTokens = 1, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = Now,
            });
            kept.Add(code);
        }
        // PIL26 and PIL27: the plane folder is still on disk.
        foreach (var code in new[] { "PIL26", "PIL27" })
        {
            Put(Row(code, MakeBranch(code)));
            Directory.CreateDirectory(Path.Combine(_root, "planes", code));
            kept.Add(code);
        }
        // PIL28: already marked deleted (its branch is left in place to show that nobody looks).
        Put(Row("PIL28", MakeBranch("PIL28")) with { BranchDeletedAt = Now.AddDays(-1) });
        // PIL29: checked out in another worktree. PIL30: a row whose branch is not its own flight branch.
        Put(Row("PIL29", MakeBranch("PIL29")));
        Git(_repo, "worktree", "add", Path.Combine(_root, "elsewhere"), "flight/pil29");
        kept.Add("PIL29");
        Put(Row("PIL30", Git(_repo, "rev-parse", "main"), branch: "main"));
        kept.Add("PIL30");

        var sweeper = Sweeper();
        (await sweeper.SweepAsync(Ct)).ShouldBe(deleted.Count);

        foreach (var code in deleted)
        {
            LocalHas(code).ShouldBeFalse($"{code} local");
            OriginHas(code).ShouldBeFalse($"{code} origin");
        }
        foreach (var code in kept.Where(c => c != "PIL30"))
        {
            // A kept branch is untouched, wherever it lived.
            if (code is not "PIL19" and not "PIL20")
            {
                (LocalHas(code) || OriginHas(code)).ShouldBeTrue($"{code} kept");
            }
        }
        LocalHas("PIL28").ShouldBeTrue();
        OriginHas("PIL28").ShouldBeTrue();
        Git(_repo, "rev-parse", "--verify", "main").ShouldNotBeEmpty();

        var line = Summaries().ShouldHaveSingleItem();
        line.Kind.ShouldBe(LandedBranchSweep.SummaryKind);
        line.Title.ShouldBe("branches: deleted 18, kept 11: 2 live-session, 2 local-ahead, 2 plane-folder, 1 checked-out, 1 folded-pending, 1 no-merged-head, 1 not-flight-branch, 1 origin-ahead");

        // The outcome is on the live row, and in the file of an archived flight and its index.
        Live("PIL1").BranchDeletedAt.ShouldBe(Now);
        Live("PIL13").LandedHead.ShouldBe(_heads["PIL13"]);
        Archived("PIL9").BranchDeletedAt.ShouldBe(Now);
        _state.ArchivedRow("PIL9")!.BranchDeletedAt.ShouldBe(Now);
        Archived("PIL14").LandedHead.ShouldBe(_heads["PIL14"]);
        Archived("PIL17").BranchDeletedAt.ShouldBeNull();
        Archived("PIL17").BranchKept.ShouldNotBeNull().ShouldContain("not the merged head");
        Live("PIL16").BranchKept.ShouldNotBeNull();
        Live("PIL28").BranchDeletedAt.ShouldBe(Now.AddDays(-1));

        // GitHub was read once for the repo, whatever the number of rows without a head.
        _github.MergedPrListings.ShouldBe(1);
        _git.Count("push").ShouldBeLessThanOrEqualTo(5);

        // A second sweep deletes nothing more and says what is left once; a third says nothing, as the same line is not raised again.
        (await sweeper.SweepAsync(Ct)).ShouldBe(0);
        Summaries().Count().ShouldBe(2);
        (await sweeper.SweepAsync(Ct)).ShouldBe(0);
        Summaries().Count().ShouldBe(2);
    }

    [Fact]
    public async Task TwelveSafeBranchesOfOneRepoCostOnePushPerBatchOfFive()
    {
        for (var i = 1; i <= 12; i++)
        {
            Put(Row($"PIL{i}", MakeBranch($"PIL{i}")));
        }

        (await Sweeper().SweepAsync(Ct)).ShouldBe(12);

        _git.Count("push").ShouldBe(3);
        _git.Count("branch").ShouldBe(3);
        Enumerable.Range(1, 12).ShouldAllBe(i => !OriginHas($"PIL{i}") && !LocalHas($"PIL{i}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusedPushIsTriedOnceThenLeftUntilAPersonAnswersAndRetryPushesExactlyOnceMore(bool archived)
    {
        for (var i = 1; i <= 3; i++)
        {
            Put(Row($"PIL{i}", MakeBranch($"PIL{i}")), archived);
        }
        FlightRow Of(int i) => archived ? Archived($"PIL{i}") : Live($"PIL{i}");
        var failing = new CountingGit(new GitCli()) { FailPush = true };

        (await Sweeper(failing).SweepAsync(Ct)).ShouldBe(0);

        failing.Count("push").ShouldBe(1);
        Enumerable.Range(1, 3).All(i => OriginHas($"PIL{i}") && LocalHas($"PIL{i}") && Of(i).BranchDeletedAt == null).ShouldBeTrue();
        Enumerable.Range(1, 3).All(i => Of(i).BranchDeleteFailed!.Contains("remote: refused") && Of(i).BranchKept == Of(i).BranchDeleteFailed && Of(i).BranchDeleteFailedAt == Now).ShouldBeTrue();
        Summaries().Single().Title.ShouldContain("3 push-failed, left until answered");
        Summaries().Single().Title.ShouldNotContain("tried again next sweep");
        var card = _state.Cards().Single(c => c.Kind == FlightCommander.BranchKeptKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Detail.ShouldNotBeNull().ShouldContain("remote: refused");
        Enumerable.Range(1, 3).ShouldAllBe(i => card.Detail!.Contains($"PIL{i} "));
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.BranchRetry, Verbs.Dismiss]);

        // Two more sweeps push nothing for them.
        await Sweeper(failing).SweepAsync(Ct);
        await Sweeper(failing).SweepAsync(Ct);
        failing.Count("push").ShouldBe(1);
        _state.Cards().Count(c => c.Kind == FlightCommander.BranchKeptKind).ShouldBe(1);

        // The retry choice clears the record and pushes nothing itself.
        var retry = card.Choices[0];
        var commander = new FlightCommander(_state, new Planes(failing), new FakeTime(() => _skew));
        var result = await commander.RunAsync(new CommandRequest { Verb = retry.Verb, Target = retry.Target, Args = retry.Args, Who = "test" }, Ct);
        result.Error.ShouldBeNull();
        failing.Count("push").ShouldBe(1);
        Enumerable.Range(1, 3).All(i => Of(i).BranchDeleteFailed == null && Of(i).BranchDeleteFailedAt == null).ShouldBeTrue();

        // Meanwhile PIL1 was deleted on origin by hand: its retry counts as deleted.
        Git(_repo, "push", "origin", ":refs/heads/flight/pil1");
        failing.FailPush = false;
        (await Sweeper(failing).SweepAsync(Ct)).ShouldBe(3);

        failing.Count("push").ShouldBe(2);
        Enumerable.Range(1, 3).All(i => !OriginHas($"PIL{i}") && Of(i).BranchDeletedAt == Now && Of(i).BranchDeleteFailed == null).ShouldBeTrue();
    }

    [Fact]
    public async Task LeavingTheRefusedBranchesKeepsEverySweepFromPushingForThem()
    {
        Put(Row("PIL1", MakeBranch("PIL1")));
        var failing = new CountingGit(new GitCli()) { FailPush = true };
        await Sweeper(failing).SweepAsync(Ct);
        var card = _state.Cards().Single(c => c.Kind == FlightCommander.BranchKeptKind);
        _state.CloseCard(card.Id, "leave them", "test");

        failing.FailPush = false;
        await Sweeper(failing).SweepAsync(Ct);

        failing.Count("push").ShouldBe(1);
        OriginHas("PIL1").ShouldBeTrue();
        Live("PIL1").BranchDeleteFailed.ShouldNotBeNull();
    }

    [Fact]
    public async Task ALaterRefusalInTheRepoWhileTheCardIsOpenIsNamedOnOneReplacingCardWithEveryCodeInRetry()
    {
        Put(Row("PIL1", MakeBranch("PIL1")));
        var failing = new CountingGit(new GitCli()) { FailPush = true };
        await Sweeper(failing).SweepAsync(Ct);
        var first = _state.Cards().Single(c => c.Kind == FlightCommander.BranchKeptKind);

        Put(Row("PIL2", MakeBranch("PIL2")), archived: true);
        await Sweeper(failing).SweepAsync(Ct);

        var open = _state.Cards(openOnly: true).Where(c => c.Kind == FlightCommander.BranchKeptKind).ToList();
        var card = open.ShouldHaveSingleItem();
        card.Id.ShouldNotBe(first.Id);
        card.Detail.ShouldNotBeNull().ShouldContain("PIL1 ");
        card.Detail.ShouldContain("PIL2 ");
        card.Detail.ShouldContain("remote: refused");
        card.Detail.ShouldContain("pilot flight branch-retry");
        var retry = card.Choices[0];
        new[] { Verbs.FlightCodeOf(retry.Target)! }.Concat(retry.Args).Order().ShouldBe(["PIL1", "PIL2"]);
        _state.Cards().Single(c => c.Id == first.Id).State.ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task ALaterRefusalAfterLeavingThemRaisesAFreshCardForTheNewBranchesOnly()
    {
        Put(Row("PIL1", MakeBranch("PIL1")));
        Put(Row("PIL2", MakeBranch("PIL2")));
        var failing = new CountingGit(new GitCli()) { FailPush = true };
        await Sweeper(failing).SweepAsync(Ct);
        _state.CloseCard(_state.Cards(openOnly: true).Single(c => c.Kind == FlightCommander.BranchKeptKind).Id, "leave them", "test");

        Put(Row("PIL3", MakeBranch("PIL3")));
        Put(Row("PIL4", MakeBranch("PIL4")));
        await Sweeper(failing).SweepAsync(Ct);

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == FlightCommander.BranchKeptKind);
        card.Detail.ShouldNotBeNull().ShouldContain("PIL3 ");
        card.Detail.ShouldContain("PIL4 ");
        card.Detail.ShouldNotContain("PIL1 ");
        var retry = card.Choices[0];
        new[] { Verbs.FlightCodeOf(retry.Target)! }.Concat(retry.Args).Order().ShouldBe(["PIL3", "PIL4"]);
    }

    [Fact]
    public async Task ARefusalAfterAHandRetryOfADismissedCardRaisesAFreshCardInsideTheDismissalHold()
    {
        Put(Row("PIL1", MakeBranch("PIL1")));
        Put(Row("PIL2", MakeBranch("PIL2")));
        var failing = new CountingGit(new GitCli()) { FailPush = true };
        await Sweeper(failing).SweepAsync(Ct);
        var first = _state.Cards(openOnly: true).Single(c => c.Kind == FlightCommander.BranchKeptKind);
        _state.CloseCard(first.Id, "dismissed by a person", "test");

        // The same two branches are retried by hand and refused again an hour later, well inside the day the dismissal holds.
        foreach (var code in new[] { "PIL1", "PIL2" })
        {
            _state.UpdateFlight(code, f => f with { BranchDeleteFailed = null, BranchDeleteFailedAt = null, BranchKept = null });
        }
        _skew = TimeSpan.FromHours(1);
        await Sweeper(failing).SweepAsync(Ct);

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == FlightCommander.BranchKeptKind);
        card.Id.ShouldNotBe(first.Id);
        card.Detail.ShouldNotBeNull().ShouldContain("PIL1 ");
        card.Detail.ShouldContain("PIL2 ");
    }

    [Theory]
    [InlineData("ls-remote")]
    [InlineData("for-each-ref")]
    [InlineData("worktree")]
    public async Task AFailedGitReadRecordsNothingAndTheNextSweepDeletesBoth(string verb)
    {
        for (var i = 1; i <= 2; i++)
        {
            Put(Row($"PIL{i}", MakeBranch($"PIL{i}")));
        }
        var git = new CountingGit(new GitCli()) { FailVerb = verb };

        (await Sweeper(git).SweepAsync(Ct)).ShouldBe(0);

        Enumerable.Range(1, 2).ShouldAllBe(i => OriginHas($"PIL{i}") && LocalHas($"PIL{i}") && Live($"PIL{i}").BranchDeletedAt == null);
        git.Count("push").ShouldBe(0);
        Summaries().Single().Title.ShouldContain("2 tried again next sweep: 2 read-failed");

        git.FailVerb = null;
        (await Sweeper(git).SweepAsync(Ct)).ShouldBe(2);
        Enumerable.Range(1, 2).ShouldAllBe(i => !OriginHas($"PIL{i}") && !LocalHas($"PIL{i}") && Live($"PIL{i}").BranchDeletedAt == Now);
    }

    [Fact]
    public async Task ThePrIsAskedForByNumberWhenTheListLacksIt()
    {
        var merged = Row("PIL1") with { Repo = "o/repo", MergedPullRequest = 7 };
        var kept = Row("PIL2") with { Repo = "o/repo", MergedPullRequest = 8 };
        var other = Row("PIL3") with { Repo = "o/repo", MergedPullRequest = 9 };
        var head1 = MakeBranch("PIL1");
        MakeBranch("PIL2");
        MakeBranch("PIL3");
        Put(merged);
        Put(kept);
        Put(other);
        // As gh pr view answers: the state says merged, mergedAt is not read so it stays null.
        _github.Prs["o/repo#7"] = GhAnswers.PullRequestByNumber(state: "merged", number: 7, head: head1, headRef: "flight/pil1");
        _github.Prs["o/repo#8"] = GhAnswers.PullRequestByNumber(state: "closed", number: 8, head: "x", headRef: "flight/pil2");
        _github.Prs["o/repo#9"] = GhAnswers.PullRequestByNumber(state: "merged", number: 9, head: "x", headRef: "flight/other");

        (await Sweeper().SweepAsync(Ct)).ShouldBe(1);

        OriginHas("PIL1").ShouldBeFalse();
        OriginHas("PIL2").ShouldBeTrue();
        OriginHas("PIL3").ShouldBeTrue();
    }

    [Fact]
    public async Task WithTheSettingOffNothingIsDeletedAndGitIsNotRun()
    {
        Put(Row("PIL1", MakeBranch("PIL1")));
        var before = _git.Total;

        (await Sweeper(enabled: () => false).SweepAsync(Ct)).ShouldBe(0);

        _git.Total.ShouldBe(before);
        OriginHas("PIL1").ShouldBeTrue();
        LocalHas("PIL1").ShouldBeTrue();
        Live("PIL1").BranchDeletedAt.ShouldBeNull();
        Summaries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightAlreadyMarkedDeletedIsNotLookedAtAgain()
    {
        Put(Row("PIL1", MakeBranch("PIL1")));
        Put(Row("PIL2", MakeBranch("PIL2")), archived: true);
        (await Sweeper().SweepAsync(Ct)).ShouldBe(2);
        var calls = _git.Total;
        var listings = _github.MergedPrListings;

        (await Sweeper().SweepAsync(Ct)).ShouldBe(0);

        _git.Total.ShouldBe(calls);
        _github.MergedPrListings.ShouldBe(listings);
    }

    [Fact]
    public async Task TheFirstSweepWaitsForTheStartDelayAndStartingIsNotHeld()
    {
        Put(Row("PIL1", MakeBranch("PIL1")));
        var before = _git.Total;
        var sweep = Sweeper();

        await sweep.StartAsync(Ct);
        await sweep.StopAsync(Ct);

        _git.Total.ShouldBe(before);
        OriginHas("PIL1").ShouldBeTrue();
    }

    /// <summary>Counts the git verbs run, and can fail a push as a remote that refuses.</summary>
    private sealed class CountingGit(IGit inner) : IGit
    {
        private readonly Dictionary<string, int> _counts = [];

        public bool FailPush { get; set; }

        /// <summary>A verb whose runs fail with a non-zero exit, as a git that cannot reach origin or read its refs.</summary>
        public string? FailVerb { get; set; }

        public int Total => _counts.Values.Sum();

        public int Count(string verb) => _counts.GetValueOrDefault(verb);

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            _counts[args[0]] = Count(args[0]) + 1;
            if (args[0] == FailVerb)
            {
                return Task.FromResult(new GitRun(128, "", "fatal: could not read"));
            }
            return args[0] == "push" && FailPush
                ? Task.FromResult(new GitRun(1, "", "remote: refused"))
                : inner.RunAsync(args, cwd, cancellationToken);
        }
    }
}
