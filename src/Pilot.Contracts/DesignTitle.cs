namespace Pilot.Contracts;

/// <summary>
/// A design's title (#1183): short and given by the designer, in DESIGN.md's <c>#</c> heading at each published version, or by <c>pilot design title</c>. It is never made from
/// the brief. It lives here, beside <see cref="DesignRow"/>, so the node's store, its check and the page's tests hold to the one rule.
/// </summary>
public static class DesignTitle
{
    /// <summary>The longest title a design can be given.</summary>
    public const int MaxLength = 40;

    /// <summary>The title in one line, trimmed; empty when there is none.</summary>
    public static string Clean(string? title) => (title ?? "").ReplaceLineEndings(" ").Trim();

    /// <summary>The problem with <paramref name="title"/> as a design's title (empty, or over <see cref="MaxLength"/> with its length and the limit); null when it is fine.</summary>
    public static string? Problem(string? title)
    {
        var clean = Clean(title);
        return clean.Length == 0
            ? "the title is empty: give the design a short name, at most " + MaxLength + " characters"
            : clean.Length > MaxLength
                ? $"the title is {clean.Length} characters, over the limit of {MaxLength}: a title is a short name, not a sentence"
                : null;
    }

    /// <summary>The title to draw for a stored one: itself when it is non-empty and at most <see cref="MaxLength"/> characters, else null (the page shows the id alone). An old long cut is never drawn.</summary>
    public static string? Shown(string? title) => Clean(title) is { Length: > 0 and <= MaxLength } clean ? clean : null;
}
