using System.Globalization;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// <c>pilot bench</c> (PIL675): a benchmark on a ref, or on a baseline and a candidate measured back to back, run with the node to
/// itself (one <see cref="JobKind.Bench"/> job) and posted as a card and, when asked, as a PR comment.
/// <para>
/// Each ref is checked out in a detached worktree of its own under the repo's <c>.claude/worktrees</c>, so BenchmarkDotNet finds the one
/// project of the ref and nothing else. The job runs, per ref in order baseline then candidate and inside its worktree, the repo's fetch
/// step and then the benchmark command, after a marker line naming the ref so the log reads in sections. When the job ends however it ends,
/// the <c>*-report-github.md</c> tables each worktree's BenchmarkDotNet left are gathered into one body, the body goes on a
/// <see cref="CardKind"/> card and the PR, and the worktrees are removed. A node that stops while the job runs leaves the worktrees (and
/// the post) undone; the next bench picks free names beside them.
/// </para>
/// </summary>
public sealed class BenchRun(NodeState state, NodeOptions options, RepoCatalog repos, Planes planes, IGit git, IGitHub github, JobRunner jobs)
{
    /// <summary>The kind of the card a finished benchmark raises.</summary>
    public const string CardKind = "bench-result";

    /// <summary>The longest comment body posted: GitHub refuses a comment over 65536 characters.</summary>
    private const int MaxBody = 60000;

    // One at a time, so two benches queued together never pick the same worktree name.
    private static readonly Lock NameGate = new();

    /// <summary>What one ref of a benchmark is: its label in the log and on the card, the ref as given, its commit and its worktree.</summary>
    private sealed record Leg(string Label, string Ref, string Sha, string Worktree);

    /// <summary>The words of a bench command, parsed.</summary>
    private sealed record Spec(string Repo, string? Ref, string? Baseline, string? Candidate, string? Fetch, int? Pr, TimeSpan Timeout, string[] Command);

    /// <summary>Queues the benchmark; its result's <c>Made</c> is the job's id. The post follows when the job ends.</summary>
    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (Parse(command.Args ?? []) is not { } spec)
        {
            return state.Complete(command, "bench takes <repo> (--ref <ref> | --baseline <ref> --candidate <ref>) [--fetch <command>] [--pr <n>] [--timeout <seconds>] -- <command>");
        }
        var hasRef = spec.Ref is not null;
        var hasPair = spec.Baseline is not null && spec.Candidate is not null;
        var hasHalf = (spec.Baseline is null) != (spec.Candidate is null);
        if (hasRef == hasPair || hasHalf || hasRef && (spec.Baseline is not null || spec.Candidate is not null))
        {
            return state.Complete(command, "bench takes one --ref, or a --baseline and a --candidate together, not both and not neither");
        }
        if (spec.Command.Length == 0)
        {
            return state.Complete(command, "bench needs the benchmark command, after --");
        }
        if (spec.Timeout > JobRunner.MaxTimeout)
        {
            return state.Complete(command, $"a job may run {JobRunner.MaxTimeout.TotalHours:0} hours at most");
        }
        var repo = repos.Repos().FirstOrDefault(r => string.Equals(r.Name, spec.Repo, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Path, spec.Repo, StringComparison.OrdinalIgnoreCase));
        if (repo is null)
        {
            return state.Complete(command, $"there is no repo {spec.Repo} among the clones this node knows (pilot repos lists them)");
        }

        SessionRow? owner = null;
        if (command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal))
        {
            var id = command.Target[Verbs.SessionTargetPrefix.Length..];
            owner = state.Sessions().FirstOrDefault(s => s.Id == id);
            if (owner is null)
            {
                return state.Complete(command, $"There is no session {id} to wake with the result");
            }
        }
        else if (command.Target != Verbs.NodeTarget)
        {
            return state.Complete(command, $"bench takes a session to wake ({Verbs.SessionTargetPrefix}<id>) or the node, not {command.Target}");
        }

        // The repo is brought up to date, so a ref that is a branch of origin is read as it stands there.
        await git.RunAsync(["fetch", "--quiet", "origin"], repo.Path, cancellationToken);
        var wanted = spec.Ref is not null
            ? new[] { ("ref", spec.Ref) }
            : new[] { ("baseline", spec.Baseline!), ("candidate", spec.Candidate!) };
        var shas = new List<(string Label, string Ref, string Sha)>();
        foreach (var (label, name) in wanted)
        {
            if (await ResolveAsync(repo.Path, name, cancellationToken) is not { } sha)
            {
                return state.Complete(command, $"{repo.Name} has no commit {name} (not as given, not as origin/{name})");
            }
            shas.Add((label, name, sha));
        }

        List<Leg> legs;
        try
        {
            legs = await MakeWorktreesAsync(repo.Path, shas, cancellationToken);
        }
        catch (FlightException ex)
        {
            return state.Complete(command, ex.Message);
        }

        var scripts = Path.Combine(options.DataFolder, "jobs");
        Directory.CreateDirectory(scripts);
        var script = Path.Combine(scripts, $"bench-{Guid.NewGuid():N}{(OperatingSystem.IsWindows() ? ".ps1" : ".sh")}");
        File.WriteAllText(script, ScriptFor(legs, spec.Fetch, ShellLine.Join(spec.Command, OperatingSystem.IsWindows())), new UTF8Encoding(encoderShouldEmitUTF8Identifier: OperatingSystem.IsWindows()));
        var line = OperatingSystem.IsWindows()
            ? $"pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(script)}"
            : $"sh {Quote(script)}";
        var timeout = spec.Timeout == TimeSpan.Zero ? JobRunner.DefaultTimeout : spec.Timeout;
        var (jobId, ended) = jobs.QueueBench(line, legs[0].Worktree, timeout, owner);
        _ = PostWhenEndedAsync(repo, legs, spec, jobId, ended, script);
        return state.Complete(command, null, made: jobId);
    }

    private async Task<string?> ResolveAsync(string repoPath, string name, CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { name, $"origin/{name}" })
        {
            var parsed = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{candidate}^{{commit}}"], repoPath, cancellationToken);
            if (parsed.Ok && parsed.Output.Trim() is { Length: > 0 } sha)
            {
                return sha;
            }
        }
        return null;
    }

    // The names are short on purpose: BenchmarkDotNet builds its project in a generated folder under the worktree, and Windows
    // refuses a path over 260 characters, which a plane's worktree name (flight/pil675 and its sibling folders) would eat.
    private async Task<List<Leg>> MakeWorktreesAsync(string repoPath, List<(string Label, string Ref, string Sha)> shas, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(repoPath, ".claude", "worktrees");
        Directory.CreateDirectory(folder);
        await git.RunAsync(["worktree", "prune"], repoPath, cancellationToken);
        var made = new List<Leg>();
        try
        {
            foreach (var (label, name, sha) in shas)
            {
                Leg leg;
                lock (NameGate)
                {
                    var n = 1;
                    while (Directory.Exists(Path.Combine(folder, $"b{n}")))
                    {
                        n++;
                    }
                    leg = new Leg(label, name, sha, Path.Combine(folder, $"b{n}"));
                    // Held until git makes it: the name is taken by its folder, so the next pick skips it.
                    Directory.CreateDirectory(leg.Worktree);
                }
                // git adds a worktree into an empty folder.
                var add = await git.RunAsync(["worktree", "add", "--detach", leg.Worktree, sha], repoPath, cancellationToken);
                if (!add.Ok)
                {
                    TryDelete(leg.Worktree);
                    throw new FlightException($"git could not make the worktree of {name} ({sha[..Math.Min(7, sha.Length)]}): {add.Why}");
                }
                made.Add(leg);
            }
        }
        catch
        {
            foreach (var leg in made)
            {
                await RemoveAsync(repoPath, leg.Worktree);
            }
            throw;
        }
        return made;
    }

    private async Task PostWhenEndedAsync(RepoInfo repo, List<Leg> legs, Spec spec, string jobId, Task<JobRow> ended, string script)
    {
        try
        {
            var job = await ended;
            var body = Body(repo, legs, job);
            var note = "";
            if (spec.Pr is { } pr)
            {
                try
                {
                    await github.CommentIssueAsync(repo.Path, pr, body.Length > MaxBody ? body[..MaxBody] + "\n\n_(cut: the rest is on the card)_" : body, CancellationToken.None);
                }
                catch (Exception ex) when (ex is FlightException or IOException or System.ComponentModel.Win32Exception)
                {
                    note = $"\n\n_The comment on #{pr.ToString(CultureInfo.InvariantCulture)} was not posted: {ex.Message}_";
                }
            }
            var title = $"benchmark of {repo.Name} {(legs.Count == 1 ? legs[0].Ref : $"{legs[0].Ref} against {legs[1].Ref}")} {(job.State == JobState.Succeeded ? "finished" : "ended badly")} ({jobId})";
            state.RaiseCard(CardBin.ForYou, CardKind, Verbs.RepoTarget(repo.Path), title, body + note);
        }
        catch (Exception ex)
        {
            state.RaiseCard(CardBin.ForYou, CardKind, Verbs.RepoTarget(repo.Path), $"benchmark of {repo.Name} ({jobId}) ended but its result could not be posted: {ex.Message.Split('\n')[0].Trim()}", ex.ToString());
        }
        finally
        {
            foreach (var leg in legs)
            {
                await RemoveAsync(repo.Path, leg.Worktree);
            }
            try
            {
                File.Delete(script);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the node's jobs folder: harmless.
            }
        }
    }

    private async Task RemoveAsync(string repoPath, string worktree)
    {
        try
        {
            await planes.RemoveAsync(repoPath, worktree, CancellationToken.None, force: true);
        }
        catch (Exception ex) when (ex is FlightException or IOException or UnauthorizedAccessException)
        {
            // Said in the node's log by nobody: the folder is under .claude/worktrees, and the next start's sweep or a person takes it.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The card and comment body: per ref a heading with the ref and its short commit and then its tables, after the job's exit and log.</summary>
    private static string Body(RepoInfo repo, List<Leg> legs, JobRow job)
    {
        var text = new StringBuilder();
        text.Append("# Benchmark of ").AppendLine(repo.Name);
        text.AppendLine();
        text.Append("Job ").Append(job.Id).Append(' ')
            .Append(job.State == JobState.Succeeded ? "succeeded (exit 0)" : job.ExitCode is { } code ? $"failed (exit {code})" : $"ended: {job.Error ?? job.State.ToString().ToLowerInvariant()}")
            .Append(", log ").AppendLine(job.LogPath);
        foreach (var leg in legs)
        {
            text.AppendLine().Append("## ").Append(leg.Label).Append(" `").Append(leg.Ref).Append("` at `").Append(leg.Sha[..Math.Min(7, leg.Sha.Length)]).AppendLine("`");
            var tables = Tables(leg.Worktree);
            if (tables.Count == 0)
            {
                text.AppendLine().AppendLine("No BenchmarkDotNet results were found (no `*-report-github.md` under `BenchmarkDotNet.Artifacts/results`): see the log.");
                continue;
            }
            foreach (var (name, table) in tables)
            {
                text.AppendLine().Append("### ").AppendLine(name).AppendLine().AppendLine(table.Trim());
            }
        }
        return text.ToString();
    }

    private static List<(string Name, string Table)> Tables(string worktree)
    {
        var found = new List<string>();
        try
        {
            var results = Path.Combine(worktree, "BenchmarkDotNet.Artifacts", "results");
            if (Directory.Exists(results))
            {
                found.AddRange(Directory.EnumerateFiles(results, "*-report-github.md"));
            }
            if (found.Count == 0)
            {
                // A command that ran from a project folder leaves its artifacts there.
                foreach (var dir in Directory.EnumerateDirectories(worktree, "BenchmarkDotNet.Artifacts", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (!dir.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && Directory.Exists(Path.Combine(dir, "results")))
                    {
                        found.AddRange(Directory.EnumerateFiles(Path.Combine(dir, "results"), "*-report-github.md"));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var tables = new List<(string, string)>();
        foreach (var file in found.OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var name = Path.GetFileName(file);
                tables.Add((name[..^"-report-github.md".Length], File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return tables;
    }

    // Per ref, in order: a marker line, the fetch step, then the command. A ref that fails does not skip the next, and the job exits
    // non-zero when any did.
    private static string ScriptFor(List<Leg> legs, string? fetch, string command)
    {
        var text = new StringBuilder();
        if (OperatingSystem.IsWindows())
        {
            text.AppendLine("$bad = 0");
            foreach (var leg in legs)
            {
                text.AppendLine($"Write-Output {PwshQuote($"=== bench {leg.Label}: {leg.Ref} ({leg.Sha[..Math.Min(7, leg.Sha.Length)]}) ===")}");
                text.AppendLine($"Set-Location -LiteralPath {PwshQuote(leg.Worktree)}");
                text.AppendLine("$global:LASTEXITCODE = 0");
                if (fetch is { Length: > 0 })
                {
                    text.AppendLine(fetch);
                    text.AppendLine("$fetched = $? -and -not $LASTEXITCODE");
                    text.AppendLine("$global:LASTEXITCODE = 0");
                }
                else
                {
                    text.AppendLine("$fetched = $true");
                }
                text.AppendLine("if ($fetched) {");
                text.AppendLine(command);
                text.AppendLine("if (-not $? -or $LASTEXITCODE) { $bad = if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 } }");
                text.AppendLine("} else { Write-Output '=== the fetch step failed, the benchmark was not run ==='; $bad = 1 }");
            }
            text.AppendLine("exit $bad");
        }
        else
        {
            text.AppendLine("bad=0");
            foreach (var leg in legs)
            {
                text.AppendLine($"echo {ShQuote($"=== bench {leg.Label}: {leg.Ref} ({leg.Sha[..Math.Min(7, leg.Sha.Length)]}) ===")}");
                text.AppendLine($"cd {ShQuote(leg.Worktree)} || bad=1");
                text.AppendLine("if {");
                text.AppendLine(fetch is { Length: > 0 } ? fetch : "true");
                text.AppendLine("}; then");
                // A subshell, so an exit in the command ends that ref only.
                text.AppendLine("(");
                text.AppendLine(command);
                text.AppendLine(")");
                text.AppendLine("code=$?; [ $code -ne 0 ] && bad=$code");
                text.AppendLine("else echo '=== the fetch step failed, the benchmark was not run ==='; bad=1; fi");
            }
            text.AppendLine("exit $bad");
        }
        return text.ToString();
    }

    private static string Quote(string path) => OperatingSystem.IsWindows() ? PwshQuote(path) : ShQuote(path);

    private static string PwshQuote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ShQuote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // bench <repo> --ref r | --baseline b --candidate c [--fetch cmd] [--pr n] [--timeout seconds] -- command…; null when it does not read.
    private static Spec? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }
        string? @ref = null, baseline = null, candidate = null, fetch = null;
        int? pr = null;
        var seconds = 0L;
        var i = 1;
        for (; i < args.Length && args[i] != "--"; i++)
        {
            if (i + 1 >= args.Length || args[i] is not ("--ref" or "--baseline" or "--candidate" or "--fetch" or "--pr" or "--timeout"))
            {
                return null;
            }
            var value = args[i + 1];
            switch (args[i])
            {
                case "--ref": @ref = value; break;
                case "--baseline": baseline = value; break;
                case "--candidate": candidate = value; break;
                case "--fetch": fetch = value; break;
                case "--pr":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        return null;
                    }
                    pr = number;
                    break;
                default:
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                    {
                        return null;
                    }
                    break;
            }
            i++;
        }
        return new Spec(args[0], @ref, baseline, candidate, fetch, pr, TimeSpan.FromSeconds(seconds), i < args.Length ? [.. args.Skip(i + 1)] : []);
    }
}
