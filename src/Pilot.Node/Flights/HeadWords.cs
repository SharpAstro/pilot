namespace Pilot.Node.Flights;

/// <summary>How a PR's head relates to the head that was reviewed.</summary>
public enum HeadRelation
{
    /// <summary>The head is the reviewed one: nothing to say, no card, no notification.</summary>
    Identical,
    /// <summary>The base was merged in since the review and the flight's files are unchanged.</summary>
    UpToDateWithBase,
    /// <summary>The head holds commits the review did not read.</summary>
    Newer,
    /// <summary>The head cannot be compared with the reviewed one (a commit is missing, or git failed).</summary>
    Unknown,
}

/// <summary>
/// A head described in words (#691): the relation leads, the shas follow as information (the card links them through
/// <see cref="CommitRefs.Named"/>). <see cref="CodeUnchanged"/> says the flight's own code is what the review read, so a head
/// that moved only by its base, by whitespace or by documentation (.md) changes is re-pinned instead of asking for a re-review.
/// </summary>
public sealed record HeadWords(HeadRelation Relation, string Words, string Shas, bool CodeUnchanged)
{
    /// <summary>Words first, shas after: <c>up to date with master: master merged in since review, code unchanged (bc3236650 · reviewed 1a149a13c)</c>.</summary>
    public string Text => Shas.Length > 0 ? $"{Words} ({Shas})" : Words;

    /// <summary>Whether the head may take over the review's pass without a new review.</summary>
    public bool Repinnable => Relation is HeadRelation.Identical or HeadRelation.UpToDateWithBase || Relation == HeadRelation.Newer && CodeUnchanged;

    private const int MostSubjects = 3;
    private const int SubjectLength = 40;

    /// <summary>
    /// Relates <paramref name="head"/> to <paramref name="reviewed"/> and <paramref name="baseName"/> (read as <paramref name="baseRef"/>,
    /// e.g. <c>origin/master</c>) from git in <paramref name="plane"/>. Never throws for a git failure: that is <see cref="HeadRelation.Unknown"/>.
    /// </summary>
    public static async Task<HeadWords> DescribeAsync(IGit git, string plane, string baseName, string baseRef, string reviewed, string head, CancellationToken cancellationToken)
    {
        var shas = $"{CommitRefs.Short(head)} · reviewed {CommitRefs.Short(reviewed)}";
        if (string.Equals(reviewed, head, StringComparison.OrdinalIgnoreCase) || head.StartsWith(reviewed, StringComparison.OrdinalIgnoreCase) || reviewed.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            return new HeadWords(HeadRelation.Identical, "identical to what was reviewed", CommitRefs.Short(head), true);
        }
        foreach (var sha in new[] { reviewed, head })
        {
            if (!(await git.RunAsync(["cat-file", "-e", $"{sha}^{{commit}}"], plane, cancellationToken)).Ok)
            {
                return new HeadWords(HeadRelation.Unknown, "not comparable with the review (a commit is not in the plane)", shas, false);
            }
        }
        var unchanged = await CodeUnchangedAsync(git, plane, baseRef, reviewed, head, cancellationToken);
        var baseTip = await git.RunAsync(["rev-parse", baseRef], plane, cancellationToken);
        var baseIn = baseTip.Ok && (await git.RunAsync(["merge-base", "--is-ancestor", baseTip.Output.Trim(), head], plane, cancellationToken)).Ok;
        var newer = await git.RunAsync(["log", "--format=%s", $"{reviewed}..{head}"], plane, cancellationToken);
        var subjects = newer.Ok ? newer.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        if (unchanged && baseIn && subjects.Length > 0 && subjects.All(s => s.StartsWith("Merge ", StringComparison.Ordinal)))
        {
            return new HeadWords(HeadRelation.UpToDateWithBase, $"up to date with {baseName}: {baseName} merged in since review, code unchanged", shas, true);
        }
        if (unchanged && baseIn && subjects.Length == 0)
        {
            // The reviewed head is not an ancestor (a rebase onto the base): the base is in and the files are the same.
            return new HeadWords(HeadRelation.UpToDateWithBase, $"up to date with {baseName}: {baseName} merged in since review, code unchanged", shas, true);
        }
        var count = subjects.Length;
        var shown = string.Join("; ", subjects.Take(MostSubjects).Select(Brief));
        var more = count > MostSubjects ? $"; and {count - MostSubjects} more" : "";
        var words = count == 0
            ? "newer than the review: rebuilt on the base"
            : $"newer than the review: {count} commit{(count == 1 ? "" : "s")} ({shown}{more})";
        return new HeadWords(HeadRelation.Newer, words, shas, unchanged);
    }

    /// <summary>The words for a head behind its base: <c>behind master by 3 commits</c>; null when it is not behind (or git failed).</summary>
    public static async Task<string?> BehindAsync(IGit git, string plane, string baseName, string baseRef, string head, CancellationToken cancellationToken)
    {
        var count = await git.RunAsync(["rev-list", "--count", $"{head}..{baseRef}"], plane, cancellationToken);
        return count.Ok && int.TryParse(count.Output.Trim(), out var n) && n > 0
            ? $"behind {baseName} by {n} commit{(n == 1 ? "" : "s")}"
            : null;
    }

    private static string Brief(string subject) => subject.Length <= SubjectLength ? subject : subject[..SubjectLength].TrimEnd() + "...";

    /// <summary>
    /// Whether the flight's own code at <paramref name="head"/> is what <paramref name="reviewed"/> read: the files each changed against
    /// the base (so what the base brought is left out) are compared by path, ignoring whitespace, with .md files left out.
    /// </summary>
    private static async Task<bool> CodeUnchangedAsync(IGit git, string plane, string baseRef, string reviewed, string head, CancellationToken cancellationToken)
    {
        async Task<HashSet<string>?> FilesAsync(string sha)
        {
            var bas = await git.RunAsync(["merge-base", baseRef, sha], plane, cancellationToken);
            if (!bas.Ok)
            {
                return null;
            }
            var names = await git.RunAsync(["diff", "--name-only", bas.Output.Trim(), sha], plane, cancellationToken);
            return names.Ok ? [.. names.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null;
        }
        if (await FilesAsync(reviewed) is not { } theirs || await FilesAsync(head) is not { } ours)
        {
            return false;
        }
        var code = theirs.Union(ours, StringComparer.Ordinal).Where(f => !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        if (code.Length == 0)
        {
            return true;
        }
        return (await git.RunAsync(["diff", "-w", "--quiet", reviewed, head, "--", .. code], plane, cancellationToken)).Ok;
    }
}
