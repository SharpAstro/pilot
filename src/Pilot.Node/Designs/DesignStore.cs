using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Designs;

/// <summary>A design verb the node refuses, with why.</summary>
public sealed class DesignException(string message) : Exception(message);

/// <summary>
/// The design store (PIL1096): <c>&lt;data&gt;/designs/DSN-&lt;n&gt;/</c> holding <c>BRIEF.md</c> (the owner's words, each piece verbatim under a
/// <c>## &lt;UTC time&gt;</c> heading, oldest first), <c>design.json</c> (the <see cref="DesignRow"/>), <c>draft/</c> and <c>versions/v&lt;k&gt;/</c>.
/// The node is its one writer. Ids come from <c>designs/ids.json</c>, a counter, never from the folders on disk, so an id is not handed out
/// twice even when a design's folder is deleted. Nothing here writes under <c>versions/</c>: a version is the publishing leg's.
/// </summary>
public sealed class DesignStore(string dataFolder, TimeProvider? time = null)
{
    /// <summary>The longest title taken from a brief's first line.</summary>
    public const int TitleLength = 80;

    public const string BriefFile = "BRIEF.md";
    public const string RecordFile = "design.json";
    public const string PrototypeFile = "prototype.json";
    public const string NotesFile = "NOTES.md";
    public const string DraftFolderName = "draft";
    public const string VersionsFolderName = "versions";

    /// <summary>What a design with no version starts its draft from: one screen, entry set, a single text node (DIR.Lib's prototype format).</summary>
    public const string StarterPrototype = """
        {
          "width": 1280,
          "height": 720,
          "entry": "home",
          "screens": [
            { "id": "home", "title": "Home", "root": { "kind": "text", "text": "New design" } }
          ]
        }
        """;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();

    /// <summary>The store's folder in <paramref name="dataFolder"/>.</summary>
    public static string FolderIn(string dataFolder) => Path.Combine(dataFolder, "designs");

    public string Root { get; } = FolderIn(dataFolder);

    /// <summary>The folder of design <paramref name="id"/>.</summary>
    public string FolderOf(string id) => Path.Combine(Root, Normalise(id));

    /// <summary><c>DSN-&lt;n&gt;</c> for an id in any case; throws when it is not one.</summary>
    public static string Normalise(string id)
    {
        var text = id.Trim();
        if (text.Length > 4 && text.StartsWith("DSN-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            return "DSN-" + n.ToString(CultureInfo.InvariantCulture);
        }
        throw new DesignException($"{id} is not a design id (DSN-<n>)");
    }

    /// <summary>The title a brief gets when none is given: its first non-empty line, cut to <see cref="TitleLength"/> characters.</summary>
    public static string TitleOf(string brief)
    {
        var line = brief.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= TitleLength ? line : line[..TitleLength];
    }

    /// <summary>Makes a design from <paramref name="brief"/>; its id comes from the counter.</summary>
    public DesignRow New(string? repo, string? title, string brief)
    {
        if (string.IsNullOrWhiteSpace(brief))
        {
            throw new DesignException("a design needs a brief: the owner's words");
        }
        lock (_lock)
        {
            Directory.CreateDirectory(Root);
            var next = ReadIds().Last + 1;
            // The counter moves first: a crash after it wastes a number, which is harmless; one before it would hand the number out twice.
            WriteAtomic(Path.Combine(Root, "ids.json"), JsonSerializer.Serialize(new DesignIds { Last = next }, PilotJsonContext.Default.DesignIds));
            var id = "DSN-" + next.ToString(CultureInfo.InvariantCulture);
            var row = new DesignRow
            {
                Id = id,
                Title = title?.Trim() is { Length: > 0 } given ? given : TitleOf(brief),
                Repo = repo?.Trim() is { Length: > 0 } named ? named : null,
                At = _time.GetUtcNow(),
                Versions = 0,
            };
            Directory.CreateDirectory(Path.Combine(Root, id));
            File.WriteAllText(Path.Combine(Root, id, BriefFile), Piece(brief, row.At!.Value));
            WriteRecord(row);
            return row;
        }
    }

    /// <summary>Appends <paramref name="text"/>, verbatim, under the time, to the brief of <paramref name="id"/>.</summary>
    public DesignRow Brief(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DesignException("there are no words to add to the brief");
        }
        lock (_lock)
        {
            var row = Find(id) ?? throw new DesignException($"There is no design {id}");
            File.AppendAllText(Path.Combine(FolderOf(row.Id), BriefFile), "\n" + Piece(text, _time.GetUtcNow()));
            return row;
        }
    }

    private static string Piece(string text, DateTimeOffset at) =>
        $"## {at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\n\n{text}\n";

    /// <summary>
    /// Makes <c>draft/</c> of <paramref name="id"/>: the current version's <c>prototype.json</c> and <c>NOTES.md</c>, or the starter prototype and an empty
    /// notes file when there is no version. Refused over an existing draft unless <paramref name="replace"/>. Returns the draft's folder.
    /// </summary>
    public string Draft(string id, bool replace)
    {
        lock (_lock)
        {
            var row = Find(id) ?? throw new DesignException($"There is no design {id}");
            var folder = FolderOf(row.Id);
            var draft = Path.Combine(folder, DraftFolderName);
            if (Directory.Exists(draft) && Directory.EnumerateFileSystemEntries(draft).Any())
            {
                if (!replace)
                {
                    throw new DesignException($"{row.Id} has a draft already ({draft}); --replace makes it again from the current version");
                }
                Directory.Delete(draft, recursive: true);
            }
            Directory.CreateDirectory(draft);
            var from = row.Current is { } current ? Path.Combine(folder, VersionsFolderName, "v" + current.ToString(CultureInfo.InvariantCulture)) : null;
            if (from is not null && File.Exists(Path.Combine(from, PrototypeFile)))
            {
                File.Copy(Path.Combine(from, PrototypeFile), Path.Combine(draft, PrototypeFile));
                var notes = Path.Combine(from, NotesFile);
                if (File.Exists(notes))
                {
                    File.Copy(notes, Path.Combine(draft, NotesFile));
                }
                else
                {
                    File.WriteAllText(Path.Combine(draft, NotesFile), "");
                }
            }
            else
            {
                File.WriteAllText(Path.Combine(draft, PrototypeFile), StarterPrototype);
                File.WriteAllText(Path.Combine(draft, NotesFile), "");
            }
            return draft;
        }
    }

    /// <summary>The record of <paramref name="id"/> (any case), or null when there is none or it cannot be read.</summary>
    public DesignRow? Find(string id)
    {
        string normal;
        try
        {
            normal = Normalise(id);
        }
        catch (DesignException)
        {
            return null;
        }
        var file = Path.Combine(Root, normal, RecordFile);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), PilotJsonContext.Default.DesignRow) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every design, newest first (by id, which only grows).</summary>
    public DesignRow[] List()
    {
        lock (_lock)
        {
            if (!Directory.Exists(Root))
            {
                return [];
            }
            return [.. Directory.EnumerateDirectories(Root, "DSN-*")
                .Select(d => Find(Path.GetFileName(d)))
                .OfType<DesignRow>()
                .OrderByDescending(r => int.Parse(r.Id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture))];
        }
    }

    /// <summary>The design whole: record, brief, versions and folders; throws when there is none.</summary>
    public DesignDetail Detail(string id)
    {
        lock (_lock)
        {
            var row = Find(id) ?? throw new DesignException($"There is no design {id}");
            var folder = FolderOf(row.Id);
            var briefFile = Path.Combine(folder, BriefFile);
            var versions = Path.Combine(folder, VersionsFolderName);
            var numbers = Directory.Exists(versions)
                ? Directory.EnumerateDirectories(versions, "v*").Select(d => int.TryParse(Path.GetFileName(d).AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).Where(n => n > 0).Order().ToArray()
                : [];
            return new DesignDetail
            {
                Row = row,
                Brief = File.Exists(briefFile) ? File.ReadAllText(briefFile) : null,
                Versions = [.. numbers.Select(n => new DesignVersionRow
                {
                    Number = n,
                    At = n == row.Current ? row.At : null,
                    Why = n == row.Current ? row.Why : null,
                    PrototypeSha256 = n == row.Current ? row.PrototypeSha256 : null,
                })],
                Folder = folder,
                DraftFolder = Path.Combine(folder, DraftFolderName),
                CurrentFolder = row.Current is { } c ? Path.Combine(versions, "v" + c.ToString(CultureInfo.InvariantCulture)) : null,
            };
        }
    }

    /// <summary>Deletes the design's folder. Its id is not handed out again: the counter stays.</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            var folder = FolderOf(id);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>The hex SHA-256 of a file's bytes, for the record's hash of a prototype.</summary>
    public static string Sha256Of(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    private DesignIds ReadIds()
    {
        var file = Path.Combine(Root, "ids.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), PilotJsonContext.Default.DesignIds) ?? new DesignIds() : MaxOnDiskIds();
        }
        catch (JsonException)
        {
            // A counter that cannot be read is rebuilt above every folder on disk: never below a number that was handed out and kept.
            return MaxOnDiskIds();
        }
    }

    private DesignIds MaxOnDiskIds() => new()
    {
        Last = Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root, "DSN-*").Select(d => int.TryParse(Path.GetFileName(d).AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max()
            : 0,
    };

    private void WriteRecord(DesignRow row) =>
        WriteAtomic(Path.Combine(FolderOf(row.Id), RecordFile), JsonSerializer.Serialize(row, PilotJsonContext.Default.DesignRow));

    private static void WriteAtomic(string file, string text)
    {
        var beside = file + ".tmp";
        File.WriteAllText(beside, text, new UTF8Encoding(false));
        File.Move(beside, file, overwrite: true);
    }
}
