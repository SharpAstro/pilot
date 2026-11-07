using Pilot.Node.Flights;
using Shouldly;

namespace Pilot.Node.Tests;

/// <summary>
/// The git a test class's repo runs on (#813). By default the in-memory <see cref="FakeRepoGit"/>: a git run is 100 to 400 ms on Windows and
/// the tests that check what the node decides make dozens each, which made the Slow step 19 minutes there and 3 on Linux. A test, or a whole
/// class, that checks what git itself does (a rebase, a conflict someone resolves, a second clone, a hook) asks for real git: the class by
/// its constructor, a test by <see cref="UseReal"/> before it has used the repo. <c>PILOT_TEST_REAL_GIT=1</c> runs everything on real git, which is
/// the fidelity check of the model and the "before" of a timing.
/// </summary>
internal sealed class TestGit : IGit
{
    private readonly Action _makeReal;
    private IGit _inner;
    private FakeRepoGit? _fake;

    /// <param name="root">The test's folder; the model's repo and origin are made in it.</param>
    /// <param name="realGit">The class asks for real git.</param>
    /// <param name="makeReal">Makes the repo in <paramref name="root"/> with git itself: what the test base did before the model.</param>
    public TestGit(string root, bool realGit, Action makeReal, bool withOrigin = true, bool withSolution = true)
    {
        _makeReal = makeReal;
        if (realGit || Environment.GetEnvironmentVariable("PILOT_TEST_REAL_GIT") == "1")
        {
            makeReal();
            _inner = new GitCli();
        }
        else
        {
            _inner = _fake = new FakeRepoGit(root, withOrigin, withSolution);
        }
    }

    /// <summary>Whether this test runs on git itself.</summary>
    public bool IsReal => _fake is null;

    /// <summary>The model this test runs on; null on real git.</summary>
    public FakeRepoGit? Fake => _fake;

    /// <summary>
    /// Moves this test to real git: for a test, or a case of a theory, in a class that otherwise checks what the node decides. Called first,
    /// before the test has used the repo: the model's folders are replaced by the real repo.
    /// </summary>
    public void UseReal()
    {
        if (_fake is not { } fake)
        {
            return;
        }
        fake.DeleteFolders();
        _makeReal();
        _inner = new GitCli();
        _fake = null;
    }

    /// <summary>What the model says to <paramref name="args"/> run in <paramref name="cwd"/>, as a test's own setup helper (<c>Git(Repo, "add", ".")</c>) prints it; null when no model owns the folder and real git is to be asked.</summary>
    public static string? Answer(string cwd, string[] args)
    {
        if (FakeRepoGit.Owning(cwd) is not { } fake)
        {
            return null;
        }
        var run = fake.Run(args, cwd);
        return run.Ok ? run.Output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {run.Error}{run.Output}");
    }

    /// <summary>Lets go of the model at the end of the test.</summary>
    public void Release() => _fake?.Release();

    /// <summary>Fails the test when the node asked the model something it cannot answer, which the node's own catch may have swallowed.</summary>
    public void Verify() =>
        (_fake?.Unsupported ?? []).ShouldBeEmpty("this test needs real git: call UseReal() first, or pass realGit: true to the base class");

    public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) => _inner.RunAsync(args, cwd, cancellationToken);

    public Task<GitRun> RunSmudgingAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) => _inner.RunSmudgingAsync(args, cwd, cancellationToken);
}
