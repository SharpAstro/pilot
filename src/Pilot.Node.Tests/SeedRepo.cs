using System.Diagnostics;

namespace Pilot.Node.Tests;

/// <summary>
/// The repo most tests fly in: <c>tianwen</c>, one commit on main, pushed to a bare <c>origin.git</c> beside it, with
/// origin's HEAD set. Built once per test run and copied into each test's folder: built per test it took about ten git
/// processes, and with the suite's classes running side by side those starts were most of a test's 3 to 5 seconds
/// (2026-10-26, the suite at 3 minutes).
/// </summary>
internal static class SeedRepo
{
    private static readonly Lazy<string> Template = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Copies the seed into <paramref name="root"/>: <c>root/origin.git</c> and <c>root/&lt;name&gt;</c>, its origin pointing at the copy.</summary>
    public static string Make(string root, string name = "tianwen")
    {
        if (name != "tianwen")
        {
            throw new ArgumentException("the seed is tianwen", nameof(name));
        }
        CopyTree(Template.Value, root);
        return Path.Combine(root, name);
    }

    private static string Build()
    {
        var root = NewTemplateRoot();
        var origin = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "tianwen");
        Directory.CreateDirectory(repo);
        Git(root, "init", "-q", "--bare", "-b", "main", origin);
        Git(repo, "init", "-q", "-b", "main");
        // The node's own rebases make commits, and a CI runner has no identity of its own to make them with.
        Git(repo, "config", "user.email", "t@example.com");
        Git(repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "first");
        Git(repo, "remote", "add", "origin", origin);
        Git(repo, "push", "-q", "-u", "origin", "main");
        Git(repo, "remote", "set-head", "origin", "main");
        return root;
    }

    private const string Prefix = "pilot-seed-";

    /// <summary>
    /// A fresh folder for a template to be built in, once a run, removed when the test process exits. A run killed before it
    /// could is swept by the next: any older than a day goes.
    /// </summary>
    public static string NewTemplateRoot()
    {
        SweepOld();
        var root = Path.Combine(Path.GetTempPath(), Prefix + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(root);
        return root;
    }

    private static int _swept;

    private static void SweepOld()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 1)
        {
            return;
        }
        foreach (var old in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
        {
            if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
            {
                Delete(old);
            }
        }
    }

    private static void Delete(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held or gone: the next run's sweep takes it.
        }
    }

    /// <summary>
    /// Copies a template built under <paramref name="template"/> into <paramref name="root"/>, with the template's path in
    /// every git config file (origins, insteadOf rules) turned into the copy's, so no git process is run for it.
    /// </summary>
    public static void CopyTree(string template, string root)
    {
        Copy(template, root);
        foreach (var config in Directory.EnumerateFiles(root, "config", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(config);
            // Git writes a path with each backslash doubled, as given, or with forward slashes.
            var moved = text.Replace(template.Replace(@"\", @"\\"), root.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase)
                .Replace(template, root, StringComparison.OrdinalIgnoreCase)
                .Replace(template.Replace('\\', '/'), root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (moved != text)
            {
                File.SetAttributes(config, FileAttributes.Normal);
                File.WriteAllText(config, moved);
            }
        }
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, target);
            // Git's object files are read-only; the tests' cleanup sets them back, as it does for files git made.
        }
    }

    private static void Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        }
    }
}
