using System.CommandLine.Parsing;
using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;

namespace Pilot.Gui;

/// <summary>
/// The Flights page's command bar and buttons: a line typed there is split as a shell would and run through the CLI's
/// own command tree (Pilot.Commands), in-process, against this window's node and as <see cref="GuiSay.Who"/>; a button
/// is one verb on the node. What either says goes back to the bar.
/// </summary>
internal static class GuiCommands
{
    /// <summary>Runs <paramref name="line"/> (<c>leg start TIA1 parser</c>, with or without a leading <c>pilot</c>); returns what it printed.</summary>
    public static async Task<string> RunAsync(string line, string socketPath, CancellationToken cancellationToken)
    {
        var args = CommandLineParser.SplitCommandLine(line).ToList();
        if (args.Count > 0 && args[0] is "pilot" or "pilot.exe")
        {
            args.RemoveAt(0);
        }
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", socketPath, .. args], Environment.CurrentDirectory, output, error, cancellationToken, GuiSay.Who);
        var said = (output.ToString() + error.ToString()).TrimEnd();
        return said.Length > 0 ? said : exit == 0 ? "ok" : $"exit {exit}";
    }

    /// <summary>Runs one action's verb on the node; returns what became of it.</summary>
    public static async Task<string> ActAsync(NodeClient client, FlightAction action, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = action.Verb, Target = action.Target, Args = action.Args, Who = GuiSay.Who }, cancellationToken);
        return envelope.Success && envelope.Response is { Ok: true }
            ? $"ok: {action.Label}"
            : $"refused: {action.Label}: {envelope.Response?.Error ?? envelope.Error}";
    }

    /// <summary>Opens a file or a URL the way the platform does (the browser, the image viewer); false when it could not.</summary>
    public static bool Open(string location)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(location) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
