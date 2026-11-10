using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Updates;

/// <summary>
/// What an update leaves in the data folder (<c>update.json</c>) for the node that starts on it: the version it came from, the one it
/// went to, how many starts of that one did not get as far as being counted a success, the last error one of them died of, and whether
/// the node has since gone back to <see cref="From"/> (<see cref="RolledBack"/>) and said so (<see cref="Told"/>).
/// </summary>
/// <param name="FailedStarts">Starts of <paramref name="To"/> not yet cleared. A start is counted before the host is built, so a crash nothing caught leaves it counted.</param>
public sealed record UpdateRecord(string From, string To, int FailedStarts = 0, string? LastError = null, bool RolledBack = false, bool Told = false);

/// <summary>What a start that failed during its trial does next.</summary>
public enum UpdateStep
{
    /// <summary>The first failed start: start the node once more.</summary>
    Retry,

    /// <summary>The second failed start in a row: put the previous release back.</summary>
    RollBack,
}

/// <summary>What <see cref="UpdateRollback.Restore"/> did: whether the previous release's files are back, whether the node of it was started, and a line for the log.</summary>
public sealed record UpdateRestore(bool Restored, bool Started, string Message);

/// <summary>
/// The start of the first node of a new version, on trial: counted when it begins and cleared once the host has started with the
/// store read. See <see cref="UpdateRollback"/>.
/// </summary>
public sealed class UpdateTrial
{
    private readonly UpdateRollback _owner;

    internal UpdateTrial(UpdateRollback owner, UpdateRecord record, bool exhausted)
    {
        _owner = owner;
        Record = record;
        Exhausted = exhausted;
    }

    /// <summary>The record as this start left it: <see cref="UpdateRecord.FailedStarts"/> includes this start.</summary>
    public UpdateRecord Record { get; private set; }

    /// <summary>The start found two starts counted already and counted no third: nothing caught the earlier ones (a crash), and the way back is next.</summary>
    public bool Exhausted { get; }

    /// <summary>The host has started and the record is gone: whatever happens now is no failed start of this update.</summary>
    public bool Cleared { get; private set; }

    /// <summary>The host has started with the store read: the update is good, and the record goes. The previous release stays for the next install to replace.</summary>
    public void Clear()
    {
        if (!Cleared)
        {
            Cleared = true;
            _owner.Forget();
        }
    }

    /// <summary>Takes this start back out of the count: it failed for a reason that is not the new version's (a store a newer node wrote).</summary>
    public void Forgive()
    {
        if (!Exhausted && !Cleared && Record.FailedStarts > 0)
        {
            Record = Record with { FailedStarts = Record.FailedStarts - 1 };
            _owner.TryWrite(Record);
        }
    }

    /// <summary>This start died of <paramref name="error"/>: keeps what it said, and says what comes next.</summary>
    public UpdateStep Failed(Exception error)
    {
        Record = Record with { LastError = UpdateRollback.Describe(error) };
        _owner.TryWrite(Record);
        return Record.FailedStarts >= UpdateRollback.StartsAllowed ? UpdateStep.RollBack : UpdateStep.Retry;
    }
}

/// <summary>
/// The way back from an update to a version that cannot start (pilot #830: one such update stopped every node that took it, and nothing
/// led back).
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><description>Before <see cref="ReleaseInstall.Swap"/>, <see cref="NodeUpdater"/> keeps the files of the release it runs from (those its
/// <c>VERSION</c> marker lists, the marker included) in <c>previous/</c> under the install, and writes the <see cref="UpdateRecord"/>
/// (<see cref="KeepPrevious"/>, <see cref="Expect"/>). A build of one's own has no marker and nothing to keep, so it is never rolled back.</description></item>
/// <item><description>The new node counts each start of the version the record names before it builds the host (<see cref="Begin"/>), and clears the record once
/// the host has started and the store is read (<see cref="UpdateTrial.Clear"/>).</description></item>
/// <item><description>A start that fails with an exception counts it and starts the node once more. At the second failed start in a row, or at a start that
/// finds two counted (a crash nothing caught), <see cref="Restore"/> puts <c>previous/</c> back with <see cref="ReleaseInstall.Swap"/> (all or nothing,
/// each replaced file renamed aside), starts the restored node and the failing one exits. A store a newer node wrote is not the new version's fault and
/// rolls nothing back.</description></item>
/// <item><description>The restored node rings one card (<see cref="Tell"/>), and <see cref="IsRolledBack"/> keeps auto-update from installing that version
/// again. A newer release, or <c>pilot update</c> by hand, still goes in.</description></item>
/// </list>
/// </remarks>
/// <param name="install">The folder the node runs from.</param>
/// <param name="dataFolder">The node's data folder, which holds the record.</param>
/// <param name="version">The version of the node that asks: its own.</param>
public sealed class UpdateRollback(string install, string dataFolder, string version)
{
    /// <summary>The record, in the data folder.</summary>
    public const string RecordFile = "update.json";

    /// <summary>How many starts of the new version are tried: the second to fail goes back.</summary>
    public const int StartsAllowed = 2;

    /// <summary>What the card about a rollback has before the version that did not start: <c>rollback/&lt;version&gt;</c>.</summary>
    public const string SubjectPrefix = "rollback/";

    /// <summary>The binary a start is tried again from: this process's own. A test replaces it.</summary>
    public string RunningNode { get; init; } = Environment.ProcessPath ?? "";

    /// <summary>Starts the node at the path to take over from this one; false if it did not start. A test replaces it.</summary>
    public Func<string, bool> StartSuccessor { get; init; } = NodeRestarter.Start;

    /// <summary>Whether the node's systemd unit restarts it, so no successor is started. A test replaces it.</summary>
    public Func<string, bool> RestartedBySystemd { get; init; } = NodeRestarter.UnderSystemdUnit;

    public string RecordPath => Path.Combine(dataFolder, RecordFile);

    /// <summary>The kept release: <c>previous/</c> under the install.</summary>
    public string PreviousPath => Path.Combine(install, ReleaseInstall.PreviousName);

    private string NodeFile => Path.Combine(install, NodeUpdater.Layout.Required[0]);

    // ---- the install, by the node that runs the old release ----

    /// <summary>
    /// Copies the files of <paramref name="old"/> (the release the install is now) into <c>previous/</c>, the marker included, replacing what an
    /// earlier install kept there. A file the marker lists that is not on disk is not copied.
    /// </summary>
    /// <exception cref="ReleaseInstallException">The copy failed: the update does not go on without a way back.</exception>
    public void KeepPrevious(ReleaseBuild old)
    {
        var previous = PreviousPath;
        try
        {
            if (Directory.Exists(previous))
            {
                Directory.Delete(previous, recursive: true);
            }
            foreach (var file in old.Files.Where(f => f != ReleaseInstall.MarkerName).Append(ReleaseInstall.MarkerName))
            {
                var from = Path.Combine(install, file);
                if (!File.Exists(from))
                {
                    continue;
                }
                var to = Path.Combine(previous, file);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(previous);
            throw new ReleaseInstallException($"the release it replaces could not be kept in {previous} for a way back: {ex.Message}", ex);
        }
    }

    /// <summary>Writes the record of an update from this version to <paramref name="to"/>, with no start counted.</summary>
    /// <exception cref="ReleaseInstallException">The record could not be written: the update does not go on without it.</exception>
    public void Expect(string to)
    {
        try
        {
            Write(new UpdateRecord(ReleaseVersions.Plain(version), ReleaseVersions.Plain(to)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReleaseInstallException($"the record of the update could not be written to {RecordPath}: {ex.Message}", ex);
        }
    }

    /// <summary>Removes the record: no update is pending (the install did not go in, or it is a build of one's own).</summary>
    public void Forget()
    {
        try
        {
            File.Delete(RecordPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A record that stays is read again at the next start, and names a version that is running or never was.
        }
    }

    /// <summary>Whether auto-update must not install <paramref name="candidate"/>: the record says it did not start twice and the node went back.</summary>
    public bool IsRolledBack(string candidate)
        => Read() is { RolledBack: true } record && SameVersion(record.To, candidate);

    // ---- the first starts of the new version ----

    /// <summary>
    /// The start of a node on trial, counted before it builds the host; null when this start is not one: no record, a record of a rollback already
    /// done, one for another version, a build of one's own (no marker for this version), or no kept release to go back to. A start that finds
    /// <see cref="StartsAllowed"/> counted counts no more and is <see cref="UpdateTrial.Exhausted"/>.
    /// </summary>
    public UpdateTrial? Begin()
    {
        if (Read() is not { RolledBack: false } record || !SameVersion(record.To, version)
            || ReleaseInstall.Installed(install, version) is null || PreviousBuild(record.From) is null)
        {
            return null;
        }
        if (record.FailedStarts >= StartsAllowed)
        {
            return new UpdateTrial(this, record, exhausted: true);
        }
        var counted = record with { FailedStarts = record.FailedStarts + 1 };
        // A start that cannot be counted cannot be rolled back either; it goes on as any start does.
        return TryWrite(counted) ? new UpdateTrial(this, counted, exhausted: false) : null;
    }

    /// <summary>Starts the node once more, from this process's own binary with <c>--after</c> this pid; false if it did not start.</summary>
    public bool StartAgain()
        => NodeRestarter.IsNode(RunningNode) && (RestartedBySystemd(RunningNode) || StartSuccessor(RunningNode));

    /// <summary>
    /// Puts <c>previous/</c> back in place of the files of the version that did not start (<see cref="ReleaseInstall.Swap"/>: all or nothing, each
    /// replaced file renamed aside), writes the record as rolled back and starts the node of the restored release (<c>--after</c> this pid); the caller
    /// exits. Nothing is deleted: the failing release's files go aside and <c>previous/</c> stays.
    /// </summary>
    public UpdateRestore Restore(UpdateRecord record)
    {
        if (PreviousBuild(record.From) is not { } kept)
        {
            return new UpdateRestore(false, false, $"pilot {record.To} did not start, and there is no pilot {record.From} in {PreviousPath} to go back to");
        }
        var present = kept.Files.Where(f => File.Exists(Path.Combine(PreviousPath, f))).ToArray();
        if (ReleaseInstall.Swap(PreviousPath, kept with { Files = present }, install, ReleaseInstall.ReadMarker(install)) is { } why)
        {
            return new UpdateRestore(false, false, $"pilot {record.To} did not start, and pilot {record.From} could not be put back: {why}");
        }

        var wrote = TryWrite(record with { RolledBack = true, Told = false });
        var started = RestartedBySystemd(NodeFile) || StartSuccessor(NodeFile);
        var message = $"pilot {record.To} did not start twice; pilot {record.From} is back in {install}"
            + (started ? ", and its node is starting" : $", but {NodeFile} did not start: the next start of the node is pilot {record.From}")
            + (wrote ? "" : $"; the record at {RecordPath} could not be written, so the node will not say it went back");
        return new UpdateRestore(true, started, message);
    }

    // ---- the node that went back ----

    /// <summary>
    /// Once, on the node that went back: one Ring card saying that the version did not start twice, that the node is on the previous one and the last error.
    /// Nothing when there is no record of a rollback to this version, or it has been told.
    /// </summary>
    /// <returns>The card, or null when there was nothing to say.</returns>
    public CardRow? Tell(NodeState state)
    {
        if (Read() is not { RolledBack: true, Told: false } record || !SameVersion(record.From, version))
        {
            return null;
        }
        var error = record.LastError ?? "no error was recorded, so the node stopped without saying why";
        var detail =
            $"pilot {record.To} was installed over pilot {record.From}, and its node did not start in {record.FailedStarts} tries in a row ({error}). " +
            $"The node put pilot {record.From}'s files back from {PreviousPath} and runs on them; the files of {record.To} were replaced. " +
            $"Auto-update does not install {record.To} again; a newer release, or pilot update node install, does.";
        var card = state.RaiseCard(
            CardBin.Ring, NodeUpdater.Kind, SubjectPrefix + record.To,
            $"pilot {record.To} did not start twice; back on {record.From}: {LegCommander.OneLine(error)}", detail,
            [new CardChoice { Label = "File it as an idea", Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(Verbs.AnyRepo), Args = [$"pilot {record.To} did not start twice and the node went back on {record.From}: {error}\n\n{detail}"] }]);
        TryWrite(record with { Told = true });
        return card;
    }

    // ---- the record ----

    /// <summary>The record, or null when there is none or it cannot be read.</summary>
    public UpdateRecord? Read()
    {
        try
        {
            if (!File.Exists(RecordPath))
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(RecordPath));
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            return Text("from") is { Length: > 0 } from && Text("to") is { Length: > 0 } to
                ? new UpdateRecord(from, to, root.TryGetProperty("failedStarts", out var n) && n.TryGetInt32(out var starts) ? starts : 0, Text("lastError"), Flag("rolledBack"), Flag("told"))
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Writes the record whole through a temp file, so a start that dies in the middle leaves the one before.</summary>
    private void Write(UpdateRecord record)
    {
        Directory.CreateDirectory(dataFolder);
        var temp = RecordPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("from", record.From);
            json.WriteString("to", record.To);
            json.WriteNumber("failedStarts", record.FailedStarts);
            if (record.LastError is { } error)
            {
                json.WriteString("lastError", error);
            }
            json.WriteBoolean("rolledBack", record.RolledBack);
            json.WriteBoolean("told", record.Told);
            json.WriteEndObject();
        }
        File.Move(temp, RecordPath, overwrite: true);
    }

    internal bool TryWrite(UpdateRecord record)
    {
        try
        {
            Write(record);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The kept release's marker, when it is a release of <paramref name="from"/> with its node in it; null otherwise.</summary>
    private ReleaseBuild? PreviousBuild(string from)
        => ReleaseInstall.ReadMarker(PreviousPath) is { } build && SameVersion(build.Version, from) && File.Exists(Path.Combine(PreviousPath, NodeUpdater.Layout.Required[0]))
            ? build
            : null;

    private static bool SameVersion(string a, string b) => ReleaseVersions.Plain(a) == ReleaseVersions.Plain(b);

    /// <summary>The error as the record keeps it: its type and the first line of its message.</summary>
    internal static string Describe(Exception error)
    {
        var line = error.Message.Split('\n')[0].Trim();
        var text = $"{error.GetType().Name}: {line}";
        return text.Length > 400 ? text[..397] + "..." : text;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What is left is replaced by the next install.
        }
    }
}
