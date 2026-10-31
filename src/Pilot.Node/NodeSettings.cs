using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// The node's settings file, <c>settings.json</c> in its data folder: what the Settings page and <c>set node</c> save,
/// read at every start. The command line (and the environment it stands for) wins over it, so an autostart entry or a
/// test can still pin a value; a key the file leaves out takes the default. A value the file holds that does not read
/// is passed over, never fatal: a node that would not start over a typo in a settings file is worse than one that
/// ignores the typo.
/// </summary>
public sealed record NodeSettings
{
    public const string FileName = "settings.json";

    public const string LanKey = "lan";
    public const string LanPortKey = "lan-port";
    public const string MachineKey = "machine";
    public const string RepoRootsKey = "repo-roots";
    public const string UpdatePolicyKey = "update-policy";
    public const string UpdateChannelKey = "update-channel";
    public const string MaxJobsKey = "max-jobs";
    public const string JobMemoryKey = "job-memory";
    public const string MergeMethodKey = "merge-method";
    public const string StartLimitKey = "start-limit";
    public const string PriorityStartLimitKey = "priority-start-limit";
    public const string PriorityReposKey = "priority-repos";
    public const string WeekLimitKey = "week-limit";
    public const string WeekPaceKey = "week-pace";
    public const string SpendAlertKey = "spend-alert";
    public const string MaintenanceKey = "maintenance";
    public const string AcceptFleetFlightsKey = "accept-fleet-flights";
    public const string AutopilotKey = "autopilot";
    public const string ReviewDeepFirstKey = "review-deep-first";
    public const string ShortcutTokenKey = "shortcut-token";
    public const string RepoTrackersKey = "repo-trackers";
    public const string NoAutoMergeReposKey = "no-auto-merge-repos";
    public const string OpusCapKey = "opus-cap";
    public const string OneFlightPerRepoKey = "one-flight-per-repo";
    /// <summary>The repos whose flights keep their legs as sub-tasks of the work item (a GitHub sub-issue, a Shortcut task); off for a repo left out.</summary>
    public const string SubTaskReposKey = "sub-task-repos";
    public const string MergeBatchKey = "merge-batch";
    /// <summary>The strongest model a leg's worker steps up to (<see cref="RoleModels.WorkerFor"/>); opus unless set.</summary>
    public const string WorkerModelMaxKey = "worker-model-max";
    /// <summary>The highest effort a leg's worker steps up to; max unless set.</summary>
    public const string WorkerEffortMaxKey = "worker-effort-max";
    /// <summary>Whether a leg's fresh worker steps up a model ladder by attempt; on unless set off.</summary>
    public const string WorkerLadderKey = "worker-ladder";

    /// <summary>How a flight's PR is merged when the merge names no method: rebase keeps every commit's subject, which a
    /// repo's docs may cite (tianwen's rule); squash makes one commit of them; merge adds a merge commit.</summary>
    public static IReadOnlyList<string> MergeMethods { get; } = ["rebase", "squash", "merge"];

    /// <summary>Every key, in the order the Settings page shows them: the node's own, then a model and an effort per role.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [LanKey, MachineKey, RepoRootsKey, UpdateChannelKey, UpdatePolicyKey, MaxJobsKey, JobMemoryKey, LanPortKey, StartLimitKey, PriorityReposKey, PriorityStartLimitKey, WeekLimitKey, WeekPaceKey, SpendAlertKey, MergeMethodKey, MaintenanceKey, AcceptFleetFlightsKey, AutopilotKey, ReviewDeepFirstKey, ShortcutTokenKey, RepoTrackersKey, NoAutoMergeReposKey, OpusCapKey, OneFlightPerRepoKey, SubTaskReposKey, MergeBatchKey,
         .. RoleModels.Roles.Select(RoleModels.ModelKey), .. RoleModels.Roles.Select(RoleModels.EffortKey), WorkerModelMaxKey, WorkerEffortMaxKey, WorkerLadderKey];

    /// <summary>Whether <paramref name="key"/> is a role's model or effort (<see cref="RoleModels"/>).</summary>
    public static bool IsRoleKey(string key) => RoleModels.Roles.Any(r => key == RoleModels.ModelKey(r) || key == RoleModels.EffortKey(r)) || key is WorkerModelMaxKey or WorkerEffortMaxKey;

    public bool? Lan { get; init; }

    /// <summary>How many Opus sessions the fleet runs at once (<see cref="OpusCapKey"/>); 0 for no cap, 3 unless set.</summary>
    public int? OpusCap { get; init; }

    /// <summary>Whether only one flight per repo is in the air at a time (<see cref="OneFlightPerRepoKey"/>); off unless set.</summary>
    public bool? OneFlightPerRepo { get; init; }

    /// <summary>Whether the node takes no new flight (<see cref="MaintenanceKey"/>): flights already made carry on, a create-flight is refused.</summary>
    public bool? Maintenance { get; init; }

    /// <summary>Whether the node is willing to take a flight another node of the fleet routes to it (<see cref="AcceptFleetFlightsKey"/>); off unless set.</summary>
    public bool? AcceptFleetFlights { get; init; }

    /// <summary>Whether a repo's ready merge queue entries are merged as one batch (<see cref="MergeBatchKey"/>), its CI run once; off unless set.</summary>
    public bool? MergeBatch { get; init; }

    /// <summary>Whether an assistant's your-only requests run directly (<see cref="AutopilotKey"/>) rather than raising a confirm card; off unless the person saved it on, and only the person may.</summary>
    public bool? Autopilot { get; init; }

    /// <summary>Whether the first review of a large diff runs on the deep model (<see cref="ReviewDeepFirstKey"/>); on unless the person saved it off. A re-review never does.</summary>
    public bool? ReviewDeepFirst { get; init; }

    /// <summary>Whether a leg's fresh worker steps up the model ladder by attempt (<see cref="WorkerLadderKey"/>); on unless set off.</summary>
    public bool? WorkerLadder { get; init; }

    /// <summary>The Shortcut API token (<see cref="ShortcutTokenKey"/>) that reads a story's title; the <c>SHORTCUT_API_TOKEN</c> variable wins over it. Never shown by the settings output beyond set or not.</summary>
    public string? ShortcutToken { get; init; }

    public int? LanPort { get; init; }

    public string? Machine { get; init; }

    public IReadOnlyList<string>? RepoRoots { get; init; }

    public UpdatePolicy? UpdatePolicy { get; init; }

    public UpdateChannel? UpdateChannel { get; init; }

    public int? MaxJobs { get; init; }

    public double? JobMemoryGigabytes { get; init; }

    public string? MergeMethod { get; init; }

    public double? StartLimit { get; init; }

    public double? PriorityStartLimit { get; init; }

    /// <summary>The 7-day window's share past which a start rings, once per window, and goes ahead.</summary>
    public double? WeekLimit { get; init; }

    /// <summary>The margin, in points, a start tolerates between the 7-day window's used share and its elapsed share; null is off.</summary>
    public double? WeekPace { get; init; }

    /// <summary>The share of the 7-day window, in percent, a leg's rounds may spend before the next round rings; 3 when unset.</summary>
    public double? SpendAlert { get; init; }

    /// <summary>The repos whose flights have runway: they go on past <see cref="StartLimit"/>, up to <see cref="PriorityStartLimit"/>.</summary>
    public IReadOnlyList<string>? PriorityRepos { get; init; }

    /// <summary>Per repo (its folder name), the tracker a bare number means (<see cref="RepoTrackersKey"/>), each entry <c>repo=shortcut</c> or <c>repo=github</c>; a repo left out means GitHub.</summary>
    public IReadOnlyList<string>? RepoTrackers { get; init; }

    /// <summary>The repos (folder names) whose PRs the node never merges by itself (<see cref="NoAutoMergeReposKey"/>), autopilot or not: an assistant's merge raises a card, and a passed flight rings.</summary>
    public IReadOnlyList<string>? NoAutoMergeRepos { get; init; }

    /// <summary>The repos (folder names) whose flights write their legs to the work item as sub-tasks (<see cref="SubTaskReposKey"/>); a repo left out writes nothing to its tracker.</summary>
    public IReadOnlyList<string>? SubTaskRepos { get; init; }

    /// <summary>The roles' models and efforts by key (<c>worker-model</c>: <c>opus</c>); a key left out takes the role's default.</summary>
    public IReadOnlyDictionary<string, string>? Roles { get; init; }

    public static string PathIn(string dataFolder) => Path.Combine(dataFolder, FileName);

    /// <summary>The file at <paramref name="file"/>; none, or one that is not a JSON object, is no settings at all.</summary>
    public static NodeSettings Read(string file)
    {
        JsonObject? root;
        try
        {
            root = File.Exists(file)
                ? JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            root = null;
        }
        if (root is null)
        {
            return new NodeSettings();
        }
        var settings = new NodeSettings();
        foreach (var key in Keys)
        {
            if (root[key] is { } value && Values(value) is { } values && With(settings, key, values) is { Settings: { } read })
            {
                settings = read;
            }
        }
        return settings;
    }

    /// <summary>Writes the file whole, through a temporary file, so a reader never sees half of it.</summary>
    public void Write(string file)
    {
        var root = new JsonObject();
        foreach (var key in Keys)
        {
            if (Get(key) is { } values)
            {
                root[key] = key switch
                {
                    LanKey => JsonValue.Create(Lan),
                    MaintenanceKey => JsonValue.Create(Maintenance),
                    OneFlightPerRepoKey => JsonValue.Create(OneFlightPerRepo),
                    OpusCapKey => JsonValue.Create(OpusCap),
                    AcceptFleetFlightsKey => JsonValue.Create(AcceptFleetFlights),
                    AutopilotKey => JsonValue.Create(Autopilot),
                    ReviewDeepFirstKey => JsonValue.Create(ReviewDeepFirst),
                    MergeBatchKey => JsonValue.Create(MergeBatch),
                    WorkerLadderKey => JsonValue.Create(WorkerLadder),
                    LanPortKey => JsonValue.Create(LanPort),
                    MaxJobsKey => JsonValue.Create(MaxJobs),
                    JobMemoryKey => JsonValue.Create(JobMemoryGigabytes),
                    RepoRootsKey or RepoTrackersKey or NoAutoMergeReposKey or SubTaskReposKey => new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]),
                    _ => JsonValue.Create(values[0]),
                };
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = file + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>The value the file holds for <paramref name="key"/>, as <c>set node</c> spells it; null when it holds none.</summary>
    public IReadOnlyList<string>? Get(string key) => key switch
    {
        LanKey => Lan is { } lan ? [lan ? "on" : "off"] : null,
        MaintenanceKey => Maintenance is { } maintenance ? [maintenance ? "on" : "off"] : null,
        OneFlightPerRepoKey => OneFlightPerRepo is { } one ? [one ? "on" : "off"] : null,
        OpusCapKey => OpusCap is { } cap ? [cap.ToString(CultureInfo.InvariantCulture)] : null,
        AcceptFleetFlightsKey => AcceptFleetFlights is { } accept ? [accept ? "on" : "off"] : null,
        AutopilotKey => Autopilot is { } autopilot ? [autopilot ? "on" : "off"] : null,
        ReviewDeepFirstKey => ReviewDeepFirst is { } deepFirst ? [deepFirst ? "on" : "off"] : null,
        MergeBatchKey => MergeBatch is { } batch ? [batch ? "on" : "off"] : null,
        WorkerLadderKey => WorkerLadder is { } ladder ? [ladder ? "on" : "off"] : null,
        LanPortKey => LanPort is { } port ? [port.ToString(CultureInfo.InvariantCulture)] : null,
        ShortcutTokenKey => ShortcutToken is { } token ? [token] : null,
        MachineKey => Machine is { } machine ? [machine] : null,
        RepoRootsKey => RepoRoots,
        UpdatePolicyKey => UpdatePolicy is { } policy ? [policy.Name()] : null,
        UpdateChannelKey => UpdateChannel is { } channel ? [channel.Name()] : null,
        MaxJobsKey => MaxJobs is { } jobs ? [jobs.ToString(CultureInfo.InvariantCulture)] : null,
        JobMemoryKey => JobMemoryGigabytes is { } memory ? [memory.ToString(CultureInfo.InvariantCulture)] : null,
        MergeMethodKey => MergeMethod is { } merge ? [merge] : null,
        StartLimitKey => StartLimit is { } limit ? [limit.ToString(CultureInfo.InvariantCulture)] : null,
        PriorityStartLimitKey => PriorityStartLimit is { } high ? [high.ToString(CultureInfo.InvariantCulture)] : null,
        WeekLimitKey => WeekLimit is { } week ? [week.ToString(CultureInfo.InvariantCulture)] : null,
        WeekPaceKey => WeekPace is { } pace ? [pace.ToString(CultureInfo.InvariantCulture)] : null,
        SpendAlertKey => SpendAlert is { } alert ? [alert.ToString(CultureInfo.InvariantCulture)] : null,
        PriorityReposKey => PriorityRepos,
        RepoTrackersKey => RepoTrackers,
        NoAutoMergeReposKey => NoAutoMergeRepos,
        SubTaskReposKey => SubTaskRepos,
        _ when IsRoleKey(key) && Roles?.GetValueOrDefault(key) is { } role => [role],
        _ => null,
    };

    /// <summary>
    /// These settings with <paramref name="key"/> set to <paramref name="values"/> (none, or <c>default</c>, takes it
    /// out), or why not: an unknown key, or a value it does not take.
    /// </summary>
    public static (NodeSettings? Settings, string? Error) With(NodeSettings settings, string key, IReadOnlyList<string> values)
    {
        var given = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
        var clear = given.Length == 0 || given is ["default"];
        string? One() => given.Length == 1 ? given[0] : null;
        switch (key)
        {
            case LanKey:
                if (clear)
                {
                    return (settings with { Lan = null }, null);
                }
                return One()?.ToLowerInvariant() switch
                {
                    "on" or "true" or "yes" => (settings with { Lan = true }, null),
                    "off" or "false" or "no" => (settings with { Lan = false }, null),
                    _ => (null, "lan takes on or off"),
                };
            case MaintenanceKey:
                if (clear)
                {
                    return (settings with { Maintenance = null }, null);
                }
                return OnOff(One()) is { } maintenance
                    ? (settings with { Maintenance = maintenance }, null)
                    : (null, "maintenance takes on or off");
            case OneFlightPerRepoKey:
                if (clear)
                {
                    return (settings with { OneFlightPerRepo = null }, null);
                }
                return OnOff(One()) is { } oneFlight
                    ? (settings with { OneFlightPerRepo = oneFlight }, null)
                    : (null, "one-flight-per-repo takes on or off");
            case OpusCapKey:
                if (clear)
                {
                    return (settings with { OpusCap = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var opus) && opus is >= 0 and <= 64
                    ? (settings with { OpusCap = opus }, null)
                    : (null, "opus-cap takes the most Opus sessions the fleet runs at once, 0 to 64 (0 for no cap)");
            case AcceptFleetFlightsKey:
                if (clear)
                {
                    return (settings with { AcceptFleetFlights = null }, null);
                }
                return OnOff(One()) is { } accept
                    ? (settings with { AcceptFleetFlights = accept }, null)
                    : (null, "accept-fleet-flights takes on or off");
            case AutopilotKey:
                if (clear)
                {
                    return (settings with { Autopilot = null }, null);
                }
                return OnOff(One()) is { } autopilot
                    ? (settings with { Autopilot = autopilot }, null)
                    : (null, "autopilot takes on or off");
            case ReviewDeepFirstKey:
                if (clear)
                {
                    return (settings with { ReviewDeepFirst = null }, null);
                }
                return OnOff(One()) is { } deepFirst
                    ? (settings with { ReviewDeepFirst = deepFirst }, null)
                    : (null, "review-deep-first takes on or off");
            case MergeBatchKey:
                if (clear)
                {
                    return (settings with { MergeBatch = null }, null);
                }
                return OnOff(One()) is { } mergeBatch
                    ? (settings with { MergeBatch = mergeBatch }, null)
                    : (null, "merge-batch takes on or off");
            case WorkerLadderKey:
                if (clear)
                {
                    return (settings with { WorkerLadder = null }, null);
                }
                return OnOff(One()) is { } ladder
                    ? (settings with { WorkerLadder = ladder }, null)
                    : (null, "worker-ladder takes on or off");
            case ShortcutTokenKey:
                return clear
                    ? (settings with { ShortcutToken = null }, null)
                    : One() is { } secret ? (settings with { ShortcutToken = secret }, null) : (null, "shortcut-token takes the API token, one word");
            case LanPortKey:
                if (clear)
                {
                    return (settings with { LanPort = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
                    ? (settings with { LanPort = port }, null)
                    : (null, "lan-port takes a TCP port, 1 to 65535");
            case MachineKey:
                if (clear)
                {
                    return (settings with { Machine = null }, null);
                }
                return MachinePrefixes.Normalise(One()) is { } prefix
                    ? (settings with { Machine = prefix }, null)
                    : (null, "machine takes two letters and a number, as in DE1");
            case RepoRootsKey:
                if (clear)
                {
                    return (settings with { RepoRoots = null }, null);
                }
                var roots = given.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return roots.FirstOrDefault(r => !Directory.Exists(r)) is { } missing
                    ? (null, $"{missing} is not a folder on this machine")
                    : (settings with { RepoRoots = roots }, null);
            case UpdatePolicyKey:
                if (clear)
                {
                    return (settings with { UpdatePolicy = null }, null);
                }
                return UpdatePolicies.Parse(One()) is { } policy
                    ? (settings with { UpdatePolicy = policy }, null)
                    : (null, $"update-policy takes {string.Join(", ", UpdatePolicies.Names)}");
            case UpdateChannelKey:
                if (clear)
                {
                    return (settings with { UpdateChannel = null }, null);
                }
                return UpdateChannels.Parse(One()) is { } channel
                    ? (settings with { UpdateChannel = channel }, null)
                    : (null, $"update-channel takes {string.Join(" or ", UpdateChannels.Names)}");
            case MaxJobsKey:
                if (clear)
                {
                    return (settings with { MaxJobs = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var jobs) && jobs is >= 1 and <= 64
                    ? (settings with { MaxJobs = jobs }, null)
                    : (null, "max-jobs takes a number, 1 to 64");
            case JobMemoryKey:
                if (clear)
                {
                    return (settings with { JobMemoryGigabytes = null }, null);
                }
                return double.TryParse(One(), NumberStyles.Float, CultureInfo.InvariantCulture, out var memory) && memory is >= 0 and <= 1024
                    ? (settings with { JobMemoryGigabytes = memory }, null)
                    : (null, "job-memory takes the least free memory in GB a second job waits for (a floor under its measured need), 0 for the measured need alone");
            case PriorityStartLimitKey:
                if (clear)
                {
                    return (settings with { PriorityStartLimit = null }, null);
                }
                return double.TryParse(One(), NumberStyles.Float, CultureInfo.InvariantCulture, out var high) && high is > 0 and <= 100
                    ? (settings with { PriorityStartLimit = high }, null)
                    : (null, "priority-start-limit takes the 5-hour window's share in percent, above 0 and up to 100");
            case WeekLimitKey:
                if (clear)
                {
                    return (settings with { WeekLimit = null }, null);
                }
                return double.TryParse(One(), NumberStyles.Float, CultureInfo.InvariantCulture, out var weekly) && weekly is > 0 and <= 100
                    ? (settings with { WeekLimit = weekly }, null)
                    : (null, "week-limit takes the 7-day window's share in percent, above 0 and up to 100");
            case WeekPaceKey:
            {
                var raw = clear ? "off" : One();
                if (raw is "off" or "none")
                {
                    return (settings with { WeekPace = null }, null);
                }
                if (raw == "on")
                {
                    return (settings with { WeekPace = 10 }, null);
                }
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin) && margin is >= 0 and <= 100
                    ? (settings with { WeekPace = margin }, null)
                    : (null, "week-pace takes off, on (a margin of 10 points) or a margin in points, 0 to 100");
            }
            case SpendAlertKey:
                if (clear)
                {
                    return (settings with { SpendAlert = null }, null);
                }
                return double.TryParse(One(), NumberStyles.Float, CultureInfo.InvariantCulture, out var share) && share is > 0 and <= 100
                    ? (settings with { SpendAlert = share }, null)
                    : (null, "spend-alert takes the 7-day window's share in percent, above 0 and up to 100");
            case PriorityReposKey:
            {
                if (clear)
                {
                    return (settings with { PriorityRepos = null }, null);
                }
                var (priority, priorityError) = RepoNames(given);
                return priority is null ? (null, priorityError) : (settings with { PriorityRepos = priority }, null);
            }
            case NoAutoMergeReposKey:
            {
                if (clear)
                {
                    return (settings with { NoAutoMergeRepos = null }, null);
                }
                var (noMerge, noMergeError) = RepoNames(given);
                return noMerge is null ? (null, noMergeError) : (settings with { NoAutoMergeRepos = noMerge }, null);
            }
            case SubTaskReposKey:
            {
                if (clear)
                {
                    return (settings with { SubTaskRepos = null }, null);
                }
                var (subTask, subTaskError) = RepoNames(given);
                return subTask is null ? (null, subTaskError) : (settings with { SubTaskRepos = subTask }, null);
            }
            case RepoTrackersKey:
            {
                if (clear)
                {
                    return (settings with { RepoTrackers = null }, null);
                }
                var entries = new List<string>();
                foreach (var entry in given)
                {
                    // The last '=' splits, as a folder given by path may hold one.
                    var at = entry.LastIndexOf('=');
                    var tracker = at > 0 ? entry[(at + 1)..].Trim().ToLowerInvariant() : "";
                    if (at <= 0 || entry[..at].Trim().Length == 0 || tracker is not ("github" or "shortcut"))
                    {
                        return (null, "repo-trackers takes repo=tracker entries, as in bullclip-backend=shortcut (github or shortcut)");
                    }
                    var (repo, folderError) = RepoName(entry[..at].Trim());
                    if (repo is null)
                    {
                        return (null, folderError);
                    }
                    entries.RemoveAll(e => e.StartsWith(repo + "=", StringComparison.OrdinalIgnoreCase));
                    entries.Add($"{repo}={tracker}");
                }
                return (settings with { RepoTrackers = entries }, null);
            }
            case StartLimitKey:
                if (clear)
                {
                    return (settings with { StartLimit = null }, null);
                }
                return double.TryParse(One(), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && limit is > 0 and <= 100
                    ? (settings with { StartLimit = limit }, null)
                    : (null, "start-limit takes the 5-hour window's share in percent, above 0 and up to 100");
            case MergeMethodKey:
                if (clear)
                {
                    return (settings with { MergeMethod = null }, null);
                }
                return One()?.ToLowerInvariant() is { } merge && MergeMethods.Contains(merge)
                    ? (settings with { MergeMethod = merge }, null)
                    : (null, $"merge-method takes {string.Join(", ", MergeMethods)}");
            case var role when IsRoleKey(role):
            {
                var roles = new Dictionary<string, string>(settings.Roles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                if (clear)
                {
                    roles.Remove(role);
                    return (settings with { Roles = roles.Count == 0 ? null : roles }, null);
                }
                var value = One()?.ToLowerInvariant();
                var effort = role.Contains("-effort", StringComparison.Ordinal);
                if (value is null || (value != RoleModels.NoneValue && (effort ? !RoleModels.Efforts.Contains(value) : !RoleModels.IsModel(value))))
                {
                    return (null, effort
                        ? $"{role} takes {string.Join(", ", RoleModels.Efforts)} or none"
                        : $"{role} takes a model (sonnet, opus, haiku, or a full id as claude-opus-5-5) or none");
                }
                roles[role] = value;
                return (settings with { Roles = roles }, null);
            }
            default:
                return (null, $"there is no setting {key}; there are {string.Join(", ", Keys)}");
        }
    }

    // A repo as the lists name it: its folder's name. A path (what the page's free entry sends for a clone the picker does not
    // list) must be a folder on this machine, and stands for its name; a bare name is taken as it is.
    private static (string? Name, string? Error) RepoName(string entry)
    {
        if (!Path.IsPathRooted(entry) && !entry.Contains('/') && !entry.Contains('\\'))
        {
            return (entry, null);
        }
        var full = Path.GetFullPath(entry);
        return Directory.Exists(full) && Path.GetFileName(Path.TrimEndingDirectorySeparator(full)) is { Length: > 0 } name
            ? (name, null)
            : (null, $"{entry} is not a folder on this machine");
    }

    private static (string[]? Names, string? Error) RepoNames(string[] given)
    {
        var names = new List<string>();
        foreach (var entry in given)
        {
            var (name, error) = RepoName(entry);
            if (name is null)
            {
                return (null, error);
            }
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
        return ([.. names], null);
    }

    /// <summary>The tracker a bare number means in <paramref name="repo"/> (its folder name): its <see cref="RepoTrackersKey"/> entry, else GitHub.</summary>
    public TrackerKind TrackerOf(string repo) =>
        RepoTrackers?.Select(e => e.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && string.Equals(p[0], repo, StringComparison.OrdinalIgnoreCase)) is { } found
            && found[1] == "shortcut" ? TrackerKind.Shortcut : TrackerKind.GitHub;

    // The on/off words set node takes for a switch; null for anything else.
    private static bool? OnOff(string? value) => value?.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" => true,
        "off" or "false" or "no" => false,
        _ => null,
    };

    // A file's value as set node's words: a list's items, or the one value as text.
    private static string[]? Values(JsonNode value) => value switch
    {
        JsonArray array => [.. array.Select(v => v is JsonValue item && item.TryGetValue<string>(out var text) ? text : "").Where(v => v.Length > 0)],
        JsonValue v when v.TryGetValue<bool>(out var flag) => [flag ? "on" : "off"],
        JsonValue v when v.TryGetValue<string>(out var text) => [text],
        JsonValue v => [v.ToJsonString()],
        _ => null,
    };
}

/// <summary>The settings file laid under a node's command line.</summary>
public static class NodeSettingsMerge
{
    /// <summary>
    /// <paramref name="options"/> (the command line's, its defaults filled in) with every setting the command line did
    /// not give (<paramref name="given"/>) taken from <paramref name="saved"/> where it holds one.
    /// </summary>
    public static NodeOptions Over(this NodeSettings saved, NodeOptions options, IReadOnlySet<string> given)
    {
        bool Free(string key) => !given.Contains(key);
        return options with
        {
            Lan = Free(NodeSettings.LanKey) && saved.Lan is { } lan ? lan : options.Lan,
            LanPort = Free(NodeSettings.LanPortKey) && saved.LanPort is { } port ? port : options.LanPort,
            MachinePrefix = Free(NodeSettings.MachineKey) && saved.Machine is { } machine ? machine : options.MachinePrefix,
            RepoRootsGiven = Free(NodeSettings.RepoRootsKey) && saved.RepoRoots is { Count: > 0 } roots ? roots : options.RepoRootsGiven,
            UpdatePolicy = Free(NodeSettings.UpdatePolicyKey) && saved.UpdatePolicy is { } policy ? policy : options.UpdatePolicy,
            UpdateChannel = Free(NodeSettings.UpdateChannelKey) && saved.UpdateChannel is { } channel ? channel : options.UpdateChannel,
            MaxJobs = Free(NodeSettings.MaxJobsKey) && saved.MaxJobs is { } jobs ? jobs : options.MaxJobs,
            JobMemoryGigabytes = Free(NodeSettings.JobMemoryKey) && saved.JobMemoryGigabytes is { } memory ? memory : options.JobMemoryGigabytes,
            Saved = saved,
            CommandLineKeys = given,
        };
    }
}
