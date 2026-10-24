using System.Text.Json;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The store: a node restarted over its data folder carries on with every record, and the black box keeps who did what.</summary>
public sealed class FileJournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2j-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static CommandRequest Note(string text, string who = "tests") =>
        new() { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [text], Who = who };

    private static SessionRow Row(string id, SessionState state, long spend = 10) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = "session " + id,
        Kind = SessionKind.Interactive,
        Cwd = "/work",
        Role = SessionRole.AdHoc,
        ContextTokens = 10,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = spend,
        StartedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>A node over the folder, run through <paramref name="act"/>, then let go as a stopped node lets go of its files.</summary>
    private SnapshotEvent Run(Action<NodeState> act, int maxDeltas = FileJournal.DefaultMaxDeltas)
    {
        using var journal = new FileJournal(_folder, maxDeltas);
        var state = new NodeState(journal, TimeProvider.System);
        act(state);
        return state.Snapshot();
    }

    private static void ShouldMatch(SnapshotEvent now, SnapshotEvent was)
    {
        now.Stream.ShouldBe(was.Stream);
        now.Seq.ShouldBe(was.Seq);
        now.Sessions.ShouldBe(was.Sessions, ignoreOrder: true);
        now.Notes.ShouldBe(was.Notes);
    }

    [Fact]
    public void ARestartKeepsEveryRecord()
    {
        var was = Run(state =>
        {
            state.PutSession(Row("aaaa1111", SessionState.Working));
            state.PutSession(Row("bbbb2222", SessionState.Idle));
            state.Execute(Note("kept across a restart"));
            state.PutSession(Row("aaaa1111", SessionState.Idle, spend: 99));
            state.RemoveSession("bbbb2222");
        });

        var now = Run(_ => { });

        ShouldMatch(now, was);
        now.Sessions.ShouldHaveSingleItem().SpendWeighted.ShouldBe(99);
        now.Notes.ShouldHaveSingleItem().Text.ShouldBe("kept across a restart");
        // And numbering carries on from where it stopped.
        Run(state => state.Execute(Note("next")).Seq.ShouldBe(was.Seq + 2));
    }

    [Fact]
    public void TheBlackBoxHasALinePerCommandSayingWhoDidWhat()
    {
        Run(state =>
        {
            state.Execute(Note("one", who: "gui"));
            state.Execute(new CommandRequest { Verb = "nonsense", Target = Verbs.NodeTarget, Args = [], Who = "cli" });
        });
        Run(state => state.Execute(Note("after a restart", who: "du1-watchtower")));

        var lines = File.ReadAllLines(Path.Combine(_folder, FileJournal.BlackBoxFile))
            .Select(l => JsonSerializer.Deserialize(l, PilotJsonContext.Default.BlackBoxLine)!)
            .ToList();

        lines.Select(l => (l.Who, l.Verb, l.Target, l.Ok)).ShouldBe(
        [
            ("gui", Verbs.Note, Verbs.NodeTarget, true),
            ("cli", "nonsense", Verbs.NodeTarget, false),
            ("du1-watchtower", Verbs.Note, Verbs.NodeTarget, true),
        ]);
        lines[1].Error.ShouldNotBeNullOrEmpty();
        lines.Select(l => l.Seq).ShouldBeInOrder();
    }

    [Fact]
    public void ALineACrashCutShortIsDroppedAndTheJournalCarriesOn()
    {
        var was = Run(state =>
        {
            state.PutSession(Row("aaaa1111", SessionState.Working));
            state.Execute(Note("whole"));
        });
        File.AppendAllText(Path.Combine(_folder, FileJournal.JournalFile), "{\"kind\":\"noteAdded\",\"seq\":");

        var now = Run(state => state.Execute(Note("after the crash")));

        now.Seq.ShouldBe(was.Seq + 2);
        now.Notes.Select(n => n.Text).ShouldBe(["whole", "after the crash"]);
        ShouldMatch(Run(_ => { }), now);
    }

    [Fact]
    public void ABoundedJournalCompactsIntoTheSnapshotAndARestartStillRestoresEverything()
    {
        var was = Run(state =>
        {
            for (var i = 0; i < 40; i++)
            {
                state.Execute(Note($"note {i}"));
                state.PutSession(Row($"s{i % 5:D7}", i % 3 == 0 ? SessionState.Working : SessionState.Idle, spend: i));
            }
        }, maxDeltas: 8);

        File.ReadAllLines(Path.Combine(_folder, FileJournal.JournalFile)).Length.ShouldBeLessThan(8);
        ShouldMatch(Run(_ => { }, maxDeltas: 8), was);
    }

    [Fact]
    public void ACrashBetweenTheSnapshotAndEmptyingTheJournalLosesNothingAndRepeatsNothing()
    {
        var journalPath = Path.Combine(_folder, FileJournal.JournalFile);
        Run(state => state.Execute(Note("before")), maxDeltas: 4);
        var oldJournal = File.ReadAllText(journalPath);
        var was = Run(state =>
        {
            for (var i = 0; i < 6; i++)
            {
                state.Execute(Note($"n{i}"));
            }
        }, maxDeltas: 4);
        // Put back deltas a compaction had already folded into the snapshot, as if it died before emptying the journal.
        File.WriteAllText(journalPath, oldJournal + File.ReadAllText(journalPath));

        var now = Run(_ => { }, maxDeltas: 4);

        ShouldMatch(now, was);
        now.Notes.Select(n => n.Text).ShouldBe(["before", "n0", "n1", "n2", "n3", "n4", "n5"]);
    }

    [Fact]
    public void AJournalThatCannotWriteLeavesTheStateAsItWas()
    {
        var journal = new FailingJournal();
        var state = new NodeState(journal, TimeProvider.System);
        state.Execute(Note("kept"));
        var before = state.Snapshot();

        journal.Fail = true;
        Should.Throw<IOException>(() => state.PutSession(Row("aaaa1111", SessionState.Working)));

        var after = state.Snapshot();
        after.Seq.ShouldBe(before.Seq);
        after.Sessions.ShouldBeEmpty();
        journal.Fail = false;
        state.Execute(Note("next")).Seq.ShouldBe(before.Seq + 2);
    }

    private static FlightRow Flight(string code, FlightState state = FlightState.Flying) => new()
    {
        Code = code,
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 1,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/w/" + code,
        Base = "origin/main",
        State = state,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>A file's bytes while the node still has it open for append.</summary>
    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        file.CopyTo(bytes);
        return bytes.ToArray();
    }

    private string JournalPath => Path.Combine(_folder, FileJournal.JournalFile);

    /// <summary>Two flights, one of them landed, and a note: what the store must never lose.</summary>
    private static void FlyAndLand(NodeState state)
    {
        state.PutFlight(Flight("PIL1"));
        state.PutFlight(Flight("PIL2"));
        state.PutFlight(Flight("PIL1", FlightState.Landed));
        state.Execute(Note("after the landing"));
    }

    /// <summary>Puts <paramref name="line"/> after the first line of the journal, so whole lines follow it.</summary>
    private void InsertAfterFirstLine(string line)
    {
        var lines = File.ReadAllLines(JournalPath).ToList();
        lines.Count.ShouldBeGreaterThan(1);
        lines.Insert(1, line);
        File.WriteAllText(JournalPath, string.Join('\n', lines) + "\n");
    }

    [Theory]
    [InlineData("{\"kind\":\"aKindOfANewerBuild\",\"seq\":3,\"at\":\"2026-01-01T00:00:00+00:00\"}")]
    [InlineData("{\"kind\":\"flightChanged\",\"seq\":3,\"at\":\"2026-01-01T00:00:00+00:00\",\"flight\":5}")]
    public void ALineThisBuildCannotReadBeforeMoreLinesRefusesTheLoadAndLeavesTheJournalAsItWas(string unreadable)
    {
        Run(FlyAndLand);
        InsertAfterFirstLine(unreadable);
        var before = File.ReadAllBytes(JournalPath);

        var error = Should.Throw<InvalidDataException>(() => Run(_ => { }));

        error.Message.ShouldContain(FileJournal.JournalFile);
        error.Message.ShouldContain("seq");
        File.ReadAllBytes(JournalPath).ShouldBe(before);
        Directory.GetFiles(_folder, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void AnUnreadableLineThatWasEndedWholeIsNotACutAndRefusesTheLoad()
    {
        Run(FlyAndLand);
        File.AppendAllText(JournalPath, "{\"kind\":\"aKindOfANewerBuild\",\"seq\":99}\n");
        var before = File.ReadAllBytes(JournalPath);

        Should.Throw<InvalidDataException>(() => Run(_ => { }));

        File.ReadAllBytes(JournalPath).ShouldBe(before);
    }

    [Fact]
    public void ACutLastLineIsDroppedWithTheWholeLinesKeptAsWrittenAndTheJournalKeptBeside()
    {
        var was = Run(FlyAndLand);
        File.AppendAllText(JournalPath, "{\"kind\":\"noteAdded\",\"seq\":");
        var withCut = File.ReadAllBytes(JournalPath);
        var whole = File.ReadAllText(JournalPath).Split('\n')[..^1];

        ShouldMatch(Run(_ => { }), was);

        File.ReadAllText(JournalPath).ShouldBe(string.Concat(whole.Select(l => l + "\n")));
        var kept = Directory.GetFiles(_folder, FileJournal.JournalFile + ".cut-*").ShouldHaveSingleItem();
        File.ReadAllBytes(kept).ShouldBe(withCut);
    }

    [Fact]
    public void ALoadThatDropsNothingLeavesTheJournalUntouched()
    {
        Run(FlyAndLand);
        var before = File.ReadAllBytes(JournalPath);

        Run(_ => { });

        File.ReadAllBytes(JournalPath).ShouldBe(before);
        Directory.GetFiles(_folder, FileJournal.JournalFile + ".cut-*").ShouldBeEmpty();
    }

    [Fact]
    public void ASnapshotWriteThatThrowsKeepsTheWholeJournalAndARestartSeesEveryFlightAndLanding()
    {
        SnapshotEvent was;
        using (var journal = new FileJournal(_folder, maxDeltas: 4))
        {
            var state = new NodeState(journal, TimeProvider.System);
            journal.BeforeSnapshotMove = () => throw new IOException("There is not enough space on the disk.");
            FlyAndLand(state);
            // More than one compaction was due: each failed, none trimmed.
            state.Execute(Note("and on"));
            state.Execute(Note("and on"));
            journal.CompactionError.ShouldBeOfType<IOException>();
            was = state.Snapshot();
        }

        // Every delta, from seq 1, is still a line of the journal.
        File.ReadAllLines(JournalPath).Length.ShouldBe((int)was.Seq);
        var now = Run(_ => { }, maxDeltas: 4);

        ShouldMatch(now, was);
        now.Flights.Select(f => (f.Code, f.State)).ShouldBe(
            [("PIL1", FlightState.Landed), ("PIL2", FlightState.Flying)], ignoreOrder: true);
    }

    [Fact]
    public void ACompactionThatFailedIsTriedAgainAndThenTrimsTheJournal()
    {
        using var journal = new FileJournal(_folder, maxDeltas: 4);
        var state = new NodeState(journal, TimeProvider.System);
        var fail = true;
        journal.BeforeSnapshotMove = () => { if (fail) { throw new IOException("busy"); } };
        FlyAndLand(state);
        fail = false;
        for (var i = 0; i < 8; i++)
        {
            state.Execute(Note($"n{i}"));
        }

        journal.CompactionError.ShouldBeNull();
        ReadShared(JournalPath).Count(b => b == (byte)10).ShouldBeLessThan(8);
    }

    [Fact]
    public void EachCompactionKeepsTheJournalAndSnapshotAsBackupsAndOnlyTheLastFew()
    {
        string[] backups(string file) => Directory.GetFiles(_folder, file + ".bak-*");
        string journalFile() => Path.Combine(_folder, FileJournal.JournalFile);

        using var journal = new FileJournal(_folder, maxDeltas: 4);
        var state = new NodeState(journal, TimeProvider.System);
        var snapshotPath = Path.Combine(_folder, FileJournal.SnapshotFile);
        var journalBefore = ReadShared(journalFile());
        var snapshotBefore = ReadShared(snapshotPath);
        var i = 0;
        while (backups(FileJournal.JournalFile).Length == 0 && i < 100)
        {
            journalBefore = ReadShared(journalFile());
            snapshotBefore = ReadShared(snapshotPath);
            state.Execute(Note($"n{i++}"));
        }

        // The first compaction kept what the journal and the snapshot held before it (the journal with the delta that brought it on).
        var backup = backups(FileJournal.JournalFile).Single();
        var firstSeq = long.Parse(backup[(backup.LastIndexOf('-') + 1)..]);
        ReadShared(backup).Take(journalBefore.Length).ShouldBe(journalBefore);
        File.ReadAllBytes($"{snapshotPath}.bak-{firstSeq}").ShouldBe(snapshotBefore);

        for (; i < 40; i++)
        {
            state.Execute(Note($"n{i}"));
        }

        backups(FileJournal.JournalFile).Length.ShouldBe(FileJournal.KeptBackups);
        backups(FileJournal.SnapshotFile).Length.ShouldBe(FileJournal.KeptBackups);
        File.Exists($"{journalFile()}.bak-{firstSeq}").ShouldBeFalse();
        // The ones kept are the latest.
        backups(FileJournal.JournalFile).Select(f => long.Parse(f[(f.LastIndexOf('-') + 1)..])).Max().ShouldBeGreaterThan(state.Seq - 8);
    }

    private static CardRow[] StoreCards(NodeState state, bool openOnly = true) =>
        [.. state.Cards(openOnly).Where(c => c.Kind == "store")];

    [Fact]
    public void AFailedSnapshotRingsACardKeepsJournalingAndTheNextCompactionClosesIt()
    {
        SnapshotEvent was;
        using (var journal = new FileJournal(_folder, maxDeltas: 4))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var fail = true;
            journal.BeforeSnapshotMove = () => { if (fail) { throw new IOException("There is not enough space on the disk."); } };

            // The delta that brings the compaction on succeeds and is journaled; nothing escapes Execute.
            for (var i = 0; i < 4; i++)
            {
                state.Execute(Note($"before {i}")).Ok.ShouldBeTrue();
            }

            var card = StoreCards(state).ShouldHaveSingleItem();
            card.Bin.ShouldBe(CardBin.ForYou);
            card.Title.ShouldContain("snapshot");
            card.Detail.ShouldNotBeNull().ShouldContain("not enough space");

            // Further deltas keep journaling; a second failed compaction is still one card, not two.
            for (var i = 0; i < 6; i++)
            {
                state.Execute(Note($"after {i}")).Ok.ShouldBeTrue();
            }
            StoreCards(state).ShouldHaveSingleItem();
            journal.CompactionError.ShouldBeOfType<IOException>();

            // The next compaction tries again, and its success closes the card.
            fail = false;
            for (var i = 0; i < 8; i++)
            {
                state.Execute(Note($"later {i}")).Ok.ShouldBeTrue();
            }
            journal.CompactionError.ShouldBeNull();
            StoreCards(state).ShouldBeEmpty();
            StoreCards(state, openOnly: false).ShouldHaveSingleItem().Outcome.ShouldNotBeNullOrEmpty();
            was = state.Snapshot();
        }

        ShouldMatch(Run(_ => { }, maxDeltas: 4), was);
    }

    [Fact]
    public void AStartingSnapshotThatCannotBeWrittenLeavesTheNodeUpAndARestartHasEveryDelta()
    {
        SnapshotEvent was;
        using (var journal = new FileJournal(_folder, maxDeltas: 4096))
        {
            journal.BeforeSnapshotMove = () => throw new IOException("There is not enough space on the disk.");
            var state = new NodeState(journal, TimeProvider.System);

            StoreCards(state).ShouldHaveSingleItem().Bin.ShouldBe(CardBin.ForYou);
            state.PutSession(Row("aaaa1111", SessionState.Working));
            state.Execute(Note("journaled all the same")).Ok.ShouldBeTrue();
            was = state.Snapshot();
        }

        // The stream and every delta come back, and the card is still there to say the snapshot is missing.
        var now = Run(state => StoreCards(state).ShouldHaveSingleItem());

        ShouldMatch(now, was);
        now.Sessions.ShouldHaveSingleItem().Id.ShouldBe("aaaa1111");
        now.Notes.ShouldHaveSingleItem().Text.ShouldBe("journaled all the same");
    }

    [Fact]
    public void AStartingSnapshotThatFailsToSerializeRingsACardInsteadOfKillingTheNode()
    {
        var journal = new SnapshotFaultJournal { Fail = true };

        var state = new NodeState(journal, TimeProvider.System);

        var card = StoreCards(state).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Detail.ShouldNotBeNull().ShouldContain("overlaps");
        state.Execute(Note("still up")).Ok.ShouldBeTrue();
        new NodeState(journal, TimeProvider.System).Snapshot().Notes.ShouldHaveSingleItem().Text.ShouldBe("still up");
    }

    private static (string Record, int Contract) ReadRecord(string folder)
    {
        using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, FileJournal.RecordFile)));
        return (record.RootElement.GetProperty("version").GetString()!, record.RootElement.GetProperty("storeContract").GetInt32());
    }

    private static Dictionary<string, byte[]> Bytes(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), f => Directory.Exists(f) ? [] : File.ReadAllBytes(f));

    [Fact]
    public void AStoreGetsARecordOfItsWriterOnLoadAndOnEachSnapshot()
    {
        using (var journal = new FileJournal(_folder, maxDeltas: 4, version: "1.2.3", contract: 1))
        {
            // Stamped on load, before anything is journaled.
            journal.Load();
            ReadRecord(_folder).ShouldBe(("1.2.3", 1));
        }

        // A later build stamps its own on load, and again with the snapshot it compacts.
        using (var journal = new FileJournal(_folder, maxDeltas: 4, version: "1.3.0", contract: 2))
        {
            var state = new NodeState(journal, TimeProvider.System);
            ReadRecord(_folder).ShouldBe(("1.3.0", 2));
            File.Delete(Path.Combine(_folder, FileJournal.RecordFile));
            for (var i = 0; i < 8; i++)
            {
                state.Execute(Note($"note {i}"));
            }
            ReadRecord(_folder).ShouldBe(("1.3.0", 2));
        }
        File.Exists(Path.Combine(_folder, FileJournal.RecordFile + ".tmp")).ShouldBeFalse();
    }

    [Fact]
    public void ARecordFromTheDefaultBuildSaysThisBuildsVersionAndContract()
    {
        Run(state => state.Execute(Note("stamped")));

        ReadRecord(_folder).ShouldBe((FileJournal.RunningVersion, FileJournal.StoreContract));
    }

    [Fact]
    public void AStoreFromANewerNodeIsRefusedAndNotAFileOfItIsTouched()
    {
        Run(state =>
        {
            state.PutSession(Row("aaaa1111", SessionState.Working));
            state.Execute(Note("a newer node's"));
        }, maxDeltas: 4);
        // Written by a node whose contract is higher, with a journal line this build could not read, and a cut tail.
        File.WriteAllText(Path.Combine(_folder, FileJournal.RecordFile), """{"version":"9.9.9+abc","storeContract":2}""");
        File.AppendAllText(Path.Combine(_folder, FileJournal.JournalFile), "{\"kind\":\"fromTheFuture\",\"seq\":99}\n{\"kind\":\"noteAdded\",\"seq\":");
        var before = Bytes(_folder);

        using (var journal = new FileJournal(_folder, version: "1.0.0", contract: 1))
        {
            var refused = Should.Throw<StoreTooNewException>(journal.Load);

            refused.Message.ShouldBe("this store is from 9.9.9+abc; run that or newer");
            refused.StoreContract.ShouldBe(2);
        }

        var after = Bytes(_folder);
        after.Keys.OrderBy(k => k).ShouldBe(before.Keys.OrderBy(k => k));
        foreach (var (file, bytes) in before)
        {
            after[file].ShouldBe(bytes, file);
        }
    }

    [Fact]
    public void AStoreWithNoRecordOrAnOlderOneLoadsAndIsStamped()
    {
        var was = Run(state => state.Execute(Note("an older node's")));

        // An older node wrote no record at all.
        File.Delete(Path.Combine(_folder, FileJournal.RecordFile));
        ShouldMatch(Run(_ => { }), was);
        ReadRecord(_folder).ShouldBe((FileJournal.RunningVersion, FileJournal.StoreContract));

        // A lower contract, or the same one, loads too, and the record becomes this build's.
        File.WriteAllText(Path.Combine(_folder, FileJournal.RecordFile), """{"version":"0.1.0","storeContract":0}""");
        ShouldMatch(Run(_ => { }), was);
        ReadRecord(_folder).ShouldBe((FileJournal.RunningVersion, FileJournal.StoreContract));
    }

    private sealed class SnapshotFaultJournal : INodeJournal
    {
        private readonly List<NodeEvent> _kept = [];

        public bool Fail { get; set; }

        public NodeHistory Load() => new(null, null, [.. _kept]);

        public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
        {
            if (Fail && nodeEvent is SnapshotEvent)
            {
                throw new ArgumentNullException("overlaps");
            }
            _kept.Add(nodeEvent);
        }
    }

    private sealed class FailingJournal : INodeJournal
    {
        private readonly InMemoryJournal _inner = new();

        public bool Fail { get; set; }

        public NodeHistory Load() => _inner.Load();

        public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
        {
            if (Fail)
            {
                throw new IOException("There is not enough space on the disk.");
            }
            _inner.Append(nodeEvent, json);
        }
    }
}
