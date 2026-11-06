using System.CommandLine;
using System.Globalization;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    private sealed partial class BuildContext
    {
        /// <summary>How long each call to the GUI may take, in seconds, when <c>--timeout</c> is not given: above the 15 s the inspector gives a screenshot itself.</summary>
        private const double DefaultInspectorSeconds = 20;

        /// <summary>The <c>gui</c> group: commands against the running Debug pilot GUI, through its inspector.</summary>
        internal Command GuiCommands()
        {
            var instance = new Option<int?>("--instance") { Description = "The pid of the GUI to check, when more than one is running (the refusal lists them)" };
            var dryRun = new Option<bool>("--dry-run") { Description = "Print the faults and raise no card (no screenshot is taken)" };
            var timeout = new Option<double?>("--timeout") { Description = $"Seconds each call to the GUI may take before the window is taken to present no frame (default {DefaultInspectorSeconds.ToString(CultureInfo.InvariantCulture)})" };
            // For a test's own registry folder; nobody else has another.
            var registry = new Option<string?>("--registry") { Description = "An inspector registry folder other than this user's", Hidden = true };
            var overlaps = Action(new Command("overlaps",
                    "Run the text-over-text check against the running pilot GUI through its inspector: every page at 500, 700 and 1400 wide and a short height, one for-you card per page and size with a fault, with its screenshot. "
                    + "The GUI must be a Debug build (only a Debug build has an inspector), and the window is put back as it was. A fault whose card is still open is not raised again; a window that presents no frame ends the run with one card saying so")
                { Options = { instance, dryRun, timeout, registry } },
                (p, client, ct) => GuiOverlaps.RunAsync(client, who, byAgent,
                    new GuiOverlapsOptions(p.GetValue(instance), p.GetValue(dryRun), TimeSpan.FromSeconds(p.GetValue(timeout) ?? DefaultInspectorSeconds), p.GetValue(registry)),
                    output, error, ct));
            return new Command("gui", "The running pilot GUI, through its inspector (a Debug build only)") { Subcommands = { overlaps } };
        }
    }
}
