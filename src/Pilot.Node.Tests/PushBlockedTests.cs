using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A leg whose work is done and whose check passes, but whose rebased branch cannot go up plain, gets its own card with two ways up (PIL474).</summary>
public sealed class PushBlockedTests : IDisposable
{
    private const string Branch = "flight/tia9";
    private const string Target = "flight/TIA9/parser";
    private const string Worker = "du1-tia9-parser";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2blocked-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeChecks _checks = new();
    private readonly LegCommander _legs;
    private readonly string _origin;
    private readonly string _head;

    public PushBlockedTests()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "-q", "--bare", "-b", "main", "remote.git");
        Git(_root, "clone", "-q", Remote, "plane");
        Git(Plane, "config", "user.email", "t@example.com");
        Git(Plane, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(Plane, "shared.txt"), "one\n");
        Commit(Plane, "first");
        Git(Plane, "push", "-q", "origin", "main");
        Git(Plane, "checkout", "-q", "-b", Branch);
        File.WriteAllText(Path.Combine(Plane, "mine.txt"), "mine\n");
        Commit(Plane, "mine");
        Git(Plane, "push", "-q", "-u", "origin", Branch);
        _origin = Git(Plane, "rev-parse", "HEAD");
        Git(_root, "clone", "-q", "-b", Branch, Remote, "other");
        // The plane rewrites what it pushed (a rebase does): origin's branch is no longer an ancestor of its head.
        Git(Plane, "commit", "-q", "--amend", "-m", "mine, rewritten", "--date=2001-01-01T00:00:00");
        _head = Git(Plane, "rev-parse", "HEAD");

        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), _checks, new FakeGitHub(), git, new Planes(git), flights, options, TimeProvider.System);
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = Plane, Issue = 9, Branch = Branch, Worktree = Plane, Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        _state.PutLeg(new LegRow
        {
            Flight = "TIA9", Name = "parser", Order = 1, Goal = "Parse", DoneWhen = "Tests pass", Check = "dotnet test", State = LegState.Flying, Worker = Worker,
        });
    }

    private string Remote => Path.Combine(_root, "remote.git");

    private string Plane => Path.Combine(_root, "plane");

    private string Other => Path.Combine(_root, "other");

    private Task<CommandResult> Run(string who, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = Target, Args = args, Who = who }, Ct);

    private async Task<CardRow> Blocked()
    {
        (await Run(Worker, "failed", "cannot push: origin has the old commit")).Ok.ShouldBeTrue();
        return _state.Cards(openOnly: true).ShouldHaveSingleItem();
    }

    private LegRow Parser => _state.Legs("TIA9").Single();

    [Fact]
    public async Task ARebasedBranchWithAPassingCheckRaisesAPushBlockedCardNamingTheForcePush()
    {
        var card = await Blocked();

        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, LegCommander.PushBlockedKind, Target));
        card.Title.ShouldBe("TIA9/parser is done, but its rebased branch needs a force push with lease or a merge");
        card.Detail.ShouldNotBeNull().Split('\n')[0].ShouldBe($"Check passed at {_head[..8]}, origin/{Branch} at {_origin[..8]} is not an ancestor of it, so a plain push is refused. Nothing was pushed or overwritten.");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Push the rebased branch with lease", Verbs.FinishLeg), ($"Merge origin/{Branch} into the plane and push", Verbs.FinishLeg)]);
        card.Choices.ShouldNotContain(c => c.Label.Contains("Mark done anyway") || c.Label.Contains("Accept"));
        Git(Remote, "rev-parse", Branch).ShouldBe(_origin);
        Parser.State.ShouldBe(LegState.Failed);
    }

    [Fact]
    public async Task AFailedLegWhoseCheckFailsGetsTheOrdinaryCardNotThePushBlockedOne()
    {
        _checks.Next = new CheckRun(1, "boom");

        (await Run(Worker, "failed", "the parser is wrong")).Ok.ShouldBeTrue();

        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Kind.ShouldBe("leg-failed");
        card.Title.ShouldBe("TIA9/parser stopped: the parser is wrong");
    }

    [Fact]
    public async Task PushWithLeaseRerunsTheCheckPushesWithTheLeaseOnOriginsShaAndFinishesTheLeg()
    {
        var card = await Blocked();
        var runs = _checks.Runs.Count;
        var push = card.Choices.Single(c => c.Label == "Push the rebased branch with lease");
        push.Args.ShouldBe([LegCommander.PushedWithLease, _origin]);

        (await Run("controller", push.Args)).Error.ShouldBeNull();

        Git(Remote, "rev-parse", Branch).ShouldBe(_head);
        _checks.Runs.Count.ShouldBe(runs + 1);
        Parser.State.ShouldBe(LegState.Done);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.PushBlockedKind);
    }

    [Fact]
    public async Task PushWithLeaseNeverOverwritesACommitPushedAfterTheCardWasRaised()
    {
        var card = await Blocked();
        File.WriteAllText(Path.Combine(Other, "theirs.txt"), "theirs\n");
        Commit(Other, "theirs");
        Git(Other, "push", "-q", "origin", Branch);
        var theirs = Git(Other, "rev-parse", "HEAD");

        var result = await Run("controller", card.Choices[0].Args);

        result.Error.ShouldNotBeNull().ShouldContain("could not push");
        Git(Remote, "rev-parse", Branch).ShouldBe(theirs);
        Parser.State.ShouldBe(LegState.Failed);
    }

    [Fact]
    public async Task MergingOriginsBranchPushesPlainKeepsItsCommitsAndFinishesTheLeg()
    {
        var card = await Blocked();
        var merge = card.Choices.Single(c => c.Label == $"Merge origin/{Branch} into the plane and push");

        (await Run("controller", merge.Args)).Error.ShouldBeNull();

        var pushed = Git(Remote, "rev-parse", Branch);
        pushed.ShouldBe(Git(Plane, "rev-parse", "HEAD"));
        Git(Remote, "merge-base", "--is-ancestor", _origin, Branch).ShouldBe("");
        Git(Remote, "merge-base", "--is-ancestor", _head, Branch).ShouldBe("");
        Parser.State.ShouldBe(LegState.Done);
    }

    [Fact]
    public async Task AMergeThatConflictsSendsTheLegRoundAgainNamingTheConflict()
    {
        var card = await Blocked();
        // Origin's mine.txt and the plane's disagree: merging them conflicts.
        Git(Other, "pull", "-q", "origin", Branch);
        File.WriteAllText(Path.Combine(Other, "mine.txt"), "theirs differs\n");
        Commit(Other, "theirs differs");
        Git(Other, "push", "-q", "origin", Branch);
        File.WriteAllText(Path.Combine(Plane, "mine.txt"), "mine differs\n");
        Commit(Plane, "mine differs");

        var result = await Run("controller", card.Choices[1].Args);

        result.Error.ShouldBeNull();
        Parser.State.ShouldBe(LegState.Flying);
        Parser.GoArounds.ShouldBe(1);
        Git(Plane, "status", "--porcelain").ShouldBe("");
    }

    [Theory]
    [InlineData(Worker)]
    [InlineData("du1-watchtower")]
    public async Task TheNewOutcomesAreRefusedToAWorkerAndTheWatchtower(string who)
    {
        var card = await Blocked();

        foreach (var choice in card.Choices)
        {
            (await Run(who, choice.Args)).Error.ShouldNotBeNull().ShouldContain("controller's");
        }
        Git(Remote, "rev-parse", Branch).ShouldBe(_origin);
        Parser.State.ShouldBe(LegState.Failed);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static void Commit(string cwd, string message)
    {
        Git(cwd, "add", ".");
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_COMMITTER_EMAIL"] = "t@example.com";
        start.Environment["GIT_AUTHOR_NAME"] = "t";
        start.Environment["GIT_AUTHOR_EMAIL"] = "t@example.com";
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
}
