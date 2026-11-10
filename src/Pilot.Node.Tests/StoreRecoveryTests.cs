using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A start whose stored state cannot be read does not stop the node (pilot #830): the unreadable files are moved aside, kept whole, and the node
/// starts from the newest backup that loads, or from nothing, and says what it lost. A store from a newer node, and one that reads, are left alone.
/// </summary>
/// <remarks>
/// The failing store is built as beta 1718 met it: a stored row (here a delta holding an <see cref="AdmissionRow"/>) with a member null where its type
/// does not allow it, which the reader hands back for a member the writer never wrote, and which the node's replay of the delta then cannot write again.
/// </remarks>
public sealed class StoreRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Files = [FileJournal.RecordFile, FileJournal.SnapshotFile, FileJournal.JournalFile];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2sr-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _held = [];

    /// <summary>Deleting the folder is itself a check: a handle left on a file would stop it.</summary>
    public void Dispose()
    {
        foreach (var held in _held)
        {
            held.Dispose();
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FixedTime(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private static CommandRequest Note(string text) =>
        new() { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [text], Who = "tests" };

    /// <summary>A store a node wrote: <paramref name="notes"/> notes, compacting every <paramref name="maxDeltas"/> events, so there are backups. Returns the seq after each note.</summary>
    private List<long> Build(int notes, int maxDeltas = 4)
    {
        var after = new List<long>();
        using var journal = new FileJournal(_folder, maxDeltas);
        var state = new NodeState(journal, TimeProvider.System);
        for (var i = 0; i < notes; i++)
        {
            state.Execute(Note($"n{i}"));
            after.Add(state.Seq);
        }
        return after;
    }

    private string InStore(string name) => Path.Combine(_folder, name);

    /// <summary>The seqs there are a backup pair at, newest first.</summary>
    private long[] Backups() =>
        [.. Directory.EnumerateFiles(_folder, $"{FileJournal.JournalFile}.bak-*").Select(f => long.Parse(f[(f.LastIndexOf('-') + 1)..])).OrderDescending()];

    /// <summary>A delta of a node that wrote its admission before <see cref="AdmissionRow.Counted"/>'s writer: the member is absent, so it reads back null.</summary>
    private static string RowThatCannotBeWrittenBack(long seq)
    {
        var line = new AdmissionChangedEvent { Seq = seq, At = Now, Admission = new AdmissionRow { OpusCount = 1, OpusCap = 3 } };
        var json = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(line, PilotJsonContext.Default.NodeEvent))!;
        json["admission"]!.AsObject().Remove("counted").ShouldBeTrue();
        return json.ToJsonString() + "\n";
    }

    /// <summary>What starting the store as a node did before this leg: it stops. Returns why, so a test can look for it on the card.</summary>
    private Exception TheStartThatStopsTheNode()
    {
        using var journal = new FileJournal(_folder);
        return Should.Throw<Exception>(() => new NodeState(journal, TimeProvider.System));
    }

    /// <summary>Every file in the store folder (not its folders) with a fingerprint of what it holds, so two folders can be compared whole.</summary>
    private Dictionary<string, string> Fingerprint() =>
        Directory.EnumerateFiles(_folder).ToDictionary(f => Path.GetFileName(f), f => Convert.ToBase64String(File.ReadAllBytes(f)));

    private Dictionary<string, byte[]> BytesOf(string folder) =>
        Files.Where(f => File.Exists(Path.Combine(folder, f))).ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(folder, f)));

    private StoreRecovery Open(TimeProvider? time = null)
    {
        time ??= new FixedTime(Now);
        var first = new FileJournal(_folder, 4);
        _held.Add(first);
        var opened = StoreRecovery.Open(first, journal => new NodeState(journal, time), time);
        _held.Add(opened);
        return opened;
    }

    private string[] AsideFolders() => Directory.Exists(_folder) ? [.. Directory.GetDirectories(_folder, StoreRecovery.AsidePrefix + "*")] : [];

    [Fact]
    public void AStoreWithARowThatCannotBeWrittenBackStartsFromTheNewestBackupKeepsTheOriginalsAndRingsOnce()
    {
        // 13 notes are 26 events and a compaction comes every 4, so the last note's events are after the newest backup and are lost with the bad row.
        var after = Build(notes: 13);
        var backups = Backups();
        backups.Length.ShouldBeGreaterThanOrEqualTo(2);
        var newest = backups[0];
        var lastSeq = after[^1] + 1;
        File.AppendAllText(InStore(FileJournal.JournalFile), RowThatCannotBeWrittenBack(lastSeq));
        var why = TheStartThatStopsTheNode();
        var original = BytesOf(_folder);
        var oldStream = JsonDocument.Parse(original[FileJournal.SnapshotFile]).RootElement.GetProperty("stream").GetString();

        var opened = Open();

        var recovered = opened.Recovered.ShouldNotBeNull();
        recovered.Backup.ShouldBe($"{FileJournal.JournalFile}.bak-{newest} and {FileJournal.SnapshotFile}.bak-{newest}");
        recovered.StartedAtSeq.ShouldBe(newest);
        recovered.LastSeq.ShouldBe(lastSeq);
        recovered.Lost.ShouldBe($"seqs {newest + 1} to {lastSeq}");
        recovered.Tried.ShouldBeEmpty();
        opened.State.Seq.ShouldBe(newest);
        // The state is the backup's: the notes made up to its seq, none after.
        var notes = opened.State.Snapshot().Notes.Select(n => n.Text).ToArray();
        notes.ShouldBe(Enumerable.Range(0, notes.Length).Select(i => $"n{i}"));
        notes.Length.ShouldBeInRange(after.Count(a => a <= newest), after.Count(a => a <= newest) + 1);
        notes.Length.ShouldBeLessThan(after.Count);
        // A stream of its own, so a client holding a lost seq takes a snapshot.
        opened.State.Stream.ShouldNotBe(oldStream);

        // The unreadable files are in the aside folder, named for the start's UTC time, byte for byte; no file was deleted.
        recovered.AsideFolder.ShouldBe(InStore("unreadable-20261107T120000Z"));
        AsideFolders().ShouldBe([recovered.AsideFolder]);
        foreach (var (name, bytes) in original)
        {
            File.ReadAllBytes(Path.Combine(recovered.AsideFolder, name)).ShouldBe(bytes, name);
        }
        // The backups stay: the pair was copied to the live names, not consumed.
        Backups().ShouldBe(backups);
        File.Exists(InStore($"{FileJournal.SnapshotFile}.bak-{newest}")).ShouldBeTrue();

        // One Ring card, and the same line in the node's log and its start log.
        var log = new StringWriter();
        var startLog = new List<string>();
        var card = opened.Report(log, startLog.Add).ShouldNotBeNull();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Kind.ShouldBe("store");
        card.Title.ShouldContain($"the backup at seq {newest}");
        card.Title.ShouldContain($"seqs {newest + 1} to {lastSeq}");
        card.Detail.ShouldNotBeNull().ShouldContain(recovered.AsideFolder);
        card.Detail.ShouldContain($"{FileJournal.JournalFile}.bak-{newest}");
        card.Detail.ShouldContain(why.GetType().Name);
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.NoteIdea]);
        opened.State.Cards(openOnly: true).Where(c => c.Kind == "store").Select(c => c.Id).ShouldBe([card.Id]);
        startLog.ShouldBe([recovered.Line()]);
        log.ToString().ShouldBe(recovered.Line() + Environment.NewLine);
        recovered.Line().ShouldContain(recovered.AsideFolder);
        recovered.Line().ShouldContain(why.GetType().Name);

        // The next start reads what this one wrote, and says nothing.
        opened.Dispose();
        var again = Open();
        again.Recovered.ShouldBeNull();
        again.State.Cards(openOnly: true).Where(c => c.Kind == "store").Select(c => c.Id).ShouldBe([card.Id]);
        AsideFolders().Length.ShouldBe(1);
    }

    [Fact]
    public void ABackupThatDoesNotLoadEitherIsPassedOverAndMovedAsideWithWhatTheAttemptMadeOfIt()
    {
        var after = Build(notes: 16);
        var backups = Backups();
        backups.Length.ShouldBeGreaterThanOrEqualTo(3);
        var (newest, next) = (backups[0], backups[1]);
        var lastSeq = after[^1] + 1;
        File.AppendAllText(InStore(FileJournal.JournalFile), RowThatCannotBeWrittenBack(lastSeq));
        // The newest backup holds the same row, the way a store that went bad before its last compaction does.
        File.AppendAllText(InStore($"{FileJournal.JournalFile}.bak-{newest}"), RowThatCannotBeWrittenBack(newest + 1));
        var badBackup = (Journal: File.ReadAllBytes(InStore($"{FileJournal.JournalFile}.bak-{newest}")), Snapshot: File.ReadAllBytes(InStore($"{FileJournal.SnapshotFile}.bak-{newest}")));
        TheStartThatStopsTheNode();

        var opened = Open();

        var recovered = opened.Recovered.ShouldNotBeNull();
        recovered.StartedAtSeq.ShouldBe(next);
        recovered.Backup.ShouldBe($"{FileJournal.JournalFile}.bak-{next} and {FileJournal.SnapshotFile}.bak-{next}");
        recovered.Lost.ShouldBe($"seqs {next + 1} to {lastSeq}");
        recovered.Tried.Single().ShouldStartWith($"the backup at seq {newest}: ");
        // The one that did not load is kept with the originals, as it was; the ones older than the one used stay where they were.
        File.ReadAllBytes(Path.Combine(recovered.AsideFolder, $"{FileJournal.JournalFile}.bak-{newest}")).ShouldBe(badBackup.Journal);
        File.ReadAllBytes(Path.Combine(recovered.AsideFolder, $"{FileJournal.SnapshotFile}.bak-{newest}")).ShouldBe(badBackup.Snapshot);
        File.Exists(InStore($"{FileJournal.JournalFile}.bak-{newest}")).ShouldBeFalse();
        File.Exists(InStore($"{FileJournal.JournalFile}.bak-{next}")).ShouldBeTrue();
        File.Exists(Path.Combine(recovered.AsideFolder, "attempt-bak-" + newest, FileJournal.JournalFile)).ShouldBeTrue();
        opened.State.Seq.ShouldBe(next);
        opened.Report(new StringWriter(), _ => { }).ShouldNotBeNull().Detail.ShouldNotBeNull().ShouldContain($"the backup at seq {newest}");
    }

    [Fact]
    public void WithEveryBackupUnreadableTheNodeStartsEmptyAndTheCardSaysSo()
    {
        var after = Build(notes: 12);
        var lastSeq = after[^1] + 1;
        File.AppendAllText(InStore(FileJournal.JournalFile), RowThatCannotBeWrittenBack(lastSeq));
        var backups = Backups();
        foreach (var seq in backups)
        {
            File.AppendAllText(InStore($"{FileJournal.JournalFile}.bak-{seq}"), RowThatCannotBeWrittenBack(seq + 1));
        }
        TheStartThatStopsTheNode();
        var original = BytesOf(_folder);
        var oldStream = JsonDocument.Parse(original[FileJournal.SnapshotFile]).RootElement.GetProperty("stream").GetString();

        var opened = Open();

        var recovered = opened.Recovered.ShouldNotBeNull();
        recovered.Backup.ShouldBeNull();
        recovered.Started.ShouldBe("an empty store");
        recovered.Lost.ShouldBe($"every seq up to {lastSeq}");
        recovered.Tried.Count.ShouldBe(backups.Length);
        opened.State.Snapshot().Notes.ShouldBeEmpty();
        opened.State.Stream.ShouldNotBe(oldStream);
        foreach (var (name, bytes) in original)
        {
            File.ReadAllBytes(Path.Combine(recovered.AsideFolder, name)).ShouldBe(bytes, name);
        }
        // None of the unreadable backups is left among the live files, and none is deleted.
        Backups().ShouldBeEmpty();
        foreach (var seq in backups)
        {
            File.Exists(Path.Combine(recovered.AsideFolder, $"{FileJournal.JournalFile}.bak-{seq}")).ShouldBeTrue();
        }

        var card = opened.Report(new StringWriter(), _ => { }).ShouldNotBeNull();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldBe($"The store could not be read: started from an empty store, lost every seq up to {lastSeq}");
        card.Detail.ShouldNotBeNull().ShouldContain("started with an empty store");

        // It is a store like any: what the node does next is kept, and the next start reads it.
        opened.State.Execute(Note("after the recovery")).Ok.ShouldBeTrue();
        opened.Dispose();
        var again = Open();
        again.Recovered.ShouldBeNull();
        again.State.Snapshot().Notes.Select(n => n.Text).ShouldBe(["after the recovery"]);
    }

    [Fact]
    public void AStoreFromANewerNodeIsStillRefusedAndNothingIsMoved()
    {
        var after = Build(notes: 12);
        File.AppendAllText(InStore(FileJournal.JournalFile), RowThatCannotBeWrittenBack(after[^1] + 1));
        File.WriteAllText(InStore(FileJournal.RecordFile), $$"""{"version":"9.9.9","storeContract":{{FileJournal.StoreContract + 1}}}""");
        var before = Fingerprint();

        var refused = Should.Throw<StoreTooNewException>(() => Open());

        refused.WriterVersion.ShouldBe("9.9.9");
        refused.StoreContract.ShouldBe(FileJournal.StoreContract + 1);
        AsideFolders().ShouldBeEmpty();
        Fingerprint().ShouldBe(before);
    }

    [Fact]
    public void AStoreThatReadsMovesNothingAndRingsNothing()
    {
        Build(notes: 12);
        var before = Directory.EnumerateFiles(_folder).Order(StringComparer.Ordinal).Select(Path.GetFileName).ToArray();
        var seq = Backups()[0];

        var opened = Open();

        opened.Recovered.ShouldBeNull();
        opened.State.Seq.ShouldBeGreaterThanOrEqualTo(seq);
        var log = new StringWriter();
        var startLog = new List<string>();
        opened.Report(log, startLog.Add).ShouldBeNull();
        log.ToString().ShouldBeEmpty();
        startLog.ShouldBeEmpty();
        opened.State.Cards().ShouldBeEmpty();
        AsideFolders().ShouldBeEmpty();
        Directory.EnumerateFiles(_folder).Order(StringComparer.Ordinal).Select(Path.GetFileName).ToArray().ShouldBe(before);
        Directory.GetDirectories(_folder).ShouldBeEmpty();
    }

    [Fact]
    public void AStoreAnOlderReleaseWroteReadsAsItDidAndMovesNothing()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Store");
        var version = Directory.EnumerateDirectories(fixtures).Order(StringComparer.Ordinal).First();
        Directory.CreateDirectory(_folder);
        foreach (var file in Directory.EnumerateFiles(version))
        {
            File.Copy(file, InStore(Path.GetFileName(file)));
        }

        var opened = Open();

        opened.Recovered.ShouldBeNull();
        opened.Report(new StringWriter(), _ => throw new InvalidOperationException("nothing to say")).ShouldBeNull();
        opened.State.Cards(openOnly: true).Where(c => c.Kind == "store").ShouldBeEmpty();
        AsideFolders().ShouldBeEmpty();
    }

    [Fact]
    public void AStartNothingCanMakeFromTheStoreOrABackupOrNothingPutsTheFilesBackAndThrowsTheFirstFailure()
    {
        var after = Build(notes: 12);
        File.AppendAllText(InStore(FileJournal.JournalFile), RowThatCannotBeWrittenBack(after[^1] + 1));
        var files = Fingerprint();
        var first = new FileJournal(_folder, 4);
        _held.Add(first);

        var stopped = Should.Throw<AggregateException>(() =>
            StoreRecovery.Open(first, _ => throw new InvalidDataException("this build reads none of it"), new FixedTime(Now)));

        stopped.InnerExceptions.ShouldAllBe(e => e is InvalidDataException);
        stopped.Message.ShouldContain("the files are as they were found");
        // Every file is where it was, byte for byte, and the aside folder holds nothing of the store as found.
        Fingerprint().ShouldBe(files);
        foreach (var aside in AsideFolders())
        {
            Directory.EnumerateFiles(aside).ShouldBeEmpty();
        }
    }

    [Fact]
    public void ASecondRecoveryInTheSameSecondKeepsTheFirstAsideFolderAndUsesAnother()
    {
        var after = Build(notes: 12);
        File.AppendAllText(InStore(FileJournal.JournalFile), RowThatCannotBeWrittenBack(after[^1] + 1));
        var firstOpen = Open();
        var first = firstOpen.Recovered.ShouldNotBeNull();
        // The store it started is whole: the next delta after the backup's last is the one that goes bad this time.
        var seq = firstOpen.State.Seq;
        firstOpen.Dispose();
        File.AppendAllText(InStore(FileJournal.JournalFile), RowThatCannotBeWrittenBack(seq + 1));

        var second = Open().Recovered.ShouldNotBeNull();

        second.AsideFolder.ShouldBe(first.AsideFolder + "-2");
        Directory.Exists(first.AsideFolder).ShouldBeTrue();
        File.Exists(Path.Combine(first.AsideFolder, FileJournal.JournalFile)).ShouldBeTrue();
        AsideFolders().Length.ShouldBe(2);
    }

    [Fact]
    public void ADifferentKindOfJournalHasNoFilesToSetAsideSoItsFailureIsTheFailure()
    {
        var time = new FixedTime(Now);

        Should.Throw<InvalidOperationException>(() => StoreRecovery.Open(new InMemoryJournal(), _ => throw new InvalidOperationException("kept in memory"), time));
    }

    [Fact]
    public async Task ANodeStartedOnAStoreItCannotReadComesUpOnTheBackupAndRingsOnceWithTheLineInBothLogs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "p2t", "sr-" + Guid.NewGuid().ToString("N")[..8]);
        var store = Path.Combine(folder, "data", "store");
        long newest;
        using (var journal = new FileJournal(store, 4))
        {
            var state = new NodeState(journal, TimeProvider.System);
            for (var i = 0; i < 12; i++)
            {
                state.Execute(Note($"n{i}"));
            }
            File.AppendAllText(Path.Combine(store, FileJournal.JournalFile), RowThatCannotBeWrittenBack(state.Seq + 1));
            newest = Directory.EnumerateFiles(store, $"{FileJournal.JournalFile}.bak-*").Select(f => long.Parse(f[(f.LastIndexOf('-') + 1)..])).Max();
        }

        await using var node = await NodeHarness.StartAsync(TestContext.Current.CancellationToken, folder: folder);

        node.State.Seq.ShouldBeGreaterThanOrEqualTo(newest);
        var cards = node.State.Cards(openOnly: true).Where(c => c.Kind == "store").ToArray();
        var card = cards.ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain($"the backup at seq {newest}");
        var aside = Directory.GetDirectories(store, StoreRecovery.AsidePrefix + "*").ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain(aside);
        File.Exists(Path.Combine(aside, FileJournal.JournalFile)).ShouldBeTrue();

        var startLog = File.ReadAllText(Path.Combine(folder, "data", NodeProgram.StartLogFile));
        startLog.ShouldContain("could not be read, so the node started from the backup at seq " + newest);
        startLog.ShouldContain(aside);
        string nodeLog;
        using (var stream = new FileStream(Directory.EnumerateFiles(Path.Combine(folder, "data", "logs"), "node*.log").First(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            nodeLog = reader.ReadToEnd();
        }
        nodeLog.ShouldContain("could not be read, so the node started from the backup at seq " + newest);
    }
}
