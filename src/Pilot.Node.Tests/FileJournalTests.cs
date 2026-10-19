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
