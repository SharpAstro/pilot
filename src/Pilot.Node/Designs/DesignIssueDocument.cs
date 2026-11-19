using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Designs;

/// <summary>What a design's issue body holds between the <see cref="DesignSyncText.Begin"/> and <see cref="DesignSyncText.End"/> markers (PIL1171), and how it is read back.</summary>
public sealed record DesignIssueMeta(string Node, string Design, int Version, string? Why, string? Title, string[]? Ideas = null, string? Commit = null, string? Folder = null);

/// <summary>
/// Where the links of a design's block point (PIL1208): a ref of the repo and the folder the design's files sit under at that ref. The design branch holds them at its root;
/// a built version's files are under <c>docs/designs/&lt;n&gt;/</c> at the merge commit.
/// </summary>
public sealed record DesignLinkBase(string Ref, string Folder = "")
{
    /// <summary>The design's branch, files at its root.</summary>
    public static DesignLinkBase OfBranch(int issue) => new(DesignSyncText.Branch(issue));

    /// <summary>The merge commit <paramref name="sha"/>, files under <paramref name="folder"/>.</summary>
    public static DesignLinkBase OfCommit(string sha, string folder) => new(sha, folder);

    /// <summary>True for a commit's folder, false for the branch.</summary>
    public bool IsCommit => Folder.Length > 0;

    /// <summary>The path of <paramref name="file"/> (relative to the design's root) at this ref.</summary>
    public string PathOf(string file) => Folder.Length > 0 ? Folder.TrimEnd('/') + "/" + file : file;
}

/// <summary>
/// The design's document as the issue's body shows it: the title, objective and each cell, every frame as its render's raw link into the design's branch. The block
/// carries one hidden meta line (the node it was made on, the design's id, the version, its why), so a node that reads the issue knows what it holds and where from.
/// Everything of the body outside the markers is the people's, and is left as it was.
/// </summary>
public static partial class DesignIssueDocument
{
    [GeneratedRegex(@"<!-- pilot:design-meta (\{.*?\}) -->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MetaLine();

    /// <summary>The raw link of file <paramref name="path"/> of the design's branch, the way an issue body shows a picture.</summary>
    public static string RawLink(string repo, int issue, string path) => RawLink(repo, DesignLinkBase.OfBranch(issue), path);

    /// <summary>The raw link of <paramref name="path"/> (relative to the design's root) at <paramref name="at"/>: one function for the branch's links and a built version's.</summary>
    public static string RawLink(string repo, DesignLinkBase at, string path) =>
        $"https://github.com/{repo}/blob/{at.Ref}/{at.PathOf(path)}?raw=true";

    /// <summary>The block for version <paramref name="version"/>: markers included.</summary>
    /// <param name="renders">The paths of the version's renders (<c>renders/&lt;file&gt;.png</c>), from which each frame's picture is chosen.</param>
    /// <param name="at">Where the links point; the design's branch when null. A commit's folder is also written to the meta line, so another node knows where the built files are.</param>
    public static string Block(string designId, DesignDocument document, int version, string why, string node, string repo, int issue, IReadOnlyCollection<string> renders, IReadOnlyCollection<string>? ideas = null, DesignLinkBase? at = null)
    {
        at ??= DesignLinkBase.OfBranch(issue);
        var meta = new JsonObject
        {
            ["node"] = node,
            ["design"] = designId,
            ["version"] = version,
            ["why"] = why,
            ["title"] = document.Title,
        };
        if (ideas is { Count: > 0 })
        {
            meta["ideas"] = new JsonArray([.. ideas.Select(i => (JsonNode?)JsonValue.Create(i))]);
        }
        if (at.IsCommit)
        {
            meta["commit"] = at.Ref;
            meta["folder"] = at.Folder;
        }
        var text = new StringBuilder();
        text.Append(DesignSyncText.Begin).Append('\n');
        // The default encoder writes '<', '>' and '&' as \u escapes, so no '-->' of the why can close the comment.
        text.Append("<!-- pilot:design-meta ").Append(meta.ToJsonString()).Append(" -->\n");
        text.Append("# ").Append(document.Title).Append("\n\n");
        if (document.Objective is { Length: > 0 } objective)
        {
            text.Append(objective).Append("\n\n");
        }
        if (document.Notes.Length > 0)
        {
            text.Append(document.Notes).Append("\n\n");
        }
        var number = 0;
        foreach (var cell in document.Cells)
        {
            number++;
            text.Append("## ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(cell.Heading).Append("\n\n");
            if (cell.Objective is { Length: > 0 } says)
            {
                text.Append(says).Append("\n\n");
            }
            foreach (var frame in cell.Frames)
            {
                var file = RenderOf(frame, renders);
                var label = (frame.Caption ?? frame.Id).Replace('\n', ' ').Replace("]", "", StringComparison.Ordinal).Replace("[", "", StringComparison.Ordinal);
                text.Append(file is null ? $"_{label} (no render)_" : $"![{label}]({RawLink(repo, at, file)})").Append("\n\n");
            }
            if (cell.Notes.Length > 0)
            {
                text.Append(cell.Notes).Append("\n\n");
            }
        }
        text.Append("---\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Design {designId} v{version}, made on {node}: {why}\n");
        text.Append(DesignSyncText.End);
        return text.ToString();
    }

    private static string? RenderOf(DesignFrame frame, IReadOnlyCollection<string> renders)
    {
        var named = frame.Size is { Length: > 0 } size ? $"renders/{frame.Id}.{size}.png" : null;
        if (named is not null && renders.Contains(named, StringComparer.Ordinal))
        {
            return named;
        }
        var plain = $"renders/{frame.Id}.png";
        if (renders.Contains(plain, StringComparer.Ordinal))
        {
            return plain;
        }
        return renders.Order(StringComparer.Ordinal).FirstOrDefault(r => r.StartsWith($"renders/{frame.Id}.", StringComparison.Ordinal));
    }

    /// <summary>
    /// <paramref name="body"/> with <paramref name="block"/> between the markers: the block replaces what was between them, or follows the body when it has none. The body
    /// outside the markers is returned exactly as it was.
    /// </summary>
    public static string Splice(string? body, string block)
    {
        body ??= "";
        var from = body.IndexOf(DesignSyncText.Begin, StringComparison.Ordinal);
        var to = from < 0 ? -1 : body.IndexOf(DesignSyncText.End, from, StringComparison.Ordinal);
        if (from >= 0 && to >= 0)
        {
            return body[..from] + block + body[(to + DesignSyncText.End.Length)..];
        }
        return body.Trim().Length == 0 ? block : body.TrimEnd() + "\n\n" + block;
    }

    /// <summary>
    /// <paramref name="body"/> with the ideas of its block replaced by <paramref name="ideas"/> (<c>owner/repo#n</c> each): the meta line and the <c>Ideas:</c> footer line change, the rest of
    /// the body is exactly as it was. A body with no readable block is returned as it was.
    /// </summary>
    public static string WithIdeas(string body, IReadOnlyCollection<string> ideas)
    {
        var from = body.IndexOf(DesignSyncText.Begin, StringComparison.Ordinal);
        var to = from < 0 ? -1 : body.IndexOf(DesignSyncText.End, from, StringComparison.Ordinal);
        if (from < 0 || to < 0 || MetaLine().Match(body, from, to - from) is not { Success: true } match)
        {
            return body;
        }
        var meta = JsonNode.Parse(match.Groups[1].Value) as JsonObject;
        if (meta is null)
        {
            return body;
        }
        meta.Remove("ideas");
        if (ideas.Count > 0)
        {
            meta["ideas"] = new JsonArray([.. ideas.Select(i => (JsonNode?)JsonValue.Create(i))]);
        }
        var block = body[from..to];
        block = block[..(match.Index - from)] + "<!-- pilot:design-meta " + meta.ToJsonString() + " -->" + block[(match.Index - from + match.Length)..];
        block = Regex.Replace(block, @"^Ideas: .*\n", "", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (ideas.Count > 0)
        {
            block += "Ideas: " + string.Join(", ", ideas) + "\n";
        }
        return body[..from] + block + body[to..];
    }

    /// <summary>What the block of <paramref name="body"/> says it holds; null when the body has no block, or one this node cannot read.</summary>
    public static DesignIssueMeta? Meta(string? body)
    {
        if (body is null)
        {
            return null;
        }
        var from = body.IndexOf(DesignSyncText.Begin, StringComparison.Ordinal);
        var to = from < 0 ? -1 : body.IndexOf(DesignSyncText.End, from, StringComparison.Ordinal);
        if (from < 0 || to < 0 || MetaLine().Match(body, from, to - from) is not { Success: true } match)
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(match.Groups[1].Value) is JsonObject o
                && o["node"]?.GetValue<string>() is { Length: > 0 } node
                && o["version"]?.GetValue<int>() is > 0 and var version)
            {
                return new DesignIssueMeta(node, o["design"]?.GetValue<string>() ?? "", version, o["why"]?.GetValue<string>(), o["title"]?.GetValue<string>(),
                    o["ideas"] is JsonArray list ? [.. list.Select(n => n?.GetValue<string>()).OfType<string>()] : null,
                    o["commit"]?.GetValue<string>() is { Length: > 0 } commit ? commit : null, o["folder"]?.GetValue<string>() is { Length: > 0 } folder ? folder : null);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
        }
        return null;
    }
}
