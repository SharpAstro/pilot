using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using SharpAstro.Png;

namespace Pilot.Commands;

/// <summary>How <see cref="GuiOverlaps"/> runs: which instance, whether it raises cards, and the budget of each call to the GUI.</summary>
/// <param name="Instance">The pid of the GUI to check; null takes the only one running.</param>
/// <param name="DryRun">Prints the faults and raises no card (and takes no screenshot).</param>
/// <param name="Timeout">How long each call to the GUI may take before the window is taken to present no frame.</param>
/// <param name="RegistryFolder">The inspector registry folder to read; null is the default one.</param>
internal sealed record GuiOverlapsOptions(int? Instance, bool DryRun, TimeSpan Timeout, string? RegistryFolder);

/// <summary>One text fault the running GUI reports for the page it shows (<c>PageFaults</c> in Pilot.Ui, as JSON): the two texts and where each sits.</summary>
internal sealed record GuiFault(string Kind, string Line, string Text, string? Other, (float X, float Y, float W, float H) Bounds, (float X, float Y, float W, float H) OtherBounds)
{
    private static string Rect((float X, float Y, float W, float H) r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X:0.#},{r.Y:0.#} {r.W:0.#}x{r.H:0.#}");

    /// <summary>The fault in one line for a card's detail or the command's output: its kind, both texts and both rects.</summary>
    public string Describe() => Other is null
        ? $"{Kind}: \"{Text}\" at {Rect(Bounds)}{(Line.Length > 0 ? $" (line: {Line})" : "")}"
        : $"{Kind}: \"{Text}\" at {Rect(Bounds)} against \"{Other}\" at {Rect(OtherBounds)}{(Line.Length > 0 ? $" (line: {Line})" : "")}";
}

/// <summary>What <c>describe</c> says about the window: <c>appState</c> of the pilot GUI, as <see cref="GuiOverlaps"/> reads it.</summary>
internal sealed record GuiState(string? Page, string[] Pages, string? Window, bool? Maximized, string? PageSize, double Dpi, IReadOnlyList<GuiFault> Faults)
{
    /// <summary>The window's size, parsed from <c>WxH</c>; null where the GUI gave none.</summary>
    public (int Width, int Height)? WindowSize => ParseSize(Window);

    internal static (int Width, int Height)? ParseSize(string? text)
    {
        var parts = text?.Split('x');
        return parts is { Length: 2 } && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0 ? (w, h) : null;
    }

    /// <summary>The state in a <c>describe</c> result, or null with why not (an app that is not pilot's GUI, or one before the page faults landed).</summary>
    public static (GuiState? State, string? Problem) Parse(JsonElement describe)
    {
        if (describe.ValueKind != JsonValueKind.Object || !describe.TryGetProperty("appState", out var app) || app.ValueKind != JsonValueKind.Object)
        {
            return (null, "describe has no appState: this GUI's inspector does not report the page it shows");
        }
        string? Text(string name) => app.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var pages = (Text("pages") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (pages.Length == 0)
        {
            return (null, "describe's appState names no pages: this GUI is older than the page faults (a Debug build of the current pilot is needed)");
        }
        // faults is a JSON array carried as a string (the inspector's state values are flat), or the array itself.
        var faults = new List<GuiFault>();
        if (app.TryGetProperty("faults", out var faultValue))
        {
            try
            {
                using var parsed = faultValue.ValueKind == JsonValueKind.String ? JsonDocument.Parse(faultValue.GetString() ?? "[]") : null;
                var array = parsed?.RootElement ?? faultValue;
                if (array.ValueKind == JsonValueKind.Array)
                {
                    faults.AddRange(array.EnumerateArray().Select(ReadFault));
                }
            }
            catch (JsonException)
            {
                return (null, "describe's appState faults is not a JSON array");
            }
        }
        return (new GuiState(Text("page"), pages, Text("window"),
            app.TryGetProperty("maximized", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False ? m.GetBoolean() : null,
            Text("pageSize"), app.TryGetProperty("dpi", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 1, faults), null);
    }

    private static GuiFault ReadFault(JsonElement f)
    {
        string? Text(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        (float, float, float, float) Rect(string name)
        {
            if (!f.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object)
            {
                return default;
            }
            float N(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0;
            return (N("x"), N("y"), N("w"), N("h"));
        }
        return new GuiFault(Text("kind") ?? "fault", Text("line") ?? "", Text("text") ?? "", Text("other"), Rect("bounds"), Rect("otherBounds"));
    }
}

/// <summary>
/// <c>pilot gui overlaps</c>: the text-over-text sweep, run against the running Debug pilot GUI through its inspector. It finds the GUI in the
/// inspector registry, walks every page at the sizes the CPU-rendered sweep uses (<see cref="Sizes"/>), reads each page's text faults from the
/// GUI (found there by the one shared <c>TextOverlap</c> check, never re-implemented here), and for each page and size with a fault that no open
/// card already names raises one for-you card on the node with the page, the size, the texts and the screenshot. The window is put back where it
/// was (page, size, maximised), and a call that does not answer within its budget ends the run with one card saying the window presented no frame.
/// </summary>
internal static class GuiOverlaps
{
    /// <summary>The <c>app</c> the pilot GUI registers as.</summary>
    public const string AppName = "pilot v2";

    /// <summary>The sizes visited: 500, 700 and 1400 wide at a normal height, and the same widths at the short height the sweep checks (a small laptop screen).</summary>
    public static readonly (int Width, int Height)[] Sizes = [(500, 900), (700, 900), (1400, 900), (500, 500), (700, 500), (1400, 500)];

    /// <summary>The title of the card a run that gets no frame raises, so a second run finds it open and raises none.</summary>
    public const string NoFrameTitle = "GUI overlap check: the window presented no frame (is the display off?)";

    /// <summary>What the output says when the inspector cannot size the window: the one upstream change that makes the sweep complete.</summary>
    public const string NoResizeNote = "the GUI's inspector has no resize verb (SdlVulkan.Renderer#154 is needed in its pinned Debug package): checked at the current size only";

    /// <summary>How many times a page's state is read before it is taken not to have arrived: the signal is queued, and the next frame applies it.</summary>
    private const int Tries = 5;

    /// <summary>Most faults a card's detail lists; the rest are counted.</summary>
    private const int MostFaultsListed = 12;

    /// <summary>The card's title for the faults on <paramref name="page"/> at <paramref name="size"/>; what a later run matches an open card by.</summary>
    internal static string TitleOf(string page, (int Width, int Height)? size) =>
        $"Text overlap on the {page} page at {(size is { } s ? $"{s.Width}x{s.Height}" : "the current size")}";

    public static async Task<int> RunAsync(NodeClient node, string who, bool agent, GuiOverlapsOptions options, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var instances = InspectorClient.Find(options.RegistryFolder, AppName);
        if (options.Instance is { } named)
        {
            instances = [.. instances.Where(i => i.Pid == named)];
        }
        if (instances.Count == 0)
        {
            await error.WriteLineAsync(options.Instance is { } pid
                ? $"pilot: no running '{AppName}' GUI with pid {pid} is registered with the inspector (only a Debug build registers; pilot gui overlaps without --instance takes the only one)"
                : $"pilot: no running '{AppName}' GUI is registered with the inspector, so nothing was checked and no card raised (only a Debug build of the GUI registers)");
            return 1;
        }
        if (instances.Count > 1)
        {
            await error.WriteLineAsync($"pilot: {instances.Count} '{AppName}' GUIs are running; name one with --instance <pid>:");
            foreach (var each in instances)
            {
                await error.WriteLineAsync($"  {each}");
            }
            return 2;
        }
        var gui = instances[0];
        var run = new Run(node, who, agent, options, gui, output, error);
        return await run.GoAsync(ct);
    }

    private sealed class Run(NodeClient node, string who, bool agent, GuiOverlapsOptions options, InspectorInstance gui, TextWriter output, TextWriter error)
    {
        private GuiState? _start;
        private bool _moved;
        private bool _gone;
        private HashSet<string> _openTitles = [];

        private Task<JsonElement> CallAsync(string method, string? paramsJson, CancellationToken ct) =>
            InspectorClient.CallAsync(gui, method, paramsJson, options.Timeout, ct);

        private async Task<(GuiState? State, string? Problem)> DescribeAsync(CancellationToken ct) =>
            GuiState.Parse(await CallAsync("describe", null, ct));

        public async Task<int> GoAsync(CancellationToken ct)
        {
            int exit;
            try
            {
                exit = await SweepAsync(ct);
            }
            catch (InspectorTimeoutException ex)
            {
                exit = await RaiseNoFrameAsync(ex.Message, ct);
            }
            catch (InspectorRefusedException ex)
            {
                await error.WriteLineAsync($"pilot: {ex.Message}");
                exit = 1;
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException)
            {
                await error.WriteLineAsync($"pilot: the GUI ({gui}) went away: {ex.Message}");
                _gone = true;
                exit = 1;
            }
            finally
            {
                await PutBackAsync(ct);
            }
            return exit;
        }

        private async Task<int> SweepAsync(CancellationToken ct)
        {
            var (start, problem) = await DescribeAsync(ct);
            if (start is null)
            {
                await error.WriteLineAsync($"pilot: {gui}: {problem}");
                return 1;
            }
            _start = start;
            await output.WriteLineAsync($"checking {gui}: {start.Pages.Length} pages, window {start.Window ?? "of unknown size"}{(start.Maximized == true ? " (maximised)" : "")}, on the {start.Page} page");

            if (!options.DryRun)
            {
                // The open cards first: a fault whose card is still open is not raised again.
                _openTitles = [.. (await PilotCommand.SnapshotAsync(node, ct)).Cards.Where(c => c.State == CardState.Open).Select(c => c.Title)];
                // One screenshot of the window as it is: a display that is off, or a minimised window, presents no frame, and that is found here
                // for the price of one call rather than after a page was visited.
                var (_, _, _, shot) = InspectorClient.DecodeScreenshot(await CallAsync("screenshot", null, ct));
                if (shot is not null)
                {
                    await error.WriteLineAsync($"pilot: {gui} cannot take a screenshot: {shot}");
                    return 1;
                }
            }

            // Whether the inspector can size the window, found by sizing it once to the first size: an inspector from before
            // SdlVulkan.Renderer#154 refuses the verb as unknown, and then every page is read at the size the window has.
            var sized = true;
            try
            {
                await CallAsync("resize", InspectorClient.SizeParams(Sizes[0].Width, Sizes[0].Height), ct);
                _moved = true;
            }
            catch (InspectorRefusedException ex) when (ex.IsUnknownMethod)
            {
                sized = false;
                await output.WriteLineAsync(NoResizeNote);
            }
            catch (InspectorRefusedException ex)
            {
                sized = false;
                await output.WriteLineAsync($"{NoResizeNote} (resize was refused: {ex.Said})");
            }

            var visits = 0;
            var found = 0;
            var raised = 0;
            var skipped = 0;
            (int Width, int Height)?[] sizes = sized ? [.. Sizes.Select(s => ((int, int)?)s)] : [null];
            foreach (var page in start.Pages)
            {
                try
                {
                    await CallAsync("postSignal", InspectorClient.SignalParams("page", "page", page), ct);
                }
                catch (InspectorRefusedException ex)
                {
                    await output.WriteLineAsync($"skipped the {page} page: {ex.Message}");
                    continue;
                }
                foreach (var size in sizes)
                {
                    if (size is { } wanted)
                    {
                        try
                        {
                            await CallAsync("resize", InspectorClient.SizeParams(wanted.Width, wanted.Height), ct);
                            _moved = true;
                        }
                        catch (InspectorRefusedException ex)
                        {
                            await output.WriteLineAsync($"skipped the {page} page at {wanted.Width}x{wanted.Height}: {ex.Message}");
                            continue;
                        }
                    }
                    var (state, why) = await ReadPageAsync(page, ct);
                    if (state is null)
                    {
                        await output.WriteLineAsync($"skipped the {page} page: {why}");
                        break;
                    }
                    visits++;
                    var at = size ?? state.WindowSize ?? start.WindowSize;
                    if (state.Faults.Count == 0)
                    {
                        continue;
                    }
                    found += state.Faults.Count;
                    var title = TitleOf(page, at);
                    await output.WriteLineAsync($"{page} at {(at is { } a ? $"{a.Width}x{a.Height}" : "the current size")}: {state.Faults.Count} fault{(state.Faults.Count == 1 ? "" : "s")}");
                    foreach (var fault in state.Faults.Take(MostFaultsListed))
                    {
                        await output.WriteLineAsync($"  {fault.Describe()}");
                    }
                    if (options.DryRun)
                    {
                        continue;
                    }
                    if (_openTitles.Contains(title))
                    {
                        skipped++;
                        await output.WriteLineAsync($"  not raised again: a card titled '{title}' is open");
                        continue;
                    }
                    if (await RaiseOverlapAsync(title, page, at, state, ct))
                    {
                        raised++;
                        _openTitles.Add(title);
                    }
                }
            }
            await output.WriteLineAsync($"{visits} visits; {found} fault{(found == 1 ? "" : "s")} found; "
                + (options.DryRun ? "dry run, no card raised" : $"{raised} card{(raised == 1 ? "" : "s")} raised, {skipped} already open"));
            return 0;
        }

        /// <summary>
        /// The state of the page <paramref name="page"/> as the GUI shows it: read until the page the GUI reports is that page (a signal is applied by
        /// the next frame, and a describe can run before it), at most <see cref="Tries"/> times.
        /// </summary>
        private async Task<(GuiState? State, string? Why)> ReadPageAsync(string page, CancellationToken ct)
        {
            string? last = null;
            for (var attempt = 0; attempt < Tries; attempt++)
            {
                var (state, problem) = await DescribeAsync(ct);
                if (state is null)
                {
                    return (null, problem);
                }
                if (string.Equals(state.Page, page, StringComparison.OrdinalIgnoreCase))
                {
                    return (state, null);
                }
                last = state.Page;
            }
            return (null, $"the window still shows the {last} page after the page signal");
        }

        private async Task<bool> RaiseOverlapAsync(string title, string page, (int Width, int Height)? at, GuiState state, CancellationToken ct)
        {
            var (rgba, width, height, problem) = InspectorClient.DecodeScreenshot(await CallAsync("screenshot", null, ct));
            if (rgba is null)
            {
                await error.WriteLineAsync($"pilot: the screenshot of the {page} page failed: {problem}; no card raised for it");
                return false;
            }
            var detail = new StringBuilder();
            detail.Append(CultureInfo.InvariantCulture,
                $"{state.Faults.Count} text fault{(state.Faults.Count == 1 ? "" : "s")} on the {page} page at {(at is { } a ? $"{a.Width}x{a.Height}" : "the current size")}");
            detail.Append(CultureInfo.InvariantCulture, $" (page area {state.PageSize ?? "unknown"}, DPI {state.Dpi:0.##}, screenshot {width}x{height}), found in the running GUI (pid {gui.Pid}).");
            var number = 0;
            foreach (var fault in state.Faults.Take(MostFaultsListed))
            {
                detail.Append(CultureInfo.InvariantCulture, $"\n{++number}. {fault.Describe()}");
            }
            if (state.Faults.Count > MostFaultsListed)
            {
                detail.Append(CultureInfo.InvariantCulture, $"\nand {state.Faults.Count - MostFaultsListed} more");
            }
            detail.Append("\nRun it again with pilot gui overlaps --dry-run to list them without raising cards.");
            return await RaiseAsync(title, detail.ToString(), PngWriter.Encode(rgba, width, height), ct);
        }

        private async Task<int> RaiseNoFrameAsync(string what, CancellationToken ct)
        {
            if (options.DryRun)
            {
                await error.WriteLineAsync($"pilot: {what}: the window presented no frame (is the display off?); a dry run raises no card");
                return PilotCommand.ExitTimedOut;
            }
            await error.WriteLineAsync($"pilot: {what}: the window presented no frame (is the display off?)");
            if (_openTitles.Contains(NoFrameTitle))
            {
                await output.WriteLineAsync($"not raised again: a card titled '{NoFrameTitle}' is open");
                return PilotCommand.ExitTimedOut;
            }
            var where = _start?.Page is { } page ? $" while the window was on the {page} page" : "";
            await RaiseAsync(NoFrameTitle,
                $"{what}{where}, in {gui}. The window did not present a frame within {options.Timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s, so the overlap check stopped there and visited nothing more. "
                + "Is the display off, the window minimised, or the GPU stalled? Run pilot gui overlaps again when it shows.", null, ct);
            return PilotCommand.ExitTimedOut;
        }

        /// <summary>A for-you card on the node, with the image uploaded first when there is one. False (and said) when the node refused it.</summary>
        private async Task<bool> RaiseAsync(string title, string detail, byte[]? png, CancellationToken ct)
        {
            string? attachment = null;
            if (png is not null)
            {
                var uploaded = await node.UploadAttachmentAsync(png, "png", ct);
                if (uploaded is not { Success: true, Response: { Length: > 0 } id })
                {
                    await error.WriteLineAsync($"pilot: could not upload the screenshot for '{title}': {uploaded.Error}; no card raised for it");
                    return false;
                }
                attachment = $"{id}.png";
            }
            var sent = await node.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = "node", Args = ["for-you", title, detail], Who = who, Agent = agent, Attachment = attachment }, ct);
            if (sent is { Success: true, Response.Ok: true })
            {
                await output.WriteLineAsync($"raised: {title}");
                return true;
            }
            await error.WriteLineAsync($"pilot: the node refused the card '{title}': {sent.Response?.Error ?? sent.Error}");
            return false;
        }

        /// <summary>
        /// The window as it was found: maximised again where it was, else its own size, and on the page it showed. Best effort, and each call keeps the
        /// budget, so a window that stopped answering costs one budget, not one per step.
        /// </summary>
        private async Task PutBackAsync(CancellationToken ct)
        {
            if (_gone || _start is not { } start)
            {
                return;
            }
            try
            {
                if (_moved)
                {
                    if (start.Maximized == true)
                    {
                        await CallAsync("maximize", null, ct);
                    }
                    else if (start.WindowSize is { } size)
                    {
                        await CallAsync("resize", InspectorClient.SizeParams(size.Width, size.Height), ct);
                    }
                }
                if (start.Page is { } page && start.Pages.Contains(page, StringComparer.OrdinalIgnoreCase))
                {
                    await CallAsync("postSignal", InspectorClient.SignalParams("page", "page", page), ct);
                }
            }
            catch (Exception ex) when (ex is InspectorTimeoutException or InspectorRefusedException or System.IO.IOException or System.Net.Sockets.SocketException)
            {
                await error.WriteLineAsync($"pilot: could not put the window back as it was ({ex.Message})");
            }
        }
    }
}
