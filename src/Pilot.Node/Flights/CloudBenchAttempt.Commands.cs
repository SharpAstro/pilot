namespace Pilot.Node.Flights;

/// <summary>The command lines of a cloud bench attempt and the log lines about them. Every secret is left out of both here, and only here.</summary>
internal sealed partial class CloudBenchAttempt
{
    private const int MaxErrorChars = 1500;

    private static readonly string[] SecretArguments = ["--password", "-p"];

    // Environment for every az call: no telemetry, no colour codes in what the log keeps. The service principal's config folder is added at login.
    private readonly Dictionary<string, string> _azEnvironment = new()
    {
        ["AZURE_CORE_COLLECT_TELEMETRY"] = "false",
        ["AZURE_CORE_NO_COLOR"] = "true",
        ["AZURE_CORE_DISABLE_CONFIRM_PROMPT"] = "true",
    };

    private string ConfigFolder => Path.Combine(_tmp, "az-config");

    /// <summary>An az command that needs no subscription or resource group of its own (the login).</summary>
    private CloudCommand Az(IReadOnlyList<string> args, TimeSpan? budget = null) => new("az", args, _tmp, budget, null, _azEnvironment);

    /// <summary>An az command in the settings' subscription and resource group.</summary>
    private CloudCommand AzOf(IReadOnlyList<string> args, TimeSpan? budget = null) =>
        Az([.. args, "--subscription", _cloud.Subscription, "--resource-group", _cloud.ResourceGroup], budget);

    private CloudCommand Git(IReadOnlyList<string> args) => new("git", args, request.RepoPath, Short);

    // Relative file names and the job's tmp as the working directory: no drive letter for scp to take for a host, and no path to quote.
    private IReadOnlyList<string> SshOptions =>
    [
        "-i", KeyFile,
        "-o", "BatchMode=yes",
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", $"UserKnownHostsFile={KnownHostsFile}",
        "-o", "ConnectTimeout=20",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=4",
        "-o", "LogLevel=ERROR",
    ];

    private CloudCommand Ssh(IReadOnlyList<string> remote, TimeSpan budget, Action<string>? onLine = null) =>
        new("ssh", [.. SshOptions, "-n", $"{User}@{_ip}", .. remote], _tmp, budget, onLine);

    private CloudCommand Scp(bool recursive, IReadOnlyList<string> paths, TimeSpan budget) =>
        new("scp", [.. (recursive ? new[] { "-r" } : []), .. SshOptions, .. paths], _tmp, budget);

    /// <summary>Runs a command, with its line in the log and, when it failed, why (a command that streams its own output says only its exit code).</summary>
    private async Task<CloudProcessRun> ExecAsync(CloudCommand command, CancellationToken cancellationToken, bool quiet = false)
    {
        if (!quiet)
        {
            Say($"$ {Display(command)}");
        }
        var run = await process.RunAsync(command, cancellationToken);
        if (!run.Ok && !quiet)
        {
            var text = command.OnLine is not null ? "" : Cut(run.Error.Trim().Length > 0 ? run.Error : run.Output);
            Say(text.Length > 0 ? $"  exit {run.ExitCode}: {text}" : $"  exit {run.ExitCode}");
        }
        return run;
    }

    private static string Cut(string text) => text.Length <= MaxErrorChars ? text.Trim() : text[..MaxErrorChars].Trim() + " ...";

    /// <summary>The command as the log shows it: the value after <c>--password</c> is three stars, and so is the secret wherever else it appears.</summary>
    private string Display(CloudCommand command)
    {
        var words = new List<string>();
        for (var i = 0; i < command.Args.Count; i++)
        {
            words.Add(command.Args[i]);
            if (SecretArguments.Contains(command.Args[i], StringComparer.Ordinal) && i + 1 < command.Args.Count)
            {
                words.Add("***");
                i++;
            }
        }
        return Redact(command.Args.Count == 0 ? command.Program : $"{command.Program} {string.Join(' ', words.Select(w => w.Length == 0 ? "\"\"" : w))}");
    }

    /// <summary>The text without the service principal's secret: the one place anything leaves for the job's log or the result.</summary>
    private string Redact(string text) =>
        _cloud.Secret is { Length: > 0 } secret ? text.Replace(secret, "***", StringComparison.Ordinal) : text;

    private void Say(string text)
    {
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length > 0)
            {
                request.Log(Redact(line));
            }
        }
    }

    private void DeleteFile(string name)
    {
        try
        {
            File.Delete(Path.Combine(_tmp, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the job's tmp, which goes with the job.
        }
    }
}
