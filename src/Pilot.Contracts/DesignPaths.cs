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

    /// <summary>Reads a text file, or null when it is not there or cannot be read.</summary>
    public static string? ReadText(string file)
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
