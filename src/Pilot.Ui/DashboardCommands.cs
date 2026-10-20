using System.CommandLine.Parsing;
using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Flights page's command bar and buttons, for either host (the GUI's window, pilot-tui's terminal): a line typed
/// there is split as a shell would and run through the CLI's own command tree (Pilot.Commands), in-process, against the
/// host's node and as the host (<c>gui</c>, <c>tui</c>); a button is one verb on the node. What either says goes back to
/// the bar.
/// </summary>
internal static class DashboardCommands
{
    /// <summary>Runs <paramref name="line"/> (<c>leg start TIA1 parser</c>, with or without a leading <c>pilot</c>); returns what it printed.</summary>
    /// <param name="node">Which node: <c>--node-socket &lt;path&gt;</c> for this machine's, <c>--node host:port</c> for one on the LAN.</param>
    /// <param name="who">The host, as the commands say who sent them.</param>
    public static async Task<string> RunAsync(string line, string[] node, string who, CancellationToken cancellationToken)
    {
        var args = CommandLineParser.SplitCommandLine(line).ToList();
        if (args.Count > 0 && args[0] is "pilot" or "pilot.exe")
        {
            args.RemoveAt(0);
        }
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync([.. node, .. args], Environment.CurrentDirectory, output, error, cancellationToken, who);
        var said = (output.ToString() + error.ToString()).TrimEnd();
        return said.Length > 0 ? said : exit == 0 ? "ok" : $"exit {exit}";
    }

    /// <summary>The node a typed <c>connect host:port</c> asks for control, or null for any other line.</summary>
    public static string? ConnectTarget(string line)
    {
        var args = CommandLineParser.SplitCommandLine(line).ToList();
        if (args.Count > 0 && args[0] is "pilot" or "pilot.exe")
        {
            args.RemoveAt(0);
        }
        return args.Count >= 2 && args[0] == "connect" && NodeClient.IsEndpoint(args[1], out var endpoint) ? endpoint : null;
    }

    /// <summary>Runs one action's verb on the node; returns what became of it.</summary>
    public static async Task<string> ActAsync(NodeClient client, FlightAction action, string who, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = action.Verb, Target = action.Target, Args = action.Args, Who = who }, cancellationToken);
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
