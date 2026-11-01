using System.Text.RegularExpressions;

namespace Pilot.Contracts;

/// <summary>An image in an idea's text: what it says it is, where it points, and the id of the node's copy when the node holds one.</summary>
/// <param name="Alt">The alt text (the name the image was pasted as); empty when it has none.</param>
/// <param name="Url">What the image points to: <c>pilot-attachment:&lt;id&gt;.&lt;ext&gt;</c> before it is filed, a GitHub URL after.</param>
/// <param name="Copy">The id of the node's copy, when <paramref name="Url"/> is a marker or the node's own file on GitHub; else null.</param>
public sealed record IdeaImage(string Alt, string Url, string? Copy = null);

/// <summary>An image as it is written in an idea's text: where it starts, how long it is, the text itself and what it points to.</summary>
public sealed record IdeaImageSpan(int At, int Length, string Raw, string Url);

/// <summary>
/// The images an idea's text carries. A client that pastes one uploads it to the node and writes
/// <c>![name](pilot-attachment:&lt;id&gt;.&lt;ext&gt;)</c> where it was pasted; filing the idea swaps each marker for the
/// image on GitHub (<see cref="Branch"/>), in the same place.
/// </summary>
public static partial class IdeaImages
{
    /// <summary>The scheme of a marker.</summary>
    public const string Scheme = "pilot-attachment:";

    /// <summary>The branch of a repo the node commits an idea's images to, as <c>ideas/&lt;id&gt;.&lt;ext&gt;</c>.</summary>
    public const string Branch = "pilot-attachments";

    /// <summary>The most an image may be, in bytes.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The marker a client writes for an image it uploaded as <paramref name="id"/>; <paramref name="name"/> loses its brackets.</summary>
    public static string Marker(string name, string id, string ext) =>
        $"![{name.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)}]({Scheme}{id}.{ext})";

    [GeneratedRegex(@"!\[(?<alt>[^\]\r\n]*)\]\(\s*<?(?<url>[^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\bsrc\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex SrcAttribute();

    [GeneratedRegex(@"\balt\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex AltAttribute();

    [GeneratedRegex(@"^pilot-attachment:(?<id>[A-Za-z0-9]+)\.(?<ext>[A-Za-z0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerUrl();

    [GeneratedRegex(@"/pilot-attachments/ideas/(?<id>[A-Za-z0-9]+)\.(?<ext>[A-Za-z0-9]+)(?:\?|#|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BranchUrl();

    /// <summary>The id and extension a marker or the node's own file on GitHub names; null for any other URL.</summary>
    public static (string Id, string Ext)? CopyOf(string url)
    {
        var match = MarkerUrl().Match(url);
        if (!match.Success)
        {
            match = BranchUrl().Match(url);
        }
        return match.Success ? (match.Groups["id"].Value, match.Groups["ext"].Value.ToLowerInvariant()) : null;
    }

    /// <summary>The marker at <paramref name="url"/> (<c>pilot-attachment:&lt;id&gt;.&lt;ext&gt;</c>), else null.</summary>
    public static (string Id, string Ext)? MarkerIn(string url)
    {
        var match = MarkerUrl().Match(url);
        return match.Success ? (match.Groups["id"].Value, match.Groups["ext"].Value.ToLowerInvariant()) : null;
    }

    /// <summary>Every Markdown image and <c>&lt;img&gt;</c> tag in <paramref name="body"/>, in the order they stand.</summary>
    public static IdeaImage[] In(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }
        var found = new List<(int At, IdeaImage Image)>();
        foreach (Match m in MarkdownImage().Matches(body))
        {
            found.Add((m.Index, Make(m.Groups["alt"].Value, m.Groups["url"].Value)));
        }
        foreach (Match m in ImgTag().Matches(body))
        {
            if (SrcAttribute().Match(m.Value) is { Success: true } src)
            {
                found.Add((m.Index, Make(AltAttribute().Match(m.Value) is { Success: true } alt ? alt.Groups["v"].Value : "", src.Groups["v"].Value)));
            }
        }
        return [.. found.OrderBy(f => f.At).Select(f => f.Image)];
    }

    /// <summary>Where each Markdown image and <c>&lt;img&gt;</c> tag stands in <paramref name="body"/>, as written, in the order they stand.</summary>
    public static IdeaImageSpan[] Spans(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }
        var found = new List<IdeaImageSpan>();
        foreach (Match m in MarkdownImage().Matches(body))
        {
            found.Add(new IdeaImageSpan(m.Index, m.Length, m.Value, m.Groups["url"].Value.Trim()));
        }
        foreach (Match m in ImgTag().Matches(body))
        {
            if (SrcAttribute().Match(m.Value) is { Success: true } src)
            {
                found.Add(new IdeaImageSpan(m.Index, m.Length, m.Value, src.Groups["v"].Value.Trim()));
            }
        }
        return [.. found.OrderBy(f => f.At)];
    }

    private static IdeaImage Make(string alt, string url) => new(alt.Trim(), url.Trim(), CopyOf(url.Trim()) is { } copy ? copy.Id : null);

    /// <summary><paramref name="body"/> with each marker in it replaced by <paramref name="replace"/>'s answer for its id and extension, where it stood.</summary>
    public static string ReplaceMarkers(string body, Func<string, string, string, string> replace) =>
        MarkdownImage().Replace(body, m =>
            MarkerIn(m.Groups["url"].Value) is { } marker
                ? replace(m.Groups["alt"].Value, marker.Id, marker.Ext)
                : m.Value);
}
