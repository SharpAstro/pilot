using System.Security.Cryptography;
using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// Where a checkout keeps its git state: its own git dir (<c>HEAD</c>, <c>index</c>, <c>logs/HEAD</c>) and the common git dir
/// the refs, <c>packed-refs</c> and <c>worktrees/</c> live in. They are one folder for a main checkout and two for a linked
/// worktree, whose <c>.git</c> is a file pointing at <c>&lt;common&gt;/worktrees/&lt;name&gt;</c>. Found by reading those files, not by
/// asking git, so no process is started to find them.
/// </summary>
internal sealed record GitLayout(string GitDir, string CommonDir)
{
    /// <summary>
    /// The layout of the checkout at <paramref name="checkout"/>; null when it cannot be read from files alone: no <c>.git</c>, no
    /// <c>HEAD</c> in it (a bare folder, a half-made repo), an IO error, or the <c>reftable</c> ref format, whose refs are not
    /// files a fingerprint could see. A caller treats null as "ask git".
    /// </summary>
    public static GitLayout? Of(string checkout)
    {
        try
        {
            var dotGit = Path.Combine(checkout, ".git");
            string gitDir;
            if (Directory.Exists(dotGit))
            {
                gitDir = Path.GetFullPath(dotGit);
            }
            else if (File.Exists(dotGit))
            {
                // "gitdir: <path>", relative to the folder the file is in.
                var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
                if (line is null)
                {
                    return null;
                }
                gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), Path.GetFullPath(checkout));
            }
            else
            {
                return null;
            }
            if (!File.Exists(Path.Combine(gitDir, "HEAD")))
            {
                return null;
            }
            // A linked worktree's git dir names the common one, relative to itself; a main checkout and a submodule have none.
            var commonFile = Path.Combine(gitDir, "commondir");
            var common = File.Exists(commonFile) ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir) : gitDir;
            return Directory.Exists(common) && !Directory.Exists(Path.Combine(common, "reftable")) ? new GitLayout(gitDir, common) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// What <c>symbolic-ref refs/remotes/origin/HEAD</c> and the <c>origin/main</c> or <c>origin/master</c> guess read: the
    /// <c>origin/HEAD</c> file, <c>packed-refs</c> (a ref can live only there) and whether the two guessed refs exist. Null when any
    /// of it cannot be read.
    /// </summary>
    public string? DefaultRefFingerprint()
    {
        var head = GitFiles.Stamp(Path.Combine(CommonDir, "refs", "remotes", "origin", "HEAD"));
        var packed = GitFiles.Stamp(Path.Combine(CommonDir, "packed-refs"), GitFiles.PackedRefsHashed);
        var main = GitFiles.Exists(Path.Combine(CommonDir, "refs", "remotes", "origin", "main"));
        var master = GitFiles.Exists(Path.Combine(CommonDir, "refs", "remotes", "origin", "master"));
        return head is null || packed is null || main is null || master is null ? null : $"{head}|{packed}|{main}|{master}";
    }

    /// <summary>
    /// What the commit and the dirt of the checkout read: its <c>HEAD</c>, the ref that names, its <c>index</c> and <c>logs/HEAD</c>,
    /// and <c>packed-refs</c>. Null when <c>HEAD</c> or any part cannot be read. It does not see an edit in the working tree
    /// that never touches the index; whoever relies on it re-reads the status on a timer of its own.
    /// </summary>
    public string? CheckoutFingerprint()
    {
        if (GitFiles.ReadText(Path.Combine(GitDir, "HEAD")) is not { } head)
        {
            return null;
        }
        var text = new StringBuilder(head);
        if (head.StartsWith("ref:", StringComparison.Ordinal))
        {
            // The branch's own file: a commit moves it without touching HEAD.
            var name = head["ref:".Length..].Trim();
            if (name.Length == 0 || Path.IsPathRooted(name) || name.Contains("..", StringComparison.Ordinal))
            {
                return null;
            }
            if (GitFiles.Stamp(Path.Combine(CommonDir, name)) is not { } branch)
            {
                return null;
            }
            text.Append('|').Append(branch);
        }
        var index = GitFiles.Stamp(Path.Combine(GitDir, "index"));
        var log = GitFiles.Stamp(Path.Combine(GitDir, "logs", "HEAD"));
        var packed = GitFiles.Stamp(Path.Combine(CommonDir, "packed-refs"), GitFiles.PackedRefsHashed);
        return index is null || log is null || packed is null ? null : text.Append('|').Append(index).Append('|').Append(log).Append('|').Append(packed).ToString();
    }

    /// <summary>
    /// What the worktree list reads: the main checkout's <c>HEAD</c>, and for each linked worktree its <c>gitdir</c> file, its
    /// <c>HEAD</c> and whether the folder that file names is still there. A new or removed worktree, a move, or a branch switch
    /// changes it; a commit in a worktree does not (its <c>HEAD</c> still names the same branch). Null when any of it cannot be read.
    /// </summary>
    public string? WorktreeListFingerprint()
    {
        if (GitFiles.ReadText(Path.Combine(GitDir, "HEAD")) is not { } main)
        {
            return null;
        }
        var text = new StringBuilder(main);
        try
        {
            var folder = Path.Combine(CommonDir, "worktrees");
            if (Directory.Exists(folder))
            {
                foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
                {
                    var gitdir = GitFiles.ReadText(Path.Combine(dir, "gitdir"));
                    var head = GitFiles.ReadText(Path.Combine(dir, "HEAD"));
                    // A worktree dir git has not finished writing, or a torn-down one's leftover, is read again next time.
                    if (gitdir is null || head is null || GitFiles.Exists(Path.GetFullPath(gitdir, dir)) is not { } exists)
                    {
                        return null;
                    }
                    text.Append('|').Append(Path.GetFileName(dir)).Append('=').Append(gitdir).Append(exists).Append(' ').Append(head);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return text.ToString();
    }

    /// <summary>
    /// Where <paramref name="defaultRef"/> (<c>origin/main</c>) points: its loose ref file, or empty for no default ref. Null for a
    /// ref not under <c>origin/</c>, whose file this does not know to look at, and when the file cannot be read.
    /// </summary>
    public string? TipFingerprint(string? defaultRef)
    {
        if (defaultRef is null)
        {
            return "";
        }
        return defaultRef.StartsWith("origin/", StringComparison.Ordinal) && !defaultRef.Contains("..", StringComparison.Ordinal)
            ? GitFiles.Stamp(Path.Combine(CommonDir, "refs", "remotes", defaultRef))
            : null;
    }
}

/// <summary>Cheap looks at one file, for a fingerprint: null is "cannot read it", never "unchanged".</summary>
internal static class GitFiles
{
    private const int SmallFile = 4096;

    /// <summary><c>packed-refs</c> is read whole up to this size (it is rewritten as a whole and can be tens of kilobytes).</summary>
    public const int PackedRefsHashed = 1 << 20;

    /// <summary>
    /// <c>-</c> for a file that is not there; else its length and write time, and for one no larger than <paramref name="hashedUpTo"/>
    /// a hash of its content as well, so two writes within one timestamp tick still tell apart (a ref file is one line). Null on an IO error.
    /// </summary>
    public static string? Stamp(string path, int hashedUpTo = SmallFile)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return "-";
            }
            var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (info.Length > hashedUpTo)
            {
                return stamp;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return $"{stamp}:{Convert.ToHexString(SHA256.HashData(stream))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>+</c> or <c>-</c> for whether the file is there; null on an IO error.</summary>
    public static string? Exists(string path)
    {
        try
        {
            return new FileInfo(path).Exists ? "+" : "-";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file's text, trimmed; null when it is not there or cannot be read.</summary>
    public static string? ReadText(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd().Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
