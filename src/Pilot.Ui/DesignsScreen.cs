using System.Globalization;
using DIR.Lib.Prototyping;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>One screen of a version's renders: its id, its title and the PNG the node drew of it (<c>renders/&lt;id&gt;.png</c>).</summary>
internal sealed record DesignRender(string Screen, string Title, string? File, IReadOnlyDictionary<string, string>? ByTarget = null);

/// <summary>
/// A design's page as the host read it: the design whole, the version shown (the current one unless a version was picked), and that version's files,
/// read from the paths <c>design show</c> returned: its <c>DESIGN.md</c>, its <c>prototype.json</c> (with its <c>screens/</c>) loaded by DIR.Lib's Prototype, its <c>NOTES.md</c> and its renders.
/// </summary>
/// <param name="Detail">What <c>design show</c> said.</param>
/// <param name="Version">The version on show; null while the design has none.</param>
/// <param name="Document">The version's prototype; null while there is no version, or when its file did not load (<see cref="Problem"/> says why).</param>
internal sealed record DesignPage(DesignDetail Detail, int? Version, PrototypeDocument? Document, string? Notes, IReadOnlyList<DesignRender> Renders, string? Problem = null)
{
    /// <summary>The version's DESIGN.md as read (PIL1171): the title, the design's objective and one cell per screen. Null for a version with none, which <see cref="Notebook"/> then makes up.</summary>
    public DesignDocument? Doc { get; init; }

    /// <summary>
    /// The document the page shows: <see cref="Doc"/>, else for an older version (one with no DESIGN.md) one cell per screen of its prototype, headed by the screen's title,
    /// whose notes are the version's NOTES.md on the first cell only, as the draft the store makes from such a version keeps them (DesignStore). Null while there is no version or its prototype did not load and it has no document.
    /// </summary>
    public DesignDocument? Notebook => Merged(Baseline, Draft);

    /// <summary>The draft's DESIGN.md (PIL1171), read when the version on show is the current one and the design has a draft: what the owner typed and the designer wrote and has not published. Null with none.</summary>
    public DesignDocument? Draft { get; init; }

    private DesignDocument? Baseline => Doc ?? (Document is { } prototype
        ? new DesignDocument(Detail.Row.Title ?? Detail.Row.Id, null,
            [.. prototype.Screens.Select((s, i) => new DesignCell(string.IsNullOrEmpty(s.Title) ? s.Id : s.Title, null, [new DesignFrame(s.Id)], i == 0 ? (Notes ?? "").Trim() : ""))])
        : null);

    // The version's document with the draft's prose laid over it, cell by cell where the draft's cell shows the same screens; the frames are always the version's.
    private static DesignDocument? Merged(DesignDocument? version, DesignDocument? draft)
    {
        if (version is null || draft is null)
        {
            return version;
        }
        var cells = version.Cells.Select((c, i) => i < draft.Cells.Count && SameFrames(c, draft.Cells[i]) ? c with { Heading = draft.Cells[i].Heading, Objective = draft.Cells[i].Objective, Notes = draft.Cells[i].Notes } : c).ToList();
        return version with { Objective = draft.Objective, Notes = draft.Notes, Cells = cells };
    }

    private static bool SameFrames(DesignCell a, DesignCell b) => a.Frames.Select(f => f.Id).SequenceEqual(b.Frames.Select(f => f.Id), StringComparer.Ordinal);

    /// <summary>Whether cell <paramref name="index"/> reads differently in the draft than in the version on show: its heading, objective or notes were changed and not published.</summary>
    public bool CellIsDraft(int index) =>
        Draft is { } draft && Baseline is { } version && index < version.Cells.Count && index < draft.Cells.Count && SameFrames(version.Cells[index], draft.Cells[index])
        && (version.Cells[index].Heading != draft.Cells[index].Heading || (version.Cells[index].Objective ?? "") != (draft.Cells[index].Objective ?? "") || version.Cells[index].Notes != draft.Cells[index].Notes);

    /// <summary>Whether the design's own objective or notes read differently in the draft.</summary>
    public bool DesignIsDraft => Draft is { } draft && Baseline is { } version && ((version.Objective ?? "") != (draft.Objective ?? "") || version.Notes != draft.Notes);

    /// <summary>Whether the draft changes any prose of the version on show.</summary>
    public bool HasDraftChange => DesignIsDraft || Enumerable.Range(0, Baseline?.Cells.Count ?? 0).Any(CellIsDraft);

    /// <summary>The page as a dump writes it, a line a part.</summary>
    public string ToDump() => string.Join('\n',
        [$"{Detail.Row.Id} {Detail.Row.Title}", $"version {(Version is { } v ? "v" + v.ToString(CultureInfo.InvariantCulture) : "none")}",
         .. Renders.Select(r => $"  screen {r.Screen} | {r.Title}"), Problem is null ? "" : $"problem: {Problem}"]);
}

/// <summary>The Designs page's data: the designs newest first and, when one is open, its page.</summary>
internal sealed record DesignsScreen(DesignRow[] Rows, string Source, DateTimeOffset AsOf, DesignPage? Open = null)
{
    /// <summary>The snapshot's ideas (PIL1152): the marked ones with no design are listed under the brief input. Null where the host has none.</summary>
    public IdeaRow[]? Ideas { get; init; }

    /// <summary>The snapshot's flights (PIL1153): the ones built from a design version (<see cref="FlightRow.FromDesign"/>) are shown on its page. Null where the host has none.</summary>
    public IReadOnlyList<FlightRow>? Flights { get; init; }

    /// <summary>The open milestones of every repo (PIL1153): the choices when a design from an idea is built. Null where the host has none.</summary>
    public IReadOnlyList<MilestoneRow>? Milestones { get; init; }

    /// <summary>The repos the node tracks, by folder name (PIL1153): the choices when a design with no repo is built. Null where the host has none.</summary>
    public IReadOnlyList<string>? Repos { get; init; }

    public static DesignsScreen Empty(string source, DateTimeOffset at) => new([], source, at);
}

/// <summary>Reads a version's files into a <see cref="DesignPage"/>, with no node: the files are the paths <c>design show</c> returns.</summary>
internal static class DesignPages
{
    /// <summary>The folder of version <paramref name="number"/> of the design <paramref name="detail"/> describes, or null when the folder is not given.</summary>
    public static string? VersionFolder(DesignDetail detail, int number) =>
        detail.Folder is { Length: > 0 } folder ? Path.Combine(folder, "versions", "v" + number.ToString(CultureInfo.InvariantCulture)) : null;

    /// <summary>The page of <paramref name="detail"/> at <paramref name="version"/> (null: the current one, or none).</summary>
    public static DesignPage Load(DesignDetail detail, int? version = null)
    {
        var number = version ?? detail.Row.Current;
        if (number is not { } n || VersionFolder(detail, n) is not { } folder)
        {
            return new DesignPage(detail, null, null, null, []);
        }
        // The document of the version (PIL1171) and, beside it, the notes an older version has instead of one.
        var notes = Read(Path.Combine(folder, "NOTES.md"));
        var doc = Read(Path.Combine(folder, DesignDocument.FileName)) is { } text ? DesignDocument.Parse(text) : null;
        var prototypeFile = Path.Combine(folder, "prototype.json");
        var json = File.Exists(prototypeFile) ? DesignFiles.AssembledJson(prototypeFile, out _) : null;
        if (json is null)
        {
            return new DesignPage(detail, n, null, notes, [], $"v{n} has no readable prototype.json") { Doc = doc };
        }
        var loaded = Prototype.Load(json);
        if (loaded.Document is not { } document)
        {
            return new DesignPage(detail, n, null, notes, [], string.Join("; ", loaded.Problems.Select(p => $"{p.Path}: {p.Message}"))) { Doc = doc };
        }
        var renders = document.Screens.Select(s =>
        {
            var file = Path.Combine(folder, "renders", s.Id + ".png");
            if (!File.Exists(file) && document.Targets.Length > 0)
            {
                // A design that names targets has a PNG per target and none at the design size: the first target's stands for the screen.
                file = Path.Combine(folder, "renders", DesignTargets.RenderFile(s.Id, document.Targets[0].Name));
            }
            // Each target's own PNG, where the node rendered one: the strip follows the target the page is shown at.
            var byTarget = document.Targets
                .Select(t => (t.Name, File: Path.Combine(folder, "renders", DesignTargets.RenderFile(s.Id, t.Name))))
                .Where(t => File.Exists(t.File))
                .ToDictionary(t => t.Name, t => t.File, StringComparer.OrdinalIgnoreCase);
            return new DesignRender(s.Id, string.IsNullOrEmpty(s.Title) ? s.Id : s.Title, File.Exists(file) ? file : null, byTarget.Count > 0 ? byTarget : null);
        }).ToList();
        return new DesignPage(detail, n, document, notes, renders) { Doc = doc, Draft = DraftOf(detail, n) };
    }

    // The draft's document, for the version that is the current one only: a draft is the next version's, laid over the one before it.
    private static DesignDocument? DraftOf(DesignDetail detail, int shown) =>
        shown == detail.Row.Current && detail.DraftFolder is { Length: > 0 } draft && Read(Path.Combine(draft, DesignDocument.FileName)) is { } text ? DesignDocument.Parse(text) : null;

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
