using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The planner's overlap check across nodes (#375): <c>touches</c> compares the files a flight's legs will change with every other
/// flight not landed on this node and on each reachable peer (by its pushed branch) and with every open PR, and says fold, connect or parallel
/// for each, or unknown where it cannot tell.
/// </summary>
public sealed class TouchesTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string GitHub = "SharpAstro/tianwen";

    private static readonly string[] Mine = ["src/DetachedShell.cs", "src/Parser.cs", "src/Lexer.cs", "src/JobRunner.cs"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeFleet _fleet = new();
    private readonly FakeGitHub _github = new();
    private readonly LegCommander _legs;

    public TouchesTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        // The checkout's remote is GitHub's, as a peer's is told apart by it; the fetch comes from a bare copy of its own.
        var bare = Path.Combine(_root, "origin.git");
        Git(_root, "clone", "-q", "--bare", Repo, bare);
        Git(Repo, "remote", "add", "origin", $"https://github.com/{GitHub}.git");
        Git(Repo, "config", $"url.{bare.Replace('\\', '/')}.insteadOf", $"https://github.com/{GitHub}.git");
        Git(Repo, "fetch", "-q", "origin");
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), TimeProvider.System) { Machine = "SE1", Fleet = _fleet };
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "SE1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), _github, git, new Planes(git), flights, options, TimeProvider.System);
        _state.PutFlight(Row("PIL375", 375, "flight/pil375", Repo));
    }

    private string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
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

    // A branch with one commit changing these files, pushed to the origin the check fetches (or left unpushed, local only).
    private void Branch(string name, bool push, params string[] files)
    {
        Git(Repo, "checkout", "-q", "-b", name, "main");
        foreach (var file in files)
        {
            var path = Path.Combine(Repo, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name + "\n");
            Git(Repo, "add", file);
        }
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", name);
        if (push)
        {
            Git(Repo, "push", "-q", "origin", name);
        }
        Git(Repo, "checkout", "-q", "main");
    }

    private static FlightRow Row(string code, int issue, string branch, string repoPath, FlightState state = FlightState.Flying) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = repoPath, Issue = issue, Branch = branch, Worktree = $"{repoPath}/.claude/worktrees/{code.ToLowerInvariant()}",
        Base = "origin/main", State = state, CreatedAt = DateTimeOffset.UtcNow,
    };

    private static FakePeer Peer(string name, params FlightRow[] flights) => new(name)
    {
        Flights = flights,
        Repos = [new RepoInfo { Name = "tianwen", Path = "D:/src/tianwen", GitHub = GitHub }],
    };

    private static PullRequestStatus Pr(int number, string title, string head) => new() { Number = number, Title = title, Head = head };

    private Task<CommandResult> Touches(params string[] files) =>
        _legs.RunAsync(new CommandRequest { Verb = Verbs.Touches, Target = "flight/PIL375", Args = files, Who = "tests" }, Ct);

    [Fact]
    public async Task AnswersALinePerFlightOnThisNodeAndOnAPeerAndPerOpenPrWithTheSharedFilesAndAChoice()
    {
        // This node: one sharing a file (connect), one sharing none (parallel), one landed (not listed).
        _state.PutFlight(Row("PIL294", 294, "flight/pil294", Repo));
        Branch("flight/pil294", push: true, "src/DetachedShell.cs", "src/Other.cs");
        _state.PutFlight(Row("PIL302", 302, "flight/pil302", Repo));
        Branch("flight/pil302", push: true, "docs/notes.md");
        _state.PutFlight(Row("PIL100", 100, "flight/pil100", Repo, FlightState.Landed));
        Branch("flight/pil100", push: true, "src/Parser.cs");
        // A peer: one sharing most of the files (fold), one not pushed (unknown).
        Branch("flight/pil300", push: true, "src/Parser.cs", "src/Lexer.cs", "src/JobRunner.cs");
        _fleet.Peers.Add(Peer("DE1", Row("PIL300", 300, "flight/pil300", "D:/src/tianwen"), Row("PIL301", 301, "flight/pil301", "D:/src/tianwen")));
        // Open PRs: a stranger's sharing a file, a flight's own PR (already compared by its branch), one gh cannot read.
        _github.OpenPrs[Repo] = [Pr(7, "Fix lexer", "fix-lexer"), Pr(8, "PIL294", "flight/pil294"), Pr(9, "Big refactor", "refactor")];
        _github.PrFiles[7] = ["src/Lexer.cs", "src/Other.cs"];
        _github.PrFiles[8] = ["src/DetachedShell.cs", "src/Other.cs"];
        _github.PrFilesFail.Add(9);

        var result = await Touches(Mine);

        result.Ok.ShouldBeTrue(result.Error);
        var lines = result.Made.ShouldNotBeNull().Split('\n');
        lines.ShouldBe(
        [
            // Folds first, then connects, then what is not known, then parallel.
            "PIL300 (DE1): fold, both change src/JobRunner.cs, src/Lexer.cs, src/Parser.cs (3 of your 4 files); it flies on another node, so it cannot be folded into from here: flight connect PIL375 PIL300 holds PIL375 until that node says it landed (or flight overlap PIL375 PIL300 file=src/JobRunner.cs file=src/Lexer.cs file=src/Parser.cs <why> to ask the controller whether to connect or plan in parallel), or plan beside it when the shared files are only touched in passing",
            "PIL294 (SE1): connect, both change src/DetachedShell.cs; flight connect PIL375 PIL294 so PIL375 branches off main once it has landed, or plan beside it when the shared files are only touched in passing",
            "PR #7 \"Fix lexer\": connect, both change src/Lexer.cs; an open PR is no flight to fold into or connect to: name it and the shared files in your planned summary, and plan the legs on those files to follow its merge",
            "PIL301 (DE1): unknown, its branch flight/pil301 is not pushed, so what it changes is not known",
            "PR #9 \"Big refactor\": unknown, gh could not list the files of PR #9: offline",
            "PIL302 (SE1): parallel, no file in common",
        ]);
    }

    [Fact]
    public async Task AFlightOfThisNodeWithTheSameFilesIsOfferedAFoldWithTheWaysToAnswerIt()
    {
        _state.PutFlight(Row("PIL294", 294, "flight/pil294", Repo));
        Branch("flight/pil294", push: true, "src/Parser.cs", "src/Lexer.cs");

        var result = await Touches(Mine);

        result.Made.ShouldBe(
            "PIL294 (SE1): fold, both change src/Lexer.cs, src/Parser.cs (2 of your 4 files); when it is the same goal, flight overlap PIL375 PIL294 file=src/Lexer.cs file=src/Parser.cs <why>; when it is another goal, flight connect PIL375 PIL294");
    }

    [Fact]
    public async Task AFlightOfThisNodeNotPushedIsReadFromItsLocalBranch()
    {
        _state.PutFlight(Row("PIL294", 294, "flight/pil294", Repo));
        Branch("flight/pil294", push: false, "src/Parser.cs");

        var result = await Touches(Mine);

        result.Made.ShouldNotBeNull().ShouldStartWith("PIL294 (SE1): connect, both change src/Parser.cs");
    }

    [Fact]
    public async Task APeerThatCannotBeReachedOrHoldsNoGrantIsListedAsUnknownNotLeftOut()
    {
        _fleet.Peers.Add(new FakePeer("DE1") { Unreachable = true });
        _fleet.Peers.Add(new FakePeer("DE2") { HasGrantHeld = false });

        var result = await Touches(Mine);

        var lines = result.Made.ShouldNotBeNull().Split('\n');
        lines.ShouldContain("de1.local:47731: unknown, this node did not answer (connection refused), so its flights are not known");
        lines.ShouldContain("de2.local:47731: unknown, no grant is held for this node, so its flights are not known");
    }

    [Fact]
    public async Task WithNoOtherFlightAndNoOpenPrItSaysSoAndSendsTheFlightParallel()
    {
        var result = await Touches(Mine);

        result.Made.ShouldBe("no other flight in the air and no open PR in tianwen: nothing to fold or connect, fly in parallel");
    }

    [Fact]
    public async Task WithNoFilesItIsRefusedSayingHowToAskAndAFlightThatLandedHasNothingToCheck()
    {
        (await Touches()).Error.ShouldNotBeNull().ShouldContain("touches needs the files PIL375's legs will change");

        _state.PutFlight(Row("PIL375", 375, "flight/pil375", Repo, FlightState.Landed));
        (await Touches(Mine)).Error.ShouldBe("PIL375 has landed; it overlaps nothing now");
    }

    /// <summary>The choice itself is pure: a file in common makes a connect, a large share of them a fold, none a parallel.</summary>
    public sealed class Choice
    {
        [Fact]
        public void NoFileInCommonIsParallel()
        {
            var judged = OverlapChoice.Judge(["a.cs", "b.cs"], ["c.cs", "d.cs"]);

            judged.Pick.ShouldBe(OverlapPick.Parallel);
            judged.Shared.ShouldBeEmpty();
        }

        [Fact]
        public void SomeFilesInCommonAreAConnect()
        {
            var judged = OverlapChoice.Judge(["a.cs", "b.cs", "c.cs", "d.cs"], ["d.cs", "x.cs", "y.cs", "z.cs"]);

            judged.Pick.ShouldBe(OverlapPick.Connect);
            judged.Shared.ShouldBe(["d.cs"]);
        }

        [Fact]
        public void ALargeShareOfTheFilesIsAFoldWhateverTheSideItIsSeenFrom()
        {
            // Half of the smaller side, and at least two files.
            OverlapChoice.Judge(["a.cs", "b.cs", "c.cs", "d.cs"], ["a.cs", "b.cs", "x.cs", "y.cs", "z.cs", "w.cs"]).Pick.ShouldBe(OverlapPick.Fold);
            OverlapChoice.Judge(["a.cs", "b.cs", "x.cs", "y.cs", "z.cs", "w.cs"], ["a.cs", "b.cs", "c.cs", "d.cs"]).Pick.ShouldBe(OverlapPick.Fold);
            // A plan inside a much larger change is as much a fold.
            OverlapChoice.Judge(["a.cs", "b.cs"], ["a.cs", "b.cs", "c.cs", "d.cs", "e.cs", "f.cs"]).Pick.ShouldBe(OverlapPick.Fold);
        }

        [Fact]
        public void OneSharedFileIsNeverAFoldHoweverSmallTheSets()
        {
            OverlapChoice.Judge(["a.cs"], ["a.cs"]).Pick.ShouldBe(OverlapPick.Connect);
            OverlapChoice.Judge(["a.cs", "b.cs"], ["a.cs", "c.cs", "d.cs"]).Pick.ShouldBe(OverlapPick.Connect);
        }

        [Fact]
        public void PathsAreComparedAsGitWritesThemIgnoringCaseSlashesAndRepeats()
        {
            var judged = OverlapChoice.Judge(["src\\Parser.cs", "./src/Lexer.cs", "src/Lexer.cs"], ["SRC/parser.cs", "src/Lexer.cs"]);

            judged.Pick.ShouldBe(OverlapPick.Fold);
            judged.Shared.ShouldBe(["src/Lexer.cs", "src/Parser.cs"]);
        }
    }

    private sealed class FakeFleet : IFleet
    {
        public List<IFleetPeer> Peers { get; } = [];

        IReadOnlyList<IFleetPeer> IFleet.Peers() => [.. Peers];
    }

    private sealed class FakePeer(string name) : IFleetPeer
    {
        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasGrantHeld { get; init; } = true;

        public bool HasGrant => HasGrantHeld;

        public bool Unreachable { get; init; }

        public FlightRow[] Flights { get; init; } = [];

        public RepoInfo[] Repos { get; init; } = [];

        private void Reach()
        {
            if (Unreachable)
            {
                throw new HttpRequestException("connection refused");
            }
        }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
                DataFolder = "/data", SocketPath = "/sock", Stream = "s",
            });
        }

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult<RepoInfo[]?>(Repos);
        }

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new FleetLoad(null, Flights));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a peer is only read here, never sent to");
    }
}
