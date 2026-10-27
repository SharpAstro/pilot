using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

internal sealed class FakeWorkers : IWorkerLauncher
{
    private int _next;

    public List<WorkerStart> Started { get; } = [];

    public List<string> Stopped { get; } = [];

    /// <summary>What the next start throws, as the claude CLI refusing it; null to start it.</summary>
    public Exception? Fail { get; set; }

    /// <summary>What the next start waits for before the worker is up, as a slow claude start; null to start at once.</summary>
    public Task? Hold { get; set; }

    public async Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
    {
        if (Fail is { } fail)
        {
            throw fail;
        }
        if (Hold is { } hold)
        {
            Hold = null;
            await hold;
        }
        Started.Add(start);
        return $"job{++_next:D5}";
    }

    public Task StopAsync(string job, CancellationToken cancellationToken)
    {
        Stopped.Add(job);
        return Task.CompletedTask;
    }
}

internal sealed class FakeChecks : ICheckRunner
{
    public CheckRun Next { get; set; } = new(0, "all green");

    public List<(string Command, string Cwd)> Runs { get; } = [];

    public Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken)
    {
        Runs.Add((command, cwd));
        return Task.FromResult(Next);
    }
}

internal sealed class FakeGitHub : IGitHub
{
    public PullRequest? Pr { get; set; }

    /// <summary>The open issues, by number, with their titles.</summary>
    public Dictionary<int, string> Issues { get; } = [];

    public Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken) =>
        Task.FromResult<IssueInfo[]>([.. Issues.Select(i => new IssueInfo { Number = i.Key, Title = i.Value })]);

    /// <summary>Each repo's open PRs, by repo folder path; a repo with none listed has none.</summary>
    public Dictionary<string, PullRequestStatus[]> OpenPrs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Repo folder paths whose PR listing fails, as a gh that could not reach GitHub.</summary>
    public HashSet<string> OpenPrsFail { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many PR listings were asked for.</summary>
    public int OpenPrCalls;

    public Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref OpenPrCalls);
        return OpenPrsFail.Contains(repoPath)
            ? Task.FromException<PullRequestStatus[]>(new FlightException("gh could not list the PRs: offline"))
            : Task.FromResult(OpenPrs.TryGetValue(repoPath, out var prs) ? prs : []);
    }

    public Task<int[]> OpenPullRequestsLabelledAsync(string repoPath, string label, CancellationToken cancellationToken) =>
        OpenPrsFail.Contains(repoPath)
            ? Task.FromException<int[]>(new FlightException("gh could not list the PRs: offline"))
            : Task.FromResult(OpenPrs.TryGetValue(repoPath, out var prs)
                ? (int[])[.. prs.Where(p => Labels.TryGetValue(p.Number, out var set) && set.Contains(label)).Select(p => p.Number)]
                : []);

    /// <summary>Every issue created, as (repo path, title, body).</summary>
    public List<(string Repo, string Title, string? Body)> IssuesCreated { get; } = [];

    /// <summary>When set, CreateIssueAsync fails with it.</summary>
    public string? CreateIssueFails { get; set; }

    public Task<IssueInfo> CreateIssueAsync(string repoPath, string title, string? body, CancellationToken cancellationToken)
    {
        if (CreateIssueFails is { } why)
        {
            return Task.FromException<IssueInfo>(new FlightException(why));
        }
        IssuesCreated.Add((repoPath, title, body));
        var number = Issues.Count == 0 ? 1 : Issues.Keys.Max() + 1;
        Issues[number] = title;
        return Task.FromResult(new IssueInfo { Number = number, Title = title });
    }

    public Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Task.FromResult(Issues.TryGetValue(number, out var title) ? title : null);

    public List<(int Number, string Head, string Method)> Merged { get; } = [];

    // The labels are what the fake keeps on the PR, so a label taken off by hand is read as GitHub says it.
    public Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken) =>
        Task.FromResult(Pr is { } pr ? pr with { Labels = Labels.TryGetValue(pr.Number, out var labels) ? [.. labels] : [] } : null);

    /// <summary>Whether a merge joins a merge queue rather than merging, as on a base that has one.</summary>
    public bool Queues { get; set; }

    public Task<bool> MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        Merged.Add((number, head, method));
        return Task.FromResult(Queues);
    }

    /// <summary>What ChecksAsync answers for any PR.</summary>
    public string? Checks { get; set; }

    /// <summary>What ChecksSummaryAsync answers for any PR, and so what ChecksAsync's status is; null to derive it from <see cref="Checks"/>, with no counts.</summary>
    public ChecksSummary? Summary { get; set; }

    public Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken) => Task.FromResult(Summary?.Status ?? Checks);

    public Task<ChecksSummary?> ChecksSummaryAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Task.FromResult(Summary ?? (Checks is { } status ? new ChecksSummary(status, 0, 0) : null));

    /// <summary>Whether the repo allows GitHub's auto-merge, as AutoMergeAllowedAsync answers.</summary>
    public bool AutoMergeAllowed { get; set; }

    /// <summary>When set, ArmAutoMergeAsync fails with it, as GitHub refusing a PR with nothing to wait for.</summary>
    public string? ArmFails { get; set; }

    /// <summary>When set, DisarmAutoMergeAsync fails with it.</summary>
    public string? DisarmFails { get; set; }

    /// <summary>Every auto-merge armed, as (PR number, head, method).</summary>
    public List<(int Number, string Head, string Method)> AutoMergeArmed { get; } = [];

    /// <summary>Every auto-merge disarmed, by PR number.</summary>
    public List<int> AutoMergeDisarmed { get; } = [];

    public Task<bool> AutoMergeAllowedAsync(string repoPath, CancellationToken cancellationToken) => Task.FromResult(AutoMergeAllowed);

    /// <summary>
    /// The mergeStateStatus GitHub gives the PR when auto-merge is armed on it: BLOCKED (a required check pending) by default;
    /// CLEAN, HAS_HOOKS and UNSTABLE are immediately mergeable, which gh's own <c>pr merge --auto</c> merges at once instead of
    /// arming (gh 2.86 merge.go), so the real arm refuses them and this fake does, merging nothing.
    /// </summary>
    public string MergeStateStatus { get; set; } = "BLOCKED";

    public Task ArmAutoMergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        if (ArmFails is { } why)
        {
            return Task.FromException(new FlightException(why));
        }
        if (GhCli.ImmediatelyMergeable(MergeStateStatus))
        {
            return Task.FromException(new FlightException($"GitHub will not arm auto-merge on #{number}: it is in {MergeStateStatus.ToLowerInvariant()} status, so it has no required check to wait for"));
        }
        AutoMergeArmed.Add((number, head, method));
        return Task.CompletedTask;
    }

    public Task<bool?> AutoMergeArmedAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(AutoMergeArmed.Any(a => a.Number == number) && !AutoMergeDisarmed.Contains(number));

    public Task DisarmAutoMergeAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        if (DisarmFails is { } why)
        {
            return Task.FromException(new FlightException(why));
        }
        AutoMergeDisarmed.Add(number);
        return Task.CompletedTask;
    }

    /// <summary>How many times the failed runs were asked to run again, each as the PR's number.</summary>
    public List<int> Reruns { get; } = [];

    public Task<int> RerunFailedChecksAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        Reruns.Add(number);
        return Task.FromResult(1);
    }

    /// <summary>The workflow runs each commit has, by commit; a commit with none listed has none.</summary>
    public Dictionary<string, WorkflowRun[]> Runs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, listing runs fails with it, as a gh that could not reach GitHub.</summary>
    public string? RunsFail { get; set; }

    public Task<WorkflowRun[]> WorkflowRunsAsync(string repoPath, string commit, CancellationToken cancellationToken) =>
        RunsFail is { } why ? Task.FromException<WorkflowRun[]>(new FlightException(why)) : Task.FromResult(Runs.TryGetValue(commit, out var runs) ? runs : []);

    /// <summary>What MergeStateAsync answers for any PR; null to derive it from <see cref="Summary"/>/<see cref="Checks"/> and <see cref="Queue"/> as GitHub would say it, none with no checks.</summary>
    public MergeState? Merge { get; set; }

    public Task<MergeState?> MergeStateAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        if (Merge is not null)
        {
            return Task.FromResult<MergeState?>(Merge);
        }
        if (Queue is { Queued: true } queued)
        {
            return Task.FromResult<MergeState?>(new MergeState(MergeKinds.Queued, Position: queued.Position));
        }
        var dropped = Queue is { Queued: false, RemovedAt: not null, RemovedReason: { Length: > 0 } reason } && reason != "merged" ? reason : null;
        var (done, total) = (Summary?.Done ?? 0, Summary?.Total ?? 0);
        // GitHub says armed for as long as its auto-merge request is on the PR, whatever the checks (a failed one is blocked first).
        if ((Summary?.Status ?? Checks) is not ChoiceStatus.Failing && AutoMergeArmed.Any(a => a.Number == number) && !AutoMergeDisarmed.Contains(number))
        {
            return Task.FromResult<MergeState?>(new MergeState(MergeKinds.Armed, done, total));
        }
        return Task.FromResult<MergeState?>((Summary?.Status ?? Checks) switch
        {
            ChoiceStatus.Failing => new MergeState(MergeKinds.Blocked, done, total, Reason: $"{Summary?.FailedName ?? "build"} failed", FailedName: Summary?.FailedName ?? "build", FailedLink: Summary?.FailedLink, DroppedFromQueue: dropped),
            ChoiceStatus.Pending => new MergeState(MergeKinds.Pending, done, total, DroppedFromQueue: dropped),
            ChoiceStatus.Passing => new MergeState(MergeKinds.Ready, done, total, DroppedFromQueue: dropped),
            _ => null,
        });
    }

    /// <summary>What MergeQueueAsync answers for any PR: null, a base with no merge queue, unless set.</summary>
    public MergeQueueState? Queue { get; set; }

    public Task<MergeQueueState?> MergeQueueAsync(string repoPath, int number, CancellationToken cancellationToken) => Task.FromResult(Queue);

    /// <summary>Every dequeue the node asked for, by PR number.</summary>
    public List<int> Dequeued { get; } = [];

    /// <summary>When set, DequeueAsync fails with it.</summary>
    public string? DequeueFails { get; set; }

    public Task DequeueAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        if (DequeueFails is { } why)
        {
            return Task.FromException(new FlightException(why));
        }
        Dequeued.Add(number);
        // The queue lets it go: the PR is not queued any more, and GitHub says it was removed by hand.
        if (Queue is { Queued: true } queued)
        {
            Queue = queued with { Queued = false, Position = null, RemovedAt = DateTimeOffset.UtcNow, RemovedReason = "manually removed" };
        }
        return Task.CompletedTask;
    }

    /// <summary>Every draft (true) or ready (false) the node asked for, by PR number.</summary>
    public List<(int Number, bool Draft)> DraftCalls { get; } = [];

    /// <summary>When set, SetDraftAsync fails with it, as a gh that could not reach GitHub.</summary>
    public string? DraftFails { get; set; }

    public Task SetDraftAsync(string repoPath, int number, bool draft, CancellationToken cancellationToken)
    {
        if (DraftFails is { } why)
        {
            throw new FlightException(why);
        }
        DraftCalls.Add((number, draft));
        return Task.CompletedTask;
    }

    public List<(string Branch, string Base, string Title, string Body)> Created { get; } = [];

    public Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        Created.Add((branch, baseBranch, title, body));
        Pr = new PullRequest(70 + Created.Count, "OPEN", "cafe0000cafe0000", $"https://github.com/x/tianwen/pull/{70 + Created.Count}");
        return Task.FromResult(Pr);
    }

    /// <summary>Any repo's PRs, by <c>owner/repo#n</c>, as gh pr view -R reads them.</summary>
    public Dictionary<string, PullRequest> Prs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<PullRequest> PullRequestOfAsync(string repo, int number, CancellationToken cancellationToken) =>
        Prs.TryGetValue($"{repo}#{number}", out var pr) ? Task.FromResult(pr) : Task.FromException<PullRequest>(new FlightException($"gh could not read {repo}#{number}: not found"));

    /// <summary>Where each repo, <c>owner/name</c>, clones from: a local bare repo standing in for GitHub.</summary>
    public Dictionary<string, string> Origins { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every clone made, by repo and path.</summary>
    public List<(string Repo, string Path)> Cloned { get; } = [];

    public async Task CloneAsync(string repo, string path, CancellationToken cancellationToken)
    {
        if (!Origins.TryGetValue(repo, out var origin))
        {
            throw new FlightException($"gh could not clone {repo}: not found");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var run = await new GitCli().RunAsync(["clone", "-q", origin, path], Path.GetDirectoryName(path)!, cancellationToken);
        if (!run.Ok)
        {
            throw new FlightException($"gh could not clone {repo}: {run.Why}");
        }
        Cloned.Add((repo, path));
    }

    /// <summary>Every <c>pilot/review</c> status set, in order: the commit, its state, its description and the page it links to.</summary>
    public List<(string Sha, string State, string Description, string? Url)> Statuses { get; } = [];

    /// <summary>When set, a status write fails with it, as GitHub refusing it.</summary>
    public string? StatusFails { get; set; }

    public Task SetCommitStatusAsync(string repoPath, string sha, string state, string description, string? targetUrl, CancellationToken cancellationToken)
    {
        if (StatusFails is { } why)
        {
            return Task.FromException(new FlightException(why));
        }
        Statuses.Add((sha, state, description, targetUrl));
        return Task.CompletedTask;
    }

    /// <summary>The labels each PR carries, by number: a pilot: label replaces any other, as the real call does; others are left.</summary>
    public Dictionary<int, HashSet<string>> Labels { get; } = [];

    /// <summary>Every pilot: label asked for, by PR number, in order.</summary>
    public List<(int Number, string Label)> LabelCalls { get; } = [];

    /// <summary>When set, a label write fails with it, as GitHub refusing it.</summary>
    public string? LabelFails { get; set; }

    public Task SetPilotLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        if (LabelFails is { } why)
        {
            return Task.FromException(new FlightException(why));
        }
        LabelCalls.Add((number, label));
        var labels = Labels.TryGetValue(number, out var existing) ? existing : Labels[number] = [];
        labels.RemoveWhere(l => l.StartsWith(PilotMarks.LabelPrefix, StringComparison.Ordinal));
        labels.Add(label);
        return Task.CompletedTask;
    }

    /// <summary>Every label asked to come off, by PR number, in order.</summary>
    public List<(int Number, string Label)> RemoveCalls { get; } = [];

    public Task RemoveLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        RemoveCalls.Add((number, label));
        if (Labels.TryGetValue(number, out var set))
        {
            set.Remove(label);
        }
        return Task.CompletedTask;
    }

    /// <summary>The login gh says it is signed in as.</summary>
    public string? Login { get; set; } = "controller";

    public Task<string?> LoginAsync(CancellationToken cancellationToken) => Task.FromResult(Login);

    /// <summary>Every review posted: the repo, the PR, the payload's path, and the payload as it was read when it went.</summary>
    public List<(string Repo, int Number, string Path, string Payload)> Posted { get; } = [];

    /// <summary>When set, a post fails with it: a FlightException as GitHub refusing it, anything else as a post that never said back.</summary>
    public Exception? PostFails { get; set; }

    public Task<PostedReview> PostReviewAsync(string repo, int number, string payload, CancellationToken cancellationToken)
    {
        if (PostFails is { } fails)
        {
            return Task.FromException<PostedReview>(fails);
        }
        var text = File.ReadAllText(payload);
        Posted.Add((repo, number, payload, text));
        var id = 9000 + Posted.Count;
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var state = doc.RootElement.GetProperty("event").GetString() switch { "APPROVE" => "APPROVED", "REQUEST_CHANGES" => "CHANGES_REQUESTED", _ => "COMMENTED" };
        return Task.FromResult(new PostedReview(id, state, $"https://github.com/{repo}/pull/{number}#pullrequestreview-{id}"));
    }
}

/// <summary>Legs: planned in order, flown one at a time by fresh workers, done only on a passing rerun, and a flight merged guarded on its head.</summary>
public abstract class LegCommanderTestsBase : IDisposable
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "p2l-" + Guid.NewGuid().ToString("N")[..8]);
    private protected readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private protected readonly FakeWorkers _workers = new();
    private protected readonly FakeChecks _checks = new();
    private protected readonly FakeGitHub _github = new();
    private protected readonly FlightCommander _flights;
    private protected readonly LegCommander _legs;

    protected LegCommanderTestsBase()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        // A check that builds or tests needs a solution where it runs (leg plan refuses one that does not): a stub, in the plane's root.
        File.WriteAllText(Path.Combine(Repo, "Tianwen.slnx"), "<Solution />\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, _checks, _github, git, new Planes(git), _flights, options, TimeProvider.System);
    }

    private protected string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        // A leg start that pulled a sibling has RepoWatch reading the repos in the background; let it finish before the folders go.
        _legs.SiblingSync?.WatchRefresh.Wait(TimeSpan.FromSeconds(30));
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private protected static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private protected Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private protected LegRow L(string name) => _state.Legs("TIA1").Single(l => l.Name == name);

    private protected async Task<FlightRow> FlightWithTwoLegs()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse the ephemeris", "done-when=The parser tests pass",
            "check=dotnet test", "budget=4_000_000", "model=sonnet", "item=Write the parser", "item=Test it")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw the planets", "done-when=The capture shows them")).Ok.ShouldBeTrue();
        return _state.Flights().Single();
    }

    private protected const string UsesDirLib = """
        <Project>
          <PropertyGroup>
            <UseLocalDirLib Condition="'$(UseLocalDirLib)' == '' AND Exists('$(MSBuildThisFileDirectory)..\DIR.Lib\src\DIR.Lib\DIR.Lib.csproj')">true</UseLocalDirLib>
          </PropertyGroup>
        </Project>
        """;

    private protected void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", message);

    /// <summary>The repo declares DIR.Lib as a UseLocal sibling, and the clone beside it is <paramref name="behind"/> commits behind its origin.</summary>
    private protected void SiblingDirLib(int behind)
    {
        File.WriteAllText(Path.Combine(Repo, "Directory.Build.props"), UsesDirLib);
        Git(Repo, "add", ".");
        Commit(Repo, "uses DIR.Lib");
        var origin = Path.Combine(_root, "dir-lib-origin");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "-b", "main");
        Commit(origin, "first");
        Git(_root, "clone", "-q", origin, "DIR.Lib");
        for (var i = 0; i < behind; i++)
        {
            Commit(origin, $"newer {i}");
        }
    }

    /// <summary>The leg commander with the node's sibling sync, as NodeApp wires it.</summary>
    private protected RepoWatch WithSiblingSync()
    {
        var git = new GitCli();
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1", RepoRootsGiven: [_root]);
        var watch = new RepoWatch(_state, new RepoCatalog(_state, options), new Planes(git), _github, TimeProvider.System) { Parallelism = 1 };
        _legs.SiblingSync = new RepoSync(git, new Planes(git), watch);
        return watch;
    }

    private protected sealed class ThrowingGit : IGit
    {
        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("git did not start");
    }

    // A flight with both legs done, reviewed and passed, and its PR open at the plane's head: the tip.
    private protected async Task<string> ReadyToMerge()
    {
        var flight = await FlightWithTwoLegs();
        _legs.Models = new RoleModels(null);
        foreach (var name in new[] { "parser", "render" })
        {
            if (L(name).State == LegState.Planned)
            {
                await Leg(Verbs.StartLeg, $"flight/TIA1/{name}");
            }
            if (L(name).State == LegState.Planned)
            {
                await Leg(Verbs.ValidateLeg, $"flight/TIA1/{name}", "go");
            }
            foreach (var i in Enumerable.Range(1, L(name).Checklist.Length))
            {
                await Leg(Verbs.Check, $"flight/TIA1/{name}", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            (await Leg(Verbs.FinishLeg, $"flight/TIA1/{name}", "done")).Ok.ShouldBeTrue();
        }
        var tip = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _github.Pr = new PullRequest(63, "OPEN", tip, "https://github.com/x/tianwen/pull/63");
        _state.PutFlight(_state.Flights().Single() with
        {
            PullRequest = 63,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        return tip;
    }

    /// <summary>git as on Windows with a process sitting in the worktree: the remove succeeds and the empty folder stays.</summary>
    private protected sealed class FolderKeepingGit : IGit
    {
        private readonly GitCli _git = new();

        public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            var run = await _git.RunAsync(args, cwd, cancellationToken);
            if (run.Ok && args is ["worktree", "remove", .., var worktree])
            {
                Directory.CreateDirectory(worktree);
            }
            return run;
        }
    }
}

/// <summary>Part 1 of LegCommanderTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class LegCommanderTests1 : LegCommanderTestsBase
{
    [Fact]
    public async Task AFlightMadeWithoutATitleTakesItsIssuesAndItsResultNamesTheCode()
    {
        _github.Issues[7] = "Moons of Jupiter";
        var flights = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System, github: _github);

        var made = await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["7"], Who = "tests" }, Ct);

        made.Made.ShouldBe("TIA7");
        _state.Flights().Single().Title.ShouldBe("Moons of Jupiter");
    }

    [Fact]
    public async Task APlannerIsStartedInThePlaneAndItsPlannedOffersTheFirstLeg()
    {
        var flight = await FlightWithTwoLegs();

        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        // A second, before the tracker has listed the first, starts no second planner over the same legs.
        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Error.ShouldBe("du1-tia1-plan is planning TIA1 already; wait for its planned card, or stop it first");

        var planner = _workers.Started.ShouldHaveSingleItem();
        (planner.Name, planner.Plane).ShouldBe(("du1-tia1-plan", Path.GetFileName(flight.Worktree)));
        planner.Brief.ShouldContain("gh issue view 1");
        planner.Brief.ShouldContain("leg plan TIA1 <name>");
        planner.Brief.ShouldContain("flight planned TIA1");
        planner.Brief.ShouldContain("- parser (planned): Parse the ephemeris");

        (await Leg(Verbs.Planned, "flight/TIA1", "parser first, then render")).Ok.ShouldBeTrue();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.PlannedKind);
        card.Title.ShouldBe("TIA1: 2 legs planned");
        card.Detail.ShouldBe("parser first, then render\n1. parser: Parse the ephemeris\n2. render: Draw the planets");
        var start = card.Choices.ShouldHaveSingleItem();
        (start.Label, start.Verb, start.Target).ShouldBe(("Start parser", Verbs.StartLeg, Verbs.LegTarget("TIA1", "parser")));
    }

    [Fact]
    public async Task APlannerAskedForOverASpentWindowWaitsSaysUntilWhenAndStartsByItselfOnceItHasReset()
    {
        await FlightWithTwoLegs();
        _legs.Models = new RoleModels(null);
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = 95, ResetsAt = DateTimeOffset.UtcNow.AddHours(1) } });

        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldBeEmpty();
        // The sweep holds the flight's own first leg too, which has its card; the planner's is one, said once.
        var waits = _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind && c.Title.Contains("planner", StringComparison.Ordinal)).ShouldHaveSingleItem();
        (waits.Bin, waits.Subject).ShouldBe((CardBin.Handled, "flight/TIA1"));
        waits.Title.ShouldStartWith("TIA1 waits to start its planner: the 5-hour window is 95 % spent");

        // The window resets (a later reset is a new window): the sweep starts it, with no one asking again.
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = 4, ResetsAt = DateTimeOffset.UtcNow.AddHours(6) } });
        await _legs.AdvanceAllAsync(Ct);

        // The sweep starts the flight's first leg as well, once the window allows it.
        _workers.Started.Count(w => w.Name == "du1-tia1-plan").ShouldBe(1);
        _state.Cards().Count(c => c.Kind == "planner-started").ShouldBe(1);
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.Count(w => w.Name == "du1-tia1-plan").ShouldBe(1);
    }

    [Fact]
    public async Task APriorityReposPlannerUsesItsOwnLimitAndOneWithNoFiguresIsStartedAtOnce()
    {
        await FlightWithTwoLegs();
        var repo = _state.Flights().Single().Repo;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), "priority-repos", [repo]).Settings);

        // 95 % is past the start limit (90) and under the priority limit (98): this repo's planner is not held by it.
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = 95, ResetsAt = DateTimeOffset.UtcNow.AddHours(1) } });
        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem();
        _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task APlannerWithNoFiguresForTheWindowIsStartedAtOnce()
    {
        await FlightWithTwoLegs();
        _legs.Models = new RoleModels(null);

        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PlannedWithNoLegToStartIsRefusedAndNoLegIsNamedPlan()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        (await Leg(Verbs.Planned, "flight/TIA1")).Error.ShouldBe("TIA1 has no leg waiting to start: plan them with plan-leg first");
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=plan", "goal=Do the work", "done-when=Its check passes")).Error
            .ShouldBe("plan names the flight's planner, not a leg; call the leg something else");
    }

    [Fact]
    public async Task LegsArePlannedInOrderWithTheirChecklistAndBudget()
    {
        await FlightWithTwoLegs();

        _state.Legs("TIA1").Select(l => (l.Name, l.Order)).ShouldBe([("parser", 1), ("render", 2)]);
        var parser = L("parser");
        parser.Checklist.Select(i => i.Text).ShouldBe(["Write the parser", "Test it"]);
        (parser.Budget, parser.Model, parser.Check, parser.State).ShouldBe((4_000_000L, "sonnet", "dotnet test", LegState.Planned));

        // Planned again: replaced in its place.
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse it better", "done-when=The parser tests pass")).Ok.ShouldBeTrue();
        (L("parser").Order, L("parser").Goal).ShouldBe((1, "Parse it better"));
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=Bad Name", "goal=Do the work", "done-when=Its check passes")).Error.ShouldNotBeNull().ShouldContain("lower-case");
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=x")).Error.ShouldNotBeNull().ShouldContain("goal and a done-when");
        // A placeholder is no brief (PIL337 went out with goal 'x' and done-when 'y').
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=menu", "goal=x", "done-when=y")).Error.ShouldNotBeNull().ShouldContain("not 'x' and 'y'");
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "oops")).Error.ShouldNotBeNull().ShouldContain("key=value");
        // TIA1818's check, 2026-10-25: a bare | in a test filter pipes in every shell, so it is refused when planned.
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=filter", "goal=Do the work", "done-when=Its check passes", "check=dotnet test --filter A|B"))
            .Error.ShouldNotBeNull().ShouldContain("--filter 'A|B'");
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=filter", "goal=Do the work", "done-when=Its check passes", "check=dotnet test --filter 'A|B'")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task APlannedLegIsRemovedTheLegsAfterItKeepTheirOrderAndAHandledCardSaysWhy()
    {
        await FlightWithTwoLegs();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=docs", "goal=Write it up", "done-when=It reads")).Ok.ShouldBeTrue();
        await Leg(Verbs.Planned, "flight/TIA1", "three legs");
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegCommander.PlannedKind);

        (await Leg(Verbs.RemoveLeg, "flight/TIA1/parser", "the", "render", "covers", "it")).Ok.ShouldBeTrue();

        _state.Legs("TIA1").Select(l => (l.Name, l.Order)).ShouldBe([("render", 1), ("docs", 2)]);
        var card = _state.Cards().Single(c => c.Kind == LegCommander.LegRemovedKind);
        (card.Bin, card.Title, card.Detail).ShouldBe((CardBin.Handled, "TIA1/parser removed by tests", "the render covers it"));
        // The plan's card offered to start the leg that went.
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.PlannedKind);

        // A leg planned afterwards goes at the end of what is left.
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=polish", "goal=Do the work", "done-when=Its check passes")).Ok.ShouldBeTrue();
        L("polish").Order.ShouldBe(3);
    }

    [Fact]
    public async Task OnlyAPlannedLegIsRemovedAFlyingDoneOrFailedOneIsRefusedWithWhy()
    {
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        (await Leg(Verbs.RemoveLeg, "flight/TIA1/parser")).Error.ShouldBe("TIA1/parser is flying; only a planned leg is taken out");
        _state.PutLeg(L("parser") with { State = LegState.Done });
        (await Leg(Verbs.RemoveLeg, "flight/TIA1/parser")).Error.ShouldBe("TIA1/parser is done; only a planned leg is taken out");
        _state.PutLeg(L("render") with { State = LegState.Failed });
        (await Leg(Verbs.RemoveLeg, "flight/TIA1/render")).Error.ShouldBe("TIA1/render is failed; only a planned leg is taken out");
        (await Leg(Verbs.RemoveLeg, "flight/TIA1/nothing")).Error.ShouldBe("TIA1 has no leg nothing");
        (await Leg(Verbs.RemoveLeg, "flight/TIA1")).Error.ShouldNotBeNull().ShouldContain("takes a leg target");

        _state.Legs("TIA1").Select(l => (l.Name, l.State)).ShouldBe([("parser", LegState.Done), ("render", LegState.Failed)]);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.LegRemovedKind);
    }

    [Fact]
    public async Task TheFlightGoesOnPastARemovedLegToTheOneAfterIt()
    {
        await FlightWithTwoLegs();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=docs", "goal=Write it up", "done-when=It reads")).Ok.ShouldBeTrue();
        _state.PutLeg(L("parser") with { State = LegState.Done });

        // Not advancing (the default): the removal alone starts nothing.
        (await Leg(Verbs.RemoveLeg, "flight/TIA1/render")).Ok.ShouldBeTrue();
        _workers.Started.ShouldBeEmpty();

        _legs.Models = new RoleModels(null);
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw the planets", "done-when=The capture shows them")).Ok.ShouldBeTrue();
        (await Leg(Verbs.RemoveLeg, "flight/TIA1/docs")).Ok.ShouldBeTrue();

        // docs was in front of render and gone, so render, the next planned leg, is the one that starts: its validator first.
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-render-validator");
        (await Leg(Verbs.ValidateLeg, "flight/TIA1/render", "go")).Ok.ShouldBeTrue();
        L("render").State.ShouldBe(LegState.Flying);
        _workers.Started.Select(w => w.Name).ShouldBe(["du1-tia1-render-validator", "du1-tia1-render"]);
        L("render").Order.ShouldBe(2);
    }

    [Fact]
    public async Task ARemovedLegStaysRemovedWhenTheJournalIsReplayed()
    {
        var folder = Path.Combine(_root, "journal");
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var git = new GitCli();
            var flights = new FlightCommander(state, new Planes(git), TimeProvider.System);
            var legs = new LegCommander(state, _workers, _checks, _github, git, new Planes(git), flights, new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1"), TimeProvider.System);
            (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            foreach (var name in new[] { "parser", "render", "docs" })
            {
                (await legs.RunAsync(new CommandRequest { Verb = Verbs.PlanLeg, Target = "flight/TIA1", Args = [$"name={name}", "goal=Do the work", "done-when=Its check passes"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            }

            (await legs.RunAsync(new CommandRequest { Verb = Verbs.RemoveLeg, Target = "flight/TIA1/render", Args = ["not needed"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        }

        using var again = new FileJournal(folder);
        var restored = new NodeState(again, TimeProvider.System);
        restored.Legs("TIA1").Select(l => (l.Name, l.Order)).ShouldBe([("parser", 1), ("docs", 2)]);
        restored.Cards().Single(c => c.Kind == LegCommander.LegRemovedKind).Detail.ShouldBe("not needed");
    }

    [Fact]
    public async Task ALegStartedWithASiblingBehindItsOriginRaisesOneCardNamingItAndThePullAndStillStarts()
    {
        SiblingDirLib(behind: 3);
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.SiblingBehindKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "flight/TIA1/parser"));
        card.Title.ShouldBe("TIA1/parser compiles against a sibling clone behind origin");
        card.Detail.ShouldBe($"DIR.Lib is 3 commits behind its origin: git -C {Path.Combine(_root, "DIR.Lib")} pull --ff-only");

        _state.Cards().Count(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBe(1);
    }

    [Fact]
    public async Task ASiblingPulledByHandSinceItWasCountedBehindGivesNeitherAHandledLineNorACard()
    {
        SiblingDirLib(behind: 3);
        WithSiblingSync();
        var flight = await FlightWithTwoLegs();
        // SiblingWatch counts it behind and caches that; then the person pulls it before the leg starts.
        (await _legs.Siblings.BehindAsync(flight.Worktree, flight.RepoPath, Ct)).ShouldHaveSingleItem().Behind.ShouldBe(3);
        Git(Path.Combine(_root, "DIR.Lib"), "pull", "-q", "--ff-only");

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem();
        _state.Cards().Where(c => c.Kind is LegCommander.SiblingPulledKind or LegCommander.SiblingBehindKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task AfterALegStartPulledASiblingRepoWatchShowsItZeroBehind()
    {
        SiblingDirLib(behind: 3);
        var watch = WithSiblingSync();
        var flight = await FlightWithTwoLegs();
        // The fetch inside the count brings origin/main forward, so the watch's first read has the clone 3 behind.
        await _legs.Siblings.BehindAsync(flight.Worktree, flight.RepoPath, Ct);
        (await watch.StatusAsync(refresh: true, Ct)).Single(r => r.Name == "DIR.Lib").Behind.ShouldBe(3);

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        await _legs.SiblingSync!.WatchRefresh;

        (await watch.StatusAsync(refresh: false, Ct)).Single(r => r.Name == "DIR.Lib").Behind.ShouldBe(0);
    }

    [Fact]
    public async Task AStaleSiblingWhoseOriginWentUnreachableAfterTheCountIsStillPulled()
    {
        SiblingDirLib(behind: 3);
        WithSiblingSync();
        var flight = await FlightWithTwoLegs();
        await _legs.Siblings.BehindAsync(flight.Worktree, flight.RepoPath, Ct);
        var origin = Path.Combine(_root, "dir-lib-origin");
        Directory.Move(origin, origin + "-gone");

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
        _state.Cards().Single(c => c.Kind == LegCommander.SiblingPulledKind).Detail.ShouldBe("main took 3 commits from origin/main");
        Git(Path.Combine(_root, "DIR.Lib"), "rev-list", "--count", "HEAD..origin/main").Trim().ShouldBe("0");
    }

    [Fact]
    public async Task ALegStartPullsAStaleCleanSiblingItselfAndRingsNobody()
    {
        SiblingDirLib(behind: 3);
        WithSiblingSync();
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem();
        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
        var pulled = _state.Cards().Single(c => c.Kind == LegCommander.SiblingPulledKind);
        (pulled.Bin, pulled.Title, pulled.Detail).ShouldBe((CardBin.Handled, "Pulled DIR.Lib for TIA1/parser", "main took 3 commits from origin/main"));
        Git(Path.Combine(_root, "DIR.Lib"), "rev-list", "--count", "HEAD..@{upstream}").Trim().ShouldBe("0");
    }

    [Fact]
    public async Task ALegStartLeavesADirtySiblingAloneAndItsCardSaysWhy()
    {
        SiblingDirLib(behind: 2);
        File.WriteAllText(Path.Combine(_root, "DIR.Lib", "wip.txt"), "mine");
        Git(Path.Combine(_root, "DIR.Lib"), "add", "wip.txt");
        WithSiblingSync();
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.SiblingBehindKind);
        card.Detail.ShouldBe($"DIR.Lib is 2 commits behind its origin and was not pulled (the main checkout has uncommitted changes): git -C {Path.Combine(_root, "DIR.Lib")} pull --ff-only");
        File.Exists(Path.Combine(_root, "DIR.Lib", "wip.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task ALegStartedWithItsSiblingsUpToDateRaisesNoCard()
    {
        SiblingDirLib(behind: 0);
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ALegStartedInARepoWithoutUseLocalSiblingsRaisesNoCard()
    {
        await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ALegStartsWhenTheSiblingWatchCannotAsk()
    {
        await FlightWithTwoLegs();
        var legs = new LegCommander(_state, _workers, _checks, _github, new GitCli(), new Planes(new GitCli()), _flights,
            new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1"), TimeProvider.System)
        {
            Siblings = new SiblingWatch(new ThrowingGit(), TimeProvider.System),
        };
        File.WriteAllText(Path.Combine(_state.Flights().Single().Worktree, "Directory.Build.props"), UsesDirLib);
        Directory.CreateDirectory(Path.Combine(_root, "DIR.Lib", ".git"));

        (await legs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = "flight/TIA1/parser", Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        _state.Cards().Where(c => c.Kind == LegCommander.SiblingBehindKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ALegStartsAFreshWorkerInThePlaneBriefedFromItAndOnlyInOrder()
    {
        var flight = await FlightWithTwoLegs();

        (await Leg(Verbs.StartLeg, "flight/TIA1/render")).Error.ShouldNotBeNull().ShouldContain("parser comes first");
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        var start = _workers.Started.ShouldHaveSingleItem();
        start.Name.ShouldBe("du1-tia1-parser");
        Path.GetFullPath(start.RepoPath).ShouldBe(Path.GetFullPath(Repo));
        start.Plane.ShouldBe("tia1");
        start.Model.ShouldBe("sonnet");
        start.Brief.ShouldContain("Parse the ephemeris");
        start.Brief.ShouldContain("Leave nothing behind in the plane: every change is committed before you finish");
        start.Brief.ShouldContain("1. [ ] Write the parser");
        start.Brief.ShouldContain("The parser tests pass");
        start.Brief.ShouldContain("reruns `dotnet test`");
        start.Brief.ShouldContain(flight.Branch);
        // A leg fails only for what stops its Done-when (PIL277 failed a finished leg over a capture it could not attach).
        start.Brief.ShouldContain("Fail only for what stops the Done-when");
        (L("parser").State, L("parser").Worker, L("parser").WorkerJob).ShouldBe((LegState.Flying, "du1-tia1-parser", "job00001"));
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Error.ShouldNotBeNull().ShouldContain("a go-around starts it again");
    }

    [Fact]
    public async Task StepsAreTickedByNumberOrTextAndUndone()
    {
        await FlightWithTwoLegs();

        (await Leg(Verbs.Check, "flight/TIA1/parser", "2")).Ok.ShouldBeTrue();
        (await Leg(Verbs.Check, "flight/TIA1/parser", "write")).Ok.ShouldBeTrue();
        L("parser").Checklist.Select(i => i.Done).ShouldBe([true, true]);
        (await Leg(Verbs.Check, "flight/TIA1/parser", "1", "undo")).Ok.ShouldBeTrue();
        L("parser").Checklist.Select(i => i.Done).ShouldBe([false, true]);
        (await Leg(Verbs.Check, "flight/TIA1/parser", "9")).Error.ShouldNotBeNull().ShouldContain("steps 1 to 2");
        (await Leg(Verbs.Check, "flight/TIA1/render", "1")).Error.ShouldNotBeNull().ShouldContain("no checklist");
    }
}

/// <summary>Part 2 of LegCommanderTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class LegCommanderTests2 : LegCommanderTestsBase
{
    [Fact]
    public async Task ALegIsDoneOnlyWithEveryStepTickedAndItsCheckPassingOnARerun()
    {
        var flight = await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Error.ShouldNotBeNull().ShouldContain("steps not ticked: Write the parser; Test it");

        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");
        _checks.Next = new CheckRun(1, "Failed: 2, Passed: 40");
        var failed = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "all good");
        failed.Error.ShouldNotBeNull().ShouldContain("check `dotnet test` failed (1)");
        L("parser").State.ShouldBe(LegState.Flying);
        L("parser").Report.ShouldNotBeNull().ShouldContain("Failed: 2");
        Path.GetFullPath(_checks.Runs.Single().Cwd).ShouldBe(Path.GetFullPath(flight.Worktree));

        _checks.Next = new CheckRun(0, "Passed: 42");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parser in, 42 tests")).Ok.ShouldBeTrue();
        (L("parser").State, L("parser").Report).ShouldBe((LegState.Done, "parser in, 42 tests"));
        _workers.Stopped.ShouldBe(["job00001"]);
        // The next leg may go now.
        (await Leg(Verbs.StartLeg, "flight/TIA1/render")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AWorkerThatFinishedItsOwnLegIsStoppedAfterAGrace()
    {
        await FlightWithTwoLegs();
        var legs = new LegCommander(_state, _workers, _checks, _github, new GitCli(), new Planes(new GitCli()), _flights,
            new NodeOptions("/s", "/d", MachinePrefix: "DU1"), TimeProvider.System) { SelfFinishGrace = TimeSpan.FromMilliseconds(50) };
        Task<CommandResult> Run(string verb, string target, string who, params string[] args) =>
            legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);
        await Run(Verbs.StartLeg, "flight/TIA1/parser", "cli");

        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "du1-tia1-parser", "failed", "no ephemeris here")).Ok.ShouldBeTrue();
        var failed = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (failed.Bin, failed.Kind, failed.Subject, failed.Detail).ShouldBe((CardBin.Ring, "leg-failed", "flight/TIA1/parser", "no ephemeris here"));
        failed.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Go around", Verbs.GoAround), ("Mark done anyway", Verbs.FinishLeg)]);

        _workers.Stopped.ShouldBeEmpty();
        for (var i = 0; i < 100 && _workers.Stopped.Count == 0; i++)
        {
            await Task.Delay(20, Ct);
        }
        _workers.Stopped.ShouldBe(["job00001"]);

        // The controller marks a failed leg done anyway; its worker and the watchtower may not, and only a failed leg is.
        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "du1-tia1-parser", "accepted")).Error.ShouldBe("marking TIA1/parser done anyway is the controller's, not du1-tia1-parser's");
        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "du1-watchtower", "accepted")).Error.ShouldBe("marking TIA1/parser done anyway is the controller's, not du1-watchtower's");
        (await Run(Verbs.FinishLeg, "flight/TIA1/render", "controller", "accepted")).Error.ShouldBe("TIA1/render is planned: only a failed leg is marked done anyway");
        (await Run(Verbs.FinishLeg, "flight/TIA1/parser", "controller", "accepted", "the ephemeris is the issue")).Ok.ShouldBeTrue();
        var parser = _state.Legs("TIA1").Single(l => l.Name == "parser");
        (parser.State, parser.Report).ShouldBe((LegState.Done, "failed, marked done anyway by controller: the ephemeris is the issue\nno ephemeris here"));
        _state.Cards(openOnly: true).ShouldBeEmpty();
        (await Run(Verbs.StartLeg, "flight/TIA1/render", "controller")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AGoAroundStopsTheWorkerAndStartsAFreshOneToldWhyAndWhatThePlaneHolds()
    {
        var flight = await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "The parser's first cut");

        (await Leg(Verbs.GoAround, "flight/TIA1/parser")).Error.ShouldNotBeNull().ShouldContain("needs why");
        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "it", "went", "in", "circles")).Ok.ShouldBeTrue();

        _workers.Stopped.ShouldBe(["job00001"]);
        var fresh = _workers.Started[^1];
        // A go-around clears the leg's failure card, if one rang.
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == "leg-failed");
        fresh.Name.ShouldBe("du1-tia1-parser");
        fresh.Brief.ShouldContain("## Go-around 1");
        fresh.Brief.ShouldContain("it went in circles");
        fresh.Brief.ShouldContain("The parser's first cut");
        fresh.Brief.ShouldContain("parser.cs");
        (L("parser").GoArounds, L("parser").WorkerJob, L("parser").State).ShouldBe((1, "job00002", LegState.Flying));
    }

    [Fact]
    public async Task AFlightMergesOnlyWithEveryLegDoneAndOnTheHeadReviewedThenLands()
    {
        var flight = await FlightWithTwoLegs();
        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldNotBeNull().ShouldContain("legs not done: parser, render");

        foreach (var name in new[] { "parser", "render" })
        {
            await Leg(Verbs.StartLeg, $"flight/TIA1/{name}");
            if (name == "render")
            {
                // A later leg's validator answers before its worker starts.
                await Leg(Verbs.ValidateLeg, $"flight/TIA1/{name}", "go");
            }
            foreach (var i in Enumerable.Range(1, L(name).Checklist.Length))
            {
                await Leg(Verbs.Check, $"flight/TIA1/{name}", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            (await Leg(Verbs.FinishLeg, $"flight/TIA1/{name}", "done")).Ok.ShouldBeTrue();
        }

        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldBe("TIA1 has not been reviewed; open its PR with the node (flight pr), which starts the review");
        // As the reviewer's pass leaves it (its own test drives the review): this one is about the merge's guards.
        _state.PutFlight(_state.Flights().Single() with { Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow } });
        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldBe("flight/tia1 has no PR");
        _github.Pr = new PullRequest(63, "OPEN", "def5678aaaabbbbccccdddd", "https://github.com/x/tianwen/pull/63");
        (await Leg(Verbs.Merge, "flight/TIA1", "abc1234")).Error.ShouldNotBeNull().ShouldContain("not the abc1234 you reviewed");
        (await Leg(Verbs.Merge, "flight/TIA1", "def56")).Error.ShouldNotBeNull().ShouldContain("7 characters");

        // A commit in the plane past the PR's head would be left behind by the merge: refused until it is pushed.
        var pushed = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(flight.Worktree, "late.md"), "a read written after the push\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "late read");
        _github.Pr = new PullRequest(63, "OPEN", pushed, "https://github.com/x/tianwen/pull/63");
        (await Leg(Verbs.Merge, "flight/TIA1", pushed[..8])).Error.ShouldBe("TIA1's plane holds 1 commit past #63's head: open the PR again (flight pr) to push it, and review what is new before it lands");
        var tip = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _github.Pr = new PullRequest(63, "OPEN", tip, "https://github.com/x/tianwen/pull/63");

        (await Leg(Verbs.Merge, "flight/TIA1", tip[..8], "rebase")).Ok.ShouldBeTrue();

        _github.Merged.ShouldBe([(63, tip, "rebase")]);
        _state.Flights().Single().State.ShouldBe(FlightState.Landed);
        Directory.Exists(flight.Worktree).ShouldBeFalse();
    }

    [Fact]
    public async Task AMergeThatOnlyJoinsTheMergeQueueLeavesTheFlightFlyingUntilGitHubMergesIt()
    {
        var tip = await ReadyToMerge();
        _github.Queues = true;

        (await Leg(Verbs.Merge, "flight/TIA1", tip[..8])).Ok.ShouldBeTrue();

        _github.Merged.ShouldBe([(63, tip, "rebase")]);
        // Queued is not merged: landing now would stop its sessions and take its plane while the queue may still drop it.
        _state.Flights().Single().State.ShouldBe(FlightState.Flying);
        Directory.Exists(_state.Flights().Single().Worktree).ShouldBeTrue();
    }

    [Fact]
    public async Task AMergePressedWhileChecksRunWaitsForThemAndTheNodeMergesOnceTheyPass()
    {
        var tip = await ReadyToMerge();
        _github.Checks = ChoiceStatus.Pending;

        // A queue takes no PR whose required checks have not passed (TIA1818's #1826): the merge waits on the flight.
        var asked = await Leg(Verbs.Merge, "flight/TIA1", tip[..8]);
        asked.Ok.ShouldBeTrue();
        _github.Merged.ShouldBeEmpty();
        _state.Flights().Single().MergeWhenGreen.ShouldBe([tip, "rebase"]);
        _state.Flights().Single().State.ShouldBe(FlightState.Flying);

        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await checks.LookAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        _state.Flights().Single().MergeQueue.ShouldBe("merges once its checks pass");

        _github.Checks = ChoiceStatus.Passing;
        await checks.LookAsync(Ct);

        _github.Merged.ShouldBe([(63, tip, "rebase")]);
        _state.Flights().Single().MergeWhenGreen.ShouldBeNull();
        _state.Flights().Single().State.ShouldBe(FlightState.Landed);
    }

    [Fact]
    public async Task AWaitingMergeWhoseChecksFailIsDroppedOnACardThatOffersItAgain()
    {
        var tip = await ReadyToMerge();
        _github.Checks = ChoiceStatus.Pending;
        (await Leg(Verbs.Merge, "flight/TIA1", tip[..8])).Ok.ShouldBeTrue();

        _github.Checks = ChoiceStatus.Failing;
        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        _state.Flights().Single().MergeWhenGreen.ShouldBeNull();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrChecks.MergeDropped);
        card.Detail.ShouldNotBeNull().ShouldContain("checks failed");
        card.Choices.Single().Verb.ShouldBe(Verbs.Merge);
    }

    [Fact]
    public async Task APlaneClaudeCodeToreDownIsCheckedOutAgainOnItsBranchBeforeTheNextWorker()
    {
        var flight = await FlightWithTwoLegs();
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "The parser");
        // As Claude Code leaves a -w worktree whose job ended by itself: unregistered, its folder still there.
        Git(Repo, "worktree", "remove", "--force", flight.Worktree);
        Directory.CreateDirectory(flight.Worktree);
        File.WriteAllText(Path.Combine(flight.Worktree, "leftover.tmp"), "held\n");

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();

        Git(flight.Worktree, "rev-parse", "--abbrev-ref", "HEAD").ShouldBe(flight.Branch);
        File.Exists(Path.Combine(flight.Worktree, "parser.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(flight.Worktree, "leftover.tmp")).ShouldBeFalse();
    }

    [Fact]
    public async Task AFolderGitLeavesAfterRemovingThePlaneGoesToo()
    {
        var flight = await FlightWithTwoLegs();

        (await new Planes(new FolderKeepingGit()).RemoveAsync(Repo, flight.Worktree, Ct)).ShouldBeNull();

        Directory.Exists(flight.Worktree).ShouldBeFalse();
    }

    [Fact]
    public async Task APlaneWhoseGitFileWentIsPrunableAndStillLands()
    {
        var flight = await FlightWithTwoLegs();
        // Listed by git, marked prunable: the folder's .git file is gone, as a half torn-down worktree leaves it.
        File.Delete(Path.Combine(flight.Worktree, ".git"));
        Git(Repo, "worktree", "list", "--porcelain").ShouldContain("prunable");

        (await new Planes(new GitCli()).IsRegisteredAsync(Repo, flight.Worktree, Ct)).ShouldBeFalse();
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget("TIA1"), Who = "tests" }, Ct)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task NoLegFinishesAndNoFlightLandsWithItsWorkLeftWhereNobodyLooks()
    {
        var flight = await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        foreach (var i in Enumerable.Range(1, L("parser").Checklist.Length))
        {
            await Leg(Verbs.Check, "flight/TIA1/parser", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // A file not committed: the leg is not done until it is committed or dropped.
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Error.ShouldBe("TIA1/parser's plane has 1 file not committed (?? parser.cs): commit them (or drop them), then finish");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the parser");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Ok.ShouldBeTrue();

        // That commit is on no remote and not in main: landing by hand would leave it on a branch nobody looks at.
        var land = await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget("TIA1"), Who = "tests" }, Ct);
        land.Error.ShouldNotBeNull().ShouldContain("TIA1's plane holds 1 commit on no remote and not in");
        land.Error.ShouldContain("the parser");
        Directory.Exists(flight.Worktree).ShouldBeTrue();
        _state.Flights().Single().State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task ALegDoneStartsTheNextAndTheLastOpensThePrHeldOnlyByASpentWindow()
    {
        await FlightWithTwoLegs();
        _legs.Models = new RoleModels(null);
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        foreach (var i in Enumerable.Range(1, L("parser").Checklist.Length))
        {
            await Leg(Verbs.Check, "flight/TIA1/parser", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // The 5-hour window spent past the start limit: the next leg waits, and says so once.
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = 95, ResetsAt = DateTimeOffset.UtcNow.AddHours(1) } });
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Ok.ShouldBeTrue();
        L("render").State.ShouldBe(LegState.Planned);
        var waits = _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind).ShouldHaveSingleItem();
        (waits.Kind, waits.Bin).ShouldBe((LegCommander.NextStepKind, CardBin.Handled));
        waits.Title.ShouldStartWith("TIA1 waits to start render: the 5-hour window is 95 % spent");
        await _legs.AdvanceAllAsync(Ct);
        _state.Cards().Count(c => c.Kind == LegCommander.NextStepKind).ShouldBe(1);

        // A priority repo has runway: its flights go on to 98 %, so at the same 95 % the sweep starts this one.
        var repo = _state.Flights().Single().Repo;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), "priority-repos", [repo]).Settings);
        (_legs.Models.StartLimitFor(repo), _legs.Models.StartLimitFor("other")).ShouldBe((98.0, 90.0));
        await _legs.AdvanceAllAsync(Ct);
        L("render").Validation.ShouldBe(LegValidation.Pending);
        (await Leg(Verbs.ValidateLeg, "flight/TIA1/render", "go")).Ok.ShouldBeTrue();
        L("render").State.ShouldBe(LegState.Flying);

        // The last leg done: its PR is opened by itself. This repo has no origin to push to, so the step fails: it rings once,
        // offering the step by hand, and the next sweep does not ring again. (ReviewTests opens one for real, on a bare origin.)
        (await Leg(Verbs.FinishLeg, "flight/TIA1/render", "done")).Ok.ShouldBeTrue();
        var rung = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.NextStepKind);
        (rung.Bin, rung.Title).ShouldBe((CardBin.Ring, "TIA1 could not open its PR by itself"));
        rung.Choices.Single().Verb.ShouldBe(Verbs.OpenPr);
        await _legs.AdvanceAllAsync(Ct);
        _state.Cards(openOnly: true).Count(c => c.Kind == LegCommander.NextStepKind).ShouldBe(1);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASweepWhileAValidatorsGoStartsTheLegRingsNothing()
    {
        await FlightWithTwoLegs();
        _legs.Models = new RoleModels(null);
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        foreach (var i in Enumerable.Range(1, L("parser").Checklist.Length))
        {
            await Leg(Verbs.Check, "flight/TIA1/parser", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Ok.ShouldBeTrue();
        L("render").Validation.ShouldBe(LegValidation.Pending);

        // The go starts render's worker, which takes its time to come up: render reads planned and validated meanwhile, and a
        // sweep in that gap asks to start it too (TIA1818's web-header, DE1 2026-10-25).
        var up = new TaskCompletionSource();
        _workers.Hold = up.Task;
        var go = Leg(Verbs.ValidateLeg, "flight/TIA1/render", "go");
        while (L("render").Validation != LegValidation.Go && !go.IsCompleted)
        {
            await Task.Yield();
        }
        var sweep = _legs.AdvanceAllAsync(Ct);
        up.SetResult();
        (await go).Ok.ShouldBeTrue();
        await sweep;

        L("render").State.ShouldBe(LegState.Flying);
        _state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.NextStepKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task APlaneAlreadyTornDownStillLands()
    {
        var flight = await FlightWithTwoLegs();
        Git(Repo, "worktree", "remove", "--force", flight.Worktree);

        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget("TIA1"), Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        _state.Flights().Single().State.ShouldBe(FlightState.Landed);
        Git(Repo, "branch", "--list", flight.Branch).ShouldContain(flight.Branch);
    }

    [Fact]
    public void ALegInAnOrgWithAUiChecklistIsToldToWorkByItAndShowACapture()
    {
        var root = Directory.CreateTempSubdirectory("pilot-org-").FullName;
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(root, "tianwen")).FullName;
            var flight = new FlightRow { Code = "TIA1", Repo = "tianwen", RepoPath = repo, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(repo, "w"), Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch };
            var leg = new LegRow { Flight = "TIA1", Name = "header", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned, Checklist = [new CheckItem { Text = "a" }] };

            // No checklist beside the clones: nothing said.
            Briefs.For(flight, leg, [leg], cli: "pilot").ShouldNotContain("UI checklist");

            var checklist = Path.Combine(Directory.CreateDirectory(Path.Combine(root, ".github", "docs")).FullName, "ui-changes.md");
            File.WriteAllText(checklist, "# UI changes\n");
            var brief = Briefs.For(flight, leg, [leg], cli: "pilot");
            brief.ShouldContain($"Read the UI checklist, {checklist}");
            brief.ShouldContain("`pilot artifact <path> <title>`");
            // The artifact is the capture's whole delivery: no PNG committed or pushed for the PR.
            brief.ShouldContain("The recorded artifact is the capture's whole delivery");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ANodeOnItsOwnSocketTellsItsWorkersCliWhichOne()
    {
        var flight = new FlightRow { Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch };
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned, Checklist = [new CheckItem { Text = "a" }] };

        Briefs.For(flight, leg, [leg], cli: @"C:\Program Files\pilot\pilot.exe", socket: @"D:\sockets\pilot.sock")
            .ShouldContain(@"`""C:\Program Files\pilot\pilot.exe"" --node-socket ""D:\sockets\pilot.sock"" check <step number>`");
        Briefs.For(flight, leg, [leg], cli: "pilot").ShouldContain("`pilot check <step number>`");
        // Claude Code refuses, in a -w session, a pilot run wrapped in a shell and a git in a loop: the brief says so up front.
        var brief = Briefs.For(flight, leg, [leg], cli: "pilot");
        brief.ShouldContain("never wrap it in `pwsh` or `bash -c`");
        brief.ShouldContain("`pilot check 1 && pilot check 2`");
    }

    [Fact]
    public async Task AWorkersRowIsMeasuredAgainstItsLegsBudget()
    {
        await FlightWithTwoLegs();
        using var profile = new TempProfile();
        var agents = new FakeClaudeAgents { Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "du1-tia1-parser", "working", pid: 4343, status: "busy")) };
        var tracker = new SessionTracker(_state, agents, profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);

        await tracker.PollAsync(Ct);

        var row = _state.Sessions().Single();
        (row.Role, row.Flight, row.Leg, row.Budget).ShouldBe((SessionRole.Worker, "TIA1", "parser", (long?)4_000_000));
    }

    [Fact]
    public void AWorkerStartsInTheBackgroundInAutoModeWithItsModelAndEffort() =>
        ClaudeWorkerLauncher.Arguments(new WorkerStart("du1-tia1-parser", "/r/tianwen", "tia1", "brief", "sonnet", "high"))
            .ShouldBe(["--bg", "-w", "tia1", "-n", "du1-tia1-parser", "--permission-mode", "auto", "--strict-mcp-config", "--model", "sonnet", "--effort", "high"]);

    [Fact]
    public void TheWatchtowerStartsInItsOwnFolderWithRemoteControlOn() =>
        ClaudeWorkerLauncher.Arguments(new WorkerStart("du1-watchtower", "/data/watchtower", null, "brief", null, null, RemoteControl: true))
            .ShouldBe(["--bg", "--remote-control", "-n", "du1-watchtower", "--permission-mode", "auto", "--strict-mcp-config"]);
}
