using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// The Settings page's side of the node: the page itself (<see cref="PageAsync"/>), <c>set node &lt;key&gt; [value…]</c>,
/// which saves a setting and restarts the node to run with it, and <c>setup node &lt;check&gt;</c>, which makes a setup
/// check's fix. Both verbs are this machine's alone: a grant from the LAN never reaches them, but for <c>set node maintenance
/// on|off</c> (<see cref="NodeApp.MaintenanceSwitch"/>).
/// </summary>
public sealed class NodeSettingsCommands(NodeOptions options, NodeState state, NodeRestarter restarter, NodeSetup setup, TimeProvider time, Flights.JobRunner jobs, RoleModels roles)
{
    // The checks run netsh and gh: a page looked at every few seconds runs them at most this often.
    private static readonly TimeSpan ChecksFor = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _checking = new(1, 1);
    private (DateTimeOffset At, SetupCheck[] Checks)? _checks;

    /// <summary>The settings page: <c>settings.json</c> and the node's own state, no setup check (they run programs).</summary>
    public Task<SettingsPage> PageAsync(CancellationToken cancellationToken)
    {
        var saved = NodeSettings.Read(options.SettingsFile);
        return Task.FromResult(new SettingsPage
        {
            Settings = [.. NodeSettings.Keys.Select(key => Row(key, saved))],
            File = options.SettingsFile,
        });
    }

    /// <summary>The setup checks as last measured, run again once they are older than the cache age or when <paramref name="refresh"/> asks.</summary>
    public async Task<SetupChecks> SetupChecksAsync(bool refresh, CancellationToken cancellationToken)
    {
        var (at, checks) = await ChecksAsync(refresh, cancellationToken);
        return new SetupChecks { Checks = checks, CheckedAt = at };
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
        var value = updated.Get(key) is { } values ? (key == NodeSettings.ShortcutTokenKey ? "set" : string.Join(' ', values)) : "the default";
        if (options.CommandLineKeys?.Contains(key) == true)
        {
            return state.Complete(command, null, setting: Row(key, updated), way: $"{key} saved as {value}, but this node's command line gives it, and wins: take it off the command line (pilot-node autostart install) for the setting to count");
        }
        // A live setting is applied below whatever it read before: its running value may be read from the very file just
        // written (priority-repos was, and was taken as in force while the node still ran without it).
        if (key == NodeSettings.ShortcutTokenKey)
        {
            roles.Apply(updated);
            return state.Complete(command, null, setting: Row(key, updated), way: $"{key} saved as {value}, and in force now");
        }
        if (!Live(key) && Running(key) == (updated.Get(key) is { } now ? Join(key, now) : Default(key)))
        {
            return state.Complete(command, null, setting: Row(key, updated), way: $"{key} saved as {value}; the node runs with it already");
        }
        // A role's model and effort are read when a session of it starts: in force for the next one, no restart.
        if (NodeSettings.IsRoleKey(key) || key is NodeSettings.MergeMethodKey or NodeSettings.StartLimitKey or NodeSettings.PriorityStartLimitKey or NodeSettings.PriorityReposKey or NodeSettings.WeekLimitKey
            or NodeSettings.MaintenanceKey or NodeSettings.AcceptFleetFlightsKey or NodeSettings.AutopilotKey or NodeSettings.RepoTrackersKey or NodeSettings.NoAutoMergeReposKey or NodeSettings.WorkerLadderKey)
        {
            roles.Apply(updated);
            return state.Complete(command, null, setting: Row(key, updated), way: key == NodeSettings.MergeMethodKey ? $"{key} saved as {value}; the next merge uses it"
                : key is NodeSettings.RepoTrackersKey or NodeSettings.NoAutoMergeReposKey ? $"{key} saved as {value}, and in force now"
                : key is NodeSettings.MaintenanceKey or NodeSettings.AcceptFleetFlightsKey or NodeSettings.AutopilotKey or NodeSettings.WorkerLadderKey ? $"{key} saved as {value}, and in force now"
                : key is NodeSettings.StartLimitKey or NodeSettings.PriorityStartLimitKey or NodeSettings.PriorityReposKey or NodeSettings.WeekLimitKey ? $"{key} saved as {value}; the next start the node makes by itself weighs it"
                : $"{key} saved as {value}; the next session of that kind starts with it");
        }
        // The job limits apply at once: a restart for them would wait for the very queue they are meant to clear.
        if (key is NodeSettings.MaxJobsKey or NodeSettings.JobMemoryKey)
        {
            jobs.MaxRunning = updated.MaxJobs ?? options.MaxJobs;
            jobs.MinFreeMemory = (long)((updated.JobMemoryGigabytes ?? options.JobMemoryGigabytes) * (1L << 30));
            jobs.Pump();
            return state.Complete(command, null, setting: Row(key, updated), way: $"{key} saved as {value}, and in force now");
        }
        var carrying = NodeRestarter.JobsCarryOn(state) is { } jobsLeft ? $" ({jobsLeft} under the next node)" : "";
        var restart = restarter.Request($"{key} = {value}");
        return state.Complete(command, null, setting: Row(key, updated), way: restart ?? $"{key} saved as {value}; the node restarts to run with it{carrying}");
    }

    /// <summary>
    /// <c>restart-node node [why…]</c>: the node starts its successor and stops, as a saved setting makes it; jobs run
    /// detached and are not waited for, so the answer (and a Handled card naming the caller) says how many carry on under the next node.
    /// </summary>
    public CommandResult RestartNode(CommandRequest command)
    {
        var why = string.Join(' ', command.Args ?? []).Trim();
        var carrying = NodeRestarter.JobsCarryOn(state) is { } jobsLeft ? $"; {jobsLeft} under the next node" : "";
        var line = $"the node restarts for {command.Who}{(why.Length > 0 ? $": {why}" : "")}{carrying}";
        var refused = restarter.Restart(why.Length > 0 ? $"{command.Who}: {why}" : command.Who,
            () => state.RaiseCard(CardBin.Handled, "restart", Verbs.NodeTarget, line));
        return state.Complete(command, refused, way: refused is null ? line : null);
    }

    /// <summary><c>setup node &lt;check&gt;</c>: the fix the checklist offers for it.</summary>
    public async Task<CommandResult> SetupAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var key = (command.Args ?? []).FirstOrDefault()?.Trim().ToLowerInvariant() ?? "";
        if (key == NodeSetup.LanKey)
        {
            return Set(command with { Args = [NodeSettings.LanKey, "on"] });
        }
        var error = await setup.FixAsync(key, cancellationToken, processes: (command.Args ?? []).Skip(1).Any(a => a.Trim().Equals("--processes", StringComparison.OrdinalIgnoreCase)));
        await ChecksAsync(fresh: true, cancellationToken);
        return state.Complete(command, error, way: error is null ? $"{key}: done" : null);
    }

    private async Task<(DateTimeOffset At, SetupCheck[] Checks)> ChecksAsync(bool fresh, CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (!fresh && _checks is { } cached && now - cached.At < ChecksFor)
            {
                return cached;
            }
            var checks = await Task.Run(() => setup.Checks().Select(check => check with { Runs = NodeSetup.RunsOf(check.Key) }).ToArray(), cancellationToken);
            _checks = (now, checks);
            return _checks.Value;
        }
        finally
        {
            _checking.Release();
        }
    }

    private SettingRow Row(string key, NodeSettings saved)
    {
        var running = Running(key);
        var file = saved.Get(key) is { } values ? (key == NodeSettings.ShortcutTokenKey ? "set" : Join(key, values)) : null;
        var pinned = options.CommandLineKeys?.Contains(key) == true;
        var started = options.Saved?.Get(key) is { } before ? (key == NodeSettings.ShortcutTokenKey ? "set" : Join(key, before)) : null;
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
            Many = key is NodeSettings.RepoRootsKey or NodeSettings.PriorityReposKey or NodeSettings.RepoTrackersKey or NodeSettings.NoAutoMergeReposKey,
            Pending = !pinned && !Live(key) && (file ?? Default(key)) != running,
        };
    }

    // The value the node runs with, as set node spells it.
    private string Running(string key) => key switch
    {
        NodeSettings.LanKey => options.Lan ? "on" : "off",
        NodeSettings.MaintenanceKey => roles.Maintenance ? "on" : "off",
        NodeSettings.AcceptFleetFlightsKey => roles.AcceptsFleetFlights ? "on" : "off",
        NodeSettings.AutopilotKey => roles.Autopilot ? "on" : "off",
        NodeSettings.WorkerLadderKey => roles.WorkerLadder ? "on" : "off",
        NodeSettings.WorkerModelMaxKey => roles.WorkerModelMax,
        NodeSettings.WorkerEffortMaxKey => roles.WorkerEffortMax,
        // Never the token itself: present or absent.
        NodeSettings.ShortcutTokenKey => roles.ShortcutToken is { Length: > 0 } ? "set" : "",
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
        NodeSettings.RepoTrackersKey => string.Join('\n', NodeSettings.Read(options.SettingsFile).RepoTrackers ?? []),
        NodeSettings.NoAutoMergeReposKey => string.Join('\n', NodeSettings.Read(options.SettingsFile).NoAutoMergeRepos ?? []),
        // A deep review runs on the reviewer's model and a step above its effort until these are set, so say that, not the bare role's.
        _ when RoleOf(key) is (RoleModels.ReviewerDeep, var effort) => (effort ? roles.ReviewerEffort(ReviewDepth.Deep) : roles.ReviewerModel(ReviewDepth.Deep)) ?? RoleModels.NoneValue,
        _ when RoleOf(key) is ({ } role, var effort) => (effort ? roles.Effort(role) : roles.Model(role)) ?? RoleModels.NoneValue,
        _ => "",
    };

    // The role a role key is for, and whether it is its effort rather than its model.
    private static (string? Role, bool Effort) RoleOf(string key) =>
        RoleModels.Roles.FirstOrDefault(r => key == RoleModels.ModelKey(r)) is { } model ? (model, false)
        : RoleModels.Roles.FirstOrDefault(r => key == RoleModels.EffortKey(r)) is { } effort ? (effort, true)
        : (null, false);

    /// <summary>Whether a setting is in force as soon as it is saved, with no restart: a role's model or effort, the merge
    /// method, the start limits, the priority repos, the job limits, maintenance and whether fleet flights are accepted.</summary>
    private static bool Live(string key) => NodeSettings.IsRoleKey(key)
        || key is NodeSettings.MergeMethodKey or NodeSettings.StartLimitKey or NodeSettings.PriorityStartLimitKey or NodeSettings.PriorityReposKey or NodeSettings.WeekLimitKey or NodeSettings.RepoTrackersKey or NodeSettings.NoAutoMergeReposKey
            or NodeSettings.MaxJobsKey or NodeSettings.JobMemoryKey or NodeSettings.MaintenanceKey or NodeSettings.AcceptFleetFlightsKey or NodeSettings.AutopilotKey or NodeSettings.ShortcutTokenKey;

    // A value as a row carries it: a list's items a line each (a folder may hold a space), else the one value.
    private static string Join(string key, IReadOnlyList<string> values) => string.Join(key is NodeSettings.RepoRootsKey or NodeSettings.PriorityReposKey or NodeSettings.RepoTrackersKey or NodeSettings.NoAutoMergeReposKey ? '\n' : ' ', values);

    // What a key is when neither the command line nor the file says.
    private static string Default(string key) => key switch
    {
        NodeSettings.LanKey => "off",
        NodeSettings.MaintenanceKey => "off",
        NodeSettings.AcceptFleetFlightsKey => "off",
        NodeSettings.AutopilotKey => "off",
        NodeSettings.WorkerLadderKey => "on",
        NodeSettings.WorkerModelMaxKey => RoleModels.DefaultWorkerModelMax,
        NodeSettings.WorkerEffortMaxKey => RoleModels.DefaultWorkerEffortMax,
        NodeSettings.ShortcutTokenKey => "",
        NodeSettings.LanPortKey => NodeWire.DefaultLanPort.ToString(CultureInfo.InvariantCulture),
        NodeSettings.MachineKey => (MachinePrefixes.Normalise(Environment.GetEnvironmentVariable("PILOT_MACHINE")) ?? MachinePrefixes.FromName(Environment.MachineName)),
        NodeSettings.RepoRootsKey => "",
        NodeSettings.UpdatePolicyKey => UpdatePolicy.Auto.Name(),
        NodeSettings.UpdateChannelKey => UpdateChannel.Stable.Name(),
        NodeSettings.MaxJobsKey => "1",
        NodeSettings.JobMemoryKey => "0",
        NodeSettings.MergeMethodKey => "rebase",
        NodeSettings.StartLimitKey => "90",
        NodeSettings.PriorityStartLimitKey => "98",
        NodeSettings.WeekLimitKey => "90",
        NodeSettings.PriorityReposKey => "",
        NodeSettings.RepoTrackersKey => "",
        NodeSettings.NoAutoMergeReposKey => "",
        _ when RoleOf(key) is (RoleModels.ReviewerDeep, var effort) => effort ? "high" : RoleModels.DefaultModel(RoleModels.Reviewer),
        _ when RoleOf(key) is ({ } role, var effort) => effort ? RoleModels.NoneValue : RoleModels.DefaultModel(role),
        _ => "",
    };

    private static (string Label, string Detail, string[] Choices) Describe(string key) => key switch
    {
        NodeSettings.LanKey => ("On the LAN", "Listen on the LAN and announce it: anyone there may watch, a command needs a grant you allow", ["on", "off"]),
        NodeSettings.MaintenanceKey => ("Maintenance", "While on, this node takes no new flight: a create-flight is refused with the reason; flights in the air, held ones, legs, reviews, landings and jobs carry on", ["on", "off"]),
        NodeSettings.AcceptFleetFlightsKey => ("Accept fleet flights", "Whether this node is willing to take a flight another node of the fleet routes to it, when a new flight is made", ["on", "off"]),
        NodeSettings.AutopilotKey => ("Autopilot", "While on, an assistant (a session you talk to) does your-only things (merge, land, settings, grants, update) directly when you asked; off, each raises a card for you to allow. Only you can change it, never an assistant", ["on", "off"]),
        NodeSettings.WorkerLadderKey => ("Worker ladder", "While on, a leg redone steps its fresh worker up by attempt: attempts 1 and 2 on the leg's model and effort, attempt 3 on opus, attempt 4 on opus at xhigh, never below the leg's plan nor past the max settings; off keeps the leg's model", ["on", "off"]),
        NodeSettings.WorkerModelMaxKey => ("Worker model max", "The strongest model the worker ladder steps up to; opus by default", ["sonnet", "opus"]),
        NodeSettings.WorkerEffortMaxKey => ("Worker effort max", "The highest effort the worker ladder steps up to; max by default", [.. RoleModels.Efforts]),
        NodeSettings.ShortcutTokenKey => ("Shortcut token", "The Shortcut API token that reads a story's title when a flight is made on one (sc-N); the SHORTCUT_API_TOKEN variable wins over it. Only whether it is set is shown", []),
        NodeSettings.LanPortKey => ("LAN port", "The TCP port other machines reach this node on", []),
        NodeSettings.MachineKey => ("Machine prefix", "Two letters and a number, at the front of this machine's session names (DE1-watchtower)", []),
        NodeSettings.RepoRootsKey => ("Repo roots", "Folders of git clones the new-flight wizard offers, beside the flights' own repos", []),
        NodeSettings.UpdatePolicyKey => ("Updates", "auto installs a newer pilot at once (jobs carry on under the new node); ask raises a card; off does not look", [.. UpdatePolicies.Names]),
        NodeSettings.UpdateChannelKey => ("Update channel", "stable follows the tagged releases; beta follows main's every push (a Debug build, needs the .NET runtime)", [.. UpdateChannels.Names]),
        NodeSettings.MaxJobsKey => ("Jobs at once", "How many background tasks run at once; the rest wait their turn", ["1", "2", "3", "4"]),
        NodeSettings.JobMemoryKey => ("Job memory", "The least free memory, in GB, a second job waits for: a floor under the measured need of its kind of command, never a cap; 0 for the measured need alone", ["0", "2", "4", "8"]),
        NodeSettings.PriorityReposKey => ("Priority repos", "Repos whose flights keep going past the start limit, up to the priority limit", []),
        NodeSettings.RepoTrackersKey => ("Repo trackers", "The tracker a bare number means when a flight is made in the repo; a repo not listed means GitHub issues", []),
        NodeSettings.NoAutoMergeReposKey => ("No auto-merge repos", "Repos whose PRs the node never merges by itself, autopilot or not; only your own merge lands them", []),
        NodeSettings.PriorityStartLimitKey => ("Priority start limit", "How far into the 5-hour window a priority repo's flight still goes on by itself, in percent", ["95", "98", "100"]),
        NodeSettings.StartLimitKey => ("Start limit", "A session the node starts by itself (a leg, a planner, a reviewer, the watchtower, a compaction) goes ahead until the account's 5-hour window is this spent, in percent; then it waits for the reset", ["80", "90", "95", "100"]),
        NodeSettings.WeekLimitKey => ("Week limit", "How far into the 7-day window the node rings, once per window, as it starts sessions; it rings and does not wait, in percent", ["80", "90", "95", "100"]),
        NodeSettings.MergeMethodKey => ("Merge method", "How a flight's PR is merged: rebase keeps every commit's subject (docs may cite them), squash makes one commit", [.. NodeSettings.MergeMethods]),
        _ when RoleOf(key) is ({ } role, false) => ($"{Title(role)} model", RoleDetail(role) + (role == RoleModels.ReviewerDeep ? "" : "; default leaves the choice to Claude Code"), ["sonnet", "opus", "haiku", RoleModels.NoneValue]),
        _ when RoleOf(key) is (RoleModels.ReviewerDeep, true) => ("Reviewer-deep effort", "The effort a deep review starts at; unset, a step above the reviewer's (high when that is unset); default leaves the choice to Claude Code", [.. RoleModels.Efforts, RoleModels.NoneValue]),
        _ when RoleOf(key) is ({ } role, true) => ($"{Title(role)} effort", $"The effort each {role} starts at; default leaves the choice to Claude Code", [.. RoleModels.Efforts, RoleModels.NoneValue]),
        _ => (key, "", []),
    };

    private static string Title(string role) => char.ToUpperInvariant(role[0]) + role[1..];

    private static string RoleDetail(string role) => role switch
    {
        RoleModels.Watchtower => "The watchtower reads cards and runs pilot for hours: the longest-lived session, so the costliest per turn",
        RoleModels.Worker => "A leg's worker, unless its plan names a model (leg plan --model), which wins",
        RoleModels.Planner => "Plans a flight's legs from its issue: short-lived, and decides everything after it",
        RoleModels.ReviewerDeep => "A deep review's model; unset, it runs on the reviewer's; default leaves the choice to Claude Code",
        _ => "Reviews a flight's PR before its merge card",
    };
}
