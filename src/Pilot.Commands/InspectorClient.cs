using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Pilot.Commands;

/// <summary>One debug-inspector core found in the registry: a running Debug app with its command server on loopback.</summary>
internal sealed record InspectorInstance(int Pid, int Port, string App, string? Title)
{
    public override string ToString() => $"pid {Pid} '{App}'{(Title is { Length: > 0 } t ? $" ({t})" : "")} on port {Port}";
}

/// <summary>The inspector answered with an error (<c>{"id":1,"error":"..."}</c>): it understood the line and refused the request.</summary>
internal sealed class InspectorRefusedException(string method, string said) : Exception($"the inspector refused {method}: {said}")
{
    public string Method { get; } = method;

    public string Said { get; } = said;

    /// <summary>Whether the refusal is the core's "unknown method" for <see cref="Method"/>: a verb the app's pinned inspector does not have.</summary>
    public bool IsUnknownMethod => Said.Contains("unknown method", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The inspector took the request and did not answer within the budget: for a screenshot, a window that presents no frame.</summary>
internal sealed class InspectorTimeoutException(string method, TimeSpan budget) : Exception(
    $"the inspector did not answer {method} within {budget.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s")
{
    public string Method { get; } = method;
}

/// <summary>
/// A small client for the inspector of a running Debug app (the debug layer of <c>DIR.Lib.Diagnostics</c>, which SdlVulkan.Renderer's
/// <c>DebugInspector</c> serves). It implements the two contracts those projects write down and does not reference them: the registry
/// (<c>DIR.Lib/src/DIR.Lib/Diagnostics/inspector-registry.md</c>) for finding an instance, and the newline-delimited JSON-RPC of the command
/// server (<c>{"id":1,"method":"describe","params":{}}</c> then <c>{"id":1,"result":...}</c> or <c>{"id":1,"error":"..."}</c>) for talking to it.
/// One request a connection, and every call has a budget, so a window that never answers cannot hold the run.
/// </summary>
internal static class InspectorClient
{
    /// <summary>How far the recorded and the observed start time of a pid may be and still be the same process: the registry contract's.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>The registry folder: <c>LocalApplicationData/DIR.Lib/inspector</c>, computed as the writer computes it (<c>DoNotVerify</c>); null where the OS has none.</summary>
    public static string? DefaultRegistryFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } root
            ? Path.Combine(root, "DIR.Lib", "inspector")
            : null;

    /// <summary>
    /// The live instances in <paramref name="folder"/> (the default when null), whose app is <paramref name="app"/>. A reader never deletes: a
    /// descriptor that does not parse, lacks <c>pid</c> or <c>tcpPort</c>, or names a process that is gone (or is a later process given the
    /// pid) is skipped. A folder that is not there is no instance registered.
    /// </summary>
    public static IReadOnlyList<InspectorInstance> Find(string? folder, string app)
    {
        folder ??= DefaultRegistryFolder;
        var found = new List<InspectorInstance>();
        if (folder is null || !Directory.Exists(folder))
        {
            return found;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            // <pid>-<tcpPort>.json, both decimal: any other name is not a descriptor.
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.IndexOf('-');
            if (dash <= 0 || !int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !int.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }
            if (Read(file) is { } instance && string.Equals(instance.App, app, StringComparison.Ordinal))
            {
                found.Add(instance);
            }
        }
        return [.. found.OrderBy(i => i.Pid).ThenBy(i => i.Port)];
    }

    /// <summary>One descriptor as an instance, or null when it is not readable, not a descriptor, or its process is not the one that wrote it.</summary>
    private static InspectorInstance? Read(string file)
    {
        string text;
        try
        {
            // Opened as the contract says: sharing Delete lets the writer's Dispose remove the file under the read.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Vanished before it was read: an instance that just exited.
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("pid", out var pidValue) || !pidValue.TryGetInt32(out var pid)
                || !root.TryGetProperty("tcpPort", out var portValue) || !portValue.TryGetInt32(out var port))
            {
                return null;
            }
            DateTime? started = root.TryGetProperty("processStart", out var startValue) && startValue.ValueKind == JsonValueKind.String
                && DateTime.TryParse(startValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : null;
            if (!IsLive(pid, started))
            {
                return null;
            }
            var app = root.TryGetProperty("app", out var appValue) && appValue.ValueKind == JsonValueKind.String ? appValue.GetString() ?? "" : "";
            var title = root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
            return new InspectorInstance(pid, port, app, title);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The contract's liveness rule: the process runs, and (where the descriptor says when it started and the OS says when the process did) is the
    /// one that wrote it. <c>bootStartTicks</c>, which Linux writers add, is not compared here: with it present the pid and, if the clock has not
    /// stepped, the start time decide, and a step makes this reader skip one live instance until it restarts.
    /// </summary>
    private static bool IsLive(int pid, DateTime? recordedStart)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return false;
        }
        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return false;
                }
                return recordedStart is not { } recorded || (process.StartTime.ToUniversalTime() - recorded).Duration() <= StartTolerance;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Its state or start time cannot be read (access denied): the pid alone decides, as the contract says.
                return true;
            }
        }
    }

    /// <summary>
    /// Sends one request to <paramref name="target"/> on a connection of its own and returns the <c>result</c> (cloned). <paramref name="paramsJson"/> is the
    /// <c>params</c> object, <c>{}</c> when null. Throws <see cref="InspectorRefusedException"/> for an error reply, <see cref="InspectorTimeoutException"/>
    /// when there is no reply within <paramref name="budget"/> (connecting included), and <see cref="IOException"/> or <see cref="SocketException"/> when
    /// the instance is gone.
    /// </summary>
    public static async Task<JsonElement> CallAsync(InspectorInstance target, string method, string? paramsJson, TimeSpan budget, CancellationToken ct)
    {
        using var budgeted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgeted.CancelAfter(budget);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, target.Port, budgeted.Token);
            using var stream = tcp.GetStream();
            // The method is one of this client's own constants (plain ASCII), so it needs no escaping.
            var request = "{\"id\":1,\"method\":\"" + method + "\",\"params\":" + (paramsJson ?? "{}") + "}\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), budgeted.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(budgeted.Token)
                ?? throw new IOException($"{target} closed the connection with no answer to {method}");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new InspectorRefusedException(method, error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : error.GetRawText());
            }
            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InspectorTimeoutException(method, budget);
        }
    }

    /// <summary>The <c>params</c> of a <c>postSignal</c> that sets one named argument: <c>{"name":"page","args":{"page":"settings"}}</c>.</summary>
    public static string SignalParams(string signal, string argument, string value)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("name", signal);
            w.WriteStartObject("args");
            w.WriteString(argument, value);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The <c>params</c> of <c>resize</c>: the window's client size in pixels.</summary>
    public static string SizeParams(int width, int height) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"width\":{width},\"height\":{height}}}");

    /// <summary>
    /// A <c>screenshot</c> result as raw RGBA: <c>{"width":w,"height":h,"format":"rgba+gzip"|"rgba","base64":"..."}</c>. Null with the reason when the
    /// result is not an image (an error object such as <c>{"error":"screenshot unavailable: GPU stalled"}</c>, or pixels that are not width x height x 4).
    /// </summary>
    public static (byte[]? Rgba, int Width, int Height, string? Problem) DecodeScreenshot(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var error))
        {
            return (null, 0, 0, error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText());
        }
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("width", out var w) || !w.TryGetInt32(out var width)
            || !result.TryGetProperty("height", out var h) || !h.TryGetInt32(out var height)
            || !result.TryGetProperty("base64", out var b64) || b64.ValueKind != JsonValueKind.String)
        {
            return (null, 0, 0, "the screenshot reply has no width, height and base64");
        }
        var format = result.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : "rgba";
        try
        {
            var payload = Convert.FromBase64String(b64.GetString() ?? "");
            byte[] rgba;
            if (format == "rgba+gzip")
            {
                using var input = new MemoryStream(payload);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                rgba = output.ToArray();
            }
            else
            {
                rgba = payload;
            }
            return width > 0 && height > 0 && rgba.LongLength == (long)width * height * 4
                ? (rgba, width, height, null)
                : (null, 0, 0, $"the screenshot is {rgba.Length} bytes, not {width}x{height} RGBA");
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return (null, 0, 0, $"the screenshot payload is not readable: {ex.Message}");
        }
    }
}
