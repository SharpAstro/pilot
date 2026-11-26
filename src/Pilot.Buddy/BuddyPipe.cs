using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Pilot.Buddy;

/// <summary>
/// The wire of the local channel: one JSON line a call, one JSON line an answer, written and read by hand (no reflection, so it is AOT-clean).
/// </summary>
public static class BuddyWire
{
    public static string Write(BuddyCall call)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", call.Tool);
            writer.WriteString("text", call.Text);
            writer.WriteString("urgency", call.Urgency);
            writer.WriteString("card", call.Card);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Write(BuddyAnswer answer)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", answer.Ok);
            writer.WriteString("text", answer.Text);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static BuddyCall? ReadCall(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.TryGetProperty("tool", out var tool) && tool.GetString() is { Length: > 0 } name
                ? new BuddyCall(name, Str(root, "text"), Str(root, "urgency"), Str(root, "card"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static BuddyAnswer? ReadAnswer(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return new BuddyAnswer(root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, Str(root, "text") ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Where the running buddy listens: one named pipe per user.</summary>
public static class BuddyPipeName
{
    public static string ForUser(string user)
    {
        var safe = new string([.. user.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        return "pilot-buddy-" + safe;
    }

    public static string Current => ForUser(Environment.UserName);

    /// <summary>True when something listens on the pipe, asked of the file system so a missing buddy is known at once and nothing waits on a connect.</summary>
    public static bool Listening(string name) =>
        File.Exists(OperatingSystem.IsWindows() ? @"\\.\pipe\" + name : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name));
}

/// <summary>
/// The MCP server's end: connects to the buddy's pipe with a short deadline. With no buddy listening it answers <see cref="BuddyAnswer.NoBuddy"/>
/// without a connect; a buddy that listens but does not answer in time gets a refusal, never a wait without end.
/// </summary>
public sealed class PipeBuddyLink(string pipeName, TimeSpan deadline, TimeProvider time) : IBuddyLink
{
    public async Task<BuddyAnswer> CallAsync(BuddyCall call, CancellationToken cancellationToken)
    {
        if (!BuddyPipeName.Listening(pipeName))
        {
            return BuddyAnswer.NoBuddy;
        }
        using var limit = new CancellationTokenSource(deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(BuddyWire.Write(call).AsMemory(), linked.Token).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            return line is null ? BuddyAnswer.NoBuddy : BuddyWire.ReadAnswer(line) ?? new BuddyAnswer(false, "the buddy answered something this server cannot read");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BuddyAnswer(false, "the buddy did not answer in time");
        }
        catch (IOException)
        {
            return BuddyAnswer.NoBuddy;
        }
    }
}

/// <summary>
/// The running buddy's pipe: awaits a connection (no polling), reads one call, hands it to the relay and writes the answer. Ends with its token.
/// </summary>
public sealed class BuddyPipeServer(string pipeName, BuddyRelay relay)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _ = ServeAsync(pipe, cancellationToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }
                var answer = BuddyWire.ReadCall(line) is { } call ? relay.Handle(call) : new BuddyAnswer(false, "the buddy could not read the call");
                await writer.WriteLineAsync(BuddyWire.Write(answer).AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The caller went away or the buddy is closing: nothing to answer.
            }
        }
    }
}
