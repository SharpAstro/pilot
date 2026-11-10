using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>What <see cref="StoreRecovery"/> did when the store a node was started on could not be read.</summary>
/// <param name="AsideFolder">Where the unreadable <c>store.json</c>, <c>snapshot.json</c> and <c>journal.jsonl</c> were moved to (kept, never deleted).</param>
/// <param name="Backup">The <c>.bak-&lt;seq&gt;</c> pair the node started from, as <c>journal.jsonl.bak-6 and snapshot.json.bak-6</c>; null for an empty store.</param>
/// <param name="StartedAtSeq">The seq of the state the node started with: the backup's, or the empty store's.</param>
/// <param name="LastSeq">The last seq the unreadable files held, read without reading them as events; null when none could be read.</param>
/// <param name="Error">Why the files could not be read: the type and message of the failure.</param>
/// <param name="Tried">One line for each backup that did not load either, newest first, and why.</param>
public sealed record StoreRecovered(string AsideFolder, string? Backup, long StartedAtSeq, long? LastSeq, string Error, IReadOnlyList<string> Tried)
{
    /// <summary>What the node started from, in words.</summary>
    public string Started => Backup is null ? "an empty store" : $"the backup at seq {StartedAtSeq}";

    /// <summary>What the start lost: the seqs after the backup, in words.</summary>
    public string Lost
    {
        get
        {
            if (LastSeq is not { } last)
            {
                return "an unknown range of seqs (the last one in the unreadable files could not be read)";
            }
            if (Backup is null)
            {
                return $"every seq up to {last}";
            }
            return last > StartedAtSeq ? $"seqs {StartedAtSeq + 1} to {last}" : "no seq (the unreadable files held nothing after the backup)";
        }
    }

    /// <summary>The one line node.log and node-start.log keep of it.</summary>
    public string Line() =>
        $"store: could not be read, so the node started from {Started}; lost {Lost}; the unreadable files are kept in {AsideFolder}; error: {Error}";
}

/// <summary>
/// Starts the node's <see cref="NodeState"/> on its store, and when the store cannot be read, sets it aside and starts from a backup
/// instead of staying down (pilot #830: a store a new build could not read stopped every node that updated onto it, and nothing led back).
/// </summary>
/// <remarks>
/// <para>The store is opened as it always was. A failure of <see cref="FileJournal.Load"/> or of <see cref="NodeState"/>'s constructor (a
/// line the build cannot read, a row it cannot write back) other than <see cref="StoreTooNewException"/>, which is a newer node's store and
/// is left alone, then goes like this:</para>
/// <list type="number">
/// <item><description><c>store.json</c>, <c>snapshot.json</c> and <c>journal.jsonl</c> are moved, not copied or deleted, into
/// <c>unreadable-&lt;utc stamp&gt;/</c> inside the store folder, after the journal that failed to read them is disposed so no handle holds one.</description></item>
/// <item><description>The backups are tried newest first. <c>journal.jsonl.bak-&lt;seq&gt;</c> and <c>snapshot.json.bak-&lt;seq&gt;</c> are made together by a
/// compaction (<see cref="FileJournal"/>), the snapshot as it was and the journal up to <c>seq</c>, so the pair is the state at <c>seq</c>. Each is copied
/// to the live names (the backup stays), given a stream of its own so a client that held the lost seqs takes a snapshot, and loaded. A pair that does not
/// load is moved aside as it is, with what the attempt made of it.</description></item>
/// <item><description>Last, an empty store.</description></item>
/// </list>
/// <para><c>decisions.jsonl</c> is not moved: it is kept whole outside any compaction and has no backup, so a line of it this build cannot read still stops the start.</para>
/// <para>No file is deleted. When nothing starts, what was moved is put back and the original failure is thrown, so the store is as it was found.</para>
/// </remarks>
public sealed class StoreRecovery : IDisposable
{
    /// <summary>What an aside folder in the store folder is named for: <c>unreadable-&lt;utc stamp&gt;</c>.</summary>
    public const string AsidePrefix = "unreadable-";

    /// <summary>What a store card about a recovery has before its aside folder: <c>recovery/&lt;aside folder&gt;</c>.</summary>
    public const string SubjectPrefix = "recovery/";

    private readonly bool _owns;

    private StoreRecovery(INodeJournal journal, NodeState state, bool owns)
    {
        Journal = journal;
        State = state;
        _owns = owns;
    }

    /// <summary>The journal the state runs on: the one opened first, or the one a recovery opened over the restored files.</summary>
    public INodeJournal Journal { get; }

    public NodeState State { get; }

    /// <summary>What the start did to get here; null when the store was read as it was.</summary>
    public StoreRecovered? Recovered { get; private set; }

    /// <summary>Disposes the journal a recovery opened; the one the container handed in is the container's to dispose.</summary>
    public void Dispose()
    {
        if (_owns)
        {
            (Journal as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Builds the node's state with <paramref name="create"/> on <paramref name="journal"/>. A journal that is no <see cref="FileJournal"/>
    /// has no files to set aside, so it is used as it is and a failure is the failure.
    /// </summary>
    /// <param name="create">Makes the <see cref="NodeState"/> over a journal; called again over each journal a recovery opens.</param>
    /// <exception cref="StoreTooNewException">The store is a newer node's: nothing was moved.</exception>
    public static StoreRecovery Open(INodeJournal journal, Func<INodeJournal, NodeState> create, TimeProvider time)
    {
        if (journal is not FileJournal files)
        {
            return new StoreRecovery(journal, create(journal), owns: false);
        }

        try
        {
            return new StoreRecovery(files, create(files), owns: false);
        }
        catch (Exception e) when (e is not (StoreTooNewException or OperationCanceledException))
        {
            // Closed first: no handle of this journal may hold a file that is about to be moved.
            files.Dispose();
            return Recover(files.Folder, create, time, e);
        }
    }

    private static StoreRecovery Recover(string folder, Func<INodeJournal, NodeState> create, TimeProvider time, Exception error)
    {
        var last = LastSeqOf(folder);
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var aside = Path.Combine(folder, AsidePrefix + stamp);
        for (var n = 2; Directory.Exists(aside); n++)
        {
            aside = Path.Combine(folder, $"{AsidePrefix}{stamp}-{n}");
        }
        Directory.CreateDirectory(aside);

        // Only what held the store as found is recorded to be put back; what an attempt made is the attempt's, and stays in the aside folder.
        var moved = new List<(string From, string To)>();
        var tried = new List<string>();
        Exception? lastFailure = null;
        try
        {
            foreach (var name in (string[])[FileJournal.RecordFile, FileJournal.SnapshotFile, FileJournal.JournalFile])
            {
                MoveTo(Path.Combine(folder, name), Path.Combine(aside, name), moved);
            }

            foreach (var seq in BackupSeqs(folder))
            {
                if (Attempt(folder, aside, create, seq, moved, out var started, out var failure))
                {
                    return Finish(started!, aside, seq, last, error, tried);
                }
                tried.Add($"the backup at seq {seq}: {Describe(failure!)}");
                lastFailure = failure;
            }

            if (Attempt(folder, aside, create, null, moved, out var empty, out lastFailure))
            {
                return Finish(empty!, aside, null, last, error, tried);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lastFailure = e;
        }

        PutBack(moved);
        throw new AggregateException(
            $"the store could not be read ({Describe(error)}) and no backup or empty store could be started either ({(lastFailure is null ? "nothing was tried" : Describe(lastFailure))}); the files are as they were found",
            error, lastFailure ?? error);
    }

    private static StoreRecovery Finish(StoreRecovery started, string aside, long? backup, long? last, Exception error, IReadOnlyList<string> tried)
    {
        started.Recovered = new StoreRecovered(
            aside,
            backup is { } seq ? $"{FileJournal.JournalFile}.bak-{seq} and {FileJournal.SnapshotFile}.bak-{seq}" : null,
            started.State.Seq, last, Describe(error), tried);
        return started;
    }

    /// <summary>
    /// Starts on <paramref name="backup"/> (copied to the live names, the backup itself left where it is) or on nothing. A start that
    /// fails leaves the store folder as it found it: the files it made go into the aside folder, and so does a backup that does not load.
    /// </summary>
    private static bool Attempt(string folder, string aside, Func<INodeJournal, NodeState> create, long? backup, List<(string From, string To)> moved,
        out StoreRecovery? started, out Exception? failure, string? label = null)
    {
        FileJournal? journal = null;
        try
        {
            if (backup is { } seq)
            {
                CopyBackup(folder, seq);
            }
            journal = new FileJournal(folder);
            started = new StoreRecovery(journal, create(journal), owns: true);
            failure = null;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            journal?.Dispose();
            var attempt = Path.Combine(aside, label ?? (backup is { } b ? $"attempt-bak-{b}" : "attempt-empty"));
            foreach (var name in (string[])[FileJournal.RecordFile, FileJournal.SnapshotFile, FileJournal.JournalFile])
            {
                MoveTo(Path.Combine(folder, name), Path.Combine(attempt, name), moved: null);
            }
            if (backup is { } n)
            {
                foreach (var name in (string[])[FileJournal.SnapshotFile, FileJournal.JournalFile])
                {
                    MoveTo(Path.Combine(folder, $"{name}.bak-{n}"), Path.Combine(aside, $"{name}.bak-{n}"), moved);
                }
            }
            started = null;
            failure = e;
            return false;
        }
    }

    /// <summary>Copies the pair made at <paramref name="seq"/> to the live names; a half that was not kept (an empty journal is not) is not copied.</summary>
    private static void CopyBackup(string folder, long seq)
    {
        var snapshot = Path.Combine(folder, $"{FileJournal.SnapshotFile}.bak-{seq}");
        var journal = Path.Combine(folder, $"{FileJournal.JournalFile}.bak-{seq}");
        if (File.Exists(snapshot))
        {
            // A stream of its own: a client that held a seq of the lost run, and asks for the deltas after it, takes a snapshot, not the new run's events under old numbers.
            var json = JsonNode.Parse(File.ReadAllBytes(snapshot)) as JsonObject ?? throw new InvalidDataException($"{snapshot} holds no snapshot");
            json["stream"] = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(folder, FileJournal.SnapshotFile), json.ToJsonString());
        }
        if (File.Exists(journal))
        {
            File.Copy(journal, Path.Combine(folder, FileJournal.JournalFile), overwrite: false);
        }
    }

    /// <summary>The seqs a compaction kept a backup at, newest first; a seq with only one half of its pair counts.</summary>
    private static List<long> BackupSeqs(string folder) =>
        [.. new[] { FileJournal.SnapshotFile, FileJournal.JournalFile }
            .SelectMany(name => Directory.EnumerateFiles(folder, $"{name}.bak-*").Select(f => Path.GetFileName(f)[(name.Length + ".bak-".Length)..]))
            .Select(suffix => long.TryParse(suffix, out var n) ? n : -1)
            .Where(n => n >= 0)
            .Distinct()
            .OrderByDescending(n => n)];

    /// <summary>
    /// The highest <c>seq</c> in the snapshot and the journal, read as plain JSON: the files are not valid events or the start would
    /// not be here, but a line's number is still there. Null when none can be read.
    /// </summary>
    private static long? LastSeqOf(string folder)
    {
        long? last = null;
        void Note(ReadOnlySpan<byte> json)
        {
            try
            {
                using var document = JsonDocument.Parse(json.ToArray());
                if (document.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var n))
                {
                    last = Math.Max(last ?? n, n);
                }
            }
            catch (JsonException)
            {
                // A cut line, or not JSON at all: it has no seq to give.
            }
        }

        try
        {
            if (File.Exists(Path.Combine(folder, FileJournal.SnapshotFile)))
            {
                Note(File.ReadAllBytes(Path.Combine(folder, FileJournal.SnapshotFile)));
            }
            if (File.Exists(Path.Combine(folder, FileJournal.JournalFile)))
            {
                foreach (var line in File.ReadLines(Path.Combine(folder, FileJournal.JournalFile)))
                {
                    if (line.Length > 0)
                    {
                        Note(System.Text.Encoding.UTF8.GetBytes(line));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // What was read stands; the rest is unknown.
        }
        return last;
    }

    /// <summary>Moves a file, never deleting it; a file that is not there is not moved. <paramref name="moved"/> remembers it so a start that comes to nothing can put it back.</summary>
    private static void MoveTo(string from, string to, List<(string From, string To)>? moved)
    {
        if (!File.Exists(from))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        moved?.Add((from, to));
    }

    private static void PutBack(List<(string From, string To)> moved)
    {
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            try
            {
                File.Move(moved[i].To, moved[i].From);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file is where it was moved to, which the failure says; the rest are still put back.
            }
        }
    }

    private static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message.Split('\n')[0].Trim()}";

    /// <summary>
    /// Says what the start did, once the state is up: the same line to node.log and node-start.log, and one Ring card naming the folder
    /// the unreadable files were kept in, the backup the node started from, the seqs it lost and the error. Nothing when the store was read as it was.
    /// </summary>
    /// <returns>The card, or null when there was nothing to say or the card could not be kept.</returns>
    public CardRow? Report(TextWriter log, Action<string> startLog)
    {
        if (Recovered is not { } recovered)
        {
            return null;
        }

        var line = recovered.Line();
        try
        {
            log.WriteLine(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The start log below, and the card, say it too.
        }
        startLog(line);

        var detail =
            $"The files of the store could not be read ({recovered.Error}). They were moved, not deleted, into {recovered.AsideFolder}: " +
            $"{FileJournal.RecordFile}, {FileJournal.SnapshotFile} and {FileJournal.JournalFile}, byte for byte as they were. " +
            (recovered.Backup is null
                ? "No backup could be read either, so the node started with an empty store. "
                : $"The node started from {recovered.Backup}, the state at seq {recovered.StartedAtSeq}. ") +
            $"Lost: {recovered.Lost}." +
            (recovered.Tried.Count == 0 ? "" : $" Not readable either: {string.Join("; ", recovered.Tried)}.");
        try
        {
            return State.RaiseCard(
                CardBin.Ring, NodeState.StoreCardKind, SubjectPrefix + Path.GetFileName(recovered.AsideFolder),
                $"The store could not be read: started from {recovered.Started}, lost {recovered.Lost}", detail,
                [new CardChoice { Label = "File it as an idea", Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(Verbs.AnyRepo), Args = [$"The store could not be read at start: {recovered.Error}\n\n{detail}"] }]);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A store that cannot record the card either has nothing more to say here: the log lines stand.
            log.WriteLine($"store: the card for a recovery could not be kept: {e.Message}");
            return null;
        }
    }
}

/// <summary>
/// Says what a start from a backup did (<see cref="StoreRecovery.Report"/>) once the host starts, not while the state is being built: a card
/// that rings goes to the brain first (<see cref="Flights.RingJudge"/>), which asks the watchtower, which needs the very state still under
/// construction, so a card raised inside its factory fails to resolve it and is never kept.
/// </summary>
internal sealed class StoreRecoveryReport(StoreRecovery recovery, TextWriter log, Action<string> startLog) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        recovery.Report(log, startLog);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
