using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Node.Flights;

namespace Pilot.Node.Sessions;

/// <summary>
/// One running <c>claude -p --input-format stream-json --output-format stream-json</c>, seen through the three things a
/// <see cref="StreamSession"/> needs of it, so a test drives the session with a fake and no real claude.
/// </summary>
public interface IStreamProcess : IAsyncDisposable
{
    /// <summary>Writes <paramref name="line"/> and a newline to its standard input, flushed.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>The next line of its standard output; null once the output has ended.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Completes with its exit code when it ends, whoever ended it.</summary>
    Task<int> Exited { get; }

    /// <summary>The last lines it wrote to standard error (what it said as it died); empty when none.</summary>
    string ErrorTail { get; }

    /// <summary>Ends it and its children now; nothing happens when it has gone already.</summary>
    void Kill();
}

/// <summary>Starts the process behind a <see cref="StreamSession"/>.</summary>
public interface IStreamProcessLauncher
{
    IStreamProcess Start(IReadOnlyList<string> arguments, string workingFolder);
}

/// <summary>What changed in a <see cref="StreamSession"/>.</summary>
public enum StreamChangeKind
{
    /// <summary>The session id is known (or changed): claude's system/init line.</summary>
    Init,
    /// <summary>A turn began (a message was taken); <see cref="StreamSession.PartialText"/> is empty again.</summary>
    TurnStarted,
    /// <summary>More of the current turn's words arrived in <see cref="StreamSession.PartialText"/>.</summary>
    Text,
    /// <summary>What it is doing changed (<see cref="StreamSession.Doing"/>).</summary>
    Doing,
    /// <summary>A message was held behind the running turn, or taken from the queue.</summary>
    Queued,
    /// <summary>A turn ended; <see cref="StreamSessionChange.Turn"/> carries its timing.</summary>
    TurnEnded,
    /// <summary>The process has ended; <see cref="StreamSessionChange.ExitCode"/> says how.</summary>
    Exited,
}

/// <param name="Kind">What changed.</param>
/// <param name="Turn">The timing of the turn that ended (<see cref="StreamChangeKind.TurnEnded"/> only).</param>
/// <param name="ExitCode">The exit code (<see cref="StreamChangeKind.Exited"/> only).</param>
/// <param name="Requested">True when the node ended it (<see cref="StreamSession.DisposeAsync"/>), false for an unexpected exit.</param>
/// <param name="Error">The last of its standard error on an exit.</param>
public sealed record StreamSessionChange(StreamChangeKind Kind, TurnTiming? Turn = null, int? ExitCode = null, bool Requested = false, string? Error = null);

/// <summary>How long one turn took.</summary>
/// <param name="StartedAt">When the turn began: the send, or, for a message held behind another turn, the end of that turn.</param>
/// <param name="FirstWord">From the start to the first text delta; null when the turn said no words (a tool-only turn).</param>
/// <param name="Length">From the start to the result.</param>
/// <param name="Waited">How long the message sat in the queue before the turn began (zero when it began at once).</param>
/// <param name="ClaudeDuration">The <c>duration_ms</c> claude reported for the turn, when it did.</param>
/// <param name="IsError">The result's <c>is_error</c>.</param>
public sealed record TurnTiming(DateTimeOffset StartedAt, TimeSpan? FirstWord, TimeSpan Length, TimeSpan Waited, TimeSpan? ClaudeDuration, bool IsError);

/// <summary>
/// A node-owned, long-lived Claude Code process that takes messages on stdin and streams its words on stdout
/// (<c>claude -p --input-format stream-json --output-format stream-json</c>), with a queue, an interrupt and per-turn timings.
/// It is the session itself, not a relay: the same process, and so the same prompt cache, answers every message.
/// </summary>
/// <remarks>
/// <para>The wire, from <c>claude --help</c> (<c>--input-format</c>, <c>--output-format</c>, <c>--include-partial-messages</c>,
/// <c>--session-id</c>, <c>--resume</c>) and the Agent SDK's documented stream-json protocol, one JSON object per line:</para>
/// <list type="bullet">
/// <item>In, a user message: <c>{"type":"user","message":{"role":"user","content":"&lt;text&gt;"}}</c>.</item>
/// <item>In, an interrupt: <c>{"type":"control_request","request_id":"&lt;id&gt;","request":{"subtype":"interrupt"}}</c>; claude ends the running turn with a result and answers with <c>{"type":"control_response","response":{"subtype":"success","request_id":"&lt;id&gt;"}}</c>.</item>
/// <item>Out, the start: <c>{"type":"system","subtype":"init","session_id":"&lt;id&gt;",...}</c>.</item>
/// <item>Out, the words as they come (<c>--include-partial-messages</c>): <c>{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"..."}}}</c>.</item>
/// <item>Out, a whole assistant message, whose content may hold <c>{"type":"tool_use","name":"Bash","input":{"command":"pilot status"}}</c>: what it is doing.</item>
/// <item>Out, a tool's answer, as a <c>user</c> message with a <c>tool_result</c> block: the tool has finished.</item>
/// <item>Out, the end of a turn: <c>{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"session_id":"&lt;id&gt;",...}</c>, one per user message, in order.</item>
/// </list>
/// <para>A message written while a turn runs is read by claude after that turn's result, so it is held as queued here, and its turn
/// begins (and is timed from) that result. Anything else claude prints is ignored, and logged once per kind.</para>
/// </remarks>
public sealed class StreamSession : IAsyncDisposable
{
    /// <summary>How many turns' timings are kept.</summary>
    public const int TimingsKept = 20;

    private readonly IStreamProcess _process;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly HashSet<string> _unknown = [];
    private readonly List<(string Text, DateTimeOffset At)> _queued = [];
    private readonly Queue<TurnTiming> _timings = new();
    private int _requests;
    private bool _stopping;
    private string _sessionId;
    private bool _working;
    private string _partialText = "";
    private string? _doing;
    private int? _exitCode;
    private DateTimeOffset _turnStarted;
    private TimeSpan _turnWaited;
    private TimeSpan? _firstWord;

    private StreamSession(IStreamProcess process, string sessionId, TimeProvider time, Action<string>? log)
    {
        _process = process;
        _sessionId = sessionId;
        _time = time;
        _log = log;
        _reader = Task.Run(ReadAsync);
    }

    /// <summary>
    /// The arguments of the process: print mode with stream-json both ways and partial words, then the shared session options
    /// (<see cref="ClaudeWorkerLauncher.SessionArguments"/>), then <c>--session-id</c> for a fresh session or <c>--resume</c> for a
    /// crash restart of <paramref name="sessionId"/>.
    /// </summary>
    public static IReadOnlyList<string> Arguments(WorkerStart start, string sessionId, bool resume) =>
    [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
        resume ? "--resume" : "--session-id", sessionId,
        .. ClaudeWorkerLauncher.SessionArguments(start),
    ];

    /// <summary>
    /// Starts the process in the start's repo folder: a fresh session under a new id, or, with <paramref name="resumeId"/>, that session
    /// again. Pass the start through <see cref="ClaudeWorkerLauncher.WithMcp"/> first to have the lean MCP config.
    /// </summary>
    public static StreamSession Start(WorkerStart start, string? resumeId, IStreamProcessLauncher launcher, TimeProvider? time = null, Action<string>? log = null)
    {
        var id = resumeId ?? Guid.NewGuid().ToString();
        var process = launcher.Start(Arguments(start, id, resumeId is not null), start.RepoPath);
        return new StreamSession(process, id, time ?? TimeProvider.System, log);
    }

    /// <summary>Raised, on the thread that saw it and never under a lock, for each change; a handler that throws is logged, not fatal.</summary>
    public event Action<StreamSessionChange>? Changed;

    /// <summary>The session id: the one asked for until claude's init line says (a resume may continue under another).</summary>
    public string SessionId => Read(() => _sessionId);

    /// <summary>True while a turn runs (from the message taken to its result).</summary>
    public bool Working => Read(() => _working);

    /// <summary>The words of the current turn so far, or of the last one once it has ended; empty before the first.</summary>
    public string PartialText => Read(() => _partialText);

    /// <summary>What it is doing in plain words ("running pilot status"); null when it is not in a tool.</summary>
    public string? Doing => Read(() => _doing);

    /// <summary>The messages held behind the running turn, oldest first.</summary>
    public IReadOnlyList<string> Queued => Read(() => (IReadOnlyList<string>)[.. _queued.Select(q => q.Text)]);

    /// <summary>The timings of the last <see cref="TimingsKept"/> turns, oldest first.</summary>
    public IReadOnlyList<TurnTiming> Timings => Read(() => (IReadOnlyList<TurnTiming>)[.. _timings]);

    /// <summary>The exit code once the process has ended; null while it runs.</summary>
    public int? ExitCode => Read(() => _exitCode);

    private T Read<T>(Func<T> read)
    {
        lock (_lock)
        {
            return read();
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> as a user message at once. It begins a turn when the session is idle; while a turn runs it is
    /// held as queued, behind what it is doing. Throws when the process has ended.
    /// </summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var changes = new List<StreamSessionChange>();
            lock (_lock)
            {
                if (_exitCode is not null)
                {
                    throw new InvalidOperationException($"the session has ended (exit {_exitCode})");
                }
                var now = _time.GetUtcNow();
                if (_working)
                {
                    _queued.Add((text, now));
                    changes.Add(new StreamSessionChange(StreamChangeKind.Queued));
                }
                else
                {
                    BeginTurn(now, TimeSpan.Zero, changes);
                }
            }
            // Written inside the write gate, so lines go out in the order the state saw them. Raised after the write: a handler
            // never delays the message.
            await _process.WriteLineAsync(UserLine(text), cancellationToken);
            Raise(changes);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Writes the interrupt control request: claude ends the running turn with a result, then takes the next queued message.</summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            string id;
            lock (_lock)
            {
                if (_exitCode is not null)
                {
                    return;
                }
                id = $"pilot-interrupt-{++_requests}";
            }
            await _process.WriteLineAsync(InterruptLine(id), cancellationToken);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>The user message line for <paramref name="text"/>.</summary>
    public static string UserLine(string text) =>
        new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = text } }.ToJsonString();

    /// <summary>The interrupt control request line for request <paramref name="requestId"/>.</summary>
    public static string InterruptLine(string requestId) =>
        new JsonObject { ["type"] = "control_request", ["request_id"] = requestId, ["request"] = new JsonObject { ["subtype"] = "interrupt" } }.ToJsonString();

    private void BeginTurn(DateTimeOffset now, TimeSpan waited, List<StreamSessionChange> changes)
    {
        _working = true;
        _partialText = "";
        _doing = null;
        _turnStarted = now;
        _turnWaited = waited;
        _firstWord = null;
        changes.Add(new StreamSessionChange(StreamChangeKind.TurnStarted));
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.ReadLineAsync(_stop.Token) is { } line)
            {
                try
                {
                    Handle(line);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.Invoke($"stream session: could not handle a line: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Invoke($"stream session: reading its output failed: {ex.Message}");
        }

        int code;
        try
        {
            code = await _process.Exited;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"stream session: its exit could not be read: {ex.Message}");
            code = -1;
        }
        var changes = new List<StreamSessionChange>();
        bool requested;
        lock (_lock)
        {
            requested = _stopping;
            _exitCode = code;
            _working = false;
            _doing = null;
            _queued.Clear();
            changes.Add(new StreamSessionChange(StreamChangeKind.Exited, ExitCode: code, Requested: requested, Error: requested ? null : _process.ErrorTail));
        }
        Raise(changes);
    }

    private void Handle(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            LogOnce("non-json", line);
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var type = Str(root, "type");
            var changes = new List<StreamSessionChange>();
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                switch (type)
                {
                    case "system" when Str(root, "subtype") == "init":
                        if (Str(root, "session_id") is { Length: > 0 } id)
                        {
                            _sessionId = id;
                            changes.Add(new StreamSessionChange(StreamChangeKind.Init));
                        }
                        break;
                    case "stream_event":
                        if (root.TryGetProperty("event", out var ev) && Str(ev, "type") == "content_block_delta"
                            && ev.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta" && Str(delta, "text") is { Length: > 0 } words)
                        {
                            if (!_working)
                            {
                                // Words nobody asked for in this session's view (a turn claude began itself): adopted as a turn.
                                BeginTurn(now, TimeSpan.Zero, changes);
                            }
                            _firstWord ??= now - _turnStarted;
                            _partialText += words;
                            if (_doing is not null)
                            {
                                _doing = null;
                                changes.Add(new StreamSessionChange(StreamChangeKind.Doing));
                            }
                            changes.Add(new StreamSessionChange(StreamChangeKind.Text));
                        }
                        break;
                    case "assistant":
                        if (ToolUse(root) is { } doing && doing != _doing)
                        {
                            _doing = doing;
                            changes.Add(new StreamSessionChange(StreamChangeKind.Doing));
                        }
                        break;
                    case "user":
                        // A tool's answer: the tool has finished.
                        if (_doing is not null)
                        {
                            _doing = null;
                            changes.Add(new StreamSessionChange(StreamChangeKind.Doing));
                        }
                        break;
                    case "result":
                        EndTurn(root, now, changes);
                        break;
                    case "control_response" or "control_request" or "rate_limit_event":
                        break;
                    default:
                        LogOnce(type is null ? "no-type" : Str(root, "subtype") is { } sub ? $"{type}/{sub}" : type, line);
                        break;
                }
            }
            Raise(changes);
        }
    }

    private void EndTurn(JsonElement result, DateTimeOffset now, List<StreamSessionChange> changes)
    {
        if (!_working)
        {
            return;
        }
        TimeSpan? reported = result.TryGetProperty("duration_ms", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetDouble(out var d)
            ? TimeSpan.FromMilliseconds(d)
            : null;
        var isError = result.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
        var timing = new TurnTiming(_turnStarted, _firstWord, now - _turnStarted, _turnWaited, reported, isError);
        _timings.Enqueue(timing);
        while (_timings.Count > TimingsKept)
        {
            _timings.Dequeue();
        }
        _doing = null;
        if (_queued.Count > 0)
        {
            // claude takes the next written message now: its turn begins here, and the held message is no longer held.
            var (_, at) = _queued[0];
            _queued.RemoveAt(0);
            _working = false;
            changes.Add(new StreamSessionChange(StreamChangeKind.TurnEnded, Turn: timing));
            changes.Add(new StreamSessionChange(StreamChangeKind.Queued));
            BeginTurn(now, now - at, changes);
        }
        else
        {
            _working = false;
            changes.Add(new StreamSessionChange(StreamChangeKind.TurnEnded, Turn: timing));
        }
    }

    private void Raise(List<StreamSessionChange> changes)
    {
        if (Changed is not { } handler)
        {
            return;
        }
        foreach (var change in changes)
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"stream session: a change handler failed on {change.Kind}: {ex.Message}");
            }
        }
    }

    private void LogOnce(string kind, string line)
    {
        if (_unknown.Add(kind))
        {
            _log?.Invoke($"stream session: ignored a '{kind}' line: {(line.Length > 200 ? line[..200] + "…" : line)}");
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The last tool_use of an assistant message, in plain words; null when it has none.</summary>
    private static string? ToolUse(JsonElement assistant)
    {
        if (!assistant.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        string? doing = null;
        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") == "tool_use" && Str(block, "name") is { } name)
            {
                doing = Describe(name, block.TryGetProperty("input", out var input) ? input : default);
            }
        }
        return doing;
    }

    /// <summary>What a tool call is, in plain words: <c>running pilot status</c>, <c>reading Program.cs</c>.</summary>
    public static string Describe(string tool, JsonElement input)
    {
        const string PilotTool = "mcp__pilot__";
        if (tool.StartsWith(PilotTool, StringComparison.Ordinal))
        {
            return "running pilot " + tool[PilotTool.Length..].Replace('_', ' ').Replace('-', ' ');
        }
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return "using " + tool["mcp__".Length..].Replace("__", " ").Replace('_', ' ');
        }
        switch (tool)
        {
            case "Bash" or "PowerShell":
                var command = (Str(input, "command") ?? "").Trim();
                var firstLine = command.Split('\n', 2)[0].Trim();
                var words = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0 && Path.GetFileNameWithoutExtension(words[0].Trim('"')).Equals("pilot", StringComparison.OrdinalIgnoreCase))
                {
                    return "running pilot " + string.Join(' ', words.Skip(1).Take(1));
                }
                return firstLine.Length == 0 ? "running a command" : $"running {(firstLine.Length > 60 ? firstLine[..60] + "…" : firstLine)}";
            case "Read":
                return "reading " + FileName(input);
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit":
                return "editing " + FileName(input);
            case "Grep" or "Glob":
                return "searching the repo";
            case "WebFetch" or "WebSearch":
                return "searching the web";
            case "Task" or "Agent":
                return "asking a helper";
            default:
                return "using " + tool;
        }
    }

    private static string FileName(JsonElement input) =>
        (Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path")) is { Length: > 0 } path ? Path.GetFileName(path.Replace('\\', '/')) : "a file";

    /// <summary>Ends the process (the exit is then reported as requested) and waits briefly for its output to end.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
        }
        try
        {
            _process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already.
        }
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            await _stop.CancelAsync();
        }
        await _process.DisposeAsync();
        _stop.Dispose();
    }
}

/// <summary><see cref="IStreamProcessLauncher"/> by the claude CLI, windowless, held in a kill-on-close job so the node's end is its end.</summary>
public sealed class ClaudeStreamProcessLauncher : IStreamProcessLauncher
{
    public IStreamProcess Start(IReadOnlyList<string> arguments, string workingFolder)
    {
        var exe = ClaudeCli.FindOnPath() ?? throw new FileNotFoundException("claude is not on PATH");
        var process = System.Diagnostics.Process.Start(ClaudeCli.StartInfo(exe, arguments, workingFolder)) ?? throw new InvalidOperationException("claude did not start");
        return new RealStreamProcess(process);
    }

    private sealed class RealStreamProcess : IStreamProcess
    {
        private readonly System.Diagnostics.Process _process;
        private readonly KillOnCloseJob? _job;
        private readonly Lock _lock = new();
        private readonly Queue<string> _errors = new();
        private readonly Task<int> _exited;

        public RealStreamProcess(System.Diagnostics.Process process)
        {
            _process = process;
            _job = KillOnCloseJob.Hold(process);
            // Standard error is drained, or a chatty claude would block on a full pipe; the last lines are kept for an exit report.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                    {
                        lock (_lock)
                        {
                            _errors.Enqueue(line);
                            while (_errors.Count > 10)
                            {
                                _errors.Dequeue();
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
            _exited = Task.Run(async () =>
            {
                await process.WaitForExitAsync();
                return process.ExitCode;
            });
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _process.StandardOutput.ReadLineAsync(cancellationToken);

        public Task<int> Exited => _exited;

        public string ErrorTail
        {
            get
            {
                lock (_lock)
                {
                    return string.Join('\n', _errors);
                }
            }
        }

        public void Kill() => _process.Kill(entireProcessTree: true);

        public ValueTask DisposeAsync()
        {
            _job?.Dispose();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
