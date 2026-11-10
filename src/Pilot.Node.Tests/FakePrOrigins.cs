namespace Pilot.Node.Tests;

/// <summary>
/// The repos <see cref="PrOrigins"/> and PrFixTestsBase's seed build with real git, built in a <see cref="FakeRepoGit"/> instead, so building them starts no
/// git process (leg 2 of PIL933): bare origins <c>github/widget.git</c> and <c>github/gadget.git</c> each with main and a PR head under <c>refs/pull/&lt;n&gt;/head</c>,
/// the work repos <c>work-&lt;name&gt;</c> that push to them, and a clone <c>repos/widget</c> whose remote is a GitHub URL that a <c>url.&lt;origin&gt;.insteadOf</c>
/// rewrite sends to the bare origin. Each step is the git command the real builder runs, answered by the model, so what is built is what git builds.
/// </summary>
internal static class FakePrOrigins
{
    /// <summary>What <see cref="Build"/> made: the model, and the heads the PR tests read.</summary>
    public sealed record Shape(FakeRepoGit Git, string WidgetOrigin, string GadgetOrigin, string WidgetClone, string WidgetHead, string GadgetHead);

    /// <summary>Runs <paramref name="args"/> in <paramref name="cwd"/> on the model and returns its trimmed output, as <see cref="PrOrigins.Git"/> does on git; a refusal throws.</summary>
    public static string Git(FakeRepoGit git, string cwd, params string[] args)
    {
        var run = git.Run(args, cwd);
        return run.Ok ? run.Output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {run.Error}");
    }

    /// <summary>
    /// What PrFixTestsBase's seed makes, in a model under <paramref name="root"/>: both origins with main and their PR head (7 for widget, 9 for gadget),
    /// <c>feature/seven</c> pushed from widget's work repo, and the widget clone under <c>repos</c> with its GitHub URL and rewrite. gadget has no clone.
    /// </summary>
    public static Shape Build(string root)
    {
        var git = FakeRepoGit.Empty(root);
        var (widget, widgetHead) = Make(git, root, "widget", 7);
        var (gadget, gadgetHead) = Make(git, root, "gadget", 9);
        Git(git, Path.Combine(root, "work-widget"), "push", "-q", "origin", "HEAD:refs/heads/feature/seven");
        var roots = Path.Combine(root, "repos");
        Directory.CreateDirectory(roots);
        Git(git, roots, "clone", "-q", widget, "widget");
        var clone = Path.Combine(roots, "widget");
        Git(git, clone, "config", "remote.origin.url", "https://github.com/acme/widget.git");
        Git(git, clone, "config", $"url.{widget.Replace('\\', '/')}.insteadOf", "https://github.com/acme/widget.git");
        return new Shape(git, widget, gadget, clone, widgetHead, gadgetHead);
    }

    /// <summary>A bare origin for <paramref name="name"/> under <paramref name="root"/>, its main one commit, and PR <paramref name="number"/> one past it; returns the origin and the PR's head.</summary>
    public static (string Origin, string Head) Make(FakeRepoGit git, string root, string name, int number)
    {
        var origin = Path.Combine(root, "github", name + ".git");
        var work = Path.Combine(root, "work-" + name);
        Directory.CreateDirectory(work);
        Git(git, root, "init", "-q", "--bare", "-b", "main", origin);
        Git(git, work, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
        Commit(git, work, "first");
        Git(git, work, "remote", "add", "origin", origin);
        Git(git, work, "push", "-q", "origin", "HEAD:main");
        return (origin, Push(git, work, number, "the change"));
    }

    /// <summary>One more commit on the PR, pushed to its head as GitHub keeps it; returns the new head. The model's <see cref="PrOrigins.Push"/>.</summary>
    public static string Push(FakeRepoGit git, string work, int number, string what)
    {
        File.WriteAllText(Path.Combine(work, $"{Guid.NewGuid():N}.txt"), what + "\n");
        Commit(git, work, what);
        Git(git, work, "push", "-q", "-f", "origin", $"HEAD:refs/pull/{number}/head");
        return Git(git, work, "rev-parse", "HEAD");
    }

    private static void Commit(FakeRepoGit git, string work, string message)
    {
        Git(git, work, "add", ".");
        Git(git, work, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }
}
