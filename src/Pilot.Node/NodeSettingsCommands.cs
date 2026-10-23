using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// The Settings page's side of the node: the page itself (<see cref="PageAsync"/>), <c>set node &lt;key&gt; [value…]</c>,
/// which saves a setting and restarts the node to run with it, and <c>setup node &lt;check&gt;</c>, which makes a setup
/// check's fix. Both verbs are this machine's alone: a grant from the LAN never reaches them.
/// </summary>
public sealed class NodeSettingsCommands(NodeOptions options, NodeState state, NodeRestarter restarter, NodeSetup setup, TimeProvider time, Flights.JobRunner jobs, RoleModels roles)
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
        // A live setting is applied below whatever it read before: its running value may be read from the very file just
        // written (priority-repos was, and was taken as in force while the node still ran without it).
        if (!Live(key) && Running(key) == (updated.Get(key) is { } now ? Join(key, now) : Default(key)))
        {
            return state.Complete(command, null, way: $"{key} saved as {value}; the node runs with it already");
        }
        // A role's model and effort are read when a session of it starts: in force for the next one, no restart.
        if (NodeSettings.IsRoleKey(key) || key is NodeSettings.MergeMethodKey or NodeSettings.StartLimitKey or NodeSettings.PriorityStartLimitKey or NodeSettings.PriorityReposKey)
        {
            roles.Apply(updated);
            return state.Complete(command, null, way: key == NodeSettings.MergeMethodKey ? $"{key} saved as {value}; the next merge uses it"
                : key is NodeSettings.StartLimitKey or NodeSettings.PriorityStartLimitKey or NodeSettings.PriorityReposKey ? $"{key} saved as {value}; the next start the node makes by itself weighs it"
                : $"{key} saved as {value}; the next session of that kind starts with it");
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
            // A live setting runs with what the file says now; any other with what it said at the start.
            Source = pinned ? SettingSources.CommandLine : (Live(key) ? file : started) is not null ? SettingSources.File : SettingSources.Default,
            Choices = choices,
            Many = key is NodeSettings.RepoRootsKey or NodeSettings.PriorityReposKey,
            Pending = !pinned && !Live(key) && (file ?? Default(key)) != running,
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
        NodeSettings.MergeMethodKey => roles.MergeMethod,
        NodeSettings.StartLimitKey => roles.StartLimit.ToString(CultureInfo.InvariantCulture),
        NodeSettings.PriorityStartLimitKey => (NodeSettings.Read(options.SettingsFile).PriorityStartLimit ?? 98).ToString(CultureInfo.InvariantCulture),
        NodeSettings.PriorityReposKey => string.Join('\n', NodeSettings.Read(options.SettingsFile).PriorityRepos ?? []),
        _ when RoleOf(key) is ({ } role, var effort) => (effort ? roles.Effort(role) : roles.Model(role)) ?? RoleModels.NoneValue,
        _ => "",
    };

    // The role a role key is for, and whether it is its effort rather than its model.
    private static (string? Role, bool Effort) RoleOf(string key) =>
        RoleModels.Roles.FirstOrDefault(r => key == RoleModels.ModelKey(r)) is { } model ? (model, false)
        : RoleModels.Roles.FirstOrDefault(r => key == RoleModels.EffortKey(r)) is { } effort ? (effort, true)
        : (null, false);

    /// <summary>Whether a setting is in force as soon as it is saved, with no restart: a role's model or effort, the merge
    /// method, the start limits, the priority repos and the job limits.</summary>
    private static bool Live(string key) => NodeSettings.IsRoleKey(key)
        || key is NodeSettings.MergeMethodKey or NodeSettings.StartLimitKey or NodeSettings.PriorityStartLimitKey or NodeSettings.PriorityReposKey
            or NodeSettings.MaxJobsKey or NodeSettings.JobMemoryKey;

    // A value as a row carries it: a list's items a line each (a folder may hold a space), else the one value.
    private static string Join(string key, IReadOnlyList<string> values) => string.Join(key is NodeSettings.RepoRootsKey or NodeSettings.PriorityReposKey ? '\n' : ' ', values);

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
        NodeSettings.MergeMethodKey => "rebase",
        NodeSettings.StartLimitKey => "90",
        NodeSettings.PriorityStartLimitKey => "98",
        NodeSettings.PriorityReposKey => "",
        _ when RoleOf(key) is ({ } role, var effort) => effort ? RoleModels.NoneValue : RoleModels.DefaultModel(role),
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
        NodeSettings.PriorityReposKey => ("Priority repos", "Repos whose flights have runway: they go on past the start limit, up to the priority limit", []),
        NodeSettings.PriorityStartLimitKey => ("Priority start limit", "How far into the 5-hour window a priority repo's flight still goes on by itself, in percent", ["95", "98", "100"]),
        NodeSettings.StartLimitKey => ("Start limit", "A flight goes on by itself (its next leg, its PR and review) until the account's 5-hour window is this spent, in percent; then it waits for the reset", ["80", "90", "95", "100"]),
        NodeSettings.MergeMethodKey => ("Merge method", "How a flight's PR is merged: rebase keeps every commit's subject (docs may cite them), squash makes one commit", [.. NodeSettings.MergeMethods]),
        _ when RoleOf(key) is ({ } role, false) => ($"{Title(role)} model", RoleDetail(role), ["sonnet", "opus", "haiku", RoleModels.NoneValue]),
        _ when RoleOf(key) is ({ } role, true) => ($"{Title(role)} effort", $"The effort each {role} starts at; none leaves it to Claude Code", [.. RoleModels.Efforts, RoleModels.NoneValue]),
        _ => (key, "", []),
    };

    private static string Title(string role) => char.ToUpperInvariant(role[0]) + role[1..];

    private static string RoleDetail(string role) => role switch
    {
        RoleModels.Watchtower => "The watchtower reads cards and runs pilot for hours: the longest-lived session, so the costliest per turn",
        RoleModels.Worker => "A leg's worker, unless its plan names a model (leg plan --model), which wins",
        RoleModels.Planner => "Plans a flight's legs from its issue: short-lived, and decides everything after it",
        _ => "Reviews a flight's PR before its merge card",
    };
}
