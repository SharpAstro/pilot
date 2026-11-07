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
    /// <summary>
    /// What is waiting to be read changed: a message was written mid-turn (<see cref="StreamSession.ReadAtNextStep"/>) or read at a step,
    /// or one was held while the process starts (<see cref="StreamSession.Queued"/>) or written once it is up.
    /// </summary>
    Queued,
    /// <summary>A turn ended; <see cref="StreamSessionChange.Turn"/> carries its timing.</summary>
    TurnEnded,
    /// <summary>The process has ended; <see cref="StreamSessionChange.ExitCode"/> says how.</summary>
    Exited,
    /// <summary>
    /// claude asks leave for a tool or puts a question to the person (a <c>can_use_tool</c> control request): <see cref="StreamSessionChange.Ask"/>
    /// is now in <see cref="StreamSession.Pending"/>, unless the session's <see cref="StreamSession.AutoAnswer"/> answered it at once.
    /// </summary>
    Asked,
    /// <summary>
    /// An ask left <see cref="StreamSession.Pending"/>: answered (by <see cref="StreamSession.AnswerAsync"/> or by <see cref="StreamSession.AutoAnswer"/>),
    /// or called off by claude (its turn ended or was interrupted, or the process ended). <see cref="StreamSessionChange.Outcome"/> says which.
    /// </summary>
    Answered,
}

/// <param name="Kind">What changed.</param>
/// <param name="Turn">The timing of the turn that ended (<see cref="StreamChangeKind.TurnEnded"/> only).</param>
/// <param name="ExitCode">The exit code (<see cref="StreamChangeKind.Exited"/> only).</param>
/// <param name="Requested">True when the node ended it (<see cref="StreamSession.DisposeAsync"/>), false for an unexpected exit.</param>
/// <param name="Error">The last of its standard error on an exit.</param>
/// <param name="Ask">The ask that arrived or left (<see cref="StreamChangeKind.Asked"/> and <see cref="StreamChangeKind.Answered"/>).</param>
/// <param name="Outcome">How an ask left (<see cref="StreamChangeKind.Answered"/>): <c>allowed</c>, <c>denied</c>, <c>answered</c> or <c>cancelled</c>, with the person's words where there are any.</param>
public sealed record StreamSessionChange(StreamChangeKind Kind, TurnTiming? Turn = null, int? ExitCode = null, bool Requested = false, string? Error = null, PendingAsk? Ask = null, string? Outcome = null);

/// <summary>One option of a question claude asks (<c>AskUserQuestion</c>).</summary>
public sealed record AskOption(string Label, string? Description);

/// <summary>One question of an <c>AskUserQuestion</c> call: its words, a short header, whether several options may be chosen, and the options.</summary>
public sealed record AskQuestion(string Question, string? Header, bool MultiSelect, IReadOnlyList<AskOption> Options);

/// <summary>
/// A <c>can_use_tool</c> control request claude is waiting on: it stops at the tool until the control response with this
/// <see cref="Id"/> arrives. <see cref="Questions"/> is set for <c>AskUserQuestion</c>, whose answer is the person's choice of options.
/// </summary>
/// <param name="Id">The control request's <c>request_id</c>: what the answer names.</param>
/// <param name="Tool">The tool's name (<c>Bash</c>, <c>Edit</c>, <c>AskUserQuestion</c>).</param>
/// <param name="Input">The tool's input, cloned off the line so it outlives it.</param>
/// <param name="ToolUseId">The tool call's id, when claude gave one.</param>
/// <param name="At">When the request arrived.</param>
/// <param name="Questions">The questions of an <c>AskUserQuestion</c>; null for any other tool.</param>
public sealed record PendingAsk(string Id, string Tool, JsonElement Input, string? ToolUseId, DateTimeOffset At, IReadOnlyList<AskQuestion>? Questions)
{
    /// <summary>The tool every question comes by.</summary>
    public const string QuestionTool = "AskUserQuestion";

    /// <summary>Whether this is a question to the person rather than leave for a tool.</summary>
    public bool IsQuestion => Tool == QuestionTool;

    /// <summary>The one line of the input that says what the tool would act on (its command, file, url, query or pattern); null when it has none.</summary>
    public string? Summary
    {
        get
        {
            if (Input.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (var name in new[] { "command", "file_path", "notebook_path", "path", "url", "query", "pattern" })
            {
                if (Input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
            return null;
        }
    }
}

/// <summary>
/// The answer to a <see cref="PendingAsk"/>: leave given or refused, with the person's reason on a refusal, and for a question the
/// option each question's answer is (a multi-select question's several options joined by a comma, an Other box's words as they are).
/// </summary>
/// <param name="Allow">True to let the tool run (for a question: to take <paramref name="Answers"/>).</param>
/// <param name="Message">What claude is told on a refusal.</param>
/// <param name="Answers">A question's text to the answer given for it.</param>
public sealed record AskAnswer(bool Allow, string? Message = null, IReadOnlyDictionary<string, string>? Answers = null)
{
    public static readonly AskAnswer Allowed = new(true);

    public static AskAnswer Denied(string? why = null) => new(false, why);
}

/// <summary>How long one turn took.</summary>
/// <param name="StartedAt">When the turn began: the send, or, for a message held behind another turn, the end of that turn.</param>
/// <param name="FirstWord">From the start to the first text delta; null when the turn said no words (a tool-only turn).</param>
/// <param name="Length">From the start to the result.</param>
/// <param name="Waited">How long the message sat unread before the turn began: held while the process started, or written mid-turn and not read at a step (zero when it began at once).</param>
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
/// <para>A message sent while a turn runs is written to stdin at once, never held: stream-json takes it at the model's next step,
/// inside the running turn (a message sent during a 20 s tool call is answered in that same turn). It is listed in
/// <see cref="ReadAtNextStep"/> until a tool's answer (the step boundary) shows claude has taken it. A turn that ends with one still
/// unread (no step came) hands the turn to it: claude reads it next, so its turn begins (and is timed from) that result.</para>
/// <para>The queue (<see cref="Queued"/>) is only for a message sent while the process is starting or restarting (<see cref="Start"/>
/// with <c>starting</c>, until <see cref="ReadyAsync"/>): nothing can be written to it yet, so it is held and written, in order, once
/// the process is up.</para>
/// <para>A permission prompt or an <c>AskUserQuestion</c> arrives, with <c>--permission-prompt-tool stdio</c>, on stdout as
/// <c>{"type":"control_request","request_id":"&lt;id&gt;","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{...},"tool_use_id":"&lt;id&gt;"}}</c>
/// and claude waits for <c>{"type":"control_response","response":{"subtype":"success","request_id":"&lt;id&gt;","response":{"behavior":"allow","updatedInput":{...}}}}</c>
/// (or <c>"behavior":"deny","message":"..."</c>). Each is listed in <see cref="Pending"/> until <see cref="AnswerAsync"/> writes its response, once:
/// a second answer to the same request is refused. A question's answer is the input again with <c>answers</c> (question text to the chosen label) added.
/// What <see cref="AutoAnswer"/> allows is answered by the session itself and never listed. Claude calls an ask off with
/// <c>control_cancel_request</c>, and a turn that ends has none left.</para>
/// <para>Anything else claude prints is ignored, and logged once per kind.</para>
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
    // Held while the process starts or restarts: not written yet.
    private readonly List<(string Text, DateTimeOffset At)> _queued = [];
    // Written mid-turn, not yet read at a step.
    private readonly List<(string Text, DateTimeOffset At)> _midTurn = [];
    private readonly Queue<TurnTiming> _timings = new();
    // can_use_tool requests claude waits on, oldest first, until a control response goes out or claude calls them off.
    private readonly List<PendingAsk> _pending = [];
    private int _requests;
    private bool _stopping;
    private bool _starting;
    private string _sessionId;
    private bool _working;
    private string _partialText = "";
    private string? _doing;
    private int? _exitCode;
    private long _contextTokens;
    private DateTimeOffset _turnStarted;
    private TimeSpan _turnWaited;
    private TimeSpan? _firstWord;

    private StreamSession(IStreamProcess process, string sessionId, TimeProvider time, Action<string>? log, bool starting)
    {
        _process = process;
        _sessionId = sessionId;
        _time = time;
        _log = log;
        _starting = starting;
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
        // Every permission prompt and every question arrives on stdout as a can_use_tool control request, answered by AnswerAsync.
        "--permission-prompt-tool", PermissionPromptTool,
    ];

    /// <summary>The value of <c>--permission-prompt-tool</c> that has claude ask on the stream itself.</summary>
    public const string PermissionPromptTool = "stdio";

    /// <summary>
    /// Starts the process in the start's repo folder: a fresh session under a new id, or, with <paramref name="resumeId"/>, that session
    /// again. Pass the start through <see cref="ClaudeWorkerLauncher.WithMcp"/> first to have the lean MCP config.
    /// With <paramref name="starting"/> the session holds every message sent to it (<see cref="Queued"/>) until <see cref="ReadyAsync"/>,
    /// for a caller that has more to do before the process takes input (a fresh start, a restart).
    /// </summary>
    public static StreamSession Start(WorkerStart start, string? resumeId, IStreamProcessLauncher launcher, TimeProvider? time = null, Action<string>? log = null, bool starting = false)
    {
        var id = resumeId ?? Guid.NewGuid().ToString();
        var process = launcher.Start(Arguments(start, id, resumeId is not null), start.RepoPath);
        return new StreamSession(process, id, time ?? TimeProvider.System, log, starting);
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

    /// <summary>The messages held while the process starts or restarts (not written yet), oldest first; empty once it is up.</summary>
    public IReadOnlyList<string> Queued => Read(() => (IReadOnlyList<string>)[.. _queued.Select(q => q.Text)]);

    /// <summary>The messages written to stdin mid-turn that claude has not read yet (it reads them at its next step), oldest first.</summary>
    public IReadOnlyList<string> ReadAtNextStep => Read(() => (IReadOnlyList<string>)[.. _midTurn.Select(q => q.Text)]);

    /// <summary>True while the session holds messages for a process that is starting or restarting.</summary>
    public bool Starting => Read(() => _starting);

    /// <summary>The timings of the last <see cref="TimingsKept"/> turns, oldest first.</summary>
    public IReadOnlyList<TurnTiming> Timings => Read(() => (IReadOnlyList<TurnTiming>)[.. _timings]);

    /// <summary>The asks claude waits on (a permission prompt, a question), oldest first; empty when none.</summary>
    public IReadOnlyList<PendingAsk> Pending => Read(() => (IReadOnlyList<PendingAsk>)[.. _pending]);

    /// <summary>
    /// Decides a request before it is listed: an answer here is written at once and the ask is never <see cref="Pending"/> (the node answers the
    /// safe tools itself); null leaves it to the person. Never asked for a question. Set before the first line is read.
    /// </summary>
    public Func<PendingAsk, AskAnswer?>? AutoAnswer { get; set; }

    /// <summary>The exit code once the process has ended; null while it runs.</summary>
    public int? ExitCode => Read(() => _exitCode);

    /// <summary>What its latest assistant message read: input, cache creation and cache read tokens (<c>message.usage</c>), so the size of its context; 0 before the first.</summary>
    public long ContextTokens => Read(() => _contextTokens);

    private T Read<T>(Func<T> read)
    {
        lock (_lock)
        {
            return read();
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> as a user message at once. It begins a turn when the session is idle; while a turn runs it is
    /// written all the same and read by claude at its next step (<see cref="ReadAtNextStep"/>). Only while the process is starting or
    /// restarting is it held (<see cref="Queued"/>), until <see cref="ReadyAsync"/>. Throws when the process has ended.
    /// </summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            await DeliverAsync(text, null, cancellationToken);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// The process is up: the messages held while it started are written now, in order, the first beginning the turn and the rest
    /// read at its steps. Does nothing when it was not starting.
    /// </summary>
    public async Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            (string Text, DateTimeOffset At)[] held;
            lock (_lock)
            {
                if (!_starting)
                {
                    return;
                }
                _starting = false;
                held = [.. _queued];
                _queued.Clear();
            }
            Raise([new StreamSessionChange(StreamChangeKind.Queued)]);
            foreach (var (text, at) in held)
            {
                await DeliverAsync(text, at, cancellationToken);
            }
        }
        finally
        {
            _writes.Release();
        }
    }

    // With the write gate held. `heldSince` is when a message held while the process started was sent, for how long it waited.
    private async Task DeliverAsync(string text, DateTimeOffset? heldSince, CancellationToken cancellationToken)
    {
        var changes = new List<StreamSessionChange>();
        var held = false;
        lock (_lock)
        {
            if (_exitCode is not null)
            {
                throw new InvalidOperationException($"the session has ended (exit {_exitCode})");
            }
            var now = _time.GetUtcNow();
            if (_starting)
            {
                _queued.Add((text, now));
                changes.Add(new StreamSessionChange(StreamChangeKind.Queued));
                held = true;
            }
            else if (_working)
            {
                _midTurn.Add((text, now));
                changes.Add(new StreamSessionChange(StreamChangeKind.Queued));
            }
            else
            {
                BeginTurn(now, heldSince is { } at ? now - at : TimeSpan.Zero, changes);
            }
        }
        if (held)
        {
            Raise(changes);
            return;
        }
        // Written inside the write gate, so lines go out in the order the state saw them. Raised after the write: a handler
        // never delays the message.
        await _process.WriteLineAsync(UserLine(text), cancellationToken);
        Raise(changes);
    }

    /// <summary>Writes the interrupt control request: claude ends the running turn with a result, then takes the next queued message.</summary>
    public Task InterruptAsync(CancellationToken cancellationToken = default) => InterruptCoreAsync(null, cancellationToken);

    /// <summary>
    /// The interrupt, written only while a turn runs and <paramref name="queued"/> is the first message written but not yet read (the one
    /// claude takes next), so an answer-now never cancels the very turn it asked for when that turn began meanwhile. Returns whether it was written.
    /// </summary>
    public Task<bool> InterruptBeforeAsync(string queued, CancellationToken cancellationToken = default) => InterruptCoreAsync(queued, cancellationToken);

    private async Task<bool> InterruptCoreAsync(string? onlyBefore, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            string id;
            lock (_lock)
            {
                if (_exitCode is not null || onlyBefore is not null && (!_working || _midTurn.Count == 0 || _midTurn[0].Text != onlyBefore))
                {
                    return false;
                }
                id = $"pilot-interrupt-{++_requests}";
            }
            await _process.WriteLineAsync(InterruptLine(id), cancellationToken);
            return true;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Writes the control response to the pending ask <paramref name="requestId"/>, once. Null when it was written; otherwise why it was
    /// not: the ask is not pending (answered already, called off, or never asked: a second answer is refused, whoever gives it), the
    /// session has ended, or a question's answer lacks one of its questions.
    /// </summary>
    public async Task<string?> AnswerAsync(string requestId, AskAnswer answer, CancellationToken cancellationToken = default)
    {
        PendingAsk ask;
        lock (_lock)
        {
            if (_exitCode is not null)
            {
                return $"the session has ended (exit {_exitCode})";
            }
            var found = _pending.FindIndex(a => a.Id == requestId);
            if (found < 0)
            {
                return $"ask {requestId} is not waiting: it was answered already, or claude called it off";
            }
            ask = _pending[found];
            if (answer.Allow && ask.IsQuestion && ask.Questions is { Count: > 0 } questions
                && questions.FirstOrDefault(q => answer.Answers is null || !answer.Answers.ContainsKey(q.Question)) is { } missing)
            {
                return $"ask {requestId} needs an answer to \"{missing.Question}\"";
            }
            // Taken off the list before the write, under the lock: a second answer arriving meanwhile finds nothing to answer.
            _pending.RemoveAt(found);
        }
        try
        {
            await _writes.WaitAsync(cancellationToken);
            try
            {
                await _process.WriteLineAsync(ControlResponseLine(ask, answer), cancellationToken);
            }
            finally
            {
                _writes.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            lock (_lock)
            {
                if (_exitCode is null)
                {
                    _pending.Insert(0, ask);
                }
            }
            return $"the answer to {requestId} could not be written: {ex.Message}";
        }
        Raise([new StreamSessionChange(StreamChangeKind.Answered, Ask: ask, Outcome: OutcomeOf(ask, answer))]);
        return null;
    }

    private static string OutcomeOf(PendingAsk ask, AskAnswer answer) =>
        !answer.Allow ? answer.Message is { Length: > 0 } why ? $"denied: {why}" : "denied"
        : ask.IsQuestion ? "answered: " + string.Join("; ", (answer.Answers ?? new Dictionary<string, string>()).Select(a => $"{a.Key} {a.Value}"))
        : "allowed";

    /// <summary>
    /// The control response line for <paramref name="ask"/>: <c>allow</c> with the tool's input as it was (a question's gains its
    /// <c>answers</c>), or <c>deny</c> with a message claude is told.
    /// </summary>
    public static string ControlResponseLine(PendingAsk ask, AskAnswer answer)
    {
        JsonObject decision;
        if (answer.Allow)
        {
            var input = JsonNode.Parse(ask.Input.ValueKind == JsonValueKind.Undefined ? "{}" : ask.Input.GetRawText()) as JsonObject ?? new JsonObject();
            if (ask.IsQuestion)
            {
                var answers = new JsonObject();
                foreach (var (question, chosen) in answer.Answers ?? new Dictionary<string, string>())
                {
                    answers[question] = chosen;
                }
                input["answers"] = answers;
            }
            decision = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input };
        }
        else
        {
            decision = new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = answer.Message is { Length: > 0 } message ? message : ask.IsQuestion ? "The person declined to answer." : "The person denied this call.",
            };
        }
        return new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = ask.Id, ["response"] = decision },
        }.ToJsonString();
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
            _midTurn.Clear();
            _starting = false;
            CallOffAll(changes);
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
            PendingAsk? arrived = null;
            if (type == "control_request" && AskOf(root, _time.GetUtcNow()) is { } asked)
            {
                // Decided outside the lock: the node's own rules (what only acts inside the plane) answer the safe ones at once.
                if (!asked.IsQuestion && AutoAnswer?.Invoke(asked) is { } auto)
                {
                    _ = Task.Run(() => AnswerAutomaticallyAsync(asked, auto));
                    return;
                }
                arrived = asked;
            }
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
                        if (ContextOf(root) is { } context)
                        {
                            _contextTokens = context;
                        }
                        if (ToolUse(root) is { } doing && doing != _doing)
                        {
                            _doing = doing;
                            changes.Add(new StreamSessionChange(StreamChangeKind.Doing));
                        }
                        break;
                    case "user":
                        // A tool's answer: the tool has finished, and the model's next step reads what was written meanwhile.
                        if (_doing is not null)
                        {
                            _doing = null;
                            changes.Add(new StreamSessionChange(StreamChangeKind.Doing));
                        }
                        if (_midTurn.Count > 0 && _working)
                        {
                            _midTurn.Clear();
                            changes.Add(new StreamSessionChange(StreamChangeKind.Queued));
                        }
                        break;
                    case "result":
                        // Claude waits on nothing once the turn is over: what was asked and not answered is moot.
                        CallOffAll(changes);
                        EndTurn(root, now, changes);
                        break;
                    case "control_request" when arrived is not null:
                        _pending.Add(arrived);
                        changes.Add(new StreamSessionChange(StreamChangeKind.Asked, Ask: arrived));
                        break;
                    case "control_cancel_request":
                        if (Str(root, "request_id") is { } cancelled && _pending.FindIndex(a => a.Id == cancelled) is >= 0 and var at)
                        {
                            changes.Add(new StreamSessionChange(StreamChangeKind.Answered, Ask: _pending[at], Outcome: "cancelled"));
                            _pending.RemoveAt(at);
                        }
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

    // With the lock held.
    private void CallOffAll(List<StreamSessionChange> changes)
    {
        foreach (var ask in _pending)
        {
            changes.Add(new StreamSessionChange(StreamChangeKind.Answered, Ask: ask, Outcome: "cancelled"));
        }
        _pending.Clear();
    }

    /// <summary>The ask a <c>can_use_tool</c> control request line is; null for any other control request.</summary>
    private static PendingAsk? AskOf(JsonElement line, DateTimeOffset now)
    {
        if (Str(line, "request_id") is not { Length: > 0 } id || !line.TryGetProperty("request", out var request) || Str(request, "subtype") != "can_use_tool"
            || Str(request, "tool_name") is not { Length: > 0 } tool)
        {
            return null;
        }
        var input = request.TryGetProperty("input", out var given) && given.ValueKind == JsonValueKind.Object ? given.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        return new PendingAsk(id, tool, input, Str(request, "tool_use_id"), now, tool == PendingAsk.QuestionTool ? QuestionsOf(input) : null);
    }

    private static IReadOnlyList<AskQuestion> QuestionsOf(JsonElement input)
    {
        var questions = new List<AskQuestion>();
        if (input.TryGetProperty("questions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Str(item, "question") is not { Length: > 0 } text)
                {
                    continue;
                }
                var options = new List<AskOption>();
                if (item.TryGetProperty("options", out var offered) && offered.ValueKind == JsonValueKind.Array)
                {
                    options.AddRange(offered.EnumerateArray().Where(o => Str(o, "label") is { Length: > 0 }).Select(o => new AskOption(Str(o, "label")!, Str(o, "description"))));
                }
                questions.Add(new AskQuestion(text, Str(item, "header"), item.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True, options));
            }
        }
        return questions;
    }

    // The node's own answer to a safe tool: written without the person, and never listed.
    private async Task AnswerAutomaticallyAsync(PendingAsk ask, AskAnswer answer)
    {
        try
        {
            await _writes.WaitAsync(_stop.Token);
            try
            {
                await _process.WriteLineAsync(ControlResponseLine(ask, answer), _stop.Token);
            }
            finally
            {
                _writes.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            _log?.Invoke($"stream session: the answer to {ask.Tool} ({ask.Id}) could not be written: {ex.Message}");
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
        if (_midTurn.Count > 0)
        {
            // No step came to read it in the turn: claude takes the next written message now. Its turn begins here.
            var (_, at) = _midTurn[0];
            _midTurn.RemoveAt(0);
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

    /// <summary>The context an assistant message of the session itself (not a subagent's) read; null when it carries no usage.</summary>
    private static long? ContextOf(JsonElement assistant)
    {
        if (assistant.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String
            || !assistant.TryGetProperty("message", out var message) || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        long Count(string name) => usage.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var v) ? v : 0;
        var total = Count("input_tokens") + Count("cache_creation_input_tokens") + Count("cache_read_input_tokens");
        return total > 0 ? total : null;
    }

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
