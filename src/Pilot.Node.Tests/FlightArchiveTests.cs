using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1083: a landed flight moves out of the live state into the archive store and comes back; the code lookups still know it.</summary>
public sealed class FlightArchiveTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2fa-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileJournal Journal() => new(Path.Combine(_root, "store"));

    private NodeState Open(INodeJournal journal) => new(journal, TimeProvider.System) { Archive = new FlightArchive(_root) };

    private static FlightRow Flight(string code, FlightState state = FlightState.Landed, int? pr = 7) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Title = $"Title of {code}", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/nowhere/{code}", Base = "main", State = state, CreatedAt = Start, PullRequest = pr, PullRequestUrl = pr is null ? null : $"https://example.test/pull/{pr}",
        LandedAt = state == FlightState.Landed ? Start.AddHours(1) : null,
        Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "reviewer", StartedAt = Start },
        ReviewRounds = [],
    };

    private static LegRow Leg(string code) => new()
    {
        Flight = code, Name = "only", Order = 1, Goal = "goal", DoneWhen = "done", State = LegState.Done,
    };

    private static CommandRequest Archive(string code, params string[] args) => new() { Verb = Verbs.ArchiveFlight, Target = Verbs.FlightTarget(code), Args = args, Who = "tests" };

    private static FlightArchiver Archiver(NodeState state, IGit? git = null) => new(state, git);

    /// <summary>A landed flight with a leg and a Handled card about it, as a landing leaves one.</summary>
    private static void Land(NodeState state, string code)
    {
        state.PutFlight(Flight(code));
        state.PutLeg(Leg(code));
        state.RaiseCard(CardBin.Handled, "plane-left", Verbs.FlightTarget(code), $"{code} landed", "left the plane");
    }

    private static bool Has(SnapshotEvent s, string code) => s.Flights.Any(f => f.Code == code) || s.Legs.Any(l => l.Flight == code) || s.Cards.Any(c => FlightMove.IsAbout(c, code));

    [Fact]
    public async Task ALandedFlightWithNothingOpenLeavesTheStateTheSnapshotAndTheMirrorAndIsWrittenToTheStore()
    {
        using var journal = Journal();
        var state = Open(journal);
        Land(state, "TIA1");
        Land(state, "TIA2");
        var mirror = new NodeMirror();
        mirror.Apply(state.Snapshot());
        using var sub = state.Subscribe(null, null, new Collecting(mirror));

        var result = await Archiver(state).RunAsync(Archive("TIA1"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        state.Flights().Select(f => f.Code).ShouldBe(["TIA2"]);
        state.Legs("TIA1").ShouldBeEmpty();
        state.Cards().ShouldNotContain(c => FlightMove.IsAbout(c, "TIA1"));
        Has(state.Snapshot(), "TIA1").ShouldBeFalse();
        Has(state.Snapshot(), "TIA2").ShouldBeTrue();
        mirror.Flights.ContainsKey("TIA1").ShouldBeFalse();
        mirror.Legs.Keys.ShouldNotContain(k => k.Flight == "TIA1");
        mirror.Cards.Values.ShouldNotContain(c => FlightMove.IsAbout(c, "TIA1"));
        mirror.Flights.ContainsKey("TIA2").ShouldBeTrue();

        File.Exists(Path.Combine(FlightArchive.FolderIn(_root), "TIA1.json")).ShouldBeTrue();
        var whole = state.Archive!.Read("TIA1").ShouldNotBeNull();
        whole.Flight.Review.ShouldNotBeNull();
        whole.Legs.Length.ShouldBe(1);
        whole.Cards.ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Handled);
        whole.Row.PullRequest.ShouldBe(7);
    }

    private sealed class Collecting(NodeMirror mirror) : INodeEventSink
    {
        public int Capacity => 1000;

        public bool TryWrite(byte[] message)
        {
            mirror.Apply(JsonSerializer.Deserialize(message, PilotJsonContext.Default.NodeEvent)!);
            return true;
        }

        public void Overflowed()
        {
        }
    }

    [Fact]
    public async Task UndoPutsTheFlightItsLegsAndItsCardsBackAndDeletesTheFile()
    {
        using var journal = Journal();
        var state = Open(journal);
        Land(state, "TIA1");
        await Archiver(state).RunAsync(Archive("TIA1"), Ct);

        var result = await Archiver(state).RunAsync(Archive("TIA1", "undo"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        state.Flights().Single().Review.ShouldNotBeNull();
        state.Legs("TIA1").Length.ShouldBe(1);
        state.Cards().Single(c => c.Bin == CardBin.Handled).Subject.ShouldBe("flight/TIA1");
        File.Exists(Path.Combine(FlightArchive.FolderIn(_root), "TIA1.json")).ShouldBeFalse();
        state.ArchivedFlightRows().ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightNotLandedOrWithAnOpenCardALiveSessionOrAnUnmergedPrIsRefusedWithTheReason()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Flying));
        // A card raised about a flight already landed is closed at once (RaiseCard), so the card is raised while it flies, and it lands with the card open.
        state.PutFlight(Flight("TIA2", FlightState.Flying));
        state.PutLeg(Leg("TIA2"));
        state.RaiseCard(CardBin.ForYou, "worker", "flight/TIA2/only", "a worker stopped");
        state.PutFlight(Flight("TIA2"));
        Land(state, "TIA3");
        state.PutSession(new SessionRow
        {
            Id = "s1", SessionId = "s1-0000-4000-8000-000000000000", Name = "x-tia3", Kind = SessionKind.Background, Cwd = "/r/w", Role = SessionRole.Worker, Flight = "TIA3",
            ContextTokens = 1, ContextWindow = 1000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });
        Land(state, "TIA4");
        state.PutMerge(new MergeRow { Flight = "TIA4", Repo = "tianwen", Head = "abc", Method = "squash", State = MergeEntryState.Checks, AddedAt = Start });

        (await Archiver(state).RunAsync(Archive("TIA1"), Ct)).Error.ShouldNotBeNull().ShouldContain("has not landed");
        (await Archiver(state).RunAsync(Archive("TIA2"), Ct)).Error.ShouldNotBeNull().ShouldContain("open about it");
        (await Archiver(state).RunAsync(Archive("TIA3"), Ct)).Error.ShouldNotBeNull().ShouldContain("live");
        (await Archiver(state).RunAsync(Archive("TIA4"), Ct)).Error.ShouldNotBeNull().ShouldContain("not merged");
        state.Flights().Length.ShouldBe(4);
        state.ArchivedFlightRows().ShouldBeEmpty();
    }

    [Fact]
    public async Task APlaneFolderOnDiskOrABranchStillThereHoldsItBack()
    {
        using var journal = Journal();
        var state = Open(journal);
        Land(state, "TIA1");
        Directory.CreateDirectory(Path.Combine(_root, "plane"));
        state.PutFlight(Flight("TIA1") with { Worktree = Path.Combine(_root, "plane") });

        (await Archiver(state).RunAsync(Archive("TIA1"), Ct)).Error.ShouldNotBeNull().ShouldContain("still on disk");

        Directory.Delete(Path.Combine(_root, "plane"));
        (await Archiver(state, new BranchThere()).RunAsync(Archive("TIA1"), Ct)).Error.ShouldNotBeNull().ShouldContain("branch flight/tia1 is not removed");
        (await Archiver(state, new BranchGone()).RunAsync(Archive("TIA1"), Ct)).Ok.ShouldBeTrue();
    }

    private sealed class BranchThere : IGit
    {
        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) => Task.FromResult(new GitRun(0, "", ""));
    }

    private sealed class BranchGone : IGit
    {
        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) => Task.FromResult(new GitRun(1, "", ""));
    }

    [Fact]
    public async Task TheArchiveSurvivesANewNodeStateOverTheSameFolder()
    {
        using (var journal = Journal())
        {
            var state = Open(journal);
            Land(state, "TIA1");
            Land(state, "TIA2");
            await Archiver(state).RunAsync(Archive("TIA1"), Ct);
        }

        using var again = Journal();
        var restarted = Open(again);

        restarted.Flights().Select(f => f.Code).ShouldBe(["TIA2"]);
        restarted.Legs("TIA1").ShouldBeEmpty();
        restarted.ArchivedFlightRows().Select(r => r.Code).ShouldBe(["TIA1"]);
        restarted.Archive!.Read("TIA1").ShouldNotBeNull().Legs.Length.ShouldBe(1);
        (await Archiver(restarted).RunAsync(Archive("TIA1", "undo"), Ct)).Ok.ShouldBeTrue();
        restarted.Flights().Length.ShouldBe(2);
    }

    [Fact]
    public async Task ACompactedSnapshotOfTheJournalHasNoArchivedFlight()
    {
        var journal = new InMemoryJournal(maxDeltas: 6);
        var state = Open(journal);
        Land(state, "TIA1");
        await Archiver(state).RunAsync(Archive("TIA1"), Ct);
        for (var i = 0; i < 10; i++)
        {
            state.RaiseCard(CardBin.Handled, "plane-left", $"flight/TIA9{i}", $"filler {i}");
        }

        var history = journal.Load();

        history.Snapshot.ShouldNotBeNull();
        Has(history.Snapshot, "TIA1").ShouldBeFalse();
        history.Deltas.ShouldNotContain(d => d is FlightAddedEvent);
    }

    [Fact]
    public async Task FlightCodesNeverHandsOutAnArchivedCodeAgain()
    {
        using var journal = Journal();
        var state = Open(journal);
        Land(state, "TIA1");
        await Archiver(state).RunAsync(Archive("TIA1"), Ct);
        var archived = state.ArchivedFlightRows();

        FlightCodes.PrefixFor("tianwen", state.Flights(), archived).ShouldBe("TIA");
        FlightCodes.Next("TIA", 1, state.Flights(), archived).ShouldBe("TIA1a");
        FlightCodes.Next("TIA", 1, state.Flights()).ShouldBe("TIA1");
    }

    [Fact]
    public async Task AFlightLandedSinceTheLastDigestIsInItAfterItWasArchived()
    {
        using var journal = Journal();
        var state = Open(journal);
        Land(state, "TIA1");
        await Archiver(state).RunAsync(Archive("TIA1"), Ct);

        var (title, detail) = new Digest(state, TimeProvider.System).Compose(Start, Start.AddHours(2));

        title.ShouldContain("1 landed");
        detail.ShouldContain("TIA1: Title of TIA1");
    }

    [Fact]
    public async Task TheIndexIsPagedNewestLandedFirstAndOneFlightIsReadWhole()
    {
        using var journal = Journal();
        var state = Open(journal);
        foreach (var code in new[] { "TIA1", "TIA2", "TIA3" })
        {
            state.PutFlight(Flight(code) with { LandedAt = Start.AddHours(int.Parse(code[3..])) });
            await Archiver(state).RunAsync(Archive(code), Ct);
        }

        var page = (await Archiver(state).RunAsync(new CommandRequest { Verb = Verbs.ArchivedFlights, Target = Verbs.NodeTarget, Args = ["0", "2"], Who = "tests" }, Ct)).ArchivedFlights.ShouldNotBeNull();
        page.Total.ShouldBe(3);
        page.Rows.Select(r => r.Code).ShouldBe(["TIA3", "TIA2"]);

        var one = (await Archiver(state).RunAsync(new CommandRequest { Verb = Verbs.ArchivedFlight, Target = "flight/TIA1", Who = "tests" }, Ct)).ArchivedFlight.ShouldNotBeNull();
        one.Flight.Code.ShouldBe("TIA1");
        (await Archiver(state).RunAsync(new CommandRequest { Verb = Verbs.ArchivedFlight, Target = "flight/TIA9", Who = "tests" }, Ct)).Ok.ShouldBeFalse();
    }
}
