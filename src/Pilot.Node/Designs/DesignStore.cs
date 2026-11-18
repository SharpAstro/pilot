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
    /// <summary>The longest title taken from a brief's first line (<see cref="DesignTitle.MaxLength"/>).</summary>
    public const int TitleLength = DesignTitle.MaxLength;

    public const string BriefFile = "BRIEF.md";
    public const string RecordFile = "design.json";
    public const string PrototypeFile = "prototype.json";
    /// <summary>The designer's own notes on a version (optional since PIL1171; the design is <see cref="DocumentFile"/>).</summary>
    public const string NotesFile = "NOTES.md";
    public const string DocumentFile = DesignDocument.FileName;
    public const string ScreensFolderName = DesignFiles.ScreensFolder;
    public const string DraftFolderName = "draft";
    public const string ChatFile = "chat.jsonl";
    public const string PicturesFolderName = "pictures";
    public const string VersionsFolderName = "versions";

    /// <summary>What a design with no version starts its draft's <c>prototype.json</c> from: the header, entry set, no screens (they are files under <c>screens/</c>).</summary>
    public const string StarterPrototype = """
        {
          "width": 1280,
          "height": 720,
          "entry": "home",
          "screens": []
        }
        """;

    /// <summary>The starter's one screen, <c>screens/home.json</c>: a single text node (DIR.Lib's prototype format).</summary>
    public const string StarterScreen = """
        { "id": "home", "title": "Home", "root": { "kind": "text", "text": "New design" } }
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
        // A design read from its GitHub issue (PIL1171) is GH-<owner>-<repo>-<n>, so it can never be a DSN id a design made here gets.
        if (text.Length > 3 && text.StartsWith("GH-", StringComparison.OrdinalIgnoreCase) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
        {
            return "GH-" + text[3..];
        }
        throw new DesignException($"{id} is not a design id (DSN-<n>)");
    }

    /// <summary>The id a design read from issue <paramref name="number"/> of <paramref name="repo"/> (<c>owner/name</c>) has here: <c>GH-&lt;owner&gt;-&lt;name&gt;-&lt;n&gt;</c>.</summary>
    public static string MirrorId(string repo, int number) =>
        "GH-" + new string([.. repo.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-')]) + "-" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Refuses a change to a design this node only reads from its GitHub issue (PIL1171).</summary>
    private static DesignRow Writable(DesignRow row) =>
        row.MadeOn is { Length: > 0 } node
            ? throw new DesignException($"{row.Id} is read from {row.IssueRepo}#{row.IssueNumber}, made on {node}: it is changed there, not here")
            : row.LockedBy is { Length: > 0 } editor && row.LockHeld != true
                ? throw new DesignException($"{row.Id}: {DesignLockText.BeingEdited(editor)}")
                : row;

    /// <summary>The title a brief gets when none is given: <see cref="DesignTitle.Of"/>, its first line whole, or cut at a word, or at the limit when it has no space, with an ellipsis.</summary>
    public static string TitleOf(string brief) => DesignTitle.Of(brief);

    private static string[] NormaliseTerminals(string[] sizes) =>
        DesignTerminals.TryNormalise(sizes, out var normalised, out var bad)
            ? normalised
            : throw new DesignException($"'{bad}' is not a terminal size: give <cols>x<rows>, for example {string.Join(", ", DesignTerminals.Standard)}");

    /// <summary>Sets the terminal sizes of design <paramref name="id"/> (each <c>&lt;cols&gt;x&lt;rows&gt;</c>); none clears them. Returns the record.</summary>
    public DesignRow SetTerminals(string id, string[] sizes)
    {
        var normalised = NormaliseTerminals(sizes);
        lock (_lock)
        {
            var row = Writable(Find(id) ?? throw new DesignException($"There is no design {id}"));
            var updated = row with { Terminals = normalised };
            WriteRecord(updated);
            return updated;
        }
    }

    /// <summary>Makes a design from <paramref name="brief"/>; its id comes from the counter. <paramref name="terminals"/> are its terminal targets.</summary>
    public DesignRow New(string? repo, string? title, string brief, string[]? terminals = null, string? fromIdea = null)
    {
        if (string.IsNullOrWhiteSpace(brief))
        {
            throw new DesignException("a design needs a brief: the owner's words");
        }
        var sizes = NormaliseTerminals(terminals ?? []);
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
                Terminals = sizes,
                FromIdea = fromIdea,
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
            var row = Writable(Find(id) ?? throw new DesignException($"There is no design {id}"));
            File.AppendAllText(Path.Combine(FolderOf(row.Id), BriefFile), "\n" + Piece(text, _time.GetUtcNow()));
            return row;
        }
    }

    /// <summary>
    /// The owner's words in the design's chat (PIL1151): appended to the brief verbatim under their time (<see cref="Brief"/>), and to <c>chat.jsonl</c> with the picture
    /// kept under <c>pictures/</c> when there is one. Returns the picture's full path, or null with none.
    /// </summary>
    public string? Say(string id, string text, string? pictureName, byte[]? picture)
    {
        lock (_lock)
        {
            var row = Brief(id, text);
            var folder = FolderOf(row.Id);
            string? relative = null;
            string? full = null;
            if (picture is { Length: > 0 })
            {
                (relative, full) = AddPicture(row.Id, pictureName, picture);
            }
            var line = new DesignChatLine { At = _time.GetUtcNow(), Text = text, Picture = relative };
            File.AppendAllText(Path.Combine(folder, ChatFile), JsonSerializer.Serialize(line, PilotJsonContext.Default.DesignChatLine) + "\n");
            return full;
        }
    }

    /// <summary>Keeps <paramref name="picture"/> under <c>pictures/</c> of <paramref name="id"/> as <c>&lt;n&gt;-&lt;name&gt;</c>; returns its path relative to the design's folder (with <c>/</c>) and in full.</summary>
    public (string Relative, string Full) AddPicture(string id, string? pictureName, byte[] picture)
    {
        lock (_lock)
        {
            var folder = FolderOf(id);
            var pictures = Path.Combine(folder, PicturesFolderName);
            Directory.CreateDirectory(pictures);
            var clean = new string([.. Path.GetFileName(pictureName ?? "picture").Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]).TrimStart('.');
            var n = Directory.EnumerateFiles(pictures).Count() + 1;
            var relative = $"{PicturesFolderName}/{n.ToString(CultureInfo.InvariantCulture)}-{(clean.Length > 0 ? clean : "picture")}";
            var full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(full, picture);
            return (relative, full);
        }
    }

    /// <summary>The design started from the idea <paramref name="fromIdea"/> (qualified, <c>pilot I381</c>; any case), the oldest when there are several; null when there is none.</summary>
    public DesignRow? FindByIdea(string fromIdea) =>
        List().Where(d => d.MadeOn is null && string.Equals(d.FromIdea, fromIdea, StringComparison.OrdinalIgnoreCase)).OrderBy(d => int.Parse(d.Id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture)).FirstOrDefault();

    /// <summary>What the owner has said in the chat of <paramref name="id"/>, oldest first; lines that cannot be read are left out.</summary>
    public DesignChatLine[] Says(string id)
    {
        lock (_lock)
        {
            var file = Path.Combine(FolderOf(id), ChatFile);
            if (!File.Exists(file))
            {
                return [];
            }
            var lines = new List<DesignChatLine>();
            foreach (var text in File.ReadAllLines(file))
            {
                try
                {
                    if (text.Length > 0 && JsonSerializer.Deserialize(text, PilotJsonContext.Default.DesignChatLine) is { } line)
                    {
                        lines.Add(line);
                    }
                }
                catch (JsonException)
                {
                }
            }
            return [.. lines];
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
            var row = Writable(Find(id) ?? throw new DesignException($"There is no design {id}"));
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
                var screens = Path.Combine(from, ScreensFolderName);
                if (File.Exists(Path.Combine(from, DocumentFile)))
                {
                    CopyWritable(Path.Combine(from, DocumentFile), Path.Combine(draft, DocumentFile));
                }
                else
                {
                    // An older version: its NOTES.md becomes the notes of the starter document, and its screens are split into screens/.
                    var old = Path.Combine(from, NotesFile);
                    File.WriteAllText(Path.Combine(draft, DocumentFile), DesignDocument.Starter(row.Title ?? row.Id, "home", File.Exists(old) ? File.ReadAllText(old).Trim() : "").Format());
                }
                var json = File.ReadAllText(Path.Combine(from, PrototypeFile));
                if (Directory.Exists(screens))
                {
                    CopyWritable(Path.Combine(from, PrototypeFile), Path.Combine(draft, PrototypeFile));
                    Directory.CreateDirectory(Path.Combine(draft, ScreensFolderName));
                    foreach (var file in Directory.EnumerateFiles(screens, "*.json"))
                    {
                        CopyWritable(file, Path.Combine(draft, ScreensFolderName, Path.GetFileName(file)));
                    }
                }
                else if (DesignFiles.Split(json) is { } split && split.Screens.Length > 0)
                {
                    File.WriteAllText(Path.Combine(draft, PrototypeFile), split.Header);
                    Directory.CreateDirectory(Path.Combine(draft, ScreensFolderName));
                    foreach (var (screenId, screen) in split.Screens)
                    {
                        File.WriteAllText(Path.Combine(draft, ScreensFolderName, screenId + ".json"), screen);
                    }
                    if (!File.Exists(Path.Combine(from, DocumentFile)))
                    {
                        // The starter's cell is replaced by one cell per screen, headed by the screen's title, with the notes kept on the first.
                        var notes = File.Exists(Path.Combine(from, NotesFile)) ? File.ReadAllText(Path.Combine(from, NotesFile)).Trim() : "";
                        var cells = split.Screens.Select((s, i) => new DesignCell(ScreenTitle(s.Json, s.Id), "", [new DesignFrame(s.Id)], i == 0 ? notes : "")).ToArray();
                        File.WriteAllText(Path.Combine(draft, DocumentFile), new DesignDocument(row.Title ?? row.Id, "", cells).Format());
                    }
                }
                else
                {
                    CopyWritable(Path.Combine(from, PrototypeFile), Path.Combine(draft, PrototypeFile));
                }
            }
            else
            {
                File.WriteAllText(Path.Combine(draft, PrototypeFile), StarterPrototype);
                Directory.CreateDirectory(Path.Combine(draft, ScreensFolderName));
                File.WriteAllText(Path.Combine(draft, ScreensFolderName, "home.json"), StarterScreen);
                File.WriteAllText(Path.Combine(draft, DocumentFile), DesignDocument.Starter(row.Title ?? row.Id).Format());
            }
            return draft;
        }
    }

    /// <summary>Rewrites the record of <paramref name="id"/> as <paramref name="change"/> makes it, under the store's lock, so a designer's state is never written over a publish. Returns the new record.</summary>
    public DesignRow Update(string id, Func<DesignRow, DesignRow> change)
    {
        lock (_lock)
        {
            var row = Find(id) ?? throw new DesignException($"There is no design {id}");
            var updated = change(row);
            WriteRecord(updated);
            return updated;
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
            // The designs made here newest first (by id, which only grows), then the ones read from an issue, newest first.
            return [.. Directory.EnumerateDirectories(Root, "DSN-*").Concat(Directory.EnumerateDirectories(Root, "GH-*"))
                .Select(d => Find(Path.GetFileName(d)))
                .OfType<DesignRow>()
                .OrderBy(r => r.MadeOn is null ? 0 : 1)
                .ThenByDescending(r => r.MadeOn is null && r.Id.StartsWith("DSN-", StringComparison.Ordinal) ? int.Parse(r.Id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture) : 0)
                .ThenByDescending(r => r.At)];
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
                Versions = [.. numbers.Select(n => row.History.FirstOrDefault(v => v.Number == n) ?? new DesignVersionRow
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
                // A version's files are read-only, which Windows will not delete.
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static string ScreenTitle(string screenJson, string fallback)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(screenJson) is System.Text.Json.Nodes.JsonObject o && o["title"] is { } t && t.GetValue<string>() is { Length: > 0 } title ? title : fallback;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private static void CopyWritable(string from, string to)
    {
        File.Copy(from, to);
        // A file copied from a version is read-only too; the draft is the one place a design is edited.
        File.SetAttributes(to, FileAttributes.Normal);
    }

    /// <summary>The prototype of <paramref name="id"/>'s draft, <c>draft/prototype.json</c>; throws when the design or the draft is not there.</summary>
    private string DraftPrototypeOf(string id, out DesignRow row)
    {
        row = Writable(Find(id) ?? throw new DesignException($"There is no design {id}"));
        var file = Path.Combine(FolderOf(row.Id), DraftFolderName, PrototypeFile);
        return File.Exists(file) ? file : throw new DesignException($"{row.Id} has no draft prototype ({file}); pilot design draft {row.Id} makes one");
    }

    /// <summary>
    /// Checks the draft of <paramref name="id"/>: every problem found, a line each (a loader problem with its JSON path, a finding as
    /// <c>screen | width x height | rule: message</c>); empty when it is clean at every <see cref="DesignSizes"/>.
    /// </summary>
    public string[] Check(string id)
    {
        lock (_lock)
        {
            var file = DraftPrototypeOf(id, out var row);
            var problems = DesignChecker.Problems(file, terminals: row.Terminals, requireDocument: true);
            Update(row.Id, r => r with { DesignerPhase = DesignerPhases.Checking, CheckFindings = problems.Length });
            return problems;
        }
    }

    /// <summary>Draws every screen of the draft at the design size into <c>draft/renders/</c> and stamps them with the hash of the prototype drawn. Refused while the prototype does not load. Returns the screen ids.</summary>
    public string[] Render(string id)
    {
        lock (_lock)
        {
            var file = DraftPrototypeOf(id, out var row);
            var document = DesignChecker.Load(file, out var problems)
                ?? throw new DesignException($"{row.Id} draft does not load, so there is nothing to render:\n{string.Join('\n', problems)}");
            var screens = DesignChecker.Render(document, Path.Combine(FolderOf(row.Id), DraftFolderName, DesignChecker.RendersFolderName), Sha256Of(file), terminals: row.Terminals);
            Update(row.Id, r => r with { DesignerPhase = DesignerPhases.Rendering });
            return screens;
        }
    }

    /// <summary>The prose parts the owner can type (<see cref="Edit"/>): a design's <c>objective</c> or <c>notes</c>, a cell's <c>heading</c>, <c>objective</c> or <c>notes</c>.</summary>
    public static readonly string[] EditParts = ["objective", "heading", "notes"];

    /// <summary>
    /// The owner typed a prose part of DESIGN.md (PIL1171): <paramref name="part"/> is <c>objective</c>, <c>heading</c> or <c>notes</c>, <paramref name="cell"/> names the cell
    /// (its heading in any case, the id of a screen it shows, or its 1-based number) or is empty for the design itself (objective and notes only). The change goes into the
    /// draft's DESIGN.md through <see cref="DesignDocument"/>, so the rest of the document is as <see cref="DesignDocument.Format"/> writes it; a frame's source is not
    /// reachable from here. With no draft, the draft is made from the current version first. Returns the text the part now reads.
    /// </summary>
    public string Edit(string id, string part, string cell, string text)
    {
        part = part.Trim().ToLowerInvariant();
        if (!EditParts.Contains(part, StringComparer.Ordinal))
        {
            throw new DesignException($"'{part}' is not a part the owner can type: {string.Join(", ", EditParts)}");
        }
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (part != "notes" && text.Length == 0)
        {
            throw new DesignException($"the {part} cannot be empty: a screen needs a heading and an objective, and so does the design");
        }
        if (part is "heading" && text.Contains('\n'))
        {
            throw new DesignException("a heading is one line");
        }
        if (part is "objective" && text.Contains("\n\n", StringComparison.Ordinal))
        {
            throw new DesignException("an objective is one paragraph: one or two lines, with no blank line in it");
        }
        if (text.Split('\n').Any(l => l.StartsWith("```", StringComparison.Ordinal) || part != "notes" && l.StartsWith('#') || l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("## ", StringComparison.Ordinal)))
        {
            throw new DesignException("typed prose cannot hold a code fence, or a line starting with # (a heading line would split the cell): a frame's source is changed through the designer");
        }
        lock (_lock)
        {
            var row = Writable(Find(id) ?? throw new DesignException($"There is no design {id}"));
            var draft = Path.Combine(FolderOf(row.Id), DraftFolderName);
            var file = Path.Combine(draft, DocumentFile);
            if (!File.Exists(file))
            {
                Draft(row.Id, replace: true);
            }
            var document = DesignDocument.Parse(File.ReadAllText(file));
            if (cell.Trim().Length == 0)
            {
                if (part == "heading")
                {
                    throw new DesignException("a heading belongs to a cell: name the cell");
                }
                document = part == "objective" ? document with { Objective = text } : document with { Notes = text };
            }
            else
            {
                var index = CellIndex(document, cell.Trim());
                var cells = document.Cells.ToList();
                cells[index] = part switch
                {
                    "heading" => cells[index] with { Heading = text },
                    "objective" => cells[index] with { Objective = text },
                    _ => cells[index] with { Notes = text },
                };
                document = document with { Cells = cells };
            }
            File.WriteAllText(file, document.Format());
            return text;
        }
    }

    private static int CellIndex(DesignDocument document, string cell)
    {
        for (var i = 0; i < document.Cells.Count; i++)
        {
            if (string.Equals(document.Cells[i].Heading, cell, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        for (var i = 0; i < document.Cells.Count; i++)
        {
            if (document.Cells[i].Frames.Any(f => string.Equals(f.Id, cell, StringComparison.Ordinal)))
            {
                return i;
            }
        }
        if (int.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= document.Cells.Count)
        {
            return number - 1;
        }
        throw new DesignException($"there is no cell '{cell}' in the document: name its heading, a screen it shows, or its number (1 to {document.Cells.Count})");
    }

    /// <summary>Removes the draft of <paramref name="id"/> (PIL1171): what was typed or designed there and not published is gone. Refused when there is none.</summary>
    public void Discard(string id)
    {
        lock (_lock)
        {
            var row = Writable(Find(id) ?? throw new DesignException($"There is no design {id}"));
            var draft = Path.Combine(FolderOf(row.Id), DraftFolderName);
            if (!Directory.Exists(draft))
            {
                throw new DesignException($"{row.Id} has no draft to discard");
            }
            foreach (var file in Directory.EnumerateFiles(draft, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(draft, recursive: true);
            if (row.DraftWhy is not null)
            {
                WriteRecord(row with { DraftWhy = null });
            }
        }
    }

    /// <summary>
    /// When the draft's prototype and screens hash as the current version's, the version's renders become the draft's, with the stamp (the renders are then of the
    /// draft as it stands, as nothing they draw changed). Returns whether it did.
    /// </summary>
    private bool TakeCurrentRenders(DesignRow row, string draft, string hash)
    {
        if (row.Current is not { } current)
        {
            return false;
        }
        var from = Path.Combine(FolderOf(row.Id), VersionsFolderName, "v" + current.ToString(CultureInfo.InvariantCulture));
        var prototype = Path.Combine(from, PrototypeFile);
        var renders = Path.Combine(from, DesignChecker.RendersFolderName);
        if (!File.Exists(prototype) || !Directory.Exists(renders) || Sha256Of(prototype) != hash)
        {
            return false;
        }
        var into = Path.Combine(draft, DesignChecker.RendersFolderName);
        if (Directory.Exists(into))
        {
            Directory.Delete(into, recursive: true);
        }
        Directory.CreateDirectory(into);
        foreach (var file in Directory.EnumerateFiles(renders))
        {
            CopyWritable(file, Path.Combine(into, Path.GetFileName(file)));
        }
        File.WriteAllText(Path.Combine(into, DesignChecker.StampFile), JsonSerializer.Serialize(new DesignRenderStamp { PrototypeSha256 = hash }, PilotJsonContext.Default.DesignRenderStamp));
        return true;
    }

    /// <summary>
    /// Makes version k+1 (k the current, 0 for none) from the draft of <paramref name="id"/>: <c>prototype.json</c>, <c>NOTES.md</c> and <c>renders/</c>, every file
    /// read-only, and adds it to the record. Refused when the draft or its <c>NOTES.md</c> is missing or empty, when the check finds anything, when the renders are
    /// not of the prototype as it stands, or when <paramref name="why"/> is not a short one-line reason (<see cref="DesignWhys.Problem"/>). A version folder that exists is never written again.
    /// </summary>
    public DesignRow Publish(string id, string why, bool owner = false)
    {
        // A typed draft (PIL1171) is published by the owner: the version's why reads 'owner: <why>', and the whole line is held to the same rules.
        var badWhy = DesignWhys.Problem(why);
        if (badWhy is null && owner)
        {
            badWhy = DesignWhys.Problem(DesignWhys.OwnerPrefix + why.Trim());
        }
        if (badWhy is not null)
        {
            throw new DesignException(badWhy);
        }
        why = (owner ? DesignWhys.OwnerPrefix : "") + why.Trim();
        lock (_lock)
        {
            var prototype = DraftPrototypeOf(id, out var row);
            var draft = Path.GetDirectoryName(prototype)!;
            var notes = Path.Combine(draft, NotesFile);
            var designFile = Path.Combine(draft, DocumentFile);
            if (!File.Exists(designFile) || string.IsNullOrWhiteSpace(File.ReadAllText(designFile)))
            {
                throw new DesignException($"{row.Id} has no {DocumentFile}: the draft's {DocumentFile} is missing or empty, and a version is a document with an objective for each screen");
            }
            var document = DesignChecker.Load(prototype, out var problems);
            var found = document is null ? problems : DesignChecker.Problems(prototype, terminals: row.Terminals, requireDocument: true);
            if (found.Length > 0)
            {
                throw new DesignException($"{row.Id} draft is not clean, so there is no version to make:\n{string.Join('\n', found)}");
            }
            var hash = Sha256Of(prototype);
            var renders = Path.Combine(draft, DesignChecker.RendersFolderName);
            if (DesignChecker.StampOf(renders) != hash && !TakeCurrentRenders(row, draft, hash))
            {
                throw new DesignException($"{row.Id} renders are not of the draft as it stands (prototype.json changed or was never rendered); pilot design render {row.Id} draws them again");
            }
            var pngs = DesignChecker.RenderFiles(document!).Select(r => (Path: Path.Combine(renders, r.File), r.Target)).ToArray();
            if (pngs.FirstOrDefault(p => !File.Exists(p.Path)) is { Path: not null } missing)
            {
                var atTarget = missing.Target.Length > 0 ? $" at target {missing.Target}" : "";
                throw new DesignException($"{row.Id} has no render {Path.GetFileName(missing.Path)}{atTarget}; pilot design render {row.Id} draws them again");
            }

            var versions = Path.Combine(FolderOf(row.Id), VersionsFolderName);
            var number = (row.Current ?? 0) + 1;
            var target = Path.Combine(versions, "v" + number.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new DesignException($"{row.Id} v{number} exists already ({target}); a version is never written again");
            }
            // Built beside, then moved into place whole: a crash leaves no half version under a name a reader trusts.
            var building = Path.Combine(versions, ".publishing-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(building, DesignChecker.RendersFolderName));
            try
            {
                File.Copy(prototype, Path.Combine(building, PrototypeFile));
                File.Copy(designFile, Path.Combine(building, DocumentFile));
                if (File.Exists(notes))
                {
                    File.Copy(notes, Path.Combine(building, NotesFile));
                }
                if (Directory.Exists(Path.Combine(draft, ScreensFolderName)))
                {
                    Directory.CreateDirectory(Path.Combine(building, ScreensFolderName));
                    foreach (var screen in Directory.EnumerateFiles(Path.Combine(draft, ScreensFolderName), "*.json"))
                    {
                        File.Copy(screen, Path.Combine(building, ScreensFolderName, Path.GetFileName(screen)));
                    }
                }
                foreach (var (png, _) in pngs)
                {
                    File.Copy(png, Path.Combine(building, DesignChecker.RendersFolderName, Path.GetFileName(png)));
                }
                foreach (var file in Directory.EnumerateFiles(building, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.ReadOnly);
                }
                Directory.Move(building, target);
            }
            catch
            {
                if (Directory.Exists(building))
                {
                    foreach (var file in Directory.EnumerateFiles(building, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(building, recursive: true);
                }
                throw;
            }

            var at = _time.GetUtcNow();
            var made = row with
            {
                Current = number,
                Versions = number,
                Why = why,
                PrototypeSha256 = hash,
                DesignerPhase = DesignerPhases.Published,
                // What waited on GitHub or was suggested for the why belongs to the draft this version was made from.
                GitHubEdits = [],
                DraftWhy = null,
                History = [.. row.History, new DesignVersionRow { Number = number, At = at, Why = why, PrototypeSha256 = hash }],
            };
            WriteRecord(made);
            return made;
        }
    }

    /// <summary>Every file of version <paramref name="version"/> of <paramref name="id"/> (path with <c>/</c>, bytes), for writing it to GitHub; throws when the version is not there.</summary>
    public IReadOnlyDictionary<string, byte[]> ReadVersion(string id, int version)
    {
        var folder = Path.Combine(FolderOf(id), VersionsFolderName, "v" + version.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(folder))
        {
            throw new DesignException($"{id} has no version {version}");
        }
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }
        return files;
    }

    /// <summary>The files a version holds, as a design read from GitHub keeps them: nothing else is taken from a branch, and no path leaves the version's folder.</summary>
    public static bool IsVersionFile(string path) =>
        !path.Contains("..", StringComparison.Ordinal) && !path.StartsWith('/') && !path.Contains('\\')
        && (path is PrototypeFile or DocumentFile or NotesFile
            || path.StartsWith(ScreensFolderName + "/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal) && path.IndexOf('/', ScreensFolderName.Length + 1) < 0
            || path.StartsWith(DesignChecker.RendersFolderName + "/", StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.Ordinal) && path.IndexOf('/', DesignChecker.RendersFolderName.Length + 1) < 0);

    /// <summary>
    /// Keeps version <paramref name="version"/> of a design read from its GitHub issue (PIL1171): <paramref name="files"/> (only <see cref="IsVersionFile"/> ones) under
    /// <c>versions/v&lt;k&gt;/</c>, read-only, and <paramref name="row"/> as its record, with the version added to its history when the record has none of that number.
    /// A version already kept is not written again. Returns the record.
    /// </summary>
    public DesignRow Mirror(DesignRow row, int version, string? why, IReadOnlyDictionary<string, byte[]> files)
    {
        lock (_lock)
        {
            var id = Normalise(row.Id);
            var folder = FolderOf(id);
            var versions = Path.Combine(folder, VersionsFolderName);
            var target = Path.Combine(versions, "v" + version.ToString(CultureInfo.InvariantCulture));
            var at = _time.GetUtcNow();
            if (!Directory.Exists(target))
            {
                var building = Path.Combine(versions, ".mirroring-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(building);
                try
                {
                    foreach (var (path, bytes) in files.Where(f => IsVersionFile(f.Key)))
                    {
                        var to = Path.Combine(building, path.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.WriteAllBytes(to, bytes);
                    }
                    foreach (var file in Directory.EnumerateFiles(building, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.ReadOnly);
                    }
                    Directory.Move(building, target);
                }
                catch
                {
                    if (Directory.Exists(building))
                    {
                        foreach (var file in Directory.EnumerateFiles(building, "*", SearchOption.AllDirectories))
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }
                        Directory.Delete(building, recursive: true);
                    }
                    throw;
                }
            }
            var prototype = Path.Combine(target, PrototypeFile);
            var hash = File.Exists(prototype) ? Sha256Of(prototype) : null;
            var history = row.History.Any(v => v.Number == version) ? row.History : [.. row.History, new DesignVersionRow { Number = version, At = at, Why = why, PrototypeSha256 = hash, SyncedAt = at }];
            var made = row with { Id = id, Current = version, Versions = version, Why = why, PrototypeSha256 = hash, History = history, SyncedVersion = version, At = row.At ?? at };
            WriteRecord(made);
            return made;
        }
    }

    /// <summary>The hex SHA-256 of a file's bytes, for the record's hash of a prototype.</summary>
    public static string Sha256Of(string file) => DesignFiles.Sha256Of(file);

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
