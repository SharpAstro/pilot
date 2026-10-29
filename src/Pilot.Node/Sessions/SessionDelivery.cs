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
/// <param name="RemoteControl">Whether the session had Remote Control on, so the copy keeps it: a resume drops it otherwise.</param>
public sealed record ResumeRequest(string SessionId, string? Name, string Cwd, string Text, bool RemoteControl = false);

/// <summary>A session to reach over the intercom: the name SendMessage knows it by, and its kind, which tells its row in a listing from another of the same name.</summary>
/// <param name="From">The name the message comes from, as the recipient reads it: <see cref="ClaudeCliDelivery.RelayFrom"/> of the command's sender.</param>
/// <param name="ReplyTo">The sender's session row, when it is one on this node: the recipient is told to answer with a say to it.</param>
/// <param name="SessionId">The recipient's session id, which finds its intercom endpoint; null when not known (the CLI relay then).</param>
public sealed record RelayRequest(string Name, SessionKind Kind, string Text, string From, string? ReplyTo = null, string? SessionId = null);

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
    /// Continues a background session, with the text as its next user turn; a process of it still running is stopped first
    /// (<c>claude --bg --resume</c>). Claude Code continues it under its own session id, with the options it was started
    /// with; where it makes a copy instead, the copy's id comes back and the row follows it.
    /// </summary>
    /// <returns>The session id it continues under.</returns>
    Task<string> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the text to a live session over Claude Code's intercom, as a cross-session message: written to its endpoint, else by a throwaway
    /// <c>claude -p</c> session allowed SendMessage. Reaches a session mid-turn and an interactive one, stopping neither.
    /// </summary>
    /// <returns>The route it took, for the log.</returns>
    Task<string> RelayAsync(RelayRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISessionDelivery"/> by the <c>claude</c> CLI. Port of v1's <c>say()</c> (stop, then
/// <c>--bg --resume</c>) and its intercom (<c>RELAY</c>, <c>RELAY_PROMPT</c>).
/// </summary>
public partial class ClaudeCliDelivery(IClaudeAgents agents, string relayFolder, ClaudeProfile? profile = null) : ISessionDelivery
{
    /// <summary>The name a message from the person who runs these sessions comes from, whichever client they typed it in.</summary>
    public const string ControllerName = "controller";

    /// <summary>The clients a person types in: a command from one of them is the controller's.</summary>
    private static readonly string[] Clients = ["gui", "tui", "cli", "web"];

    /// <summary>How long a stopped session's process may take to go.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(20);

    /// <summary>How long a resumed copy may take to be listed with its session id: a second or two.</summary>
    private static readonly TimeSpan ListedWait = TimeSpan.FromSeconds(10);

    /// <summary>How long, once the printed job is listed, the session's own id may take to run again in place.</summary>
    private static readonly TimeSpan InPlaceWait = TimeSpan.FromSeconds(5);

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
        // A resume starts a fresh process beside the old one, which runs on idle unless stopped. The caller stops a
        // session it reads as idle, but a row can read ended while its process runs, and every say to the watchtower then
        // left one more behind (sixteen alive at once on DE1, 2026-10-23). So whatever the row says, a process the
        // listing still shows for this session is stopped first.
        if (await FindAsync(r => r.SessionId == request.SessionId && r.Pid is not null, cancellationToken) is not null)
        {
            await StopAsync(request.SessionId, cancellationToken);
        }

        var args = ResumeArguments(request);
        var run = await ClaudeCli.RunAsync(args, request.Text, request.Cwd, ResumeTimeout, cancellationToken);
        var job = Backgrounded().Match(run.Output);
        if (run.ExitCode != 0 || !job.Success)
        {
            throw new SessionDeliveryException($"claude --bg --resume failed ({run.ExitCode}): {Clip(run.Output + run.Error)}");
        }

        // The job it prints is not always the one that carries on: woken in place, the session runs again under its own id
        // with a new process, and the job printed is a short-lived one of its own that ends with no turn (0 tokens). The
        // row followed that job, took its own running session for a copy left behind, read ended, and the watchtower
        // started another beside it (DE1, 2026-10-24). So the session's own id, running again, wins; the printed job only
        // when it stays the one running.
        var id = job.Groups[1].Value;
        var until = DateTimeOffset.UtcNow + ListedWait;
        DateTimeOffset? jobSeen = null;
        string? jobSession = null;
        while (true)
        {
            var text = await ListOrEmptyAsync(cancellationToken);
            var listed = ClaudeAgentsJson.Parse(text) ?? [];
            if (listed.FirstOrDefault(r => r.SessionId == request.SessionId && r.Pid is not null) is not null)
            {
                return request.SessionId;
            }
            if (jobSession is null && listed.FirstOrDefault(r => r.Id == id) is { } row)
            {
                jobSession = row.SessionId;
                jobSeen = DateTimeOffset.UtcNow;
            }
            if (jobSeen is { } seen && DateTimeOffset.UtcNow - seen > InPlaceWait)
            {
                return jobSession!;
            }
            if (DateTimeOffset.UtcNow > until)
            {
                return jobSession ?? throw new SessionDeliveryException($"the session was continued as job {id}, but claude agents did not list it within {ListedWait.TotalSeconds:0} s");
            }
            await Task.Delay(Step, cancellationToken);
        }
    }

    private async Task<string> ListOrEmptyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await agents.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "[]";
        }
    }

    /// <summary>
    /// The resume's arguments: the full session id and nothing else. A background session keeps the options it was started
    /// with (its name, permission mode, model, effort, Remote Control, and a worker's --strict-mcp-config), and Claude
    /// Code wakes it with them under its own id; any flag passed here makes it start a copy under a new id with those
    /// flags alone. A worker's copy lost --strict-mcp-config and sat "blocked" on the repo's MCP approval, which a
    /// background session has no one to ask (TIA1658's and TIA1774's workers, compacted, 2026-10-23). The session id
    /// must be the full one: the short job id starts a copy too.
    /// </summary>
    public static IReadOnlyList<string> ResumeArguments(ResumeRequest request) => ["--bg", "--resume", request.SessionId];

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
    public static string RelayText(string text, string? replyTo = null) =>
        text.Trim() + "\n\n" + (replyTo is { Length: > 0 } ? ReplyNote(replyTo) : RelayNote);

    /// <summary>How each closing line <see cref="RelayText"/> adds begins: the transcript page cuts a prompt there, leaving it off what a person reads.</summary>
    public const string RelayMark = "(Sent through the pilot node.";

    /// <summary>The closing line <see cref="RelayText"/> adds when the sender is a person or a client: the answer is the recipient's own reply.</summary>
    public const string RelayNote = RelayMark + " The sender cannot be messaged back: answer in your own reply, not with SendMessage.)";

    /// <summary>
    /// The closing line when the sender is a session on this node: the way back is a say to its row, as SendMessage
    /// reaches only the relay, gone once it has sent. A session told only to answer in its own reply SendMessage'd its
    /// answer anyway, to an address that had gone, and the sender never heard it (DE1's pilot session, 2026-10-28).
    /// </summary>
    public static string ReplyNote(string replyTo) =>
        $"{RelayMark} To answer, run: pilot say {replyTo} \"<your answer>\". Not SendMessage: it cannot reach the sender.)";

    /// <summary>The route a relay took when it wrote straight to the session's endpoint.</summary>
    public const string RouteDirect = "direct";

    /// <summary>
    /// The text to the session over its intercom endpoint (<see cref="Intercom"/>), and only when that fails by the
    /// <c>claude -p</c> relay (<see cref="RelayViaCliAsync"/>).
    /// </summary>
    /// <returns>The route: <c>direct</c>, or <c>cli-relay (direct: why not)</c>.</returns>
    public async Task<string> RelayAsync(RelayRequest request, CancellationToken cancellationToken)
    {
        string why;
        try
        {
            var endpoint = request.SessionId is { } id ? profile?.FindIntercom(id) : null;
            if (endpoint is null)
            {
                throw new SessionDeliveryException(profile is null || request.SessionId is null ? "no profile to find its endpoint in" : "no live process has its session record");
            }
            await Intercom.SendAsync(endpoint, request.From, RelayText(request.Text, request.ReplyTo), cancellationToken);
            return RouteDirect;
        }
        catch (SessionDeliveryException ex)
        {
            why = ex.Message;
        }

        await RelayViaCliAsync(request, cancellationToken);
        return $"cli-relay (direct: {why})";
    }

    /// <summary>The fallback: a throwaway <c>claude -p</c> session allowed SendMessage carries the text.</summary>
    protected virtual async Task<string> RelayViaCliAsync(RelayRequest request, CancellationToken cancellationToken)
    {
        var mark = $"======== {Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..4])} ========";
        var kind = request.Kind == SessionKind.Background ? "bg" : "interactive";
        var prompt = string.Format(System.Globalization.CultureInfo.InvariantCulture, RelayPrompt, mark, kind, request.Name, RelayText(request.Text, request.ReplyTo));
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
