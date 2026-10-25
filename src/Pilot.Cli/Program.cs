using Console.Lib;
using Pilot.Commands;

// pilot v2's command line: what the controller, the watchtower, workers and scripts use against the node.
return await PilotCommand.RunAsync(args, Environment.CurrentDirectory, System.Console.Out, System.Console.Error, CancellationToken.None, console: true, rich: RenderAsync);

// --output-format auto or rich: Markdown rendered as mdcat renders it, when the terminal can show it. Auto asks the terminal
// only when standard output is one, so a pipe, a script or an agent reads plain text without waiting on a probe.
static async Task<bool> RenderAsync(string format, string markdown, TextWriter output, TextWriter error)
{
    MarkdownOutput.TryParseFormat(format, out var requested);
    var (resolved, probe) = await MarkdownOutput.ResolveFormatAsync(requested);
    if (resolved != OutputFormat.Rich)
    {
        return false;
    }
    await MarkdownOutput.RenderAsync(markdown, output, new MarkdownOutputOptions(Probe: probe), error);
    return true;
}
