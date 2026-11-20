using System.Text;
using System.Text.Json;
using DIR.Lib;
using DIR.Lib.Prototyping;
using Pilot.Cells;
using Pilot.Contracts;
using SharpAstro.Png;

namespace Pilot.Node.Designs;

/// <summary>
/// The check and the render of a design's draft (PIL1096), done by the node with DIR.Lib's <see cref="Prototype"/>: <c>Load</c> reads the draft's
/// <c>prototype.json</c> and says every problem with its JSON path, <c>Check</c> draws each screen at <see cref="DesignSizes"/> and reports what the
/// wireframe rules find, <c>Render</c> draws each screen at the design size. Nothing here uses reflection-based JSON (AOT).
/// </summary>
public static class DesignChecker
{
    /// <summary>The folder of a draft's renders, <c>draft/renders/</c>.</summary>
    public const string RendersFolderName = "renders";

    /// <summary>
    /// The UI face the check and the render draw with: the same <c>Fonts/DejaVuSans.ttf</c> the GUI's pages and its prototype player draw with
    /// (<c>Pilot.Gui</c> ships the file; the node's project links the same file), so what the check measures is what the page plays, on any OS.
    /// </summary>
    public static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    /// <summary>
    /// The monospace face the cell grids are drawn in (<c>Fonts/DejaVuSansMono.ttf</c>, shipped by <c>Pilot.Gui</c> and linked into the node's project), the one the GUI
    /// paints a console view's grid with.
    /// </summary>
    public static string MonoFontPath => Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSansMono.ttf");

    /// <summary>The stamp beside the renders: the hash of the prototype they were drawn from.</summary>
    public const string StampFile = "render.json";

    /// <summary>
    /// Every problem of the prototype at <paramref name="prototypeFile"/>, a line each: a loader problem as <c>&lt;json path&gt;: &lt;message&gt;</c>, a finding as
    /// <c>&lt;screen&gt; | &lt;width&gt;x&lt;height&gt; | &lt;rule&gt;: &lt;message&gt;</c>. Empty when the prototype is clean at every <see cref="DesignSizes"/>.
    /// </summary>
    public static string[] Problems(string prototypeFile, string? fontPath = null, string[]? terminals = null, bool requireDocument = false)
    {
        var loaded = Load(prototypeFile, out var problems);
        if (loaded is null)
        {
            return problems;
        }
        try
        {
            // A document that names targets is checked at each of them, the target's name on every finding; one that names none at the sweep of sizes, as #1096's did.
            var findings = NamesTargets(loaded)
                ? Prototype.Check(loaded, fontPath: fontPath ?? FontPath)
                : Prototype.Check(loaded, DesignSizes.All, fontPath: fontPath ?? FontPath);
            // A target whose name cannot be a file name is said here, before a render refuses it (the review of #1116).
            var names = loaded.Targets.Select((t, i) => (t.Name, i)).Where(t => !DesignTargets.IsPlainName(t.Name))
                .Select(t => $"$.targets[{t.i}].name: '{t.Name}' is not a plain file name (no path separator, '..' or character a file name cannot hold)");
            return [.. names.Concat(findings.Select(Line)).Concat(CellLines(loaded, terminals ?? [])).Concat(DocumentLines(prototypeFile, loaded, requireDocument)).Distinct(StringComparer.Ordinal)];
        }
        catch (FileNotFoundException ex)
        {
            // No font on the machine: a check that drew no text would call everything clean, so it says so instead.
            return [$"the check cannot run: {ex.Message}"];
        }
    }

    /// <summary>
    /// The cell findings (PIL1098) of every screen, and of each state variant of it, at each of <paramref name="terminals"/> (<c>&lt;cols&gt;x&lt;rows&gt;</c>), as
    /// <c>&lt;screen&gt; | &lt;cols&gt;x&lt;rows&gt; cells | &lt;rule&gt;: &lt;message&gt;</c>: Console.Lib's <c>CheckCells</c> over the screen arranged and painted in cells.
    /// </summary>
    private static IEnumerable<string> CellLines(PrototypeDocument document, string[] terminals)
    {
        foreach (var terminal in terminals)
        {
            if (!DesignTerminals.TryParse(terminal, out var columns, out var rows))
            {
                yield return $"$: terminal target '{terminal}' is not <cols>x<rows>";
                continue;
            }
            foreach (var screen in document.Screens)
            {
                foreach (var (label, name, value) in PrototypeCells.Variants(screen))
                {
                    var state = new PrototypeState();
                    if (name is not null)
                    {
                        state.Set(name, value);
                    }
                    var cells = PrototypeCells.Build(screen, state, columns, rows);
                    var shown = label == "default" ? screen.Id : $"{screen.Id} [{label}]";
                    foreach (var finding in cells.Findings())
                    {
                        yield return $"{shown} | {columns}x{rows} cells | {finding}";
                    }
                }
            }
        }
    }

    /// <summary>
    /// The findings of the design's document (PIL1171), <c>DESIGN.md | &lt;cell heading&gt; | &lt;rule&gt;: &lt;message&gt;</c> a line each: no design objective, a cell with no objective, a frame whose
    /// id has no screen, a screen no frame names, a size that is neither a built-in target nor <c>&lt;cols&gt;x&lt;rows&gt;</c>. A missing DESIGN.md is a finding only when <paramref name="require"/>.
    /// </summary>
    private static IEnumerable<string> DocumentLines(string prototypeFile, PrototypeDocument loaded, bool require)
    {
        var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(prototypeFile))!, DesignDocument.FileName);
        string text;
        try
        {
            if (!File.Exists(file))
            {
                return require ? [$"{DesignDocument.FileName} | - | document: {DesignDocument.FileName} is missing; the design is a document with one cell per screen"] : [];
            }
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"{DesignDocument.FileName} | - | document: cannot be read: {ex.Message}"];
        }
        var document = DesignDocument.Parse(text);
        var lines = new List<string>();
        var title = DesignTitle.Clean(document.Title);
        // The starter's heading is the id when there is no title yet, so an id alone is no title.
        if (title.Length == 0 || title.StartsWith("DSN-", StringComparison.OrdinalIgnoreCase) && title.Length > 4 && title[4..].All(char.IsAsciiDigit))
        {
            lines.Add($"{DesignDocument.FileName} | - | title: The design has no title. Start the document with '# <title>', a short name of at most {DesignTitle.MaxLength} characters (not the id, not a sentence).");
        }
        else if (DesignTitle.Problem(title) is { } tooLong)
        {
            lines.Add($"{DesignDocument.FileName} | {title} | title: {tooLong}");
        }
        if (string.IsNullOrWhiteSpace(document.Objective))
        {
            lines.Add($"{DesignDocument.FileName} | {document.Title} | objective: This design has no objective. Say in one or two lines what it is for.");
        }
        var known = loaded.Screens.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in document.Cells)
        {
            if (string.IsNullOrWhiteSpace(cell.Objective))
            {
                lines.Add($"{DesignDocument.FileName} | {cell.Heading} | objective: {DesignDocument.NoObjectiveMessage}");
            }
            foreach (var frame in cell.Frames)
            {
                named.Add(frame.Id);
                if (!known.Contains(frame.Id))
                {
                    lines.Add($"{DesignDocument.FileName} | {cell.Heading} | frame: the block names screen '{frame.Id}', which has no screen file (screens/{frame.Id}.json)");
                }
                if (frame.Size is { } size && !DesignDocument.IsValidSize(size))
                {
                    lines.Add($"{DesignDocument.FileName} | {cell.Heading} | size: '{size}' is neither {string.Join(", ", DesignTargets.BuiltIn.Select(t => t.Name))} nor <cols>x<rows>");
                }
            }
        }
        var files = DesignFiles.ScreenFileIds(prototypeFile).ToHashSet(StringComparer.Ordinal);
        foreach (var id in known.Where(id => !named.Contains(id)))
        {
            var what = files.Contains(id) ? $"screens/{id}.json" : $"screen '{id}'";
            lines.Add($"{DesignDocument.FileName} | - | screen: {what} is named by no frame block; add a cell for it or remove it");
        }
        return lines;
    }

    private static string Line(PrototypeFinding f)
    {
        // A state variant (a when's name=value) is named after its screen; the default state is the screen alone.
        var screen = f.State == "default" || f.State.Length == 0 ? f.Screen : $"{f.Screen} [{f.State}]";
        var at = string.IsNullOrEmpty(f.Target) ? "" : f.Target + " | ";
        return $"{screen} | {at}{f.Width}x{f.Height} | {f.Finding.Rule}: {f.Finding.Message}";
    }

    /// <summary>True when the document's own <c>targets</c> says where it is for; false for the one target DIR.Lib gives a document that names none.</summary>
    public static bool NamesTargets(PrototypeDocument document) =>
        !(document.Targets.Length == 1 && string.Equals(document.Targets[0].Name, PrototypeTargets.DefaultName, StringComparison.Ordinal));

    /// <summary>The PNG names a render of <paramref name="document"/> leaves: <c>&lt;screen&gt;.png</c> for a document naming no target, else <c>&lt;screen&gt;.&lt;target&gt;.png</c> per target, each with its target's name (empty for none).</summary>
    public static (string File, string Target)[] RenderFiles(PrototypeDocument document) =>
        NamesTargets(document)
            ? [.. document.Targets.SelectMany(t => document.Screens.Select(s => (DesignTargets.RenderFile(s.Id, t.Name), t.Name)))]
            : [.. document.Screens.Select(s => (s.Id + ".png", ""))];

    /// <summary>The prototype at <paramref name="prototypeFile"/>, or null with <paramref name="problems"/> (a line each, with its JSON path) when it cannot be read.</summary>
    public static PrototypeDocument? Load(string prototypeFile, out string[] problems)
    {
        var json = DesignFiles.AssembledJson(prototypeFile, out var problem);
        if (json is null)
        {
            problems = [problem];
            return null;
        }
        var result = Prototype.Load(json);
        if (result.Document is { } document)
        {
            problems = [];
            return document;
        }
        problems = [.. result.Problems.Select(p => $"{p.Path}: {p.Message}")];
        return null;
    }

    /// <summary>
    /// Draws every screen of <paramref name="document"/> into <paramref name="rendersFolder"/>, after clearing the PNGs of an earlier render, and stamps the
    /// folder with <paramref name="prototypeSha256"/>: a document that names targets as <c>&lt;screen&gt;.&lt;target&gt;.png</c> at each target, returning
    /// <c>screen.target</c> for each; one that names none as <c>&lt;screen id&gt;.png</c> at its design size, returning the screens' ids.
    /// </summary>
    public static string[] Render(PrototypeDocument document, string rendersFolder, string prototypeSha256, string? fontPath = null, string[]? terminals = null, string? monoFontPath = null)
    {
        Directory.CreateDirectory(rendersFolder);
        foreach (var old in Directory.EnumerateFiles(rendersFolder, "*.png").Concat(Directory.EnumerateFiles(rendersFolder, "*.txt")).ToArray())
        {
            File.Delete(old);
        }
        var stamp = Path.Combine(rendersFolder, StampFile);
        if (File.Exists(stamp))
        {
            // A stamp is of what was drawn: while the PNGs are being remade there is none to trust.
            File.Delete(stamp);
        }
        string[] made;
        if (NamesTargets(document))
        {
            // One PNG per screen per named target, at the target's width and height times its scale.
            var images = Prototype.Render(document, fontPath: fontPath ?? FontPath);
            foreach (var (target, screen, image) in images)
            {
                File.WriteAllBytes(Path.Combine(rendersFolder, DesignTargets.RenderFile(screen, target)), PngWriter.Encode(image.Pixels, image.Width, image.Height));
            }
            made = [.. images.Select(i => $"{i.Item2}.{i.Item1}")];
        }
        else
        {
            var images = Prototype.Render(document, ((uint)document.Width, (uint)document.Height), fontPath: fontPath ?? FontPath);
            foreach (var (screen, image) in images)
            {
                File.WriteAllBytes(Path.Combine(rendersFolder, screen + ".png"), PngWriter.Encode(image.Pixels, image.Width, image.Height));
            }
            made = [.. images.Select(i => i.Screen)];
        }
        RenderCells(document, rendersFolder, terminals ?? [], monoFontPath ?? MonoFontPath);
        File.WriteAllText(stamp, JsonSerializer.Serialize(new DesignRenderStamp { PrototypeSha256 = prototypeSha256 }, PilotJsonContext.Default.DesignRenderStamp), new UTF8Encoding(false));
        return made;
    }

    /// <summary>
    /// For each terminal target writes <c>&lt;screen&gt;.&lt;cols&gt;x&lt;rows&gt;.txt</c>, the screen's rows as text with trailing spaces trimmed, and
    /// <c>&lt;screen&gt;.&lt;cols&gt;x&lt;rows&gt;.png</c>, the same cells drawn as a grid at 1:2 cells in the monospace face, from the entry state.
    /// </summary>
    private static void RenderCells(PrototypeDocument document, string rendersFolder, string[] terminals, string monoFontPath)
    {
        foreach (var terminal in terminals)
        {
            if (!DesignTerminals.TryParse(terminal, out var columns, out var rows))
            {
                continue;
            }
            foreach (var screen in document.Screens)
            {
                var cells = PrototypeCells.Build(screen, new PrototypeState(), columns, rows);
                var name = $"{screen.Id}.{columns}x{rows}";
                File.WriteAllText(Path.Combine(rendersFolder, name + ".txt"), cells.Text(), new UTF8Encoding(false));
                var (width, height) = PrototypeCells.PixelSize(columns, rows);
                using var renderer = new RgbaImageRenderer(width, height);
                renderer.Surface.Clear(new RGBAColor32(18, 18, 20, 255));
                PrototypeCells.Draw(renderer, cells.Buffer, monoFontPath);
                File.WriteAllBytes(Path.Combine(rendersFolder, name + ".png"), PngWriter.Encode(renderer.Surface.Pixels, renderer.Surface.Width, renderer.Surface.Height));
            }
        }
    }

    /// <summary>The hash a folder of renders was stamped with, or null when there is no readable stamp.</summary>
    public static string? StampOf(string rendersFolder)
    {
        try
        {
            var file = Path.Combine(rendersFolder, StampFile);
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), PilotJsonContext.Default.DesignRenderStamp)?.PrototypeSha256 : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
