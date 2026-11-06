using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pilot.Node.Flights;

namespace Pilot.Node.Tests;

/// <summary>
/// An in-memory git for the tests that check what pilot decides, not what git does (#813). A git run costs 100 to 400 ms on
/// Windows (process start under the process-creation lock and the scanner), and a flight's lifecycle in the node makes about
/// 50 of them, so a test class of a few dozen flights took minutes there and seconds on Linux. This models the repo the
/// review tests build: one tianwen repo with a bare origin, branches, commits as trees of text files, the worktrees a flight's
/// plane is, and the index, so <c>status</c>, <c>add</c> and <c>commit</c> work on the plane's real folder.
/// </summary>
/// <remarks>
/// What it answers is what the node's flight path asks: refs and revisions, ancestry, ranges, diffs and their stat, worktrees,
/// fetch and push against the modelled origin, commit, and a path-level merge. Anything else (rebase, LFS, plumbing for a laid
/// PR) throws <see cref="NotSupportedException"/> and is kept in <see cref="Unsupported"/>, so a test that needs real git fails
/// loudly instead of passing on a guess: those tests stay on <see cref="GitCli"/> (see <see cref="ReviewTestsBase"/>).
/// </remarks>
internal sealed class FakeRepoGit : IGit
{
    private sealed record Commit(string Id, int Seq, IReadOnlyList<string> Parents, IReadOnlyDictionary<string, string> Tree, string Message);

    private sealed class Worktree(string path, string? branch, string? detached, Dictionary<string, string> index)
    {
        public string Path { get; } = path;

        public string? Branch { get; set; } = branch;

        /// <summary>The commit a detached HEAD is at; null on a branch.</summary>
        public string? Detached { get; set; } = detached;

        public Dictionary<string, string> Index { get; set; } = index;

        /// <summary>A merge that stopped (on conflicts, or by <c>--no-commit</c>): the commit being merged, and the paths in conflict.</summary>
        public (string Target, List<string> Paths)? Merging { get; set; }

        /// <summary>What a merge that is finished by <c>git commit --no-edit</c> is called.</summary>
        public string? MergeMessage { get; set; }

        /// <summary>A rebase that stopped on a conflict: the head it began at, which <c>rebase --abort</c> returns to.</summary>
        public string? Rebasing { get; set; }
    }

    private static readonly ConcurrentDictionary<string, FakeRepoGit> Roots = new(StringComparer.OrdinalIgnoreCase);

    private static int _seq;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Commit> _commits = [];
    private readonly Dictionary<string, string> _origin = [];
    private readonly Dictionary<string, string> _refs = [];
    private readonly List<Worktree> _worktrees = [];
    private readonly string _root;
    private readonly string _originPath;
    private readonly List<string> _unsupported = [];
    private readonly Dictionary<string, string> _config = [];
    private bool _originHead;

    /// <summary>Whether a bare origin repo exists to fetch from and push to; a remote can be configured (<see cref="_remoteUrl"/>) with none reachable, as a GitHub URL is offline.</summary>
    private bool _hasOrigin;

    /// <summary>The URL of the remote <c>origin</c>; null when the repo has none.</summary>
    private string? _remoteUrl;

    /// <summary>
    /// A repo <c>tianwen</c> under <paramref name="root"/>, with one commit on main. With <paramref name="withOrigin"/> it has a bare origin beside it
    /// holding that commit, as <see cref="SeedRepo"/> makes them; without, it is a <c>git init</c> repo with no remote, as the leg and overlap tests build.
    /// <paramref name="withSolution"/> adds the stub solution a check that builds or tests needs in the plane's root (leg plan refuses a repo without one).
    /// </summary>
    public FakeRepoGit(string root, bool withOrigin = true, bool withSolution = true)
    {
        _root = Norm(root);
        _hasOrigin = withOrigin;
        _originHead = withOrigin;
        RepoPath = Path.Combine(root, "tianwen");
        _originPath = Path.Combine(root, "origin.git");
        _remoteUrl = withOrigin ? _originPath : null;
        Directory.CreateDirectory(Path.Combine(RepoPath, ".git"));
        if (withOrigin)
        {
            Directory.CreateDirectory(_originPath);
        }
        WriteConfigFile();
        var tree = new Dictionary<string, string> { ["README.md"] = "hello\n" };
        if (withSolution)
        {
            tree["Tianwen.slnx"] = "<Solution />\n";
        }
        var first = MakeCommit([], tree, "first");
        _refs["refs/heads/main"] = first.Id;
        if (withOrigin)
        {
            _origin["refs/heads/main"] = first.Id;
            _refs["refs/remotes/origin/main"] = first.Id;
        }
        var main = new Worktree(Norm(RepoPath), "main", null, new Dictionary<string, string>(tree));
        _worktrees.Add(main);
        Materialize(main, tree, new Dictionary<string, string>());
        Roots[_root] = this;
    }

    public string RepoPath { get; }

    /// <summary>Git runs answered.</summary>
    public int Runs { get; private set; }

    /// <summary>Commands this model does not answer, each as the git line; a test that left any here used real git's job.</summary>
    public IReadOnlyList<string> Unsupported
    {
        get
        {
            lock (_gate)
            {
                return [.. _unsupported];
            }
        }
    }

    /// <summary>The model whose folders <paramref name="cwd"/> is in; null when it is in none.</summary>
    public static FakeRepoGit? Owning(string cwd)
    {
        var path = Norm(cwd);
        foreach (var (root, model) in Roots)
        {
            if (Under(path, root))
            {
                return model;
            }
        }
        return null;
    }

    /// <summary>Forgets the model; its folders are the test's to delete.</summary>
    public void Release() => Roots.TryRemove(_root, out _);

    /// <summary>Removes the model's folders (the repo, its planes and the origin), for a test that moves to real git before it has used any.</summary>
    public void DeleteFolders()
    {
        Release();
        Directory.Delete(RepoPath, recursive: true);
        if (Directory.Exists(_originPath))
        {
            Directory.Delete(_originPath, recursive: true);
        }
    }

    public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) =>
        Task.FromResult(Run(args, cwd));

    public GitRun Run(IReadOnlyList<string> args, string cwd)
    {
        lock (_gate)
        {
            Runs++;
            var rest = new List<string>(args);
            while (rest.Count >= 2 && rest[0] is "-c" or "-C")
            {
                if (rest[0] == "-C")
                {
                    cwd = Path.IsPathRooted(rest[1]) ? rest[1] : Path.Combine(cwd, rest[1]);
                }
                rest.RemoveRange(0, 2);
            }
            try
            {
                return rest.Count == 0 ? Fail("usage: git <command>") : Dispatch(rest[0], rest.Skip(1).ToList(), cwd);
            }
            catch (NotSupportedException ex)
            {
                _unsupported.Add($"git {string.Join(' ', args)}");
                throw new NotSupportedException($"FakeRepoGit does not answer: git {string.Join(' ', args)} ({ex.Message}). A test that needs it runs on real git.");
            }
        }
    }

    private GitRun Dispatch(string verb, List<string> a, string cwd)
    {
        var wt = WorktreeOf(cwd);
        // A bare origin is made, or cloned from the repo, beside it: the only repos made besides the one modelled.
        if (wt is null && verb is "init" or "clone" && Under(Norm(cwd), _root))
        {
            return verb == "init" ? Init(a, cwd) : Clone(a, cwd);
        }
        if (wt is null && !Under(Norm(cwd), Norm(_originPath)))
        {
            return Fail($"fatal: not a git repository (or any of the parent directories): .git", 128);
        }
        if (a.Any(x => x is "origin" or "refs/remotes/origin/HEAD") && verb is "remote" or "fetch" or "push" or "ls-remote" or "symbolic-ref")
        {
            // A repo made by git init has no remote; one whose remote is a URL no model serves (GitHub's) cannot be reached: each fails as git says.
            if (_remoteUrl is null && !(verb == "remote" && a is ["add", ..]))
            {
                switch (verb)
                {
                    case "remote":
                        return Fail("error: No such remote 'origin'", 2);
                    case "symbolic-ref":
                        return Fail("fatal: ref refs/remotes/origin/HEAD is not a symbolic ref", 128);
                    default:
                        return Fail("fatal: 'origin' does not appear to be a git repository\nfatal: Could not read from remote repository.", 128);
                }
            }
            if (!_hasOrigin && verb is "fetch" or "push" or "ls-remote")
            {
                return Fail($"fatal: unable to access '{_remoteUrl}': Could not resolve host: github.com", 128);
            }
        }
        return verb switch
        {
            "rev-parse" => RevParse(a, wt),
            "symbolic-ref" => SymbolicRef(a, wt),
            "remote" => Remote(a),
            "fetch" => Fetch(a),
            "push" => Push(a, wt),
            "worktree" => WorktreeCommand(a),
            "merge-base" => MergeBaseCommand(a, wt),
            "rev-list" => RevList(a, wt),
            "log" => Log(a, wt),
            "diff" => Diff(a, wt),
            "show" => Show(a, wt),
            "cat-file" => CatFile(a, wt),
            "status" => Status(a, wt),
            "add" => Add(a, wt),
            "commit" => CommitCommand(a, wt),
            "merge" => Merge(a, wt),
            "reset" => Reset(a, wt),
            "cherry" => Cherry(a, wt),
            "cherry-pick" => CherryPick(a, wt),
            "branch" => Branch(a, wt),
            "rebase" => Rebase(a, wt),
            "config" => Config(a),
            "ls-remote" => LsRemote(a),
            "switch" => Switch(a, wt),
            _ => throw new NotSupportedException("verb"),
        };
    }

    // ---- objects and refs ----

    private Commit MakeCommit(IReadOnlyList<string> parents, IReadOnlyDictionary<string, string> tree, string message)
    {
        var seq = Interlocked.Increment(ref _seq);
        var text = new StringBuilder().Append(seq).Append('\n').Append(string.Join(',', parents)).Append('\n').Append(message).Append('\n');
        foreach (var (path, content) in tree.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            text.Append(path).Append('\0').Append(content).Append('\0');
        }
        var id = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        var commit = new Commit(id, seq, parents, new Dictionary<string, string>(tree), message);
        _commits[id] = commit;
        return commit;
    }

    private Worktree? WorktreeOf(string cwd)
    {
        var path = Norm(cwd);
        return _worktrees.Where(w => Under(path, w.Path)).OrderByDescending(w => w.Path.Length).FirstOrDefault();
    }

    private string? HeadId(Worktree wt) => wt.Detached ?? (wt.Branch is { } b && _refs.TryGetValue("refs/heads/" + b, out var id) ? id : null);

    /// <summary>The commit <paramref name="rev"/> names in the repo <paramref name="wt"/> is in (the origin when null); null when it names none.</summary>
    private string? Resolve(string rev, Worktree? wt)
    {
        if (rev.EndsWith("^{commit}", StringComparison.Ordinal))
        {
            rev = rev[..^"^{commit}".Length];
        }
        if (rev.EndsWith("^{tree}", StringComparison.Ordinal))
        {
            throw new NotSupportedException("^{tree}");
        }
        // Trailing ^, ^N and ~N walk parents (^N is the Nth parent, ~N is N first parents), applied left to right.
        var ops = new List<(char Kind, int N)>();
        while (true)
        {
            var cut = rev.LastIndexOfAny(['^', '~']);
            if (cut <= 0 || !rev[(cut + 1)..].All(char.IsDigit))
            {
                break;
            }
            var digits = rev[(cut + 1)..];
            ops.Add((rev[cut], digits.Length == 0 ? 1 : int.Parse(digits)));
            rev = rev[..cut];
        }
        var id = ResolveName(rev, wt);
        for (var k = ops.Count - 1; k >= 0 && id is not null; k--)
        {
            var (kind, n) = ops[k];
            if (kind == '^')
            {
                id = n == 0 ? id : _commits[id].Parents.Count >= n ? _commits[id].Parents[n - 1] : null;
                continue;
            }
            for (var i = 0; id is not null && i < n; i++)
            {
                id = _commits[id].Parents.Count > 0 ? _commits[id].Parents[0] : null;
            }
        }
        return id;
    }

    private string? ResolveName(string name, Worktree? wt)
    {
        if (name == "HEAD")
        {
            return wt is null ? Origin("refs/heads/main") : HeadId(wt);
        }
        if (name.StartsWith("refs/", StringComparison.Ordinal))
        {
            return wt is null ? Origin(name) : _refs.GetValueOrDefault(name);
        }
        if (name.Contains('@'))
        {
            throw new NotSupportedException("@ revision");
        }
        if (wt is null && Origin("refs/heads/" + name) is { } pushed)
        {
            return pushed;
        }
        if (wt is not null && _refs.TryGetValue("refs/heads/" + name, out var local))
        {
            return local;
        }
        if (wt is not null && _refs.TryGetValue("refs/remotes/" + name, out var remote))
        {
            return remote;
        }
        if (_commits.ContainsKey(name))
        {
            return name;
        }
        if (name.Length >= 4 && name.All(Uri.IsHexDigit))
        {
            var found = _commits.Keys.Where(k => k.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return found.Count == 1 ? found[0] : null;
        }
        return null;
    }

    private string? Origin(string name) => _origin.GetValueOrDefault(name);

    private HashSet<string> Ancestors(string id, bool firstParent = false)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>([id]);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next))
            {
                continue;
            }
            var parents = _commits[next].Parents;
            foreach (var p in firstParent ? parents.Take(1) : parents)
            {
                stack.Push(p);
            }
        }
        return seen;
    }

    private bool IsAncestor(string maybe, string of) => Ancestors(of).Contains(maybe);

    private string? MergeBase(string a, string b)
    {
        var common = Ancestors(a).Intersect(Ancestors(b)).ToList();
        // The best common ancestors are those no other common ancestor descends from.
        var best = common.Where(c => !common.Any(o => o != c && IsAncestor(c, o))).ToList();
        return best.Count == 0 ? null : best.OrderByDescending(c => _commits[c].Seq).First();
    }

    private static string Short(string id) => id[..7];

    // ---- commands: refs and revisions ----

    private GitRun RevParse(List<string> a, Worktree? wt)
    {
        if (a is ["--path-format=absolute", "--git-common-dir"])
        {
            return Ok(Norm(Path.Combine(RepoPath, ".git")) + "\n");
        }
        if (a is ["--abbrev-ref", "HEAD"] && wt is not null)
        {
            return Ok((wt.Branch ?? "HEAD") + "\n");
        }
        if (a is ["--git-common-dir"])
        {
            // Relative to where it is asked: ".git" in the main checkout, absolute in a linked worktree.
            return Ok((wt == _worktrees[0] ? ".git" : Norm(Path.Combine(RepoPath, ".git"))) + "\n");
        }
        var quiet = a.Remove("--quiet");
        var verify = a.Remove("--verify");
        if (a.Count != 1 || a[0].StartsWith('-'))
        {
            throw new NotSupportedException("rev-parse form");
        }
        var id = Resolve(a[0], wt);
        if (id is null)
        {
            return verify && quiet ? new GitRun(1, "", "") : Fail($"fatal: ambiguous argument '{a[0]}': unknown revision or path not in the working tree.", 128);
        }
        return Ok(id + "\n");
    }

    private GitRun SymbolicRef(List<string> a, Worktree? wt)
    {
        a.Remove("--short");
        if (a is ["refs/remotes/origin/HEAD"])
        {
            return _originHead ? Ok("origin/main\n") : Fail("fatal: ref refs/remotes/origin/HEAD is not a symbolic ref", 128);
        }
        if (a is ["HEAD"] && wt is not null)
        {
            return wt.Branch is { } b ? Ok(b + "\n") : Fail("fatal: ref HEAD is not a symbolic ref", 128);
        }
        throw new NotSupportedException("symbolic-ref form");
    }

    private GitRun Remote(List<string> a)
    {
        switch (a)
        {
            case ["add", "origin", var url]:
                if (_remoteUrl is not null)
                {
                    return Fail("error: remote origin already exists.", 3);
                }
                _remoteUrl = url;
                _config["remote.origin.url"] = url;
                WriteConfigFile();
                return Ok("");
            case ["get-url", "origin"]:
                // As git prints it: with the url.<base>.insteadOf rewrites applied.
                var rewrite = _config.Where(c => c.Key.StartsWith("url.", StringComparison.Ordinal) && c.Key.EndsWith(".insteadOf", StringComparison.Ordinal) && c.Value == _remoteUrl)
                    .Select(c => c.Key["url.".Length..^".insteadOf".Length]).FirstOrDefault();
                return Ok((rewrite ?? _remoteUrl) + "\n");
            case ["set-head", "origin", "--auto"]:
                _originHead = _hasOrigin;
                return Ok("");
            case ["set-head", "origin", "--delete"]:
                _originHead = false;
                return Ok("");
            case ["set-head", "origin", var branch] when !branch.StartsWith('-'):
                _originHead = _refs.ContainsKey("refs/remotes/origin/" + branch);
                return _originHead ? Ok("") : Fail($"error: Not a valid ref: refs/remotes/origin/{branch}", 1);
            default:
                throw new NotSupportedException("remote form");
        }
    }

    /// <summary><c>git init --bare [-b main] origin.git</c>: the origin the repo's remote can be pointed at.</summary>
    private GitRun Init(List<string> a, string cwd)
    {
        a.RemoveAll(x => x is "-q" or "--quiet");
        var bare = a.Remove("--bare");
        var at = a.IndexOf("-b");
        if (at >= 0)
        {
            a.RemoveRange(at, 2);
        }
        if (!bare || a.Count != 1 || Norm(Path.Combine(cwd, a[0])) != Norm(_originPath))
        {
            throw new NotSupportedException("init of a repo other than the origin");
        }
        Directory.CreateDirectory(_originPath);
        _hasOrigin = true;
        return Ok("");
    }

    /// <summary><c>git clone --bare &lt;the repo&gt; origin.git</c>: the origin holding the repo's branches.</summary>
    private GitRun Clone(List<string> a, string cwd)
    {
        a.RemoveAll(x => x is "-q" or "--quiet");
        var bare = a.Remove("--bare");
        if (!bare || a.Count != 2 || Norm(Path.Combine(cwd, a[0])) != Norm(RepoPath) || Norm(Path.Combine(cwd, a[1])) != Norm(_originPath))
        {
            throw new NotSupportedException("clone of anything but the repo into the origin");
        }
        Directory.CreateDirectory(_originPath);
        foreach (var (name, id) in _refs.Where(r => r.Key.StartsWith("refs/heads/", StringComparison.Ordinal)).ToList())
        {
            _origin[name] = id;
        }
        _hasOrigin = true;
        return Ok("");
    }

    /// <summary><c>git branch --list &lt;pattern&gt;</c>: the local branches that match, one per line, the checked out one starred.</summary>
    private GitRun Branch(List<string> a, Worktree? wt)
    {
        if (wt is null || a is not ["--list", var pattern])
        {
            throw new NotSupportedException("branch form");
        }
        var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");
        return Ok(string.Concat(_refs.Keys.Where(k => k.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(k => k["refs/heads/".Length..])
            .Where(b => regex.IsMatch(b)).Order(StringComparer.Ordinal).Select(b => (b == wt.Branch ? "* " : "  ") + b + "\n")));
    }

    private GitRun Config(List<string> a)
    {
        a.Remove("--local");
        switch (a)
        {
            case [var key, var value] when !key.StartsWith('-'):
                _config[key] = value;
                if (key == "remote.origin.url")
                {
                    WriteConfigFile();
                }
                return Ok("");
            case ["--get", var key]:
                return _config.TryGetValue(key, out var found) ? Ok(found + "\n") : new GitRun(1, "", "");
            default:
                throw new NotSupportedException("config form");
        }
    }

    /// <summary>The clone's <c>.git/config</c> as the repo catalog reads it from the folder, without asking git (it names the clone by its origin's URL).</summary>
    private void WriteConfigFile() =>
        File.WriteAllText(Path.Combine(RepoPath, ".git", "config"),
            "[core]\n\trepositoryformatversion = 0\n" + (_remoteUrl is not null
                ? $"[remote \"origin\"]\n\turl = {_config.GetValueOrDefault("remote.origin.url", _remoteUrl)}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n"
                : ""));

    /// <summary><c>git ls-remote origin &lt;pattern&gt;</c>: the origin's branches whose name ends with the pattern.</summary>
    private GitRun LsRemote(List<string> a)
    {
        a.RemoveAll(x => x is "--heads" or "-h" or "--quiet" or "-q");
        if (a.Count is < 1 or > 2 || a[0] != "origin" || a.Any(x => x.StartsWith('-')))
        {
            throw new NotSupportedException("ls-remote form");
        }
        var pattern = a.Count == 2 ? a[1] : null;
        return Ok(string.Concat(_origin.Where(o => o.Key.StartsWith("refs/heads/", StringComparison.Ordinal) && (pattern is null || o.Key == pattern || o.Key.EndsWith("/" + pattern, StringComparison.Ordinal)))
            .OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Value}\t{o.Key}\n")));
    }

    /// <summary><c>git switch [-c] &lt;branch&gt;</c>: a branch made at HEAD and moved to, or an existing one moved to.</summary>
    private GitRun Switch(List<string> a, Worktree? wt)
    {
        a.RemoveAll(x => x is "-q" or "--quiet");
        var create = a.Remove("-c");
        if (wt is null || a.Count != 1 || a[0].StartsWith('-'))
        {
            throw new NotSupportedException("switch form");
        }
        var head = HeadId(wt);
        if (create)
        {
            if (_refs.ContainsKey("refs/heads/" + a[0]))
            {
                return Fail($"fatal: a branch named '{a[0]}' already exists", 128);
            }
            _refs["refs/heads/" + a[0]] = head ?? throw new NotSupportedException("switch -c on an unborn branch");
            wt.Branch = a[0];
            wt.Detached = null;
            return Ok("");
        }
        if (!_refs.TryGetValue("refs/heads/" + a[0], out var target))
        {
            return Fail($"fatal: invalid reference: {a[0]}", 128);
        }
        var before = wt.Index;
        wt.Branch = a[0];
        wt.Detached = null;
        wt.Index = new Dictionary<string, string>(_commits[target].Tree);
        Materialize(wt, _commits[target].Tree, before);
        return Ok("");
    }

    private GitRun Fetch(List<string> a)
    {
        var prune = a.Remove("--prune");
        a.RemoveAll(x => x is "--quiet" or "-q");
        if (a.Count == 0 || a[0] != "origin")
        {
            throw new NotSupportedException("fetch form");
        }
        if (a.Count == 1)
        {
            foreach (var (name, id) in _origin.Where(o => o.Key.StartsWith("refs/heads/", StringComparison.Ordinal)))
            {
                _refs["refs/remotes/origin/" + name["refs/heads/".Length..]] = id;
            }
            if (prune)
            {
                foreach (var stale in _refs.Keys.Where(k => k.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) && k != "refs/remotes/origin/HEAD"
                    && !_origin.ContainsKey("refs/heads/" + k["refs/remotes/origin/".Length..])).ToList())
                {
                    _refs.Remove(stale);
                }
            }
            return Ok("");
        }
        foreach (var spec in a.Skip(1))
        {
            var parts = spec.TrimStart('+').Split(':');
            if (parts.Length != 2)
            {
                throw new NotSupportedException("fetch refspec");
            }
            if (!_origin.TryGetValue(parts[0], out var id))
            {
                return Fail($"fatal: couldn't find remote ref {parts[0]}", 128);
            }
            _refs[parts[1]] = id;
        }
        return Ok("");
    }

    private GitRun Push(List<string> a, Worktree? wt)
    {
        var force = a.Any(x => x is "--force" or "-f") || a.Any(x => x.StartsWith('+'));
        string? lease = a.FirstOrDefault(x => x.StartsWith("--force-with-lease=", StringComparison.Ordinal));
        var positional = a.Where(x => !x.StartsWith('-')).ToList();
        if (wt is null || positional.Count is < 1 or > 2 || positional[0] != "origin")
        {
            throw new NotSupportedException("push form");
        }
        var spec = positional.Count == 2 ? positional[1].TrimStart('+') : "HEAD";
        var colon = spec.IndexOf(':');
        var src = colon < 0 ? spec : spec[..colon];
        var dst = colon < 0 ? (src == "HEAD" ? wt.Branch ?? throw new NotSupportedException("push of a detached HEAD") : src) : spec[(colon + 1)..];
        var dstRef = dst.StartsWith("refs/", StringComparison.Ordinal) ? dst : "refs/heads/" + dst;
        var id = Resolve(src, wt);
        if (id is null)
        {
            return Fail($"error: src refspec {src} does not match any", 1);
        }
        if (_origin.TryGetValue(dstRef, out var old) && old != id)
        {
            var fastForward = IsAncestor(old, id);
            var leased = lease is not null && lease.Split('=', 2)[1].Split(':') is [_, var expected] && Resolve(expected, wt) == old;
            if (!fastForward && !force && !leased)
            {
                return Fail($" ! [rejected]        {src} -> {dst} (non-fast-forward)\nerror: failed to push some refs to '{_originPath}'", 1);
            }
        }
        _origin[dstRef] = id;
        if (dstRef.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            _refs["refs/remotes/origin/" + dstRef["refs/heads/".Length..]] = id;
        }
        return Ok("");
    }

    // ---- commands: worktrees ----

    private GitRun WorktreeCommand(List<string> a)
    {
        switch (a)
        {
            case ["list", "--porcelain"]:
                var text = new StringBuilder();
                foreach (var w in _worktrees)
                {
                    text.Append("worktree ").Append(w.Path).Append('\n').Append("HEAD ").Append(HeadId(w)).Append('\n');
                    text.Append(w.Branch is { } b ? $"branch refs/heads/{b}\n" : "detached\n");
                    if (!Directory.Exists(w.Path))
                    {
                        text.Append("prunable gitdir file points to non-existent location\n");
                    }
                    text.Append('\n');
                }
                return Ok(text.ToString());
            case ["prune"]:
                _worktrees.RemoveAll(w => w != _worktrees[0] && !Directory.Exists(w.Path));
                return Ok("");
            case ["unlock", _]:
                return Fail("fatal: not locked", 128);
            case ["remove", ..]:
                return WorktreeRemove(a);
            case ["add", ..]:
                return WorktreeAdd(a.Skip(1).ToList());
            default:
                throw new NotSupportedException("worktree form");
        }
    }

    private GitRun WorktreeRemove(List<string> a)
    {
        var force = a.Contains("--force");
        var path = Norm(a.Last());
        var wt = _worktrees.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
        if (wt is null || wt == _worktrees[0])
        {
            return Fail($"fatal: '{a.Last()}' is not a working tree", 128);
        }
        if (!force && Dirty(wt))
        {
            return Fail($"fatal: '{a.Last()}' contains modified or untracked files, use --force to delete it", 128);
        }
        if (Directory.Exists(wt.Path))
        {
            Directory.Delete(wt.Path, recursive: true);
        }
        _worktrees.Remove(wt);
        return Ok("");
    }

    private GitRun WorktreeAdd(List<string> a)
    {
        a.Remove("--no-track");
        string? newBranch = null;
        var reset = false;
        var detach = false;
        var positional = new List<string>();
        for (var i = 0; i < a.Count; i++)
        {
            switch (a[i])
            {
                case "-b" or "-B":
                    reset = a[i] == "-B";
                    newBranch = a[++i];
                    break;
                case "--detach":
                    detach = true;
                    break;
                case "--force" or "-f" or "--quiet" or "-q":
                    break;
                default:
                    positional.Add(a[i]);
                    break;
            }
        }
        if (positional.Count is < 1 or > 2)
        {
            throw new NotSupportedException("worktree add form");
        }
        var path = Norm(positional[0]);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() || _worktrees.Any(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail($"fatal: '{positional[0]}' already exists", 128);
        }
        string? branch = null;
        string commit;
        if (newBranch is not null)
        {
            var from = positional.Count == 2 ? positional[1] : "HEAD";
            commit = Resolve(from, _worktrees[0]) ?? "";
            if (commit.Length == 0)
            {
                return Fail($"fatal: invalid reference: {from}", 128);
            }
            if (!reset && _refs.ContainsKey("refs/heads/" + newBranch))
            {
                return Fail($"fatal: a branch named '{newBranch}' already exists", 128);
            }
            branch = newBranch;
        }
        else
        {
            var target = positional.Count == 2 ? positional[1] : "HEAD";
            if (!detach && _refs.ContainsKey("refs/heads/" + target))
            {
                branch = target;
            }
            else if (!detach && positional.Count == 1)
            {
                throw new NotSupportedException("worktree add of a new branch by name");
            }
            commit = Resolve(target, _worktrees[0]) ?? "";
            if (commit.Length == 0)
            {
                return Fail($"fatal: invalid reference: {target}", 128);
            }
        }
        if (branch is not null && _worktrees.FirstOrDefault(w => w.Branch == branch) is { } holder)
        {
            return Fail($"fatal: '{branch}' is already checked out at '{holder.Path}'", 128);
        }
        if (branch is not null)
        {
            _refs["refs/heads/" + branch] = commit;
        }
        var tree = _commits[commit].Tree;
        var wt = new Worktree(path, branch, branch is null ? commit : null, new Dictionary<string, string>(tree));
        Directory.CreateDirectory(path);
        Materialize(wt, tree, new Dictionary<string, string>());
        _worktrees.Add(wt);
        return Ok("");
    }

    // ---- commands: history ----

    private GitRun MergeBaseCommand(List<string> a, Worktree? wt)
    {
        var ancestor = a.Remove("--is-ancestor");
        if (a.Count != 2)
        {
            throw new NotSupportedException("merge-base form");
        }
        var x = Resolve(a[0], wt);
        var y = Resolve(a[1], wt);
        if (x is null || y is null)
        {
            return Fail($"fatal: Not a valid object name {(x is null ? a[0] : a[1])}", 128);
        }
        if (ancestor)
        {
            return new GitRun(IsAncestor(x, y) ? 0 : 1, "", "");
        }
        return MergeBase(x, y) is { } b ? Ok(b + "\n") : new GitRun(1, "", "");
    }

    /// <summary>The commits a rev-list or log line selects, newest first: what its revisions include, less what <c>^X</c>, <c>X..Y</c> and <c>--not</c> exclude.</summary>
    private (List<Commit> Commits, List<Commit> Left) Select(List<string> revs, Worktree? wt, bool firstParent)
    {
        var include = new HashSet<string>();
        var exclude = new HashSet<string>();
        var left = new HashSet<string>();
        var negate = false;
        foreach (var token in revs)
        {
            if (token == "--not")
            {
                negate = true;
                continue;
            }
            if (token == "--remotes")
            {
                foreach (var id in _refs.Where(r => r.Key.StartsWith("refs/remotes/", StringComparison.Ordinal)).Select(r => r.Value))
                {
                    (negate ? exclude : include).UnionWith(Ancestors(id));
                }
                continue;
            }
            if (token.Contains("...", StringComparison.Ordinal))
            {
                var parts = token.Split("...");
                var l = ResolveOrThrow(parts[0], wt);
                var r = ResolveOrThrow(parts[1], wt);
                var lSet = Ancestors(l);
                var rSet = Ancestors(r, firstParent);
                include.UnionWith(lSet.Union(rSet));
                exclude.UnionWith(lSet.Intersect(Ancestors(r)));
                left.UnionWith(lSet.Except(Ancestors(r)));
            }
            else if (token.Contains("..", StringComparison.Ordinal))
            {
                var parts = token.Split("..");
                exclude.UnionWith(Ancestors(ResolveOrThrow(parts[0], wt)));
                include.UnionWith(Ancestors(ResolveOrThrow(parts[1], wt), firstParent));
            }
            else if (token.StartsWith('^'))
            {
                exclude.UnionWith(Ancestors(ResolveOrThrow(token[1..], wt)));
            }
            else
            {
                var id = ResolveOrThrow(token, wt);
                (negate ? exclude : include).UnionWith(Ancestors(id, firstParent && !negate));
            }
        }
        var picked = include.Except(exclude).Select(i => _commits[i]).OrderByDescending(c => c.Seq).ToList();
        return (picked, picked.Where(c => left.Contains(c.Id)).ToList());
    }

    private string ResolveOrThrow(string rev, Worktree? wt) =>
        Resolve(rev, wt) ?? throw new FakeGitFailure($"fatal: bad revision '{rev}'");

    private sealed class FakeGitFailure(string message) : Exception(message);

    private GitRun RevList(List<string> a, Worktree? wt)
    {
        var count = a.Remove("--count");
        var reverse = a.Remove("--reverse");
        var firstParent = a.Remove("--first-parent");
        var noMerges = a.Remove("--no-merges");
        var merges = a.Remove("--merges");
        var leftRight = a.Remove("--left-right");
        var parents = a.Remove("--parents");
        // -n N or -nN: the newest N.
        int? limit = null;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] == "-n" && i + 1 < a.Count && int.TryParse(a[i + 1], out var n))
            {
                limit = n;
                a.RemoveRange(i, 2);
                break;
            }
        }
        if (a.Any(x => x.StartsWith('-') && x is not ("--not" or "--remotes")))
        {
            throw new NotSupportedException("rev-list option");
        }
        try
        {
            var (commits, left) = Select(a, wt, firstParent);
            if (noMerges)
            {
                commits = [.. commits.Where(c => c.Parents.Count < 2)];
            }
            if (merges)
            {
                commits = [.. commits.Where(c => c.Parents.Count > 1)];
            }
            if (count && leftRight)
            {
                return Ok($"{left.Count}\t{commits.Count - left.Count}\n");
            }
            if (count)
            {
                return Ok(commits.Count + "\n");
            }
            if (limit is { } most)
            {
                commits = [.. commits.Take(most)];
            }
            if (reverse)
            {
                commits.Reverse();
            }
            return Ok(string.Concat(commits.Select(c => (parents ? string.Join(' ', c.Parents.Prepend(c.Id)) : c.Id) + "\n")));
        }
        catch (FakeGitFailure ex)
        {
            return Fail(ex.Message, 128);
        }
    }

    private GitRun Log(List<string> a, Worktree? wt)
    {
        var reverse = a.Remove("--reverse");
        var oneline = a.Remove("--oneline");
        var format = a.FirstOrDefault(x => x.StartsWith("--format=", StringComparison.Ordinal));
        a.Remove(format ?? "");
        var limit = a.FirstOrDefault(x => x.Length > 1 && x[0] == '-' && x[1..].All(char.IsDigit));
        a.Remove(limit ?? "");
        if (!oneline && format is not ("--format=%h %s" or "--format=%P\t%s") || a.Any(x => x.StartsWith('-') && x != "--not"))
        {
            throw new NotSupportedException("log form");
        }
        try
        {
            var commits = Select(a, wt, firstParent: false).Commits;
            if (reverse)
            {
                commits.Reverse();
            }
            if (limit is not null)
            {
                commits = [.. commits.Take(int.Parse(limit[1..]))];
            }
            return Ok(string.Concat(commits.Select(c => format == "--format=%P\t%s"
                ? $"{string.Join(' ', c.Parents)}\t{FirstLine(c.Message)}\n"
                : $"{Short(c.Id)} {FirstLine(c.Message)}\n")));
        }
        catch (FakeGitFailure ex)
        {
            return Fail(ex.Message, 128);
        }
    }

    private GitRun Show(List<string> a, Worktree? wt)
    {
        var colon = a.Count == 1 ? a[0].IndexOf(':') : -1;
        if (colon <= 0)
        {
            throw new NotSupportedException("show form");
        }
        var rev = a[0][..colon];
        var path = a[0][(colon + 1)..];
        var id = Resolve(rev, wt);
        if (id is null)
        {
            return Fail($"fatal: invalid object name '{rev}'.", 128);
        }
        return _commits[id].Tree.TryGetValue(path, out var content) ? Ok(content) : Fail($"fatal: path '{path}' does not exist in '{rev}'", 128);
    }

    private GitRun CatFile(List<string> a, Worktree? wt)
    {
        if (a is not ["-e", var rev])
        {
            throw new NotSupportedException("cat-file form");
        }
        return Resolve(rev, wt) is null ? Fail($"fatal: Not a valid object name {rev}", 128) : Ok("");
    }

    private GitRun Cherry(List<string> a, Worktree? wt)
    {
        if (a.Count != 2)
        {
            throw new NotSupportedException("cherry form");
        }
        try
        {
            var upstream = ResolveOrThrow(a[0], wt);
            var head = ResolveOrThrow(a[1], wt);
            string Signature(Commit c)
            {
                var parent = c.Parents.Count > 0 ? _commits[c.Parents[0]].Tree : new Dictionary<string, string>();
                return string.Join('\0', Changes(parent, c.Tree).Select(ch => $"{ch.Path}\0{ch.New}"));
            }
            var theirs = Select([$"{head}..{upstream}"], wt, false).Commits.Select(Signature).ToHashSet();
            var mine = Select([$"{upstream}..{head}"], wt, false).Commits;
            mine.Reverse();
            return Ok(string.Concat(mine.Select(c => $"{(theirs.Contains(Signature(c)) ? '-' : '+')} {c.Id}\n")));
        }
        catch (FakeGitFailure ex)
        {
            return Fail(ex.Message, 128);
        }
    }

    /// <summary><c>git cherry-pick &lt;commit&gt;</c>: one commit's change laid on HEAD as a new commit of the same message (a conflict is git's to show: not modelled).</summary>
    private GitRun CherryPick(List<string> a, Worktree? wt)
    {
        if (wt is null || a.Count != 1 || a[0].StartsWith('-'))
        {
            throw new NotSupportedException("cherry-pick form");
        }
        var id = Resolve(a[0], wt);
        if (id is null)
        {
            return Fail($"fatal: bad revision '{a[0]}'", 128);
        }
        var pick = _commits[id];
        var current = HeadId(wt)!;
        var parentTree = pick.Parents.Count > 0 ? _commits[pick.Parents[0]].Tree : new Dictionary<string, string>();
        var (merged, conflicts) = Merge3(parentTree, _commits[current].Tree, pick.Tree, null, false);
        if (conflicts.Count > 0)
        {
            throw new NotSupportedException("cherry-pick that conflicts");
        }
        var previous = wt.Index;
        var commit = MakeCommit([current], merged, pick.Message);
        SetHead(wt, commit.Id);
        wt.Index = new Dictionary<string, string>(merged);
        Materialize(wt, merged, previous);
        return Ok($"[{wt.Branch ?? "detached HEAD"} {Short(commit.Id)}] {FirstLine(pick.Message)}\n");
    }

    // ---- commands: diff ----

    private sealed record Change(string Path, string? Old, string? New);

    private static List<Change> Changes(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
    [
        .. before.Keys.Union(after.Keys).Order(StringComparer.Ordinal)
            .Select(p => new Change(p, before.GetValueOrDefault(p), after.GetValueOrDefault(p)))
            .Where(c => c.Old != c.New),
    ];

    private GitRun Diff(List<string> a, Worktree? wt)
    {
        var nameOnly = a.Remove("--name-only");
        var numstat = a.Remove("--numstat");
        var stat = a.Remove("--stat");
        var quiet = a.Remove("--quiet");
        var unmerged = a.Remove("--diff-filter=U");
        // -b and --ignore-blank-lines: a change of whitespace or blank lines is no change.
        var ignoreSpace = a.Remove("-b") | a.Remove("--ignore-blank-lines");
        a.RemoveAll(x => x is "--no-color" or "--no-ext-diff" or "--src-prefix=a/" or "--dst-prefix=b/" or "--no-renames");
        var dash = a.IndexOf("--");
        var pathspec = dash < 0 ? [] : a.Skip(dash + 1).ToList();
        var revs = dash < 0 ? a : a.Take(dash).ToList();
        if (unmerged)
        {
            return Ok(string.Concat((wt?.Merging?.Paths ?? []).Order(StringComparer.Ordinal).Select(p => p + "\n")));
        }
        if (revs.Any(x => x.StartsWith('-')) || revs.Count is < 1 or > 2)
        {
            throw new NotSupportedException("diff form");
        }
        try
        {
            string from;
            string to;
            if (revs.Count == 2)
            {
                from = ResolveOrThrow(revs[0], wt);
                to = ResolveOrThrow(revs[1], wt);
            }
            else if (revs[0].Contains("...", StringComparison.Ordinal))
            {
                var parts = revs[0].Split("...");
                to = ResolveOrThrow(parts[1], wt);
                from = MergeBase(ResolveOrThrow(parts[0], wt), to) ?? throw new FakeGitFailure("fatal: no merge base");
            }
            else if (revs[0].Contains("..", StringComparison.Ordinal))
            {
                var parts = revs[0].Split("..");
                from = ResolveOrThrow(parts[0], wt);
                to = ResolveOrThrow(parts[1], wt);
            }
            else
            {
                throw new NotSupportedException("diff against the working tree");
            }
            var changes = Changes(_commits[from].Tree, _commits[to].Tree).Where(c => pathspec.Count == 0 || pathspec.Contains(c.Path)).ToList();
            if (ignoreSpace)
            {
                changes = [.. changes.Where(c => c.Old is null || c.New is null || Squash(c.Old) != Squash(c.New))];
            }
            if (quiet)
            {
                return new GitRun(changes.Count == 0 ? 0 : 1, "", "");
            }
            if (nameOnly)
            {
                return Ok(string.Concat(changes.Select(c => c.Path + "\n")));
            }
            if (numstat)
            {
                return Ok(string.Concat(changes.Select(c => { var (add, del) = Count(c); return $"{add}\t{del}\t{c.Path}\n"; })));
            }
            return Ok(stat ? Stat(changes) : Patch(changes));
        }
        catch (FakeGitFailure ex)
        {
            return Fail(ex.Message, 128);
        }
    }

    /// <summary>The text with runs of whitespace as one space, trailing space gone and blank lines dropped.</summary>
    private static string Squash(string text) =>
        string.Join('\n', text.Split('\n').Select(l => string.Join(' ', l.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0));

    private static string[] Lines(string? text) => string.IsNullOrEmpty(text) ? [] : text.TrimEnd('\n').Split('\n');

    private static (int Added, int Removed) Count(Change c)
    {
        var old = Lines(c.Old);
        var now = Lines(c.New);
        if (c.Old is null || c.New is null)
        {
            return (now.Length, old.Length);
        }
        // Longest common subsequence of lines: what git's diff counts as unchanged. A pair too large for the table is a rewrite.
        if ((long)old.Length * now.Length > 4_000_000)
        {
            return (now.Length, old.Length);
        }
        var table = new int[old.Length + 1, now.Length + 1];
        for (var i = old.Length - 1; i >= 0; i--)
        {
            for (var j = now.Length - 1; j >= 0; j--)
            {
                table[i, j] = old[i] == now[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }
        return (now.Length - table[0, 0], old.Length - table[0, 0]);
    }

    private static string Stat(List<Change> changes)
    {
        if (changes.Count == 0)
        {
            return "";
        }
        var rows = changes.Select(c => (c.Path, Counts: Count(c))).ToList();
        var nameWidth = rows.Max(r => r.Path.Length);
        var max = rows.Max(r => r.Counts.Added + r.Counts.Removed);
        var numberWidth = max.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var graphWidth = Math.Max(1, 80 - nameWidth - numberWidth - 6);
        int Scale(int n) => max <= graphWidth || n == 0 ? n : 1 + n * (graphWidth - 1) / max;
        var text = new StringBuilder();
        foreach (var (path, (add, del)) in rows)
        {
            text.Append(' ').Append(path.PadRight(nameWidth)).Append(" | ").Append((add + del).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(numberWidth))
                .Append(' ').Append('+', Scale(add)).Append('-', Scale(del)).Append('\n');
        }
        var added = rows.Sum(r => r.Counts.Added);
        var removed = rows.Sum(r => r.Counts.Removed);
        text.Append($" {rows.Count} file{(rows.Count == 1 ? "" : "s")} changed");
        if (added > 0 || removed == 0)
        {
            text.Append($", {added} insertion{(added == 1 ? "" : "s")}(+)");
        }
        if (removed > 0)
        {
            text.Append($", {removed} deletion{(removed == 1 ? "" : "s")}(-)");
        }
        return text.Append('\n').ToString();
    }

    /// <summary>A unified diff with each changed file as one hunk of its whole text: the node reads it as a fingerprint and for a reviewer's eyes, never applies it.</summary>
    private static string Patch(List<Change> changes)
    {
        var text = new StringBuilder();
        foreach (var c in changes)
        {
            var old = Lines(c.Old);
            var now = Lines(c.New);
            text.Append($"diff --git a/{c.Path} b/{c.Path}\n");
            if (c.Old is null)
            {
                text.Append("new file mode 100644\n");
            }
            else if (c.New is null)
            {
                text.Append("deleted file mode 100644\n");
            }
            text.Append("index 0000000..1111111").Append(c.Old is not null && c.New is not null ? " 100644" : "").Append('\n');
            text.Append(c.Old is null ? "--- /dev/null\n" : $"--- a/{c.Path}\n").Append(c.New is null ? "+++ /dev/null\n" : $"+++ b/{c.Path}\n");
            text.Append($"@@ -{(old.Length == 0 ? 0 : 1)},{old.Length} +{(now.Length == 0 ? 0 : 1)},{now.Length} @@\n");
            foreach (var line in old)
            {
                text.Append('-').Append(line).Append('\n');
            }
            foreach (var line in now)
            {
                text.Append('+').Append(line).Append('\n');
            }
        }
        return text.ToString();
    }

    // ---- commands: the working tree and the index ----

    private GitRun Status(List<string> a, Worktree? wt)
    {
        if (wt is null || a.FirstOrDefault() != "--porcelain")
        {
            throw new NotSupportedException("status form");
        }
        return Ok(string.Concat(StatusLines(wt).Select(l => l + "\n")));
    }

    private bool Dirty(Worktree wt) => StatusLines(wt).Count > 0;

    private List<string> StatusLines(Worktree wt)
    {
        var head = HeadId(wt) is { } id ? _commits[id].Tree : new Dictionary<string, string>();
        var disk = Snapshot(wt.Path);
        var lines = new List<string>();
        foreach (var path in head.Keys.Union(wt.Index.Keys).Union(disk.Keys).Order(StringComparer.Ordinal))
        {
            var inHead = head.TryGetValue(path, out var h);
            var inIndex = wt.Index.TryGetValue(path, out var i);
            var onDisk = disk.TryGetValue(path, out var d);
            if (!inIndex && onDisk && !inHead)
            {
                lines.Add($"?? {path}");
                continue;
            }
            var x = inHead == inIndex && h == i ? ' ' : !inHead ? 'A' : !inIndex ? 'D' : 'M';
            var y = inIndex == onDisk && i == d ? ' ' : !onDisk ? 'D' : 'M';
            if (x != ' ' || y != ' ')
            {
                lines.Add($"{x}{y} {path}");
            }
        }
        if (wt.Merging is { } merging)
        {
            lines.RemoveAll(l => merging.Paths.Any(p => l.EndsWith(" " + p, StringComparison.Ordinal)));
            lines.AddRange(merging.Paths.Select(p => $"UU {p}"));
        }
        return lines;
    }

    private static Dictionary<string, string> Snapshot(string folder)
    {
        var files = new Dictionary<string, string>();
        if (!Directory.Exists(folder))
        {
            return files;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            // What git has no say in: its own folder, the planes nested in a main checkout, and the plane's local Claude settings (ignored).
            if (relative.StartsWith(".git/", StringComparison.Ordinal) || relative == ".git" || relative.StartsWith(".claude/worktrees/", StringComparison.Ordinal)
                || relative == ".claude/settings.local.json")
            {
                continue;
            }
            files[relative] = File.ReadAllText(file);
        }
        return files;
    }

    private GitRun Add(List<string> a, Worktree? wt)
    {
        if (wt is null)
        {
            throw new NotSupportedException("add in the origin");
        }
        a.RemoveAll(x => x is "-A" or "--all");
        var disk = Snapshot(wt.Path);
        if (a.Count == 0 || a.Contains("."))
        {
            wt.Index = disk;
            return Ok("");
        }
        foreach (var path in a)
        {
            if (disk.TryGetValue(path, out var content))
            {
                wt.Index[path] = content;
            }
            else if (wt.Index.ContainsKey(path))
            {
                wt.Index.Remove(path);
            }
            else
            {
                return Fail($"fatal: pathspec '{path}' did not match any files", 128);
            }
        }
        return Ok("");
    }

    private GitRun CommitCommand(List<string> a, Worktree? wt)
    {
        if (wt is null)
        {
            throw new NotSupportedException("commit in the origin");
        }
        string? message = null;
        var all = false;
        var allowEmpty = false;
        var amend = false;
        var quiet = false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            if (x == "-m")
            {
                message = a[++i];
            }
            else if (x == "--allow-empty")
            {
                allowEmpty = true;
            }
            else if (x == "--amend")
            {
                amend = true;
            }
            else if (x.StartsWith("--date=", StringComparison.Ordinal))
            {
                // The commit's date: nothing here is ordered by it.
            }
            else if (x.StartsWith('-') && !x.StartsWith("--", StringComparison.Ordinal) && x[1..].All(c => c is 'q' or 'a' or 'm'))
            {
                all |= x.Contains('a');
                quiet |= x.Contains('q');
                if (x.Contains('m'))
                {
                    message = a[++i];
                }
            }
            else if (x is "--quiet" or "--no-edit")
            {
                quiet |= x == "--quiet";
            }
            else
            {
                throw new NotSupportedException("commit option " + x);
            }
        }
        var amended = amend ? HeadId(wt) : null;
        // No -m: a merge that stopped is finished under the name it was given (--no-edit); an amend keeps the message.
        message ??= amend && amended is not null ? _commits[amended].Message : wt.Merging is not null ? wt.MergeMessage : null;
        if (message is null)
        {
            throw new NotSupportedException("commit without -m");
        }
        if (all)
        {
            var disk = Snapshot(wt.Path);
            foreach (var path in wt.Index.Keys.ToList())
            {
                if (disk.TryGetValue(path, out var content))
                {
                    wt.Index[path] = content;
                }
                else
                {
                    wt.Index.Remove(path);
                }
            }
        }
        var parent = HeadId(wt);
        var parents = new List<string>();
        if (amended is not null)
        {
            // An amend replaces the head: the new commit has the old one's parents.
            parents.AddRange(_commits[amended].Parents);
            parent = amended;
        }
        else if (parent is not null)
        {
            parents.Add(parent);
        }
        if (wt.Merging is { } merging)
        {
            parents.Add(merging.Target);
        }
        var same = parent is not null && _commits[parent].Tree.Count == wt.Index.Count && _commits[parent].Tree.All(t => wt.Index.TryGetValue(t.Key, out var v) && v == t.Value);
        if (same && !allowEmpty && !amend && wt.Merging is null)
        {
            return new GitRun(1, $"On branch {wt.Branch}\nnothing to commit, working tree clean\n", "");
        }
        var commit = MakeCommit(parents, wt.Index, message);
        SetHead(wt, commit.Id);
        wt.Merging = null;
        return Ok(quiet ? "" : $"[{wt.Branch ?? "detached HEAD"} {Short(commit.Id)}] {FirstLine(message)}\n");
    }

    private void SetHead(Worktree wt, string id)
    {
        if (wt.Branch is { } b)
        {
            _refs["refs/heads/" + b] = id;
        }
        else
        {
            wt.Detached = id;
        }
    }

    private GitRun Reset(List<string> a, Worktree? wt)
    {
        a.RemoveAll(x => x is "--quiet" or "-q");
        if (wt is null || a is not ["--hard", var rev])
        {
            throw new NotSupportedException("reset form");
        }
        var id = Resolve(rev, wt);
        if (id is null)
        {
            return Fail($"fatal: ambiguous argument '{rev}': unknown revision or path not in the working tree.", 128);
        }
        var before = wt.Index;
        SetHead(wt, id);
        wt.Index = new Dictionary<string, string>(_commits[id].Tree);
        wt.Merging = null;
        Materialize(wt, _commits[id].Tree, before);
        return Ok("");
    }

    /// <summary>
    /// A three-way merge path by path, as git decides a file: the side that changed it wins, both changing it differently is a conflict
    /// (the conflicted path keeps our text), unless <paramref name="favour"/> says whose text a conflict takes (<c>-X ours|theirs</c>) or
    /// <paramref name="strategyOurs"/> keeps ours whole (<c>-s ours</c>).
    /// </summary>
    private static (Dictionary<string, string> Merged, List<string> Conflicts) Merge3(
        IReadOnlyDictionary<string, string> baseTree, IReadOnlyDictionary<string, string> mine, IReadOnlyDictionary<string, string> theirs, string? favour, bool strategyOurs)
    {
        var merged = new Dictionary<string, string>();
        var conflicts = new List<string>();
        foreach (var path in baseTree.Keys.Union(mine.Keys).Union(theirs.Keys).Order(StringComparer.Ordinal))
        {
            var b = baseTree.GetValueOrDefault(path);
            var o = mine.GetValueOrDefault(path);
            var t = theirs.GetValueOrDefault(path);
            string? result;
            if (strategyOurs || o == t || t == b)
            {
                result = o;
            }
            else if (o == b)
            {
                result = t;
            }
            else if (favour == "theirs")
            {
                result = t;
            }
            else if (favour == "ours")
            {
                result = o;
            }
            else
            {
                result = o;
                conflicts.Add(path);
            }
            if (result is not null)
            {
                merged[path] = result;
            }
        }
        return (merged, conflicts);
    }

    /// <summary>The conflicted files as git leaves them in the folder: both texts between markers.</summary>
    private static void WriteConflicts(Worktree wt, List<string> conflicts, IReadOnlyDictionary<string, string> mine, IReadOnlyDictionary<string, string> theirs, string theirName)
    {
        foreach (var path in conflicts)
        {
            var file = System.IO.Path.Combine(wt.Path, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"<<<<<<< HEAD\n{mine.GetValueOrDefault(path)}=======\n{theirs.GetValueOrDefault(path)}>>>>>>> {theirName}\n");
        }
    }

    private GitRun Merge(List<string> a, Worktree? wt)
    {
        if (wt is null)
        {
            throw new NotSupportedException("merge in the origin");
        }
        if (a is ["--abort"])
        {
            if (wt.Merging is null)
            {
                return Fail("fatal: There is no merge to abort (MERGE_HEAD missing).", 128);
            }
            var head = _commits[HeadId(wt)!].Tree;
            var before = wt.Index;
            wt.Index = new Dictionary<string, string>(head);
            wt.Merging = null;
            Materialize(wt, head, before);
            return Ok("");
        }
        var noFastForward = a.Remove("--no-ff");
        var noCommit = a.Remove("--no-commit");
        var strategyOurs = false;
        string? favour = null;
        string? message = null;
        a.RemoveAll(x => x is "--no-edit" or "--quiet" or "-q");
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] == "-m")
            {
                message = a[i + 1];
                a.RemoveRange(i, 2);
                i--;
            }
            else if (a[i] == "-s" && a.ElementAtOrDefault(i + 1) == "ours")
            {
                strategyOurs = true;
                a.RemoveRange(i, 2);
                i--;
            }
            else if (a[i] == "-X" && a.ElementAtOrDefault(i + 1) is "ours" or "theirs")
            {
                favour = a[i + 1];
                a.RemoveRange(i, 2);
                i--;
            }
        }
        if (a.Count != 1 || a[0].StartsWith('-') || wt.Merging is not null)
        {
            throw new NotSupportedException("merge form");
        }
        var target = Resolve(a[0], wt);
        if (target is null)
        {
            return Fail($"merge: {a[0]} - not something we can merge", 1);
        }
        var current = HeadId(wt)!;
        if (IsAncestor(target, current))
        {
            return Ok("Already up to date.\n");
        }
        var previous = wt.Index;
        if (IsAncestor(current, target) && !noFastForward && !strategyOurs)
        {
            SetHead(wt, target);
            wt.Index = new Dictionary<string, string>(_commits[target].Tree);
            Materialize(wt, _commits[target].Tree, previous);
            return Ok($"Updating {Short(current)}..{Short(target)}\nFast-forward\n");
        }
        var baseId = MergeBase(current, target);
        if (baseId is null)
        {
            return Fail("fatal: refusing to merge unrelated histories", 128);
        }
        var mine = _commits[current].Tree;
        var theirs = _commits[target].Tree;
        var (merged, conflicts) = Merge3(_commits[baseId].Tree, mine, theirs, favour, strategyOurs);
        var text = message ?? $"Merge {a[0]} into {wt.Branch ?? "HEAD"}";
        if (conflicts.Count > 0 || noCommit)
        {
            wt.Merging = (target, conflicts);
            wt.MergeMessage = text;
            wt.Index = new Dictionary<string, string>(merged);
            Materialize(wt, merged, previous);
            WriteConflicts(wt, conflicts, mine, theirs, a[0]);
            return conflicts.Count == 0
                ? Ok("Automatic merge went well; stopped before committing as requested\n")
                : new GitRun(1, string.Concat(conflicts.Select(p => $"CONFLICT (content): Merge conflict in {p}\n")) + "Automatic merge failed; fix conflicts and then commit the result.\n", "");
        }
        var commit = MakeCommit([current, target], merged, text);
        SetHead(wt, commit.Id);
        wt.Index = new Dictionary<string, string>(merged);
        Materialize(wt, merged, previous);
        return Ok("Merge made by the 'ort' strategy.\n");
    }

    /// <summary>
    /// <c>git rebase &lt;upstream&gt;</c>: the branch's own commits replayed one by one onto the upstream (merge commits dropped, a commit
    /// whose change is already there dropped), stopping on the first conflict; <c>--abort</c> goes back to where it began.
    /// </summary>
    private GitRun Rebase(List<string> a, Worktree? wt)
    {
        if (wt is null)
        {
            throw new NotSupportedException("rebase in the origin");
        }
        if (a is ["--abort"])
        {
            if (wt.Rebasing is not { } began)
            {
                return Fail("fatal: No rebase in progress?", 128);
            }
            var before = wt.Index;
            wt.Rebasing = null;
            wt.Index = new Dictionary<string, string>(_commits[began].Tree);
            Materialize(wt, _commits[began].Tree, before);
            return Ok("");
        }
        if (a.Count != 1 || a[0].StartsWith('-') || wt.Rebasing is not null)
        {
            throw new NotSupportedException("rebase form");
        }
        var upstream = Resolve(a[0], wt);
        if (upstream is null)
        {
            return Fail($"fatal: invalid upstream '{a[0]}'", 128);
        }
        var head = HeadId(wt)!;
        if (IsAncestor(upstream, head))
        {
            return Ok($"Current branch {wt.Branch ?? "HEAD"} is up to date.\n");
        }
        var previous = wt.Index;
        if (IsAncestor(head, upstream))
        {
            SetHead(wt, upstream);
            wt.Index = new Dictionary<string, string>(_commits[upstream].Tree);
            Materialize(wt, _commits[upstream].Tree, previous);
            return Ok("Successfully rebased and updated.\n");
        }
        var picks = Select([$"{upstream}..{head}"], wt, firstParent: false).Commits.Where(c => c.Parents.Count < 2).Reverse().ToList();
        var tip = upstream;
        IReadOnlyDictionary<string, string> tipTree = _commits[upstream].Tree;
        foreach (var pick in picks)
        {
            var parentTree = pick.Parents.Count > 0 ? _commits[pick.Parents[0]].Tree : new Dictionary<string, string>();
            var (merged, conflicts) = Merge3(parentTree, tipTree, pick.Tree, null, false);
            if (conflicts.Count > 0)
            {
                wt.Rebasing = head;
                wt.Index = new Dictionary<string, string>(merged);
                Materialize(wt, merged, previous);
                WriteConflicts(wt, conflicts, tipTree, pick.Tree, Short(pick.Id));
                return new GitRun(1, string.Concat(conflicts.Select(p => $"CONFLICT (content): Merge conflict in {p}\n")), $"error: could not apply {Short(pick.Id)}... {FirstLine(pick.Message)}\n");
            }
            if (merged.Count == tipTree.Count && merged.All(m => tipTree.TryGetValue(m.Key, out var v) && v == m.Value))
            {
                continue;
            }
            tip = MakeCommit([tip], merged, pick.Message).Id;
            tipTree = merged;
        }
        SetHead(wt, tip);
        wt.Index = new Dictionary<string, string>(tipTree);
        Materialize(wt, tipTree, previous);
        return Ok("Successfully rebased and updated.\n");
    }

    // ---- the folder ----

    /// <summary>Writes <paramref name="tree"/> into the worktree's folder and removes the files of <paramref name="previous"/> it no longer has.</summary>
    private static void Materialize(Worktree wt, IReadOnlyDictionary<string, string> tree, IReadOnlyDictionary<string, string> previous)
    {
        foreach (var path in previous.Keys.Where(p => !tree.ContainsKey(p)))
        {
            var file = System.IO.Path.Combine(wt.Path, path);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        foreach (var (path, content) in tree)
        {
            var file = System.IO.Path.Combine(wt.Path, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            if (!File.Exists(file) || File.ReadAllText(file) != content)
            {
                File.WriteAllText(file, content);
            }
        }
    }

    // ---- helpers ----

    private static string FirstLine(string message) => message.Split('\n')[0];

    private static GitRun Ok(string output) => new(0, output, "");

    private static GitRun Fail(string error, int code = 1) => new(code, "", error + "\n");

    private static string Norm(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)).Replace('\\', '/');

    private static bool Under(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
