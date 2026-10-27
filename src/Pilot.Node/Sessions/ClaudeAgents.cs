using System.Diagnostics;
using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>
/// One row of <c>claude agents --json --all</c>: an interactive or background session, completed ones included. An
/// interactive row has no <c>id</c> and no <c>state</c>; a background row has both, and a <c>status</c> while its
/// process lives.
/// </summary>
/// <param name="Id">Claude Code's short id, when the row has one (a background row does).</param>
/// <param name="Pid">The process, while one runs.</param>
/// <param name="Kind"><c>interactive</c> or <c>background</c>.</param>
/// <param name="Status">While it runs: <c>idle</c>, <c>busy</c> or <c>waiting</c>.</param>
/// <param name="State">A background session's state: <c>working</c>, <c>blocked</c>, <c>done</c>, <c>failed</c>.</param>
public sealed record ClaudeAgentRow(
    string? Id,
    string SessionId,
    int? Pid,
    string Cwd,
    string Kind,
    string? Name,
    string? Status,
    string? State,
    DateTimeOffset? StartedAt,
    string? WaitingFor)
{
    /// <summary>The row's id, else the first eight characters of its session id, which is what Claude Code's short id is.</summary>
    public string ShortId => Id is { Length: > 0 } ? Id : SessionId[..Math.Min(8, SessionId.Length)];
}

/// <summary>Reads the JSON of <c>claude agents --json --all</c>. Port of v1's <c>agents()</c>.</summary>
public static class ClaudeAgentsJson
{
    /// <returns>
    /// The rows, or null when <paramref name="json"/> is not a JSON array: a failed or half-written listing says nothing
    /// about the sessions, so the caller keeps what it knew rather than ending them all. A row with no session id is
    /// left out, as it has no transcript to follow.
    /// </returns>
    public static IReadOnlyList<ClaudeAgentRow>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var rows = new List<ClaudeAgentRow>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || Text(element, "sessionId") is not { Length: > 0 } sessionId || !ClaudeProfile.IsSafeId(sessionId))
                {
                    continue;
                }

                rows.Add(new ClaudeAgentRow(
                    Id: Text(element, "id"),
                    SessionId: sessionId,
                    Pid: element.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var p) && p > 0 ? p : null,
                    Cwd: Text(element, "cwd") ?? "",
                    Kind: Text(element, "kind") ?? "interactive",
                    Name: Text(element, "name"),
                    Status: Text(element, "status"),
                    State: Text(element, "state"),
                    StartedAt: element.TryGetProperty("startedAt", out var started) && started.ValueKind == JsonValueKind.Number && started.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : null,
                    WaitingFor: Text(element, "waitingFor")));
            }
            return rows;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>The process call behind the tracker: tests give it a fixture listing.</summary>
public interface IClaudeAgents
{
    /// <summary>The text <c>claude agents --json --all</c> prints. Throws when it cannot be had.</summary>
    Task<string> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Runs the <c>claude</c> CLI, windowless, as v1's <c>claude()</c> does.</summary>
public sealed class ClaudeCliAgents : IClaudeAgents
{
    /// <summary>How long one listing may take before it is given up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        var run = await ClaudeCli.RunAsync(["agents", "--json", "--all"], stdin: null, cwd: null, Timeout, cancellationToken);
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException($"claude agents exited {run.ExitCode}: {run.Error.Trim()}");
        }
        return run.Output;
    }
}

/// <summary>What one <c>claude</c> call printed.</summary>
public sealed record ClaudeRun(int ExitCode, string Output, string Error);

/// <summary>Runs the <c>claude</c> CLI, windowless, as v1's <c>claude()</c> does: every call the node makes goes through here.</summary>
public static class ClaudeCli
{
    /// <summary>
    /// Runs <c>claude</c> with <paramref name="args"/>, <paramref name="stdin"/> on its standard input (closed when
    /// null), in <paramref name="cwd"/> (the node's own when null). Killed, its children with it, when
    /// <paramref name="timeout"/> passes or <paramref name="cancellationToken"/> fires; a timeout throws
    /// <see cref="TimeoutException"/>.
    /// </summary>
    public static async Task<ClaudeRun> RunAsync(IReadOnlyList<string> args, string? stdin, string? cwd, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var exe = FindOnPath() ?? throw new FileNotFoundException("claude is not on PATH");
        var start = StartInfo(exe, args, cwd);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("claude did not start");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), limit.Token);
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);
            return new ClaudeRun(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"claude {string.Join(' ', args.Take(2))} took longer than {timeout.TotalSeconds:0} s");
            }
            throw;
        }
    }

    /// <summary>What <see cref="RunAsync"/> starts: <paramref name="exe"/> with <paramref name="args"/>, windowless, with the build environment and none of the node's own session variables.</summary>
    internal static ProcessStartInfo StartInfo(string exe, IReadOnlyList<string> args, string? cwd)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (cwd is not null)
        {
            start.WorkingDirectory = cwd;
        }
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // The node's own session variables out, so the call is a fresh top-level one; the profile stays: it is the
        // one account everything runs on.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) && !string.Equals(k, "CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        // The build environment, which a worker session's own builds (its Bash and PowerShell tools, and the jobs it
        // starts) inherit, so they leave no reused nodes behind and fan out no wider than a job's.
        return BuildEnvironment.Apply(start);
    }

    private static string? FindOnPath()
    {
        string[] names = OperatingSystem.IsWindows() ? ["claude.exe", "claude.cmd", "claude"] : ["claude"];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
