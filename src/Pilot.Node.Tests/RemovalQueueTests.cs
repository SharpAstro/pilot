using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The removal queue (#284): a folder a process holds open is queued, not retried by a card every sweep.</summary>
public sealed class RemovalQueueTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2q-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FakeGitHub _github = new();
    private readonly NodeOptions _options;
    private readonly RemovalQueue _queue;
    private readonly Planes _planes;
    private readonly PrReviews _reviews;
    private readonly string _widgetHead;

    public RemovalQueueTests()
    {
        var (widget, head) = PrOrigins.Make(_root, "widget", 7);
        _widgetHead = head;
        Directory.CreateDirectory(Roots);
        PrOrigins.Git(Roots, "clone", "-q", widget, "widget");
        PrOrigins.Git(WidgetClone, "config", "remote.origin.url", "https://github.com/acme/widget.git");
        PrOrigins.Git(WidgetClone, "config", $"url.{widget.Replace('\\', '/')}.insteadOf", "https://github.com/acme/widget.git");
        _github.Prs["acme/widget#7"] = new PullRequest(7, "OPEN", head, "https://github.com/acme/widget/pull/7", Title: "PR 7", Author: "ada", Base: "main");

        var git = new GitCli();
        _options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1", RepoRootsGiven: [Roots]);
        _queue = new RemovalQueue(RemovalQueue.FileIn(_options.DataFolder), TimeProvider.System);
        _planes = new Planes(git, _queue);
        _reviews = new PrReviews(_state, _workers, _github, git, _planes, new RepoCatalog(_state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            PostsItself = false,
        };
    }

    private string Roots => Path.Combine(_root, "repos");

    private string WidgetClone => Path.Combine(Roots, "widget");

    public void Dispose()
    {
        try
        {
            PrOrigins.Git(WidgetClone, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        PrOrigins.Delete(_root);
    }

    private Task<CommandResult> Run(string verb, string target, string who, params string[] args) =>
        _reviews.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);

    private PrReviewRow Row() => _reviews.Of(new PrRef("acme", "widget", 7)).ShouldNotBeNull();

    private static FileStream Hold(string folder) =>
        new(Path.Combine(folder, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None);

    private async Task<PrReviewRow> StartedAsync()
    {
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        return Row();
    }

    /// <summary>Ends the review as its verdict card closing does: a Post is not what this is about.</summary>
    private async Task EndHeldReviewAsync(PrReviewRow review)
    {
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", review.Reviewer, "comment", "Reads well.")).Error.ShouldBeNull();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.ReadyKind);
        _state.CloseCard(card.Id, "dismissed", "controller");
        await _reviews.SweepAsync(Ct);
    }

    [Fact]
    public void AnEntryIsQueuedOnceByItsFullPathAndAMissingOrBrokenFileIsAnEmptyQueue()
    {
        _queue.Entries().ShouldBeEmpty();
        var folder = Path.Combine(_root, "Held");
        _queue.Add(folder, RemovalQueue.PlaneKind, "PIL1", "a process holds it");
        _queue.Add(folder.ToUpperInvariant() + Path.DirectorySeparatorChar, RemovalQueue.PlaneKind, "PIL1", "again");

        var entry = _queue.Entries().ShouldHaveSingleItem();
        (entry.Path, entry.OwnerKind, entry.Owner, entry.Why, entry.StartsTried).ShouldBe((Path.GetFullPath(folder), "plane", "PIL1", "a process holds it", 0));
        _queue.Contains(folder).ShouldBeTrue();

        File.WriteAllText(RemovalQueue.FileIn(_options.DataFolder), "{ not json");
        _queue.Entries().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewWhoseWorktreeIsHeldClosesAtOnceWithOneHandledLineAndAQueueEntry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var review = await StartedAsync();

        using (Hold(review.Worktree))
        {
            await EndHeldReviewAsync(review);

            Row().State.ShouldBe(PrReviewState.Closed);
            _state.Cards(openOnly: true).ShouldNotContain(c => c.Bin == CardBin.ForYou);
            var line = _state.Cards().Single(c => c.Kind == PrReviews.LeftKind);
            line.Bin.ShouldBe(CardBin.Handled);
            line.Detail.ShouldNotBeNull().ShouldContain($"left {review.Worktree} for the next start: a process holds it");
            var entry = _queue.Entries().ShouldHaveSingleItem();
            (entry.Path, entry.OwnerKind, entry.Owner).ShouldBe((Path.GetFullPath(review.Worktree), "review", "acme/widget#7"));
            entry.Why.ShouldNotBeEmpty();
        }

        // Nothing is tried again by the sweep: no card, no second entry.
        await _reviews.SweepAsync(Ct);
        _queue.Entries().Count.ShouldBe(1);
    }

    [Fact]
    public async Task ANewStartRemovesAQueuedFolderNoLongerHeldAndKeepsOneStillHeldWithItsStartsRaised()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var folder = Path.Combine(_root, "left");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), "x");
        _queue.Add(folder, RemovalQueue.ReviewKind, "acme/widget#7", "held");

        using (Hold(folder))
        {
            await Start().StartingAsync(Ct);
            _queue.Entries().ShouldHaveSingleItem().StartsTried.ShouldBe(1);
            await Start().StartingAsync(Ct);
            _queue.Entries().ShouldHaveSingleItem().StartsTried.ShouldBe(2);
            Directory.Exists(folder).ShouldBeTrue();
        }

        await Start().StartingAsync(Ct);
        Directory.Exists(folder).ShouldBeFalse();
        _queue.Entries().ShouldBeEmpty();

        // A new node on the same data folder reads the same file.
        RemovalQueueStart Start() => new(new RemovalQueue(RemovalQueue.FileIn(_options.DataFolder), TimeProvider.System));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANewPinAtAQueuedHeldPathIsMadeBesideItAndAReleasedOneIsClearedFirst()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var first = await StartedAsync();
        var queued = first.Worktree;

        using (Hold(queued))
        {
            await EndHeldReviewAsync(first);
            (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();

            var beside = Row();
            beside.Worktree.ShouldBe($"{queued}-2");
            Directory.Exists(beside.Worktree).ShouldBeTrue();
            // The reviewer enters the sibling, not the held base worktree.
            _workers.Started.Last().Plane.ShouldBe(Path.GetFileName(beside.Worktree));
            _queue.Contains(queued).ShouldBeTrue();
        }

        // Released: the queued path is tried once more and used again.
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        Row().Worktree.ShouldBe(queued);
        _queue.Contains(queued).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALandedFlightWhosePlaneFolderIsHeldSaysSoOnAHandledLineAndQueuesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var repo = Path.Combine(_root, "tianwen");
        Directory.CreateDirectory(repo);
        PrOrigins.Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        PrOrigins.Git(repo, "add", ".");
        PrOrigins.Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var flights = new FlightCommander(_state, _planes, TimeProvider.System);
        (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["1"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var flight = _state.Flights().Single();

        // An ignored file is held, so git's status (which reads tracked files) still finds the plane clean.
        File.AppendAllText(Path.Combine(repo, ".git", "info", "exclude"), "held.tmp\n");
        File.WriteAllText(Path.Combine(flight.Worktree, "held.tmp"), "x");
        using (new FileStream(Path.Combine(flight.Worktree, "held.tmp"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var land = await flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "tests" }, Ct);
            land.Ok.ShouldBeTrue(land.Error);
        }

        _state.Flights().Single().State.ShouldBe(FlightState.Landed);
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.ForYou && c.Kind == "plane-left");
        _state.Cards().Single(c => c.Kind == "plane-left").Bin.ShouldBe(CardBin.Handled);
        var entry = _queue.Entries().ShouldHaveSingleItem();
        (entry.Path, entry.OwnerKind, entry.Owner).ShouldBe((Path.GetFullPath(flight.Worktree), "plane", flight.Code));
    }

    private string QueuedFolder(RemovalQueue queue)
    {
        var folder = Path.Combine(_root, "idle-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), "x");
        queue.Add(folder, RemovalQueue.ReviewKind, "acme/widget#7", "held");
        return folder;
    }

    [Fact]
    public async Task AnIdleNodeTriesTheQueueAfterTheIdleIntervalAndNotBeforeAndABusyOneNot()
    {
        var time = new Sessions.ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        var queue = new RemovalQueue(RemovalQueue.FileIn(Path.Combine(_root, "idle-data")), time);
        var runner = new RemovalQueueStart(queue, state, time);
        await runner.StartingAsync(Ct);
        var folder = QueuedFolder(queue);

        time.Advance(TimeSpan.FromMinutes(59));
        runner.Look().ShouldBeFalse();
        Directory.Exists(folder).ShouldBeTrue();

        // A working session keeps it from trying, however long.
        state.PutSession(new SessionRow
        {
            Id = "w0000001", SessionId = "w0000001-0000-4000-8000-000000000000", Name = "du1-w", Kind = SessionKind.Background, Cwd = "/r/w",
            Role = SessionRole.Worker, State = SessionState.Working, StartedAt = time.GetUtcNow(), LastResponseAt = time.GetUtcNow(),
            ContextTokens = 0, ContextWindow = 1_000_000, SpendWeighted = 0,
        });
        time.Advance(TimeSpan.FromHours(2));
        runner.Look().ShouldBeFalse();
        Directory.Exists(folder).ShouldBeTrue();

        state.PutSession(state.Sessions().Single() with { State = SessionState.Idle });
        time.Advance(TimeSpan.FromMinutes(59));
        runner.Look().ShouldBeFalse();
        time.Advance(TimeSpan.FromMinutes(2));
        runner.Look().ShouldBeTrue();
        Directory.Exists(folder).ShouldBeFalse();
        queue.Entries().ShouldBeEmpty();

        // At most once per interval.
        var again = QueuedFolder(queue);
        time.Advance(TimeSpan.FromMinutes(30));
        runner.Look().ShouldBeFalse();
        Directory.Exists(again).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFolderHeldPastThreeStartsRingsOnceNamingTheHolderAndTheCardClosesOnceItIsReleased()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var time = new Sessions.ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        var queue = new RemovalQueue(RemovalQueue.FileIn(Path.Combine(_root, "ring-data")), time);
        var folder = QueuedFolder(queue);
        RemovalQueueStart Start() => new(queue, state, time);

        using (Hold(folder))
        {
            for (var start = 1; start <= 3; start++)
            {
                await Start().StartingAsync(Ct);
                state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == RemovalQueueStart.Kind);
            }

            await Start().StartingAsync(Ct);
            var card = state.Cards(openOnly: true).Where(c => c.Kind == RemovalQueueStart.Kind).ShouldHaveSingleItem();
            card.Bin.ShouldBe(CardBin.ForYou);
            var detail = card.Detail.ShouldNotBeNull();
            detail.ShouldContain(Path.GetFullPath(folder));
            detail.ShouldContain("4 starts tried");
            detail.ShouldContain($"(pid {Environment.ProcessId})");

            await Start().StartingAsync(Ct);
            state.Cards().Count(c => c.Kind == RemovalQueueStart.Kind).ShouldBe(1);
        }

        await Start().StartingAsync(Ct);
        queue.Entries().ShouldBeEmpty();
        state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == RemovalQueueStart.Kind);
    }

    [Fact]
    public async Task AFolderQueuedForMoreThanADayRingsWithHolderUnknownWhereNoneCanBeNamed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var time = new Sessions.ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        var queue = new RemovalQueue(RemovalQueue.FileIn(Path.Combine(_root, "old-data")), time);
        var folder = QueuedFolder(queue);

        using (Hold(folder))
        {
            var runner = new RemovalQueueStart(queue, state, time) { Holders = _ => [] };
            await runner.StartingAsync(Ct);
            state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == RemovalQueueStart.Kind);
            time.Advance(TimeSpan.FromHours(25));
            await runner.StartingAsync(Ct);
            state.Cards(openOnly: true).Where(c => c.Kind == RemovalQueueStart.Kind).ShouldHaveSingleItem().Detail.ShouldNotBeNull().ShouldContain("holder unknown");
        }
    }

    [Fact]
    public async Task AFolderThatBecomesDueAfterTheCardRangIsNamedByItAndOneReleasedIsDropped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var time = new Sessions.ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        var queue = new RemovalQueue(RemovalQueue.FileIn(Path.Combine(_root, "grow-data")), time);
        var a = QueuedFolder(queue);
        time.Advance(TimeSpan.FromHours(20));
        var b = QueuedFolder(queue);
        var runner = new RemovalQueueStart(queue, state, time) { Holders = _ => [] };
        CardRow[] Cards() => [.. state.Cards(openOnly: true).Where(c => c.Kind == RemovalQueueStart.Kind)];

        using var holdA = Hold(a);
        using var holdB = Hold(b);
        time.Advance(TimeSpan.FromHours(5));
        await runner.StartingAsync(Ct);
        var first = Cards().ShouldHaveSingleItem();
        first.Detail.ShouldNotBeNull().ShouldContain(Path.GetFullPath(a));
        first.Detail.ShouldNotContain(Path.GetFullPath(b));

        // The same set leaves the card be; B past a day joins it as a new one.
        await runner.StartingAsync(Ct);
        Cards().ShouldHaveSingleItem().Id.ShouldBe(first.Id);
        time.Advance(TimeSpan.FromHours(20));
        await runner.StartingAsync(Ct);
        var second = Cards().ShouldHaveSingleItem();
        second.Id.ShouldNotBe(first.Id);
        second.Detail.ShouldNotBeNull().ShouldContain(Path.GetFullPath(a));
        second.Detail.ShouldContain(Path.GetFullPath(b));

        holdA.Dispose();
        await runner.StartingAsync(Ct);
        var third = Cards().ShouldHaveSingleItem();
        third.Detail.ShouldNotBeNull().ShouldContain(Path.GetFullPath(b));
        third.Detail.ShouldNotContain(Path.GetFullPath(a));
    }

    [Fact]
    public async Task AFreshStartNamesAFolderThatBecameDueByStartsAfterTheCardAlreadyOpenRang()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var time = new Sessions.ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        var queue = new RemovalQueue(RemovalQueue.FileIn(Path.Combine(_root, "restart-data")), time);
        var a = QueuedFolder(queue);
        CardRow[] Cards() => [.. state.Cards(openOnly: true).Where(c => c.Kind == RemovalQueueStart.Kind)];
        Task Start() => new RemovalQueueStart(queue, state, time) { Holders = _ => [] }.StartingAsync(Ct);

        using var holdA = Hold(a);
        for (var start = 1; start <= 4; start++)
        {
            await Start();
        }
        var first = Cards().ShouldHaveSingleItem();
        first.Detail.ShouldNotBeNull().ShouldContain(Path.GetFullPath(a));

        var b = QueuedFolder(queue);
        using var holdB = Hold(b);
        for (var start = 5; start <= 7; start++)
        {
            await Start();
            Cards().ShouldHaveSingleItem().Id.ShouldBe(first.Id);
        }
        await Start();
        var second = Cards().ShouldHaveSingleItem();
        second.Id.ShouldNotBe(first.Id);
        second.Detail.ShouldNotBeNull().ShouldContain(Path.GetFullPath(a));
        second.Detail.ShouldContain(Path.GetFullPath(b));

        // Both released: the next start removes them and closes the card.
        holdA.Dispose();
        holdB.Dispose();
        await Start();
        Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task IdleTriesDoNotRaiseStartsTriedAndDoNotRingBeforeADay()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var time = new Sessions.ManualTime(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var state = new NodeState(new InMemoryJournal(), time);
        var queue = new RemovalQueue(RemovalQueue.FileIn(Path.Combine(_root, "idle-count-data")), time);
        var runner = new RemovalQueueStart(queue, state, time) { Holders = _ => [] };
        await runner.StartingAsync(Ct);
        var folder = QueuedFolder(queue);

        using (Hold(folder))
        {
            for (var hour = 1; hour <= 6; hour++)
            {
                time.Advance(TimeSpan.FromMinutes(61));
                runner.Look().ShouldBeTrue();
            }
            queue.Entries().ShouldHaveSingleItem().StartsTried.ShouldBe(0);
            state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == RemovalQueueStart.Kind);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AQueuedCachePathReusedAfterReleaseIsNoLongerInTheQueue()
    {
        var (gizmo, head) = PrOrigins.Make(_root, "gizmo", 3);
        _github.Origins["acme/gizmo"] = gizmo;
        _github.Prs["acme/gizmo#3"] = new PullRequest(3, "OPEN", head, "https://github.com/acme/gizmo/pull/3", Title: "PR 3", Author: "ada", Base: "main");
        var cached = Path.Combine(_reviews.CloneCache, "acme", "gizmo");
        Directory.CreateDirectory(cached);
        File.WriteAllText(Path.Combine(cached, "partial"), "x");
        _queue.Add(cached, RemovalQueue.CacheKind, "acme/gizmo", "held");

        (await Run(Verbs.ReviewPr, "pr/acme/gizmo#3", "controller")).Error.ShouldBeNull();

        Directory.Exists(Path.Combine(cached, ".git")).ShouldBeTrue();
        _queue.Contains(cached).ShouldBeFalse();
    }
}
