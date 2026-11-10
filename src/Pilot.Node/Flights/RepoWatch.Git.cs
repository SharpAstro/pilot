using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The git half of a repo's read, kept from the last round while the files it read are as they were. Every round used to start
/// about ten git processes per repo, 7,000 an hour across the node, each queued behind Process.Start's lock (pilot #794). Now a
/// round fingerprints the files a reading depends on (git's own state: <c>HEAD</c>, the ref it names, <c>index</c>,
/// <c>logs/HEAD</c>, <c>packed-refs</c>, the default branch's ref and the <c>worktrees</c> folder) and runs git only for the part
/// whose fingerprint moved: the worktree list, the main checkout, and each worktree on its own, so a commit in one worktree
/// re-reads that worktree and nothing else. A fingerprint that cannot be read (a file git is writing, an IO error, a layout
/// that is not plain files) is never a match, and a reading that failed is never kept: stale is worse than slow.
/// </summary>
public sealed partial class RepoWatch
{
    /// <summary>
    /// How long a checkout's working-tree status is trusted when nothing git-side moved. An edit to a tracked file that never
    /// touches the index changes <c>git status</c> and no file a fingerprint looks at (HEAD, the refs, the index), so the status is
    /// read again on this timer of its own, whatever the fingerprint says.
    /// </summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How old a git status an explicit refresh still takes (#914, the owner: "unless that was within like 5s ago").</summary>
    public TimeSpan RefreshFresh { get; init; } = TimeSpan.FromSeconds(5);

    // The freshness the read running holds git status to: the refresh's when it is an explicit one.
    private TimeSpan StatusFresh => _runningStrict ? RefreshFresh : StatusInterval;

    /// <summary>The longest any kept reading is trusted, fingerprint or not: a net under whatever the fingerprint cannot see (a config change, a locked worktree).</summary>
    public TimeSpan Revalidate { get; init; } = TimeSpan.FromHours(1);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, RepoMemory> _memory = new(PathComparer);

    /// <summary>What was last read of one repo, by part; a part left out is read afresh. One refresh runs at a time, so a repo's memory has one writer.</summary>
    private sealed class RepoMemory
    {
        public ListMemory? List { get; set; }

        public MainMemory? Main { get; set; }

        public Dictionary<string, WorktreeMemory> Worktrees { get; set; } = new(PathComparer);
    }

    private sealed record ListMemory(string Fingerprint, DateTimeOffset At, IReadOnlyList<WorktreeEntry> Entries);

    private sealed record MainMemory(string Fingerprint, DateTimeOffset At, DateTimeOffset StatusAt, bool Dirty, (int Ahead, int Behind)? Sync);

    private sealed record WorktreeMemory(string Fingerprint, DateTimeOffset At, DateTimeOffset StatusAt, bool Dirty, (int Files, int Added, int Removed)? Diff);

    private sealed record WorktreeRead(WorktreeEntry Entry, bool? Dirty, (int Files, int Added, int Removed)? Diff);

    private sealed record GitSide(IReadOnlyList<WorktreeEntry>? Entries, string? DefaultRef, bool? Dirty, (int Ahead, int Behind)? Sync, List<WorktreeRead> Worktrees);

    /// <summary>A read of git or gh: what it says, or null with the reason put in the repo's errors. Never fails the rest.</summary>
    private sealed class Reads(List<string> errors, CancellationToken cancellationToken)
    {
        public List<string> Errors => errors;

        public async Task<T?> Ref<T>(string what, Func<Task<T?>> read) where T : class
        {
            try
            {
                return await read();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        public async Task<T?> Value<T>(string what, Func<Task<T?>> read) where T : struct
        {
            try
            {
                return await read() ?? Failed<T>(what);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        private T? Failed<T>(string what) where T : struct
        {
            errors.Add($"git could not read {what}");
            return null;
        }
    }

    /// <summary>Whether something read at <paramref name="at"/> is still within <paramref name="span"/>; a clock set back makes it stale.</summary>
    private static bool Fresh(DateTimeOffset at, TimeSpan span, DateTimeOffset now) => now >= at && now - at < span;

    /// <summary>A checkout's fingerprint with the default branch and where it points folded in; null when any part cannot be read.</summary>
    private static string? Print(string? checkout, string? defaultRef, string? tip) => checkout is null || tip is null ? null : $"{checkout}|{defaultRef}|{tip}";

    private async Task<GitSide> ReadGitAsync(RepoInfo repo, DateTimeOffset now, Reads reads, CancellationToken cancellationToken)
    {
        var errors = reads.Errors;
        var memory = _memory.GetOrAdd(repo.Path, _ => new RepoMemory());
        var layout = GitLayout.Of(repo.Path);

        // Each fingerprint is taken before the git run it guards, so a change during the run leaves one the next round will not match.
        var listPrint = layout?.WorktreeListFingerprint();
        IReadOnlyList<WorktreeEntry>? entries;
        if (listPrint is not null && memory.List is { } listed && listed.Fingerprint == listPrint && Fresh(listed.At, Revalidate, now))
        {
            entries = listed.Entries;
        }
        else
        {
            var before = errors.Count;
            entries = await reads.Ref("worktrees", () => planes.WorktreesAsync(repo.Path, cancellationToken));
            if (entries is null && errors.Count == before)
            {
                errors.Add("git could not list the worktrees");
            }
            memory.List = listPrint is not null && entries is not null && errors.Count == before ? new ListMemory(listPrint, now, entries) : null;
        }

        // Planes keeps the default branch by its own fingerprint, so this starts no git while origin's refs are as they were.
        var refErrors = errors.Count;
        var defaultRef = await reads.Ref("default branch", () => planes.DefaultRefAsync(repo.Path, cancellationToken));
        var refRead = errors.Count == refErrors;
        var tip = layout?.TipFingerprint(defaultRef);

        var mainPrint = refRead ? Print(layout?.CheckoutFingerprint(), defaultRef, tip) : null;
        bool? dirty;
        (int Ahead, int Behind)? sync;
        if (mainPrint is not null && memory.Main is { } main && main.Fingerprint == mainPrint && Fresh(main.At, Revalidate, now))
        {
            dirty = main.Dirty;
            sync = main.Sync;
            if (!Fresh(main.StatusAt, StatusFresh, now))
            {
                var before = errors.Count;
                dirty = await reads.Value("main checkout's status", () => planes.IsDirtyAsync(repo.Path, cancellationToken, withoutPlanes: true));
                memory.Main = dirty is { } read && errors.Count == before ? main with { Dirty = read, StatusAt = now } : null;
            }
        }
        else
        {
            var before = errors.Count;
            dirty = await reads.Value("main checkout's status", () => planes.IsDirtyAsync(repo.Path, cancellationToken, withoutPlanes: true));
            sync = defaultRef is null ? null : await reads.Value($"main checkout against {defaultRef}", () => planes.AheadBehindAsync(repo.Path, defaultRef, cancellationToken));
            memory.Main = mainPrint is not null && dirty is { } read && errors.Count == before ? new MainMemory(mainPrint, now, now, read, sync) : null;
        }

        var worktrees = new List<WorktreeRead>();
        var kept = new Dictionary<string, WorktreeMemory>(PathComparer);
        foreach (var entry in (entries ?? []).Skip(1).Where(e => !e.Prunable && Directory.Exists(e.Path)))
        {
            var print = refRead ? Print(GitLayout.Of(entry.Path)?.CheckoutFingerprint(), defaultRef, tip) : null;
            bool? wDirty;
            (int Files, int Added, int Removed)? diff;
            if (print is not null && memory.Worktrees.TryGetValue(entry.Path, out var last) && last.Fingerprint == print && Fresh(last.At, Revalidate, now))
            {
                wDirty = last.Dirty;
                diff = last.Diff;
                var current = last;
                if (!Fresh(last.StatusAt, StatusFresh, now))
                {
                    var before = errors.Count;
                    wDirty = await reads.Value($"{entry.Path}'s status", () => planes.IsDirtyAsync(entry.Path, cancellationToken));
                    current = wDirty is { } read && errors.Count == before ? last with { Dirty = read, StatusAt = now } : null;
                }
                if (current is not null)
                {
                    kept[entry.Path] = current;
                }
            }
            else
            {
                var before = errors.Count;
                wDirty = await reads.Value($"{entry.Path}'s status", () => planes.IsDirtyAsync(entry.Path, cancellationToken));
                diff = defaultRef is null ? null : await reads.Value($"{entry.Path}'s diff", () => planes.DiffStatAsync(entry.Path, defaultRef, cancellationToken));
                if (print is not null && wDirty is { } read && errors.Count == before)
                {
                    kept[entry.Path] = new WorktreeMemory(print, now, now, read, diff);
                }
            }
            worktrees.Add(new WorktreeRead(entry, wDirty, diff));
        }
        // A worktree no longer listed is forgotten with its memory.
        memory.Worktrees = kept;

        return new GitSide(entries, defaultRef, dirty, sync, worktrees);
    }
}
