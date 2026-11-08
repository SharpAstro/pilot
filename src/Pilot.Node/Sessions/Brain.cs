using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>The brain could not answer in time or at all: it is down, busy with the person, or said nothing the node can read. The caller runs the old path.</summary>
public sealed class BrainDownException(string message) : Exception(message);

/// <summary>What the brain answered one request of the node.</summary>
/// <param name="Answer">The JSON object it answered with, cloned off the stream.</param>
/// <param name="Took">From the write to its stdin to the closing brace of the answer.</param>
/// <param name="FirstWord">From the write to its first word of the turn that held the answer; null when none was seen.</param>
public sealed record BrainReply(JsonElement Answer, TimeSpan Took, TimeSpan? FirstWord);

/// <summary>How one ask of the brain went, kept for the last <see cref="BrainClient.TimingsKept"/>.</summary>
/// <param name="Kind">What was asked: <c>summary</c>, <c>ring</c>, <c>wording</c>.</param>
/// <param name="Took">How long it took, to the answer or to the give-up.</param>
/// <param name="Why">Null when the brain answered; otherwise why the old path ran.</param>
public sealed record BrainTiming(string Kind, TimeSpan Took, string? Why);

/// <summary>
/// The node's brain as a thinker (PIL855): one message on the brain's stdin and a structured answer back, in place of a one-off
/// <c>claude -p</c> call. The seam tests give a fake, so no test runs <c>claude</c>.
/// </summary>
public interface IBrain
{
    /// <summary>
    /// Asks <paramref name="request"/> (what to decide and the JSON object to answer with) of kind <paramref name="kind"/>, giving up after
    /// <paramref name="timeout"/>. Throws <see cref="BrainDownException"/>, saying why, when the brain is not there, is waiting on the person,
    /// does not answer in time, or does not answer in a form the node can read: never blocks past the timeout.
    /// </summary>
    Task<BrainReply> AskAsync(string kind, string request, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Why the brain cannot be asked right now (no process, starting, waiting on the person); null when it can.</summary>
    string? Unavailable => null;
}

/// <summary>
/// <see cref="IBrain"/> over the stream session of the watchtower. The request is a user message written to its stdin at once (mid-turn too:
/// stream-json reads it at the model's next step), tagged with an id; the answer is the one line <c>NODE-ANSWER &lt;id&gt; {json}</c> in the
/// words of the turn, found as they stream, so a request is answered in the turn it is written in, whether the brain was idle or working.
/// One request at a time; the wait for the turn counts against its timeout.
/// </summary>
public sealed class BrainClient(Func<StreamSession?> session, TimeProvider time) : IBrain
{
    /// <inheritdoc />
    public string? Unavailable => Reason(session());

    /// <summary>How many timings are kept.</summary>
    public const int TimingsKept = 20;

    /// <summary>The marker line the brain answers with.</summary>
    public const string AnswerMarker = "NODE-ANSWER";

    /// <summary>Where each ask's timing and any fall back to the old path is noted; null notes none.</summary>
    public Action<string>? Log { get; set; }

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Queue<BrainTiming> _timings = new();
    private int _ids;

    /// <summary>The last <see cref="TimingsKept"/> asks, oldest first.</summary>
    public IReadOnlyList<BrainTiming> Timings
    {
        get
        {
            lock (_lock)
            {
                return [.. _timings];
            }
        }
    }

    public async Task<BrainReply> AskAsync(string kind, string request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        try
        {
            var reply = await AskCoreAsync(kind, request, timeout, cancellationToken);
            Note(kind, time.GetElapsedTime(started), null);
            return reply;
        }
        catch (BrainDownException ex)
        {
            Note(kind, time.GetElapsedTime(started), ex.Message);
            throw;
        }
    }

    private void Note(string kind, TimeSpan took, string? why)
    {
        lock (_lock)
        {
            _timings.Enqueue(new BrainTiming(kind, took, why));
            while (_timings.Count > TimingsKept)
            {
                _timings.Dequeue();
            }
        }
        Log?.Invoke(why is null ? $"brain: {kind} answered in {took.TotalSeconds:0.0} s" : $"brain: {kind} not answered after {took.TotalSeconds:0.0} s ({why}); the old path runs");
    }

    private async Task<BrainReply> AskCoreAsync(string kind, string request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await _one.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrainDownException("it was busy with another request");
        }
        try
        {
            if (Reason(session()) is { } down)
            {
                throw new BrainDownException(down);
            }
            var stream = session()!;
            var id = $"n{Interlocked.Increment(ref _ids)}";
            var answered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wrote = time.GetTimestamp();
            TimeSpan? firstWord = null;

            void Check()
            {
                if (Extract(stream.PartialText, id) is { } found)
                {
                    answered.TrySetResult(found);
                }
            }

            void Changed(StreamSessionChange change)
            {
                switch (change.Kind)
                {
                    case StreamChangeKind.Text:
                        firstWord ??= time.GetElapsedTime(wrote);
                        Check();
                        break;
                    case StreamChangeKind.Exited:
                        answered.TrySetException(new BrainDownException("it ended before it answered"));
                        break;
                    case StreamChangeKind.Asked:
                        // It stops at a question for the person: the turn will not end for the node's sake.
                        answered.TrySetException(new BrainDownException("it is waiting on the person"));
                        break;
                }
            }

            stream.Changed += Changed;
            try
            {
                try
                {
                    await stream.SendAsync(Message(id, kind, request), limit.Token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
                {
                    throw new BrainDownException($"it could not be written to: {ex.Message}");
                }
                Check();
                var json = await answered.Task.WaitAsync(limit.Token);
                return new BrainReply(json, time.GetElapsedTime(wrote), firstWord);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrainDownException($"it did not answer in {timeout.TotalSeconds:0} s");
            }
            finally
            {
                stream.Changed -= Changed;
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Why the brain cannot be asked now; null when it can.</summary>
    private static string? Reason(StreamSession? stream) =>
        stream is null ? "it has no running process"
        : stream.ExitCode is not null ? "its process has ended"
        : stream.Starting ? "its process is starting"
        : stream.Pending.Count > 0 ? "it is waiting on the person"
        : null;

    /// <summary>
    /// How a request begins: in the node's voice (<see cref="NodeVoice.Prefix"/>), so a reader of the transcript takes it as the node's and
    /// never as the person's, then the request's tag.
    /// </summary>
    public const string RequestPrefix = NodeVoice.Prefix + "[node request ";

    /// <summary>
    /// Whether <paramref name="text"/>, a message waiting to be read or a line of the transcript, is one of the node's requests of its brain.
    /// </summary>
    public static bool IsRequest(string text) => text.StartsWith(RequestPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="text"/>, the brain's words so far or one entry of them, holds its answer to a request: kept out of the person's
    /// chat. Anywhere in it, as <see cref="Extract"/> finds it, so an answer after a few words of lead-in is kept out too.
    /// </summary>
    public static bool IsAnswer(string text) => text.Contains(AnswerMarker + " ", StringComparison.Ordinal);

    /// <summary>
    /// The person's part of <paramref name="text"/>: the words before the brain's answer to the node, when it holds one, else all of it. A
    /// request read mid-turn is answered in the turn that is answering the person, so one turn can hold both: their reply stays theirs.
    /// </summary>
    public static string ForPerson(string text)
    {
        var at = text.IndexOf(AnswerMarker + " ", StringComparison.Ordinal);
        return at < 0 ? text : text[..at].TrimEnd();
    }

    /// <summary>
    /// Whether a transcript entry is one side of a brain exchange: a request (read as a note, its <see cref="NodeVoice.Prefix"/> cut off)
    /// or the answer to one. Neither is the person's conversation.
    /// </summary>
    public static bool IsBrainExchange(TranscriptEntry entry) =>
        entry.Kind == TranscriptKind.User ? entry.Note && entry.Text.StartsWith(RequestPrefix[NodeVoice.Prefix.Length..], StringComparison.Ordinal)
        : entry.Kind == TranscriptKind.Assistant && IsAnswer(entry.Text) && ForPerson(entry.Text).Length == 0;

    /// <summary>The message written to the brain's stdin for request <paramref name="id"/>.</summary>
    public static string Message(string id, string kind, string request) => $"""
        {RequestPrefix}{id}: {kind}] This is the node asking, not the person; do not answer in chat.

        {request.Trim()}

        Answer from what is above and what you already know; run no tool for this. Reply with exactly one line and nothing else: {AnswerMarker} {id} followed by one JSON object on that same line, no line breaks inside it (write a line break inside a text as \n).
        """;

    /// <summary>The JSON object after <c>NODE-ANSWER &lt;id&gt;</c> in <paramref name="text"/>, once it is whole; null before that.</summary>
    public static JsonElement? Extract(string text, string id)
    {
        var marker = $"{AnswerMarker} {id}";
        var at = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var rest = text[(at + marker.Length)..].TrimStart();
        if (!rest.StartsWith('{'))
        {
            return null;
        }
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(rest));
            if (!JsonDocument.TryParseValue(ref reader, out var doc))
            {
                return null;
            }
            using (doc)
            {
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
        }
        catch (JsonException)
        {
            // Not whole yet.
            return null;
        }
    }
}
