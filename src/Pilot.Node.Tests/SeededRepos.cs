using System.Diagnostics;
using Pilot.Node.Flights;
using Shouldly;

namespace Pilot.Node.Tests;

/// <summary>
/// The repos an idea, issue or milestone test files into (#952): one or several, each with a first commit, optionally with a GitHub-looking
/// remote and a local bare origin that a fetch reaches through an <c>insteadOf</c> rewrite (#462). One copy of the setup the twelve
/// classes each carried as a private <c>Git(...)</c> helper. By default the setup git is answered by the in-memory <see cref="FakeRepoGit"/>
/// (through <see cref="TestGit.Answer"/>) and the node under test gets it as its <see cref="Git"/>; real git runs only under
/// <c>PILOT_TEST_REAL_GIT=1</c> or when the class passes <c>realGit: true</c>, for a test about git itself.
/// </summary>
internal sealed class SeededRepos : IDisposable
{
    private readonly string _root;

    /// <param name="root">The test's folder; every repo, plane and origin lives under it.</param>
    /// <param name="realGit">The class asks for real git.</param>
    public SeededRepos(string root, bool realGit = false)
    {
        _root = root;
        TestEnvironment.OwnFolder(root);
        if (realGit || Environment.GetEnvironmentVariable("PILOT_TEST_REAL_GIT") == "1")
        {
            Directory.CreateDirectory(root);
            Git = new GitCli();
        }
        else
        {
            Model = FakeRepoGit.Empty(root);
            Git = Model;
        }
    }

    /// <summary>The git the node under test runs on: the model, or git itself.</summary>
    public IGit Git { get; }

    /// <summary>The model; null on real git.</summary>
    public FakeRepoGit? Model { get; }

    /// <summary>Whether this test runs on git itself.</summary>
    public bool IsReal => Model is null;

    /// <summary>The planes the node under test works through, on <see cref="Git"/>.</summary>
    public Planes Planes() => new(Git);

    /// <summary>
    /// A repo <c>root/&lt;name&gt;</c> with one commit of <c>README.md</c> and any <paramref name="files"/> (path to content).
    /// <paramref name="remoteUrl"/> is the origin remote it names (a GitHub URL, which no model serves); with <paramref name="withOrigin"/> a bare copy
    /// under <c>root/origins</c> is made and fetched from instead of GitHub, and origin's HEAD is set.
    /// </summary>
    public string Seed(string name, string? remoteUrl = null, bool withOrigin = false, IReadOnlyDictionary<string, string>? files = null)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        Run(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        foreach (var (path, content) in files ?? new Dictionary<string, string>())
        {
            var file = Path.Combine(repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }
        if (remoteUrl is not null)
        {
            Run(repo, "remote", "add", "origin", remoteUrl);
        }
        Run(repo, "add", ".");
        Run(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        if (withOrigin)
        {
            if (remoteUrl is null)
            {
                throw new ArgumentException("an origin that a fetch reaches needs the remote url it stands in for", nameof(remoteUrl));
            }
            var bare = Path.Combine(_root, "origins", name + ".git");
            Run(_root, "clone", "-q", "--bare", repo, bare);
            Run(repo, "config", $"url.{bare.Replace('\\', '/')}.insteadOf", remoteUrl);
            Run(repo, "fetch", "-q", "origin");
            Run(repo, "remote", "set-head", "origin", "main");
        }
        return repo;
    }

    /// <summary>A git command of the test's setup: answered by the model when it owns the folder, else by git.</summary>
    public string Run(string cwd, params string[] args) => TestGit.Answer(cwd, args) ?? RunReal(cwd, args);

    private static string RunReal(string cwd, string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = TestEnvironment.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    /// <summary>Lets go of the model, removes the test's folder, and fails the test when the node asked the model something it cannot answer.</summary>
    public void Dispose()
    {
        if (Model is null)
        {
            // Planes the node made leave worktree entries a prune clears; real git only, the model has none to clear.
            foreach (var repo in Directory.Exists(_root) ? Directory.EnumerateDirectories(_root) : [])
            {
                if (Directory.Exists(Path.Combine(repo, ".git")))
                {
                    try
                    {
                        RunReal(repo, ["worktree", "prune"]);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }
        Model?.Release();
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            TestDirs.DeleteTree(_root);
        }
        (Model?.Unsupported ?? []).ShouldBeEmpty("this test needs real git: pass realGit: true to SeededRepos");
    }
}
