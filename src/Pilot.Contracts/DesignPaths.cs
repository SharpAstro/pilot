using System.Globalization;

namespace Pilot.Contracts;

/// <summary>
/// Where a design's files are (PIL1187), the one place that knows: a version's folder, its <c>DESIGN.md</c>, the draft's, and a screen's render PNG at the design
/// size or at a target or a terminal size. The Designs page and the MCP server both locate files through it, from the paths <c>design show</c> returns.
/// </summary>
public static class DesignPaths
{
    public const string VersionsFolder = "versions";
    public const string RendersFolder = "renders";

    /// <summary>The folder of version <paramref name="number"/> of the design <paramref name="detail"/> describes, or null when the design's folder is not given.</summary>
    public static string? VersionFolder(DesignDetail detail, int number) =>
        detail.Folder is { Length: > 0 } folder ? Path.Combine(folder, VersionsFolder, "v" + number.ToString(CultureInfo.InvariantCulture)) : null;

    /// <summary>The draft's folder, or null when it is not given.</summary>
    public static string? DraftFolder(DesignDetail detail) => detail.DraftFolder is { Length: > 0 } draft ? draft : null;

    /// <summary>The <c>DESIGN.md</c> of a version's or the draft's <paramref name="folder"/>.</summary>
    public static string DocumentFile(string folder) => Path.Combine(folder, DesignDocument.FileName);

    /// <summary>The <c>NOTES.md</c> of a version's folder, which an older version has instead of a document.</summary>
    public static string NotesFile(string folder) => Path.Combine(folder, "NOTES.md");

    /// <summary>The <c>prototype.json</c> of a version's or the draft's folder.</summary>
    public static string PrototypeFile(string folder) => Path.Combine(folder, "prototype.json");

    /// <summary>A version's <c>renders/</c> folder.</summary>
    public static string RendersOf(string folder) => Path.Combine(folder, RendersFolder);

    /// <summary>
    /// The PNG standing for screen <paramref name="screen"/> at its design size: <c>renders/&lt;screen&gt;.png</c>, else, for a design that names targets (it has a PNG per
    /// target and none at the design size), the first target's. The path may not exist; the caller says so.
    /// </summary>
    public static string ScreenRender(string folder, string screen, string? firstTarget = null)
    {
        var plain = Path.Combine(RendersOf(folder), screen + ".png");
        return !File.Exists(plain) && firstTarget is { Length: > 0 } ? TargetRender(folder, screen, firstTarget) : plain;
    }

    /// <summary>The PNG of a screen at the target called <paramref name="target"/>: <c>renders/&lt;screen&gt;.&lt;target&gt;.png</c>.</summary>
    public static string TargetRender(string folder, string screen, string target) =>
        Path.Combine(RendersOf(folder), DesignTargets.RenderFile(screen, target));

    /// <summary>The PNG of a screen drawn as terminal cells: <c>renders/&lt;screen&gt;.&lt;cols&gt;x&lt;rows&gt;.png</c>.</summary>
    public static string TerminalRender(string folder, string screen, int columns, int rows) =>
        Path.Combine(RendersOf(folder), string.Create(CultureInfo.InvariantCulture, $"{screen}.{columns}x{rows}.png"));

    /// <summary>
    /// The name of the first target of the <c>prototype.json</c> in <paramref name="folder"/> (a string entry as it is, an object by its <c>name</c>, else <c>&lt;width&gt;x&lt;height&gt;</c>), or null
    /// when it names none or cannot be read: the target whose PNG stands for a screen when the design has none at its design size.
    /// </summary>
    public static string? FirstTarget(string folder)
    {
        if (ReadText(PrototypeFile(folder)) is not { } json)
        {
            return null;
        }
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != System.Text.Json.JsonValueKind.Array || targets.GetArrayLength() == 0)
            {
                return null;
            }
            // Named as DesignTargets.Resolve does, which the Designs page and the node's renders use: a built-in's own spelling, and an entry that is neither a
            // built-in nor sized is no target (a render of it was never written). Contracts cannot take DIR.Lib's parser, so this mirrors it.
            var first = targets[0];
            if (first.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return DesignTargets.Resolve(first.GetString())?.Name;
            }
            if (first.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }
            var label = first.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString() : null;
            int? width = first.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : null;
            int? height = first.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : null;
            return DesignTargets.Resolve(label, width, height)?.Name;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a text file, or null when it is not there or cannot be read.</summary>
    public static string? ReadText(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
