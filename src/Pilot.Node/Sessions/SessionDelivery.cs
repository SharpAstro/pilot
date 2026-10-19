using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>A background session to continue with a user turn: which, where it runs, and what it is told.</summary>
/// <param name="SessionId">The session to continue.</param>
/// <param name="Name">Its name, kept on the copy; null when it has none.</param>
/// <param name="Cwd">Its working folder, where the copy runs.</param>
/// <param name="Text">The user turn.</param>
public sealed record ResumeRequest(string SessionId, string? Name, string Cwd, string Text);

/// <summary>A session to reach over the intercom: the name SendMessage knows it by, and its kind, which tells its row in a listing from another of the same name.</summary>
/// <param name="From">The name the message comes from, as the recipient reads it: <see cref="ClaudeCliDelivery.RelayFrom"/> of the command's sender.</param>
public sealed record RelayRequest(string Name, SessionKind Kind, string Text, string From);

/// <summary>A delivery that did not happen, with why: the say's error.</summary>
public sealed class SessionDeliveryException(string message) : Exception(message);

/// <summary>
/// The <c>claude</c> calls behind <c>say</c>, so a test decides the way and checks the refusals and the re-keying
/// without a process. Each throws <see cref="SessionDeliveryException"/> when it could not do its part.
/// </summary>
public interface ISessionDelivery
{
    /// <summary>Stops a running background session and returns once its process has gone (<c>claude stop</c>).</summary>
    Task StopAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Continues a background session whose process has gone, with the text as its next user turn
    /// (<c>claude --bg --resume</c>). Claude Code continues it under a new session id holding the whole conversation.
    /// </summary>
    /// <returns>The new session id.</returns>
    Task<string> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the text to a live session over Claude Code's intercom, as a cross-session message, by a throwaway
    /// <c>claude -p</c> session allowed SendMessage. Reaches a session mid-turn and an interactive one, stopping neither.
    /// </summary>
    /// <returns>The relay's answer.</returns>
    Task<string> RelayAsync(RelayRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISessionDelivery"/> by the <c>claude</c> CLI. Port of v1's <c>say()</c> (stop, then
/// <c>--bg --resume</c>) and its intercom (<c>RELAY</c>, <c>RELAY_PROMPT</c>).
/// </summary>
public sealed partial class ClaudeCliDelivery(IClaudeAgents agents, ClaudeProfile profile, string relayFolder) : ISessionDelivery
{
    /// <summary>The name a message from the person who runs these sessions comes from, whichever client they typed it in.</summary>
    public const string ControllerName = "controller";

    /// <summary>The clients a person types in: a command from one of them is the controller's.</summary>
    private static readonly string[] Clients = ["gui", "tui", "cli", "web"];

    /// <summary>How long a stopped session's process may take to go.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(20);

    /// <summary>How long a resumed copy may take to be listed with its session id: a second or two.</summary>
    private static readonly TimeSpan ListedWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(500);

    /// <summary>v1's limits: 60 s for <c>claude stop</c>, 120 s for <c>--bg</c>, 180 s for the relay.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// v1's <c>RELAY_PROMPT</c> for one recipient, and what to do when the name is not enough: a resumed session's
    /// earlier copies keep its name, and SendMessage then asks for the row's <c>[ref]</c>, which only a listing in the
    /// relay's own session gives.
    /// </summary>
    private const string RelayPrompt = """
        You carry one message over the intercom for the person who runs these sessions. Call the SendMessage tool once,
        with `to` set to the recipient's name exactly as written and `message` set to the text between the two {0}
        lines exactly as written: change nothing, add nothing, keep every line.

        If SendMessage answers that the name is not enough (more than one row has it, or it asks for a ref), call
        ListAgents, take the one row named exactly as the recipient that is a session on this machine of kind {1}
        (not a Remote Control row), and send again with `to` set to that row's name and ref exactly as the row prints
        them, as in `name [ref]`. If there is not exactly one such row, do not send.

        Do nothing else. Then answer with one line: "delivered", or "failed:" and what the tool said.

        Recipient: {2}

        {0}
        {3}
        {0}
        """;

    public async Task StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        // A background session's job id is the first eight characters of its session id.
        var job = sessionId[..Math.Min(8, sessionId.Length)];
        var run = await ClaudeCli.RunAsync(["stop", job], stdin: null, cwd: null, StopTimeout, cancellationToken);
        if (run.ExitCode != 0)
        {
            // A background session whose process outlived its job record: claude stop cannot reach it, though it runs
            // (PIL5's sessions worker, idle 13 hours, had none). Its process is the session, so it is ended by its pid.
            if (!TryEndOrphan(await FindAsync(r => r.SessionId == sessionId, cancellationToken)))
            {
                throw new SessionDeliveryException($"claude stop {job} failed ({run.ExitCode}): {Clip(run.Output + run.Error)}");
            }
        }

        var until = DateTimeOffset.UtcNow + StopWait;
        while (true)
        {
            var row = await FindAsync(r => r.SessionId == sessionId, cancellationToken);
            if (row is not { Pid: not null })
            {
                return;
            }
            if (DateTimeOffset.UtcNow > until)
            {
                throw new SessionDeliveryException($"{job} was told to stop but its process {row.Pid} still runs after {StopWait.TotalSeconds:0} s");
            }
            await Task.Delay(Step, cancellationToken);
        }
    }

    public async Task<string> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken)
    {
        var settings = profile.FindTranscript(request.SessionId) is { } transcript ? ResumeSettings.Read(transcript) : ResumeSettings.None;
        var args = ResumeArguments(request, settings);
        var run = await ClaudeCli.RunAsync(args, request.Text, request.Cwd, ResumeTimeout, cancellationToken);
        var job = Backgrounded().Match(run.Output);
        if (run.ExitCode != 0 || !job.Success)
        {
            throw new SessionDeliveryException($"claude --bg --resume failed ({run.ExitCode}): {Clip(run.Output + run.Error)}");
        }

        var id = job.Groups[1].Value;
        var until = DateTimeOffset.UtcNow + ListedWait;
        while (true)
        {
            if (await FindAsync(r => r.Id == id, cancellationToken) is { } row)
            {
                return row.SessionId;
            }
            if (DateTimeOffset.UtcNow > until)
            {
                throw new SessionDeliveryException($"the session was continued as job {id}, but claude agents did not list it within {ListedWait.TotalSeconds:0} s");
            }
            await Task.Delay(Step, cancellationToken);
        }
    }

    /// <summary>
    /// The resume's arguments: the session's name, and the permission mode, model and effort it last ran with, which a
    /// resume does not carry over on its own (v1 passes its workers' every time).
    /// </summary>
    public static IReadOnlyList<string> ResumeArguments(ResumeRequest request, ResumeSettings settings)
    {
        var args = new List<string> { "--bg", "--resume", request.SessionId };
        if (request.Name is { Length: > 0 } name)
        {
            args.AddRange(["-n", name]);
        }
        if (settings.PermissionMode is { Length: > 0 } mode)
        {
            args.AddRange(["--permission-mode", mode]);
        }
        if (settings.Model is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]);
        }
        if (settings.Effort is { Length: > 0 } effort)
        {
            args.AddRange(["--effort", effort]);
        }
        return args;
    }

    /// <summary>v1's relay arguments, with ListAgents added for a name that is not enough.</summary>
    public static IReadOnlyList<string> RelayArguments(string from) =>
    [
        "-p", "--model", "haiku", "--tools", "SendMessage,ListAgents", "--allowedTools", "SendMessage,ListAgents", "--strict-mcp-config",
        "--no-session-persistence", "--name", from, "--output-format", "json",
    ];

    /// <summary>
    /// The name a relayed message comes from, by who sent the command: a client is the controller, a session (the
    /// watchtower, a worker) its own name. The recipient reads it as the sender (<c>Message from @controller</c>), so it
    /// says who spoke, not which program carried it.
    /// </summary>
    public static string RelayFrom(string? who)
    {
        var name = (who ?? "").Trim();
        if (name.Length == 0 || Clients.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return ControllerName;
        }
        var safe = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        return safe.Length == 0 ? ControllerName : safe;
    }

    /// <summary>
    /// The relayed message as its recipient reads it: the text, and a closing line saying where its answer goes. The
    /// sender a recipient sees is the relay, a <c>claude -p</c> session gone once it has sent: PIL5's interactive check
    /// asked for a reply, and the recipient SendMessage'd it back to the relay, which failed, instead of answering in
    /// its own turn, where the GUI reads it.
    /// </summary>
    public static string RelayText(string text) =>
        text.Trim() + "\n\n(Sent through the pilot node. The sender cannot be messaged back: answer in your own reply, not with SendMessage.)";

    public async Task<string> RelayAsync(RelayRequest request, CancellationToken cancellationToken)
    {
        var mark = $"======== {Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..4])} ========";
        var kind = request.Kind == SessionKind.Background ? "bg" : "interactive";
        var prompt = string.Format(System.Globalization.CultureInfo.InvariantCulture, RelayPrompt, mark, kind, request.Name, RelayText(request.Text));
        Directory.CreateDirectory(relayFolder);
        var run = await ClaudeCli.RunAsync(RelayArguments(request.From), prompt, relayFolder, RelayTimeout, cancellationToken);

        string answer;
        bool isError;
        try
        {
            using var doc = JsonDocument.Parse(run.Output);
            answer = ClaudeAgentsJson.Text(doc.RootElement, "result")?.Trim() ?? "";
            isError = doc.RootElement.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            throw new SessionDeliveryException($"the intercom failed ({run.ExitCode}): {Clip(run.Output + run.Error)}");
        }

        if (isError || !IsDelivered(answer))
        {
            throw new SessionDeliveryException($"the intercom did not deliver it: {Clip(answer.Length > 0 ? answer : run.Output + run.Error)}");
        }
        return answer;
    }

    /// <summary>Whether the relay's answer says it delivered: "delivered", and no "failed".</summary>
    public static bool IsDelivered(string answer) =>
        answer.Contains("delivered", StringComparison.OrdinalIgnoreCase) && !answer.Contains("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ends a listed background session's process by its pid; false when there is none to end.</summary>
    private static bool TryEndOrphan(ClaudeAgentRow? row)
    {
        if (row is not { Pid: { } pid } || !string.Equals(row.Kind, "background", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<ClaudeAgentRow?> FindAsync(Func<ClaudeAgentRow, bool> match, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await agents.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        return ClaudeAgentsJson.Parse(text)?.FirstOrDefault(match);
    }

    private static string Clip(string text)
    {
        text = text.Trim();
        return text.Length > 400 ? text[..400] + "..." : text;
    }

    /// <summary>What <c>claude --bg</c> prints: <c>backgrounded · &lt;job id&gt;</c>. v1's <c>backgrounded()</c>.</summary>
    [GeneratedRegex(@"backgrounded\s*\S\s*([0-9a-f]{8})")]
    private static partial Regex Backgrounded();
}

/// <summary>What a session last ran with, from its transcript: what a resume has to pass again.</summary>
/// <param name="PermissionMode">The last <c>permission-mode</c> line's mode.</param>
/// <param name="Model">The last real response's <c>requestedModel</c>, else its <c>message.model</c>.</param>
/// <param name="Effort">The last real response's <c>effort</c>.</param>
public sealed record ResumeSettings(string? PermissionMode, string? Model, string? Effort)
{
    public static readonly ResumeSettings None = new(null, null, null);

    /// <summary>Reads the transcript at <paramref name="path"/>; <see cref="None"/> when it cannot be read.</summary>
    public static ResumeSettings Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            return Read(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }

    /// <summary>Reads transcript lines from <paramref name="reader"/>.</summary>
    public static ResumeSettings Read(TextReader reader)
    {
        string? mode = null, model = null, effort = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"permission-mode\"", StringComparison.Ordinal) && !line.Contains("\"assistant\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                switch (ClaudeAgentsJson.Text(root, "type"))
                {
                    case "permission-mode":
                        mode = ClaudeAgentsJson.Text(root, "permissionMode") ?? mode;
                        break;
                    case "assistant" when root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object:
                        var said = ClaudeAgentsJson.Text(message, "model");
                        var sidechain = root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True;
                        if (said is null || said == "<synthetic>" || sidechain)
                        {
                            break;
                        }
                        model = ClaudeAgentsJson.Text(root, "requestedModel") ?? said;
                        effort = ClaudeAgentsJson.Text(root, "effort") ?? effort;
                        break;
                }
            }
            catch (JsonException)
            {
                // Not a line of ours to read.
            }
        }
        return new ResumeSettings(mode, model, effort);
    }
}
