using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// The Settings page's side of the node: the page itself (<see cref="PageAsync"/>), <c>set node &lt;key&gt; [value…]</c>,
/// which saves a setting and restarts the node to run with it, and <c>setup node &lt;check&gt;</c>, which makes a setup
/// check's fix. Both verbs are this machine's alone: a grant from the LAN never reaches them.
/// </summary>
public sealed class NodeSettingsCommands(NodeOptions options, NodeState state, NodeRestarter restarter, NodeSetup setup, TimeProvider time, Flights.JobRunner jobs)
{
    // The checks run netsh and gh: a page looked at every few seconds runs them at most this often.
    private static readonly TimeSpan ChecksFor = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _checking = new(1, 1);
    private (DateTimeOffset At, SetupCheck[] Checks)? _checks;

    public async Task<SettingsPage> PageAsync(CancellationToken cancellationToken)
    {
        var saved = NodeSettings.Read(options.SettingsFile);
        return new SettingsPage
        {
            Settings = [.. NodeSettings.Keys.Select(key => Row(key, saved))],
            Checks = await ChecksAsync(fresh: false, cancellationToken),
            Restart = restarter.Waiting,
            File = options.SettingsFile,
        };
    }

    /// <summary><c>set node &lt;key&gt; [value…]</c>: saves it, then restarts the node to run with it.</summary>
    public CommandResult Set(CommandRequest command)
    {
        var args = command.Args ?? [];
        if (args.Length == 0)
        {
            return state.Complete(command, $"set takes a setting ({string.Join(", ", NodeSettings.Keys)}), then its value; none, or default, takes it out");
        }
        var key = args[0].Trim().ToLowerInvariant();
        var (updated, error) = NodeSettings.With(NodeSettings.Read(options.SettingsFile), key, args[1..]);
        if (updated is null)
        {
            return state.Complete(command, error);
        }
        try
        {
            updated.Write(options.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return state.Complete(command, $"{options.SettingsFile} could not be written: {ex.Message}");
        }
        var value = updated.Get(key) is { } values ? string.Join(' ', values) : "the default";
        if (options.CommandLineKeys?.Contains(key) == true)
        {
            return state.Complete(command, null, way: $"{key} saved as {value}, but this node's command line gives it, and wins: take it off the command line (pilot-node autostart install) for the setting to count");
        }
        if (Running(key) == (updated.Get(key) is { } now ? Join(key, now) : Default(key)))
        {
            return state.Complete(command, null, way: $"{key} saved as {value}; the node runs with it already");
        }
        // The job limits apply at once: a restart for them would wait for the very queue they are meant to clear.
        if (key is NodeSettings.MaxJobsKey or NodeSettings.JobMemoryKey)
        {
            jobs.MaxRunning = updated.MaxJobs ?? options.MaxJobs;
            jobs.MinFreeMemory = (long)((updated.JobMemoryGigabytes ?? options.JobMemoryGigabytes) * (1L << 30));
            jobs.Pump();
            return state.Complete(command, null, way: $"{key} saved as {value}, and in force now");
        }
        var restart = restarter.Request($"{key} = {value}");
        return state.Complete(command, null, way: restart ?? $"{key} saved as {value}; the node restarts to run with it");
    }

    /// <summary><c>setup node &lt;check&gt;</c>: the fix the checklist offers for it.</summary>
    public async Task<CommandResult> SetupAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var key = (command.Args ?? []).FirstOrDefault()?.Trim().ToLowerInvariant() ?? "";
        if (key == NodeSetup.LanKey)
        {
            return Set(command with { Args = [NodeSettings.LanKey, "on"] });
        }
        var error = await setup.FixAsync(key, cancellationToken);
        await ChecksAsync(fresh: true, cancellationToken);
        return state.Complete(command, error, way: error is null ? $"{key}: done" : null);
    }

    private async Task<SetupCheck[]> ChecksAsync(bool fresh, CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (!fresh && _checks is { } cached && now - cached.At < ChecksFor)
            {
                return cached.Checks;
            }
            var checks = await Task.Run(() => setup.Checks().ToArray(), cancellationToken);
            _checks = (now, checks);
            return checks;
        }
        finally
        {
            _checking.Release();
        }
    }

    private SettingRow Row(string key, NodeSettings saved)
    {
        var running = Running(key);
        var file = saved.Get(key) is { } values ? Join(key, values) : null;
        var pinned = options.CommandLineKeys?.Contains(key) == true;
        var started = options.Saved?.Get(key) is { } before ? Join(key, before) : null;
        var (label, detail, choices) = Describe(key);
        return new SettingRow
        {
            Key = key,
            Label = label,
            Detail = detail,
            Value = running,
            Saved = file,
            Source = pinned ? SettingSources.CommandLine : started is not null ? SettingSources.File : SettingSources.Default,
            Choices = choices,
            Many = key == NodeSettings.RepoRootsKey,
            Pending = !pinned && (file ?? Default(key)) != running,
        };
    }

    // The value the node runs with, as set node spells it.
    private string Running(string key) => key switch
    {
        NodeSettings.LanKey => options.Lan ? "on" : "off",
        NodeSettings.LanPortKey => options.LanPort.ToString(CultureInfo.InvariantCulture),
        NodeSettings.MachineKey => options.Machine,
        NodeSettings.RepoRootsKey => Join(key, options.RepoRoots),
        NodeSettings.UpdatePolicyKey => options.UpdatePolicy.Name(),
        NodeSettings.UpdateChannelKey => options.UpdateChannel.Name(),
        NodeSettings.MaxJobsKey => jobs.MaxRunning.ToString(CultureInfo.InvariantCulture),
        NodeSettings.JobMemoryKey => (jobs.MinFreeMemory / (double)(1L << 30)).ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    // A value as a row carries it: a list's items a line each (a folder may hold a space), else the one value.
    private static string Join(string key, IReadOnlyList<string> values) => string.Join(key == NodeSettings.RepoRootsKey ? '\n' : ' ', values);

    // What a key is when neither the command line nor the file says.
    private static string Default(string key) => key switch
    {
        NodeSettings.LanKey => "off",
        NodeSettings.LanPortKey => NodeWire.DefaultLanPort.ToString(CultureInfo.InvariantCulture),
        NodeSettings.MachineKey => (MachinePrefixes.Normalise(Environment.GetEnvironmentVariable("PILOT_MACHINE")) ?? MachinePrefixes.FromName(Environment.MachineName)),
        NodeSettings.RepoRootsKey => "",
        NodeSettings.UpdatePolicyKey => UpdatePolicy.Auto.Name(),
        NodeSettings.UpdateChannelKey => UpdateChannel.Stable.Name(),
        NodeSettings.MaxJobsKey => "1",
        NodeSettings.JobMemoryKey => "2",
        _ => "",
    };

    private static (string Label, string Detail, string[] Choices) Describe(string key) => key switch
    {
        NodeSettings.LanKey => ("On the LAN", "Listen on the LAN and announce it: anyone there may watch, a command needs a grant you allow", ["on", "off"]),
        NodeSettings.LanPortKey => ("LAN port", "The TCP port other machines reach this node on", []),
        NodeSettings.MachineKey => ("Machine prefix", "Two letters and a number, at the front of this machine's session names (DE1-watchtower)", []),
        NodeSettings.RepoRootsKey => ("Repo roots", "Folders of git clones the new-flight wizard offers, beside the flights' own repos", []),
        NodeSettings.UpdatePolicyKey => ("Updates", "auto installs a newer pilot once no job runs; ask raises a card; off does not look", [.. UpdatePolicies.Names]),
        NodeSettings.UpdateChannelKey => ("Update channel", "stable follows the tagged releases; beta follows main's every push (a Debug build, needs the .NET runtime)", [.. UpdateChannels.Names]),
        NodeSettings.MaxJobsKey => ("Jobs at once", "How many background tasks run at once; the rest wait their turn", ["1", "2", "3", "4"]),
        NodeSettings.JobMemoryKey => ("Job memory", "The free memory, in GB, a job waits for before it starts; 0 to not wait", ["0", "2", "4", "8"]),
        _ => (key, "", []),
    };
}
