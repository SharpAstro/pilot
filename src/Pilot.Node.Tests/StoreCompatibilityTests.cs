using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node reads the store an older release wrote. Each folder under <c>Fixtures/Store/&lt;version&gt;/</c> is a store written by the code of
/// that release (see PILOT.md for how it is made and refreshed): it is loaded by <see cref="FileJournal"/>, replayed into
/// <see cref="NodeState"/>, serialized again as a fresh snapshot and started on a second time, and any record that comes back with a
/// member null where its type does not allow it fails the test naming <c>Type.Member</c>.
/// </summary>
/// <remarks>
/// Beta 1718 stopped every node that updated onto it: #808 added <c>AdmissionRow.WaitingOnPerson</c> as <c>init; = []</c>, a journal
/// written before it read back with null, and writing that row again was refused. No test read a store an older release had written.
/// </remarks>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed class StoreCompatibilityTests : IDisposable
{
    private static readonly string StoreFixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Store");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2sc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>The versions with a fixture, one folder each.</summary>
    public static TheoryData<string> Versions => new(Directory.EnumerateDirectories(StoreFixtures).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal).ToArray());

    /// <summary>A store's files as a node left them, copied to a folder of their own to start on (a start stamps <c>store.json</c> and may keep a backup).</summary>
    private string Copy(string version)
    {
        var target = Path.Combine(_folder, version);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(StoreFixtures, version)))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return target;
    }

    private static string Describe(string version, string stage, IReadOnlyList<string> found) =>
        $"A store written by {version} comes back with members null that their type does not allow ({stage}): {string.Join(", ", found)}. " +
        "A member added to a stored record must be nullable or default for what the reader hands it (see PILOT.md).";

    [Fact]
    public void ThereIsAFixtureOfAnOlderRelease() =>
        Directory.EnumerateDirectories(StoreFixtures).ShouldNotBeEmpty($"{StoreFixtures} holds no store fixture: see PILOT.md");

    [Theory]
    [MemberData(nameof(Versions))]
    public void AStoreAnOlderReleaseWroteStartsAndRestoresEveryMemberItsTypeRequires(string version)
    {
        var folder = Copy(version);

        // As the journal reads it, before any node state is built on it: the snapshot, every delta and every decision.
        NodeHistory history;
        using (var journal = new FileJournal(folder))
        {
            history = journal.Load();
            var asRead = NullMembers.Find([history.Snapshot, .. history.Deltas, .. journal.Decisions()]);
            Assert.True(asRead.Count == 0, Describe(version, "as read from its files", asRead));
        }
        history.Snapshot.ShouldNotBeNull($"{version} has a snapshot");
        history.Deltas.ShouldNotBeEmpty($"{version} has a journal after its snapshot");

        // Started: loaded, replayed, and serialized again (the node does that for each delta it holds), then a fresh snapshot of the whole state.
        SnapshotEvent first;
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            first = state.Snapshot();
            var restored = NullMembers.Find(first, state.Cards(), state.Flights(), state.Legs(), state.Jobs(), state.PrReviews(), state.Merges(), state.Admission(), state.Machine(), state.Usage(), state.Repos(), state.Node(), state.PullRequests());
            Assert.True(restored.Count == 0, Describe(version, "after replay", restored));

            journal.Append(first, JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(first, PilotJsonContext.Default.NodeEvent));
        }

        // Started again on what the first start wrote: the snapshot it just made, read by this build.
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var again = state.Snapshot();
            var restored = NullMembers.Find(again, journal.Decisions());
            Assert.True(restored.Count == 0, Describe(version, "after a fresh snapshot and a second start", restored));

            again.Seq.ShouldBe(first.Seq);
            again.Sessions.Length.ShouldBe(first.Sessions.Length);
            again.Flights.Length.ShouldBe(first.Flights.Length);
            again.Legs.Length.ShouldBe(first.Legs.Length);
            again.Cards.Length.ShouldBe(first.Cards.Length);
            again.Jobs.Length.ShouldBe(first.Jobs.Length);
            again.PrReviews.Length.ShouldBe(first.PrReviews.Length);
            again.Notes.Length.ShouldBe(first.Notes.Length);
            (again.Admission is null).ShouldBe(first.Admission is null);
        }
    }

    [Fact]
    public void TheStoreContractIsNoNewerThanTheNewestFixture()
    {
        var newest = Directory.EnumerateDirectories(StoreFixtures)
            .Select(d => (Version: Path.GetFileName(d), Contract: ContractOf(d)))
            .OrderByDescending(f => f.Contract)
            .First();

        FileJournal.StoreContract.ShouldBeLessThanOrEqualTo(
            newest.Contract,
            $"FileJournal.StoreContract is {FileJournal.StoreContract} and the newest store fixture ({newest.Version}) is at {newest.Contract}: " +
            "a contract bump needs a new fixture, written by a release that carries it (see PILOT.md)");
    }

    private static int ContractOf(string folder)
    {
        using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, FileJournal.RecordFile)));
        return record.RootElement.GetProperty("storeContract").GetInt32();
    }

    [Fact]
    public void EachFixtureSaysWhichReleaseWroteItInItsFolderName()
    {
        foreach (var folder in Directory.EnumerateDirectories(StoreFixtures))
        {
            using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, FileJournal.RecordFile)));
            record.RootElement.GetProperty("version").GetString().ShouldNotBeNullOrEmpty();
            Path.GetFileName(folder).ShouldBe(record.RootElement.GetProperty("version").GetString(), $"{folder} is named for the release that wrote it");
        }
    }

    // The walk itself: that a member null where its type has no "?" is found, and named.

    [Fact]
    public void AMemberForcedNullIsNamedByItsTypeAndMember()
    {
        var row = new AdmissionRow { OpusCount = 1, OpusCap = 3 };
        // The setter turns a null into empty, so the null goes in below it, as a reader that sets the field itself would leave it.
        typeof(AdmissionRow).GetField("<WaitingOnPerson>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull().SetValue(row, null);
        var snapshot = new SnapshotEvent { Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Sessions = [], Notes = [], Admission = row };

        NullMembers.Find(snapshot).ShouldBe(["AdmissionRow.WaitingOnPerson"]);
        NullMembers.Find(row).ShouldBe(["AdmissionRow.WaitingOnPerson"]);
    }

    [Fact]
    public void ANullEntryInAnArrayOfNonNullableEntriesIsNamed()
    {
        var row = new AdmissionRow { OpusCount = 1, OpusCap = 3, Counted = [null!] };

        NullMembers.Find(row).ShouldBe(["AdmissionRow.Counted[]"]);
    }

    [Fact]
    public void ARequiredMemberForcedNullIsNamed()
    {
        var note = new NodeNote { At = DateTimeOffset.UnixEpoch, Who = "w", Text = "t" };
        typeof(NodeNote).GetField("<Text>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull().SetValue(note, null);

        NullMembers.Find(new NoteAddedEvent { Seq = 2, At = DateTimeOffset.UnixEpoch, Note = note }).ShouldBe(["NodeNote.Text"]);
    }

    [Fact]
    public void NullWhereTheTypeAllowsItAndComputedMembersAreNotReported()
    {
        // PeersNotCounted is string?, Id and RetiredAt are optional, and Line() is a method: none is a finding.
        var snapshot = new SnapshotEvent
        {
            Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Sessions = [], Notes = [new NodeNote { At = DateTimeOffset.UnixEpoch, Who = "w", Text = "t" }],
            Admission = new AdmissionRow { OpusCount = 1, OpusCap = 3 },
        };

        NullMembers.Find(snapshot).ShouldBeEmpty();
    }

    [Fact]
    public void TheContractsSampleSnapshotHasNoNullMemberTheTypeForbids()
    {
        var sample = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot.sample.json")), PilotJsonContext.Default.NodeEvent);

        NullMembers.Find(sample).ShouldBeEmpty();
    }

    // The writer: a store it writes holds every kind of stored record, or a fixture made by it would test less than it says.

    [Fact]
    public void TheFixtureWriterWritesEveryKindOfStoredRecord()
    {
        var folder = Path.Combine(_folder, "written");
        StoreFixtureWriter.Write(folder, version: "0.0.0-writer");

        using var journal = new FileJournal(folder);
        var history = journal.Load();

        var snapshot = history.Snapshot.ShouldNotBeNull();
        snapshot.Sessions.ShouldNotBeEmpty();
        snapshot.Notes.ShouldNotBeEmpty();
        snapshot.Flights.ShouldNotBeEmpty();
        snapshot.Legs.ShouldNotBeEmpty();
        snapshot.Cards.ShouldNotBeEmpty();
        snapshot.Jobs.ShouldNotBeEmpty();
        snapshot.PrReviews.ShouldNotBeEmpty();
        snapshot.Merges.ShouldNotBeEmpty();
        snapshot.Ideas.ShouldNotBeEmpty();
        snapshot.IdeaMoves.ShouldNotBeEmpty();
        snapshot.Milestones.ShouldNotBeEmpty();
        snapshot.Issues.ShouldNotBeEmpty();
        snapshot.Summaries.ShouldNotBeEmpty();
        snapshot.Usage.ShouldNotBeNull();
        snapshot.Machine.ShouldNotBeNull();
        snapshot.Admission.ShouldNotBeNull();
        snapshot.Repos.ShouldNotBeNull();
        snapshot.Node.ShouldNotBeNull();
        snapshot.PullRequests.ShouldNotBeNull();

        // A delta of every kind the stream has but the snapshot: a new kind is a new type under NodeEvent, and fails here until the writer makes one.
        var kinds = typeof(NodeEvent).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.DerivedType).Where(t => t != typeof(SnapshotEvent)).ToHashSet();
        var written = history.Deltas.Select(d => d.GetType()).ToHashSet();
        kinds.Except(written).Select(t => t.Name).Order().ShouldBeEmpty("the fixture writer leaves out a delta of each of these kinds");

        journal.Decisions().ShouldNotBeEmpty();
        File.Exists(Path.Combine(folder, FileJournal.RecordFile)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(folder, FileJournal.RecordFile)).ShouldContain("0.0.0-writer");
    }
}
