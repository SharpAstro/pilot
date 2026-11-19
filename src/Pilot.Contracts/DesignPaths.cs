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
    /// The name of the first target of the <c>prototype.json</c> in <paramref name="folder"/>, exactly as written: the <c>name</c> of the first entry of <c>targets</c> when that
    /// entry is an object whose <c>name</c> is a non-empty string, else null (no targets, another shape, or a file that cannot be read). It is the target whose PNG stands for a
    /// screen when the design has none at its design size.
    /// </summary>
    /// <remarks>
    /// The node names a target's render <c>&lt;screen&gt;.&lt;name&gt;.png</c> from the name DIR.Lib's <c>Prototype</c> reads, which is the entry's <c>name</c> as written (no case
    /// change, no built-in lookup, any positive width and height), and the Designs page reads the same <c>Targets[0].Name</c>. Contracts cannot take DIR.Lib, so this reads that one
    /// member the same way, and <see cref="FirstTargetName"/> is the rule the page and this share.
    /// </remarks>
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
            var first = targets[0];
            return first.ValueKind == System.Text.Json.JsonValueKind.Object && first.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String
                ? FirstTargetName([name.GetString()])
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The first of a prototype's target names as written, or null when there is none or it is empty: the one rule the Designs page and MCP name the first target by.</summary>
    public static string? FirstTargetName(IEnumerable<string?> names) => names.FirstOrDefault() is { Length: > 0 } first ? first : null;

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
