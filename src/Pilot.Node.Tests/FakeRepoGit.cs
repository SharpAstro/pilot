using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pilot.Node.Flights;

namespace Pilot.Node.Tests;

/// <summary>
/// An in-memory git for the tests that check what pilot decides, not what git does (#813). A git run costs 100 to 400 ms on
/// Windows (process start, one at a time under .NET's process-creation lock; not the scanner, which the hosted image has off), and a flight's lifecycle in the node makes about
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

    /// <summary>A bare repo the model serves as a remote: its branches (and the pull heads GitHub keeps), and the commits it holds.</summary>
    private sealed class Bare(string path, string head)
    {
        public string Path { get; } = path;

        /// <summary>The branch the bare repo's HEAD names, which a clone checks out.</summary>
        public string Head { get; } = head;

        public Dictionary<string, string> Refs { get; } = [];

        public HashSet<string> Present { get; } = [];
    }

    /// <summary>One non-bare repo of the model: a clone or a work repo, with its refs, worktrees, config and the commits its object store holds.</summary>
    private sealed class Repo(string raw)
    {
        public string Raw { get; } = raw;

        public string Path { get; } = Norm(raw);

        public Dictionary<string, string> Refs { get; } = [];

        public List<Worktree> Worktrees { get; } = [];

        public Dictionary<string, string> Cfg { get; } = [];

        /// <summary>The URL of the remote <c>origin</c>; null when the repo has none.</summary>
        public string? RemoteUrl { get; set; }

        public bool OriginHead { get; set; }

        /// <summary>The commit the last fetch of a named ref brought, which <c>FETCH_HEAD</c> names.</summary>
        public string? FetchHead { get; set; }

        /// <summary>The commits in the repo's object store: made here, fetched, or reachable from what was. Git never forgets one, so neither does this.</summary>
        public HashSet<string> Present { get; } = [];
    }

    private static readonly ConcurrentDictionary<string, FakeRepoGit> Roots = new(StringComparer.OrdinalIgnoreCase);

    private static int _seq;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Commit> _commits = [];
    private readonly List<Repo> _repos = [];
    private readonly Dictionary<string, Bare> _bares = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly List<string> _unsupported = [];

    // What the git run being answered addresses: the repo of its folder (an empty one in a bare repo or none), and the bare repo it reads
    // as origin (the one its folder is in, or its remote reaches). Set under the gate by each Run.
    private Repo _repo = null!;
    private Bare? _bare;

    private Dictionary<string, string> Refs => _repo.Refs;

    private List<Worktree> Worktrees => _repo.Worktrees;

    private Dictionary<string, string> Cfg => _repo.Cfg;

    private string? RemoteUrl
    {
        get => _repo.RemoteUrl;
        set => _repo.RemoteUrl = value;
    }

    private bool OriginHead
    {
        get => _repo.OriginHead;
        set => _repo.OriginHead = value;
    }

    /// <summary>The refs of the bare origin addressed; none when the remote cannot be reached.</summary>
    private Dictionary<string, string> OriginRefs => _bare?.Refs ?? [];

    /// <summary>
    /// A model of the repo the review tests build: <c>tianwen</c> under <paramref name="root"/>, with one commit on main. With <paramref name="withOrigin"/> it has a bare origin beside it
    /// holding that commit, as <see cref="SeedRepo"/> makes them; without, it is a <c>git init</c> repo with no remote, as the leg and overlap tests build.
    /// <paramref name="withSolution"/> adds the stub solution a check that builds or tests needs in the plane's root (leg plan refuses a repo without one).
    /// </summary>
    public FakeRepoGit(string root, bool withOrigin = true, bool withSolution = true)
        : this(root)
    {
        var repoPath = Path.Combine(root, "tianwen");
        var originPath = Path.Combine(root, "origin.git");
        var repo = AddRepo(repoPath, "main");
        _repo = repo;
        repo.RemoteUrl = withOrigin ? originPath : null;
        WriteConfigFile(repo);
        var tree = new Dictionary<string, string> { ["README.md"] = "hello\n" };
        if (withSolution)
        {
            tree["Tianwen.slnx"] = "<Solution />\n";
        }
        var first = MakeCommit([], tree, "first");
        repo.Refs["refs/heads/main"] = first.Id;
        if (withOrigin)
        {
            var bare = AddBare(originPath, "main");
            bare.Refs["refs/heads/main"] = first.Id;
            Take(bare.Present, first.Id);
            repo.Refs["refs/remotes/origin/main"] = first.Id;
            repo.OriginHead = true;
        }
        repo.Worktrees[0].Index = new Dictionary<string, string>(tree);
        Materialize(repo.Worktrees[0], tree, new Dictionary<string, string>());
    }

    private FakeRepoGit(string root)
    {
        _root = Norm(root);
        _repo = new Repo(root);
        Roots[_root] = this;
    }

    /// <summary>
    /// A model with nothing in it yet, under <paramref name="root"/>: its repos and bare origins are made by the git commands themselves
    /// (<c>init</c>, <c>clone</c>, <c>remote add</c>, <c>push</c>), as <see cref="FakePrOrigins"/> does for the PR tests' shape. Unlike the default model it has no tianwen.
    /// </summary>
    public static FakeRepoGit Empty(string root)
    {
        Directory.CreateDirectory(root);
        return new FakeRepoGit(root);
    }

    private Repo AddRepo(string path, string branch)
    {
        var repo = new Repo(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        repo.Worktrees.Add(new Worktree(repo.Path, branch, null, []));
        _repos.Add(repo);
        return repo;
    }

    private Bare AddBare(string path, string head)
    {
        Directory.CreateDirectory(path);
        return _bares[Norm(path)] = new Bare(Norm(path), head);
    }

    /// <summary>The first repo made: tianwen in the default model.</summary>
    public string RepoPath => _repos[0].Raw;

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

    private Func<string, IReadOnlyList<string>?>? _preReceive;

    /// <summary>
    /// The origin's pre-receive hook, which real git runs from <c>origin.git/hooks/pre-receive</c>: given the ref a push would move, the lines the
    /// hook prints on stderr when it refuses the push (exit 1), or null to let it through. Null means no hook. It runs only for a push that
    /// would move a ref, after the non-fast-forward check, as git's does.
    /// </summary>
    public Func<string, IReadOnlyList<string>?>? PreReceive
    {
        get
        {
            lock (_gate)
            {
                return _preReceive;
            }
        }
        set
        {
            lock (_gate)
            {
                _preReceive = value;
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
        foreach (var folder in _repos.Select(r => r.Path).Concat(_bares.Keys))
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            // The parents a repo is made in (repos, github) are the model's too: drop each that is now empty, up to the root.
            for (var parent = Path.GetDirectoryName(folder); parent is not null && Norm(parent).Length > _root.Length && Under(Norm(parent), _root); parent = Path.GetDirectoryName(parent))
            {
                if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    break;
                }
                Directory.Delete(parent);
            }
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
        // The repo whose worktree the folder is in; failing that, the bare repo whose folder it is in (the answers of a bare repo are its refs').
        var wt = WorktreeOf(cwd);
        var inBare = wt is null ? _bares.Values.FirstOrDefault(b => Under(Norm(cwd), b.Path)) : null;
        _repo = wt is null ? new Repo(_root) : _repos.First(r => r.Worktrees.Contains(wt));
        _bare = inBare ?? RemoteBare(_repo);
        // Repos and bare origins are made, and cloned, under the model's root.
        if (wt is null && inBare is null && verb is "init" or "clone" && Under(Norm(cwd), _root))
        {
            return verb == "init" ? Init(a, cwd) : Clone(a, cwd);
        }
        if (wt is null && inBare is null)
        {
            return Fail($"fatal: not a git repository (or any of the parent directories): .git", 128);
        }
        if (a.Any(x => x is "origin" or "refs/remotes/origin/HEAD") && verb is "remote" or "fetch" or "push" or "ls-remote" or "symbolic-ref")
        {
            // A repo made by git init has no remote; one whose remote is a URL no model serves (GitHub's) cannot be reached: each fails as git says.
            if (RemoteUrl is null && !(verb == "remote" && a is ["add", ..]))
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
            if (_bare is null && verb is "fetch" or "push" or "ls-remote")
            {
                return Fail($"fatal: unable to access '{FetchUrl(_repo)}': Could not resolve host: github.com", 128);
            }
        }
        return verb switch
        {
            "rev-parse" => RevParse(a, wt),
            "show-ref" => ShowRef(a, wt),
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
            "ls-files" => LsFiles(a, wt),
            "add" => Add(a, wt),
            "commit" => CommitCommand(a, wt),
            "merge" => Merge(a, wt),
            "merge-tree" => MergeTree(a, wt),
            "reset" => Reset(a, wt),
            "cherry" => Cherry(a, wt),
            "cherry-pick" => CherryPick(a, wt),
            "branch" => Branch(a, wt),
            "rebase" => Rebase(a, wt),
            "config" => Config(a),
            "ls-remote" => LsRemote(a),
            "switch" => Switch(a, wt),
            "update-ref" => UpdateRef(a, wt),
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
        Take(_repo.Present, id);
        return commit;
    }

    /// <summary>Puts <paramref name="id"/> and every commit it descends from into an object store, as a fetch or a push sends them.</summary>
    private void Take(HashSet<string> store, string id)
    {
        var stack = new Stack<string>([id]);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!store.Add(next))
            {
                continue;
            }
            foreach (var parent in _commits[next].Parents)
            {
                stack.Push(parent);
            }
        }
    }

    private Worktree? WorktreeOf(string cwd)
    {
        var path = Norm(cwd);
        return _repos.SelectMany(r => r.Worktrees).Where(w => Under(path, w.Path)).OrderByDescending(w => w.Path.Length).FirstOrDefault();
    }

    private string? HeadId(Worktree wt) => wt.Detached ?? (wt.Branch is { } b && Refs.TryGetValue("refs/heads/" + b, out var id) ? id : null);

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
            return wt is null ? Origin("refs/heads/" + (_bare?.Head ?? "main")) : HeadId(wt);
        }
        if (name == "FETCH_HEAD" && wt is not null)
        {
            return _repo.FetchHead;
        }
        if (name.StartsWith("refs/", StringComparison.Ordinal))
        {
            return wt is null ? Origin(name) : Refs.GetValueOrDefault(name);
        }
        if (name.Contains('@'))
        {
            throw new NotSupportedException("@ revision");
        }
        if (wt is null && Origin("refs/heads/" + name) is { } pushed)
        {
            return pushed;
        }
        if (wt is not null && Refs.TryGetValue("refs/heads/" + name, out var local))
        {
            return local;
        }
        if (wt is not null && Refs.TryGetValue("refs/remotes/" + name, out var remote))
        {
            return remote;
        }
        // A commit is named by its id only where the repo's object store has it: one of a PR's head is not in a clone until it is fetched.
        var present = wt is null ? _bare?.Present : _repo.Present;
        if (present is not null && present.Contains(name))
        {
            return name;
        }
        if (present is not null && name.Length >= 4 && name.All(Uri.IsHexDigit))
        {
            var found = present.Where(k => k.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return found.Count == 1 ? found[0] : null;
        }
        return null;
    }

    private string? Origin(string name) => OriginRefs.GetValueOrDefault(name);

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
            return Ok(Norm(Path.Combine(_repo.Path, ".git")) + "\n");
        }
        if (a is ["--abbrev-ref", "HEAD"] && wt is not null)
        {
            return Ok((wt.Branch ?? "HEAD") + "\n");
        }
        if (a is ["--git-common-dir"])
        {
            // Relative to where it is asked: ".git" in the main checkout, absolute in a linked worktree.
            return Ok((wt == Worktrees[0] ? ".git" : Norm(Path.Combine(_repo.Path, ".git"))) + "\n");
        }
        var quiet = a.Remove("--quiet");
        var verify = a.Remove("--verify");
        if (a.Count != 1 || a[0].StartsWith('-'))
        {
            throw new NotSupportedException("rev-parse form");
        }
        // <rev>^{tree}: the commit's tree, by the id merge-tree gives the same contents.
        var treeOf = a[0].EndsWith("^{tree}", StringComparison.Ordinal);
        var id = Resolve(treeOf ? a[0][..^"^{tree}".Length] : a[0], wt);
        if (id is null)
        {
            return verify && quiet ? new GitRun(1, "", "") : Fail($"fatal: ambiguous argument '{a[0]}': unknown revision or path not in the working tree.", 128);
        }
        return Ok((treeOf ? TreeId(_commits[id].Tree) : id) + "\n");
    }

    /// <summary><c>git show-ref --verify [--quiet] &lt;ref&gt;</c>: the ref's commit and name, or git's refusal of a ref that is not there.</summary>
    private GitRun ShowRef(List<string> a, Worktree? wt)
    {
        var quiet = a.Remove("--quiet") | a.Remove("-q");
        if (!a.Remove("--verify") || a.Count != 1 || a[0].StartsWith('-'))
        {
            throw new NotSupportedException("show-ref form");
        }
        var found = (wt is null ? OriginRefs : Refs).TryGetValue(a[0], out var id);
        return found ? Ok(quiet ? "" : $"{id} {a[0]}\n") : quiet ? new GitRun(1, "", "") : Fail($"fatal: '{a[0]}' - not a valid ref", 128);
    }

    private GitRun SymbolicRef(List<string> a, Worktree? wt)
    {
        a.Remove("--short");
        if (a is ["refs/remotes/origin/HEAD"])
        {
            return OriginHead ? Ok($"origin/{_bare?.Head ?? "main"}\n") : Fail("fatal: ref refs/remotes/origin/HEAD is not a symbolic ref", 128);
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
                if (RemoteUrl is not null)
                {
                    return Fail("error: remote origin already exists.", 3);
                }
                RemoteUrl = url;
                Cfg["remote.origin.url"] = url;
                WriteConfigFile(_repo);
                return Ok("");
            case ["set-url", "origin", var url] when !url.StartsWith('-'):
                // A GitHub URL names the repo as a peer's checkout does; it reaches the bare origin only through a url.<origin>.insteadOf rewrite.
                RemoteUrl = url;
                Cfg["remote.origin.url"] = url;
                WriteConfigFile(_repo);
                return Ok("");
            case ["get-url", "origin"]:
                return Ok(FetchUrl(_repo) + "\n");
            case ["set-head", "origin", "--auto"]:
                OriginHead = _bare is not null;
                return Ok("");
            case ["set-head", "origin", "--delete"]:
                OriginHead = false;
                return Ok("");
            case ["set-head", "origin", var branch] when !branch.StartsWith('-'):
                OriginHead = Refs.ContainsKey("refs/remotes/origin/" + branch);
                return OriginHead ? Ok("") : Fail($"error: Not a valid ref: refs/remotes/origin/{branch}", 1);
            default:
                throw new NotSupportedException("remote form");
        }
    }

    /// <summary>
    /// The URL git fetches origin from: the remote's, with the <c>url.&lt;base&gt;.insteadOf</c> rewrite of its longest matching prefix applied
    /// (as <c>remote get-url</c> prints it).
    /// </summary>
    private static string? FetchUrl(Repo repo)
    {
        if (repo.RemoteUrl is not { } url)
        {
            return null;
        }
        string? prefix = null;
        string? replacement = null;
        foreach (var (key, value) in repo.Cfg)
        {
            if (key.StartsWith("url.", StringComparison.Ordinal) && key.EndsWith(".insteadOf", StringComparison.Ordinal) && value.Length > 0
                && url.StartsWith(value, StringComparison.Ordinal) && (prefix is null || value.Length > prefix.Length))
            {
                prefix = value;
                replacement = key["url.".Length..^".insteadOf".Length];
            }
        }
        return prefix is null ? url : replacement + url[prefix.Length..];
    }

    /// <summary>
    /// The bare repo a fetch or push of <paramref name="repo"/> reaches: the one the remote's URL (rewritten) is the folder of. A GitHub URL is offline,
    /// reaching nothing unless a rewrite sends it to a bare repo of the model.
    /// </summary>
    private Bare? RemoteBare(Repo repo)
    {
        if (FetchUrl(repo) is not { } url || url.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        // A relative remote (../origin.git) is relative to the repo, as git reads it, not to the test process's folder.
        return _bares.GetValueOrDefault(Norm(Path.IsPathRooted(url) ? url : Path.Combine(repo.Path, url)));
    }

    /// <summary>
    /// <c>git init [--bare] [-b main] [&lt;folder&gt;]</c>: a repo or a bare origin made under the model's root, with no commit. With no <c>-b</c> its branch is main, as the tests' git is set up (init.defaultBranch), not git's own master.
    /// </summary>
    private GitRun Init(List<string> a, string cwd)
    {
        a.RemoveAll(x => x is "-q" or "--quiet");
        var bare = a.Remove("--bare");
        string? branch = null;
        var at = a.IndexOf("-b");
        if (at >= 0 && at + 1 < a.Count)
        {
            branch = a[at + 1];
            a.RemoveRange(at, 2);
        }
        if (a.Count > 1 || a.Any(x => x.StartsWith('-')))
        {
            throw new NotSupportedException("init form");
        }
        var target = Path.GetFullPath(a.Count == 1 ? Path.Combine(cwd, a[0]) : cwd);
        if (!Under(Norm(target), _root))
        {
            throw new NotSupportedException("init outside the model's folders");
        }
        if (bare)
        {
            AddBare(target, branch ?? "main");
        }
        else
        {
            var made = AddRepo(target, branch ?? "main");
            WriteConfigFile(made);
        }
        return Ok("");
    }

    /// <summary>
    /// <c>git clone --bare &lt;a repo&gt; &lt;folder&gt;</c>: a bare origin holding the repo's branches; or <c>git clone &lt;a bare origin&gt; &lt;folder&gt;</c>: a
    /// repo with the origin's branches as remote-tracking ones and its default branch checked out (what the origin holds under refs/pull is not copied, as git's is not).
    /// </summary>
    private GitRun Clone(List<string> a, string cwd)
    {
        a.RemoveAll(x => x is "-q" or "--quiet");
        var bare = a.Remove("--bare");
        if (a.Count != 2 || a.Any(x => x.StartsWith('-')))
        {
            throw new NotSupportedException("clone form");
        }
        var source = Norm(Path.IsPathRooted(a[0]) ? a[0] : Path.Combine(cwd, a[0]));
        var target = Path.GetFullPath(Path.Combine(cwd, a[1]));
        if (!Under(Norm(target), _root))
        {
            throw new NotSupportedException("clone outside the model's folders");
        }
        if (bare)
        {
            var from = _repos.FirstOrDefault(r => r.Path.Equals(source, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException("clone --bare of anything but a repo of the model");
            var made = AddBare(target, from.Worktrees[0].Branch ?? "main");
            foreach (var (name, id) in from.Refs.Where(r => r.Key.StartsWith("refs/heads/", StringComparison.Ordinal)).ToList())
            {
                made.Refs[name] = id;
                Take(made.Present, id);
            }
            return Ok("");
        }
        var origin = _bares.GetValueOrDefault(source) ?? throw new NotSupportedException("clone of anything but a bare origin of the model");
        if (!origin.Refs.TryGetValue("refs/heads/" + origin.Head, out var tip))
        {
            throw new NotSupportedException("clone of an origin with no default branch");
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return Fail($"fatal: destination path '{a[1]}' already exists and is not an empty directory.", 128);
        }
        var repo = AddRepo(target, origin.Head);
        repo.RemoteUrl = source;
        repo.OriginHead = true;
        repo.Refs["refs/heads/" + origin.Head] = tip;
        repo.Cfg[$"branch.{origin.Head}.remote"] = "origin";
        repo.Cfg[$"branch.{origin.Head}.merge"] = "refs/heads/" + origin.Head;
        foreach (var (name, id) in origin.Refs.Where(r => r.Key.StartsWith("refs/heads/", StringComparison.Ordinal)))
        {
            repo.Refs["refs/remotes/origin/" + name["refs/heads/".Length..]] = id;
            Take(repo.Present, id);
        }
        var tree = _commits[tip].Tree;
        repo.Worktrees[0].Index = new Dictionary<string, string>(tree);
        Materialize(repo.Worktrees[0], tree, new Dictionary<string, string>());
        WriteConfigFile(repo);
        return Ok("");
    }

    /// <summary><c>git update-ref &lt;ref&gt; &lt;commit&gt;</c>: sets a ref of the repo the folder is in (of the origin when it is a bare one's folder), as a fake gh does for refs/pull/&lt;n&gt;/head.</summary>
    private GitRun UpdateRef(List<string> a, Worktree? wt)
    {
        if (a is not [var name, var target] || name.StartsWith('-') || target.StartsWith('-'))
        {
            throw new NotSupportedException("update-ref form");
        }
        var refs = wt is null ? _bare?.Refs : Refs;
        if (refs is null || Resolve(target, wt) is not { } id)
        {
            return Fail($"fatal: {target}: not a valid SHA1", 128);
        }
        refs[name] = id;
        return Ok("");
    }

    /// <summary><c>git branch --list &lt;pattern&gt;</c>: the local branches that match, one per line, the checked out one starred.</summary>
    private GitRun Branch(List<string> a, Worktree? wt)
    {
        // A branch made at a start point, tracking it when that is a remote-tracking branch (git branch --track <name> origin/<name>).
        a.Remove("--track");
        // git branch <name>: a branch at HEAD, which stays checked out as it is.
        if (wt is not null && a is [var atHead] && !atHead.StartsWith('-'))
        {
            a = [atHead, "HEAD"];
        }
        if (wt is not null && a is [var made, var start] && !made.StartsWith('-') && !start.StartsWith('-'))
        {
            if (Refs.ContainsKey("refs/heads/" + made))
            {
                return Fail($"fatal: a branch named '{made}' already exists", 128);
            }
            if (Resolve(start, wt) is not { } at)
            {
                return Fail($"fatal: not a valid object name: '{start}'", 128);
            }
            Refs["refs/heads/" + made] = at;
            if (start.StartsWith("origin/", StringComparison.Ordinal))
            {
                Cfg[$"branch.{made}.remote"] = "origin";
                Cfg[$"branch.{made}.merge"] = "refs/heads/" + start["origin/".Length..];
            }
            return Ok("");
        }
        if (wt is null || a is not ["--list", var pattern])
        {
            throw new NotSupportedException("branch form");
        }
        var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");
        return Ok(string.Concat(Refs.Keys.Where(k => k.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(k => k["refs/heads/".Length..])
            .Where(b => regex.IsMatch(b)).Order(StringComparer.Ordinal).Select(b => (b == wt.Branch ? "* " : "  ") + b + "\n")));
    }

    private GitRun Config(List<string> a)
    {
        a.Remove("--local");
        switch (a)
        {
            case [var key, var value] when !key.StartsWith('-'):
                Cfg[key] = value;
                if (key == "remote.origin.url")
                {
                    RemoteUrl = value;
                }
                if (key == "remote.origin.url" || key.StartsWith("url.", StringComparison.Ordinal))
                {
                    WriteConfigFile(_repo);
                }
                return Ok("");
            case ["--get", var key]:
                return Cfg.TryGetValue(key, out var found) ? Ok(found + "\n") : new GitRun(1, "", "");
            default:
                throw new NotSupportedException("config form");
        }
    }

    /// <summary>A repo's <c>.git/config</c> as the repo catalog reads it from the folder, without asking git (it names the clone by its origin's URL).</summary>
    private static void WriteConfigFile(Repo repo)
    {
        var text = new StringBuilder("[core]\n\trepositoryformatversion = 0\n");
        if (repo.RemoteUrl is { } url)
        {
            text.Append($"[remote \"origin\"]\n\turl = {url}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        }
        foreach (var (key, value) in repo.Cfg.Where(c => c.Key.StartsWith("url.", StringComparison.Ordinal) && c.Key.EndsWith(".insteadOf", StringComparison.Ordinal)))
        {
            text.Append($"[url \"{key["url.".Length..^".insteadOf".Length]}\"]\n\tinsteadOf = {value}\n");
        }
        File.WriteAllText(Path.Combine(repo.Path, ".git", "config"), text.ToString());
    }

    /// <summary><c>git ls-remote origin &lt;pattern&gt;</c>: the origin's branches whose name ends with the pattern.</summary>
    private GitRun LsRemote(List<string> a)
    {
        a.RemoveAll(x => x is "--heads" or "-h" or "--quiet" or "-q");
        if (a.Count is < 1 or > 2 || a[0] != "origin" || a.Any(x => x.StartsWith('-')))
        {
            throw new NotSupportedException("ls-remote form");
        }
        var pattern = a.Count == 2 ? a[1] : null;
        return Ok(string.Concat(OriginRefs.Where(o => o.Key.StartsWith("refs/heads/", StringComparison.Ordinal) && (pattern is null || o.Key == pattern || o.Key.EndsWith("/" + pattern, StringComparison.Ordinal)))
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
            if (Refs.ContainsKey("refs/heads/" + a[0]))
            {
                return Fail($"fatal: a branch named '{a[0]}' already exists", 128);
            }
            Refs["refs/heads/" + a[0]] = head ?? throw new NotSupportedException("switch -c on an unborn branch");
            wt.Branch = a[0];
            wt.Detached = null;
            return Ok("");
        }
        if (!Refs.TryGetValue("refs/heads/" + a[0], out var target))
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
            foreach (var (name, id) in OriginRefs.Where(o => o.Key.StartsWith("refs/heads/", StringComparison.Ordinal)))
            {
                Refs["refs/remotes/origin/" + name["refs/heads/".Length..]] = id;
                Take(_repo.Present, id);
            }
            if (prune)
            {
                foreach (var stale in Refs.Keys.Where(k => k.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) && k != "refs/remotes/origin/HEAD"
                    && !OriginRefs.ContainsKey("refs/heads/" + k["refs/remotes/origin/".Length..])).ToList())
                {
                    Refs.Remove(stale);
                }
            }
            return Ok("");
        }
        foreach (var spec in a.Skip(1))
        {
            var parts = spec.TrimStart('+').Split(':');
            if (parts.Length > 2)
            {
                throw new NotSupportedException("fetch refspec");
            }
            // A short name is looked up as git does: as given, under refs/, under refs/tags/, under refs/heads/.
            var from = new[] { parts[0], "refs/" + parts[0], "refs/tags/" + parts[0], "refs/heads/" + parts[0] }.FirstOrDefault(OriginRefs.ContainsKey);
            if (from is null)
            {
                return Fail($"fatal: couldn't find remote ref {parts[0]}", 128);
            }
            var id = OriginRefs[from];
            Take(_repo.Present, id);
            _repo.FetchHead = id;
            if (parts.Length == 2)
            {
                Refs[parts[1]] = id;
            }
            else if (from.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                // A branch fetched by name also moves its remote-tracking ref, as git does when the remote's fetch refspec covers it.
                Refs["refs/remotes/origin/" + from["refs/heads/".Length..]] = id;
            }
        }
        return Ok("");
    }

    private GitRun Push(List<string> a, Worktree? wt)
    {
        var force = a.Any(x => x is "--force" or "-f") || a.Any(x => x.StartsWith('+'));
        string? lease = a.FirstOrDefault(x => x.StartsWith("--force-with-lease=", StringComparison.Ordinal));
        // Anything else starting with a dash (--delete, --tags, --mirror, --all, ...) changes what the push does: the model must not read it as a plain push.
        if (a.FirstOrDefault(x => x.StartsWith('-') && x is not ("--force" or "-f" or "-u" or "--set-upstream" or "-q" or "--quiet") && !x.StartsWith("--force-with-lease=", StringComparison.Ordinal)) is { } unknown)
        {
            throw new NotSupportedException($"push flag {unknown}");
        }
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
        var known = OriginRefs.TryGetValue(dstRef, out var old);
        var moves = !known || old != id;
        if (known && moves)
        {
            var fastForward = IsAncestor(old!, id);
            var leased = lease is not null && lease.Split('=', 2)[1].Split(':') is [_, var expected] && Resolve(expected, wt) == old;
            if (!fastForward && !force && !leased)
            {
                return Fail($" ! [rejected]        {src} -> {dst} (non-fast-forward)\nerror: failed to push some refs to '{_bare!.Path}'", 1);
            }
        }
        // The origin's pre-receive hook sees the update before any ref moves (an up-to-date push sends none); git relays a refusal line by line as "remote: ...".
        if (moves && _preReceive?.Invoke(dstRef) is { } refusal)
        {
            var relayed = string.Concat(refusal.Select(l => "remote: " + l + "\n"));
            return Fail($"{relayed}To {_bare!.Path}\n ! [remote rejected] {src} -> {dst} (pre-receive hook declined)\nerror: failed to push some refs to '{_bare!.Path}'", 1);
        }
        OriginRefs[dstRef] = id;
        Take(_bare!.Present, id);
        if (dstRef.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            Refs["refs/remotes/origin/" + dstRef["refs/heads/".Length..]] = id;
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
                foreach (var w in Worktrees)
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
                Worktrees.RemoveAll(w => w != Worktrees[0] && !Directory.Exists(w.Path));
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
        var wt = Worktrees.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
        if (wt is null || wt == Worktrees[0])
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
        Worktrees.Remove(wt);
        return Ok("");
    }

    private GitRun WorktreeAdd(List<string> a)
    {
        // Git sets the new branch's upstream when its start point is a remote-tracking branch (branch.autoSetupMerge), --track or not; --no-track stops it.
        var noTrack = a.Remove("--no-track");
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
                case "--force" or "-f" or "--quiet" or "-q" or "--track":
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
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() || Worktrees.Any(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail($"fatal: '{positional[0]}' already exists", 128);
        }
        string? branch = null;
        string commit;
        if (newBranch is not null)
        {
            var from = positional.Count == 2 ? positional[1] : "HEAD";
            commit = Resolve(from, Worktrees[0]) ?? "";
            if (commit.Length == 0)
            {
                return Fail($"fatal: invalid reference: {from}", 128);
            }
            if (!reset && Refs.ContainsKey("refs/heads/" + newBranch))
            {
                return Fail($"fatal: a branch named '{newBranch}' already exists", 128);
            }
            branch = newBranch;
        }
        else
        {
            var target = positional.Count == 2 ? positional[1] : "HEAD";
            if (!detach && Refs.ContainsKey("refs/heads/" + target))
            {
                branch = target;
            }
            else if (!detach && positional.Count == 1)
            {
                throw new NotSupportedException("worktree add of a new branch by name");
            }
            commit = Resolve(target, Worktrees[0]) ?? "";
            if (commit.Length == 0)
            {
                return Fail($"fatal: invalid reference: {target}", 128);
            }
        }
        if (branch is not null && Worktrees.FirstOrDefault(w => w.Branch == branch) is { } holder)
        {
            return Fail($"fatal: '{branch}' is already checked out at '{holder.Path}'", 128);
        }
        if (branch is not null)
        {
            Refs["refs/heads/" + branch] = commit;
            if (newBranch is not null && !noTrack && positional.Count == 2 && positional[1].StartsWith("origin/", StringComparison.Ordinal)
                && Refs.ContainsKey("refs/remotes/" + positional[1]))
            {
                Cfg[$"branch.{branch}.remote"] = "origin";
                Cfg[$"branch.{branch}.merge"] = "refs/heads/" + positional[1]["origin/".Length..];
            }
        }
        var tree = _commits[commit].Tree;
        var wt = new Worktree(path, branch, branch is null ? commit : null, new Dictionary<string, string>(tree));
        Directory.CreateDirectory(path);
        Materialize(wt, tree, new Dictionary<string, string>());
        // A linked worktree has a .git file naming its admin folder; the node tells a worktree from a folder around it by that file.
        File.WriteAllText(Path.Combine(path, ".git"), $"gitdir: {Norm(Path.Combine(_repo.Path, ".git", "worktrees", Path.GetFileName(path)))}\n");
        Worktrees.Add(wt);
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
                foreach (var id in Refs.Where(r => r.Key.StartsWith("refs/remotes/", StringComparison.Ordinal)).Select(r => r.Value))
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
        var maxCount = a.FirstOrDefault(x => x.StartsWith("--max-count=", StringComparison.Ordinal) && x.Length > "--max-count=".Length && x["--max-count=".Length..].All(char.IsDigit));
        a.Remove(maxCount ?? "");
        limit ??= maxCount is null ? null : "-" + maxCount["--max-count=".Length..];
        // A pathspec after -- keeps the commits that change one of those files (or folders).
        var dash = a.IndexOf("--");
        var pathspec = dash < 0 ? [] : a.Skip(dash + 1).ToList();
        if (dash >= 0)
        {
            a.RemoveRange(dash, a.Count - dash);
        }
        if (!oneline && format is not ("--format=%h %s" or "--format=%P\t%s" or "--format=%s") || a.Any(x => x.StartsWith('-') && x != "--not"))
        {
            throw new NotSupportedException("log form");
        }
        try
        {
            var commits = Select(a, wt, firstParent: false).Commits;
            if (pathspec.Count > 0)
            {
                // Git's default history simplification: a commit shows when its files under the pathspec differ from every parent's (a root's: when it has any).
                bool Under(string path) => pathspec.Any(p => path == p || path.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal));
                IEnumerable<KeyValuePair<string, string>> Of(IReadOnlyDictionary<string, string> tree) => tree.Where(t => Under(t.Key)).OrderBy(t => t.Key, StringComparer.Ordinal);
                commits = [.. commits.Where(c => c.Parents.Count == 0
                    ? Of(c.Tree).Any()
                    : c.Parents.All(p => !Of(c.Tree).SequenceEqual(Of(_commits[p].Tree))))];
            }
            // Git takes the N newest first and reverses them after (log --reverse -2 shows the second-newest, then the newest).
            if (limit is not null)
            {
                commits = [.. commits.Take(int.Parse(limit[1..]))];
            }
            if (reverse)
            {
                commits.Reverse();
            }
            return Ok(string.Concat(commits.Select(c => format switch
            {
                "--format=%P\t%s" => $"{string.Join(' ', c.Parents)}\t{FirstLine(c.Message)}\n",
                "--format=%s" => $"{FirstLine(c.Message)}\n",
                _ => $"{Short(c.Id)} {FirstLine(c.Message)}\n",
            })));
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
            IReadOnlyDictionary<string, string> toTree;
            if (revs.Count == 2)
            {
                from = ResolveOrThrow(revs[0], wt);
                toTree = _commits[ResolveOrThrow(revs[1], wt)].Tree;
            }
            else if (revs[0].Contains("...", StringComparison.Ordinal))
            {
                var parts = revs[0].Split("...");
                var to = ResolveOrThrow(parts[1], wt);
                from = MergeBase(ResolveOrThrow(parts[0], wt), to) ?? throw new FakeGitFailure("fatal: no merge base");
                toTree = _commits[to].Tree;
            }
            else if (revs[0].Contains("..", StringComparison.Ordinal))
            {
                var parts = revs[0].Split("..");
                from = ResolveOrThrow(parts[0], wt);
                toTree = _commits[ResolveOrThrow(parts[1], wt)].Tree;
            }
            else if (wt is not null && wt.Merging is null)
            {
                // One commit: the working tree against it, so what is committed since, staged and not staged together (PlaneChangesAsync's question).
                from = ResolveOrThrow(revs[0], wt);
                toTree = WorkingTree(wt);
            }
            else
            {
                throw new NotSupportedException("diff against the working tree of the origin, or mid-merge");
            }
            var changes = Changes(_commits[from].Tree, toTree).Where(c => pathspec.Count == 0 || pathspec.Contains(c.Path)).ToList();
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

    /// <summary>
    /// The text as <c>git diff -b --ignore-blank-lines</c> compares it: runs of whitespace inside a line as one space, trailing space gone, blank lines
    /// dropped. A leading run is kept (as one space), since <c>-b</c> does not ignore indentation appearing where there was none.
    /// </summary>
    private static string Squash(string text) =>
        string.Join('\n', text.Split('\n').Select(l =>
        {
            var words = string.Join(' ', l.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));
            return words.Length > 0 && l[0] is ' ' or '\t' ? " " + words : words;
        }).Where(l => l.Length > 0));

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

    /// <summary>
    /// The tracked files as they are on disk: each path in the index with its content on disk, and none for one deleted there. A file git
    /// does not track is not in it (git diff leaves it out; <c>ls-files --others</c> names it).
    /// </summary>
    private Dictionary<string, string> WorkingTree(Worktree wt)
    {
        var disk = Snapshot(wt.Path);
        return wt.Index.Keys.Where(disk.ContainsKey).ToDictionary(p => p, p => disk[p], StringComparer.Ordinal);
    }

    /// <summary><c>git ls-files --others --exclude-standard [-z]</c>: the files on disk the index does not hold, by path.</summary>
    private GitRun LsFiles(List<string> a, Worktree? wt)
    {
        var nul = a.Remove("-z");
        var others = a.Remove("--others") | a.Remove("-o");
        var standard = a.Remove("--exclude-standard");
        if (wt is null || !others || a.Count > 0)
        {
            throw new NotSupportedException("ls-files form");
        }
        var disk = Snapshot(wt.Path);
        // The model keeps no ignore rules: a .gitignore it would have to read is real git's job.
        if (standard && disk.Keys.Any(p => p == ".gitignore" || p.EndsWith("/.gitignore", StringComparison.Ordinal)))
        {
            throw new NotSupportedException("ls-files with a .gitignore");
        }
        var end = nul ? '\0' : '\n';
        return Ok(string.Concat(disk.Keys.Where(p => !wt.Index.ContainsKey(p)).Order(StringComparer.Ordinal).Select(p => p + end)));
    }

    private List<string> StatusLines(Worktree wt)
    {
        var head = HeadId(wt) is { } id ? _commits[id].Tree : new Dictionary<string, string>();
        var disk = Snapshot(wt.Path);
        // As git lists them: what is tracked and changed (an unmerged path as UU), by path, then what is untracked, by path.
        var tracked = new List<(string Path, string Line)>();
        var untracked = new List<string>();
        foreach (var path in head.Keys.Union(wt.Index.Keys).Union(disk.Keys).Order(StringComparer.Ordinal))
        {
            var inHead = head.TryGetValue(path, out var h);
            var inIndex = wt.Index.TryGetValue(path, out var i);
            var onDisk = disk.TryGetValue(path, out var d);
            if (wt.Merging is { } merging && merging.Paths.Contains(path))
            {
                tracked.Add((path, $"UU {path}"));
                continue;
            }
            if (!inIndex && onDisk && !inHead)
            {
                untracked.Add($"?? {path}");
                continue;
            }
            var x = inHead == inIndex && h == i ? ' ' : !inHead ? 'A' : !inIndex ? 'D' : 'M';
            var y = inIndex == onDisk && i == d ? ' ' : !onDisk ? 'D' : 'M';
            if (x != ' ' || y != ' ')
            {
                tracked.Add((path, $"{x}{y} {path}"));
            }
        }
        return [.. tracked.Select(t => t.Line), .. untracked];
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
            Refs["refs/heads/" + b] = id;
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
    /// <c>git merge-tree --write-tree [--name-only] [--no-messages] &lt;a&gt; &lt;b&gt;</c>: a test merge that touches no folder. The tree's id on the first line, then (when it
    /// conflicts, exit 1) the conflicted paths. The id is a hash of the merged files, not a stored tree: nothing in the model reads a tree object back.
    /// </summary>
    /// <summary>A tree's id: the same for the same paths and contents, as <c>merge-tree</c> and <c>rev-parse X^{tree}</c> both say it.</summary>
    private static string TreeId(IReadOnlyDictionary<string, string> tree)
    {
        var text = new StringBuilder();
        foreach (var (path, content) in tree.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            text.Append(path).Append('\0').Append(content).Append('\0');
        }
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private GitRun MergeTree(List<string> a, Worktree? wt)
    {
        if (!a.Remove("--write-tree"))
        {
            throw new NotSupportedException("merge-tree without --write-tree");
        }
        var nameOnly = a.Remove("--name-only");
        a.Remove("--no-messages");
        if (a.Count != 2 || a.Any(x => x.StartsWith('-')) || wt is null)
        {
            throw new NotSupportedException("merge-tree form");
        }
        var mine = Resolve(a[0], wt);
        var theirs = Resolve(a[1], wt);
        if (mine is null || theirs is null)
        {
            return Fail($"fatal: not something we can merge: {(mine is null ? a[0] : a[1])}", 128);
        }
        if (MergeBase(mine, theirs) is not { } baseId)
        {
            return Fail("fatal: refusing to merge unrelated histories", 128);
        }
        var (merged, conflicts) = Merge3(_commits[baseId].Tree, _commits[mine].Tree, _commits[theirs].Tree, null, false);
        var id = TreeId(merged);
        if (conflicts.Count == 0)
        {
            return Ok(id + "\n");
        }
        return new GitRun(1, id + "\n" + (nameOnly ? string.Concat(conflicts.Select(p => p + "\n")) : ""), "");
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
