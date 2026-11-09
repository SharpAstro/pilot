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
    public const string JobPriorityKey = "job-priority";
    /// <summary>Whether a job in a plane builds into output under the plane (MSBuild artifacts output), so two planes never share a sibling clone's obj and the sibling lock is skipped for it; off unless set on.</summary>
    public const string JobPlaneOutputKey = "job-plane-output";
    /// <summary>
    /// What <see cref="JobPlaneOutputKey"/> is when the settings file does not say: off (PIL802). A plane builds on the published
    /// packages, so it compiles no sibling and has nothing to share; only a <c>pilot: sibling source</c> flight holds clones.
    /// </summary>
    public const bool JobPlaneOutputDefault = false;
    public const string MergeMethodKey = "merge-method";
    public const string StartLimitKey = "start-limit";
    public const string PriorityStartLimitKey = "priority-start-limit";
    public const string PriorityReposKey = "priority-repos";
    public const string WeekLimitKey = "week-limit";
    public const string WeekPaceKey = "week-pace";
    public const string SpendAlertKey = "spend-alert";
    public const string WatchtowerCompactShareKey = "watchtower-compact-share";
    public const string WatchtowerChatMessagesKey = "watchtower-chat-messages";
    public const string WatchtowerCompactIdleKey = "watchtower-compact-idle";
    public const string WatchtowerCompactAboveKey = "watchtower-compact-above";
    public const string MaintenanceKey = "maintenance";
    public const string AcceptFleetFlightsKey = "accept-fleet-flights";
    public const string AutopilotKey = "autopilot";
    public const string ReviewDeepFirstKey = "review-deep-first";
    public const string UserSkillsKey = "user-skills";
    public const string ShortcutTokenKey = "shortcut-token";
    public const string RepoTrackersKey = "repo-trackers";
    public const string NoAutoMergeReposKey = "no-auto-merge-repos";
    public const string PrWatchIdleKey = "pr-watch-idle";
    /// <summary>How many go-arounds a leg may have, whatever their reasons, before the watchtower and the node send it round no more.</summary>
    public const string MaxGoAroundsKey = "max-go-arounds";
    public const int DefaultMaxGoArounds = 5;
    /// <summary>How many days a job's scratch folder may sit before the disk sweep deletes it, whatever its session did.</summary>
    public const string ScratchDaysKey = "scratch-days";
    public const int DefaultScratchDays = 2;
    public const string DiskAlertGbKey = "disk-alert-gb";
    public const int DefaultDiskAlertGb = 20;
    /// <summary>
    /// Runner-hours of CI a flight's PR may spend before the node stops pushing for it and rings the owner (PIL795). The same flight
    /// red for more than <see cref="FlightCiRedHours"/> in a row stops it too.
    /// </summary>
    public const string FlightCiBudgetKey = "flight-ci-budget";
    public const int DefaultFlightCiBudget = 4;
    /// <summary>Hours a flight's PR may stay red before the node stops pushing for it (PIL795).</summary>
    public const int FlightCiRedHours = 3;

    /// <summary>Hours a flight may stay open before the digest and the brief call it stale (#736).</summary>
    public const string StaleFlightHoursKey = "stale-flight-hours";
    public const int DefaultStaleFlightHours = 12;
    /// <summary>Hours a flight's PR may sit open with green CI before it is called stale.</summary>
    public const string StalePrHoursKey = "stale-pr-hours";
    public const int DefaultStalePrHours = 12;
    /// <summary>Hours a leg may sit rechecking its check before it is called stale.</summary>
    public const string StaleRecheckHoursKey = "stale-recheck-hours";
    public const int DefaultStaleRecheckHours = 2;
    /// <summary>Hours a ringing card may stay open before it is called stale.</summary>
    public const string StaleCardHoursKey = "stale-card-hours";
    public const int DefaultStaleCardHours = 2;
    /// <summary>How many times a flight's hold may pass its timeout before it is called stale.</summary>
    public const string StaleHoldsKey = "stale-holds";
    public const int DefaultStaleHolds = 2;
    /// <summary>Hours a flight may be held on a flight of a node that cannot be asked before it is called stale (#753).</summary>
    public const string StaleUnreachableHoursKey = "stale-unreachable-hours";
    public const int DefaultStaleUnreachableHours = 1;

    /// <summary>The five staleness bounds' keys (#736).</summary>
    public static IReadOnlyList<string> StaleKeys { get; } = [StaleFlightHoursKey, StalePrHoursKey, StaleRecheckHoursKey, StaleCardHoursKey, StaleHoldsKey, StaleUnreachableHoursKey];

    /// <summary>The default of a staleness key; 0 for any other key.</summary>
    public static int StaleDefault(string key) => key switch
    {
        StaleFlightHoursKey => DefaultStaleFlightHours,
        StalePrHoursKey => DefaultStalePrHours,
        StaleRecheckHoursKey => DefaultStaleRecheckHours,
        StaleCardHoursKey => DefaultStaleCardHours,
        StaleHoldsKey => DefaultStaleHolds,
        StaleUnreachableHoursKey => DefaultStaleUnreachableHours,
        _ => 0,
    };

    /// <summary>The value saved for a staleness key; null when unset.</summary>
    public int? StaleValue(string key) => key switch
    {
        StaleFlightHoursKey => StaleFlightHours,
        StalePrHoursKey => StalePrHours,
        StaleRecheckHoursKey => StaleRecheckHours,
        StaleCardHoursKey => StaleCardHours,
        StaleHoldsKey => StaleHolds,
        StaleUnreachableHoursKey => StaleUnreachableHours,
        _ => null,
    };

    private NodeSettings WithStale(string key, int? value) => key switch
    {
        StaleFlightHoursKey => this with { StaleFlightHours = value },
        StalePrHoursKey => this with { StalePrHours = value },
        StaleRecheckHoursKey => this with { StaleRecheckHours = value },
        StaleCardHoursKey => this with { StaleCardHours = value },
        StaleUnreachableHoursKey => this with { StaleUnreachableHours = value },
        _ => this with { StaleHolds = value },
    };

    /// <summary>How long the pull request watch waits between reads while no client shows the Pull requests page.</summary>
    public static readonly TimeSpan DefaultPrWatchIdle = TimeSpan.FromMinutes(15);
    public const string OpusCapKey = "opus-cap";

    /// <summary>The most reviews and fixers this node runs at once (#912); a start past it waits for a slot.</summary>
    public const string ReviewLimitKey = "review-limit";
    public const string OneFlightPerRepoKey = "one-flight-per-repo";
    /// <summary>The repos whose flights keep their legs as sub-tasks of the work item (a GitHub sub-issue, a Shortcut task); off for a repo left out.</summary>
    public const string SubTaskReposKey = "sub-task-repos";
    public const string MergeBatchKey = "merge-batch";
    /// <summary>Whether a leg's fresh worker steps up a model ladder by attempt; on unless set off.</summary>
    public const string WorkerLadderKey = "worker-ladder";

    /// <summary>How a flight's PR is merged when the merge names no method: rebase keeps every commit's subject, which a
    /// repo's docs may cite (tianwen's rule); squash makes one commit of them; merge adds a merge commit.</summary>
    public static IReadOnlyList<string> MergeMethods { get; } = ["rebase", "squash", "merge"];

    /// <summary>Every key, in the order the Settings page shows them: the node's own, then a range per role.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [LanKey, MachineKey, RepoRootsKey, UpdateChannelKey, UpdatePolicyKey, MaxJobsKey, JobMemoryKey, JobPriorityKey, JobPlaneOutputKey, LanPortKey, StartLimitKey, PriorityReposKey, PriorityStartLimitKey, WeekLimitKey, WeekPaceKey, SpendAlertKey, WatchtowerCompactShareKey, WatchtowerChatMessagesKey, WatchtowerCompactIdleKey, WatchtowerCompactAboveKey, MergeMethodKey, MaintenanceKey, AcceptFleetFlightsKey, AutopilotKey, ReviewDeepFirstKey, UserSkillsKey, ShortcutTokenKey, RepoTrackersKey, NoAutoMergeReposKey, OpusCapKey, ReviewLimitKey, OneFlightPerRepoKey, SubTaskReposKey, MergeBatchKey, PrWatchIdleKey, MaxGoAroundsKey, ScratchDaysKey, DiskAlertGbKey, FlightCiBudgetKey, StaleFlightHoursKey, StalePrHoursKey, StaleRecheckHoursKey, StaleCardHoursKey, StaleHoldsKey, StaleUnreachableHoursKey,
         .. RoleModels.Roles.Select(RoleModels.RangeKey), WorkerLadderKey];

    /// <summary>Whether <paramref name="key"/> is a role's range (<see cref="RoleModels"/>).</summary>
    public static bool IsRoleKey(string key) => RoleModels.Roles.Any(r => key == RoleModels.RangeKey(r));

    public bool? Lan { get; init; }

    /// <summary>How many Opus sessions the fleet runs at once (<see cref="OpusCapKey"/>); 0 for no cap, 3 unless set.</summary>
    public int? OpusCap { get; init; }

    /// <summary>How many reviews and fixers this node runs at once (<see cref="ReviewLimitKey"/>); 0 for no limit, 2 unless set.</summary>
    public int? ReviewLimit { get; init; }

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

    /// <summary>Whether the node installs and keeps current the Claude Code skills it carries (<see cref="UserSkillsKey"/>, <see cref="Node.UserSkills"/>); on unless the person saved it off.</summary>
    public bool? UserSkills { get; init; }

    /// <summary>Whether a leg's fresh worker steps up the model ladder by attempt (<see cref="WorkerLadderKey"/>); on unless set off.</summary>
    public bool? WorkerLadder { get; init; }

    /// <summary>Whether the node's jobs in a plane build into <c>&lt;plane&gt;/artifacts</c> (<see cref="JobPlaneOutputKey"/>); <see cref="JobPlaneOutputDefault"/> unless set.</summary>
    public bool? JobPlaneOutput { get; init; }

    /// <summary>The Shortcut API token (<see cref="ShortcutTokenKey"/>) that reads a story's title; the <c>SHORTCUT_API_TOKEN</c> variable wins over it. Never shown by the settings output beyond set or not.</summary>
    public string? ShortcutToken { get; init; }

    public int? LanPort { get; init; }

    public string? Machine { get; init; }

    public IReadOnlyList<string>? RepoRoots { get; init; }

    public UpdatePolicy? UpdatePolicy { get; init; }

    public UpdateChannel? UpdateChannel { get; init; }

    public int? MaxJobs { get; init; }

    /// <summary>The cap on a leg's go-arounds (<see cref="MaxGoAroundsKey"/>); <see cref="DefaultMaxGoArounds"/> unless set.</summary>
    public int? MaxGoArounds { get; init; }

    /// <summary>Days before a job's scratch is swept (<see cref="ScratchDaysKey"/>); <see cref="DefaultScratchDays"/> unless set.</summary>
    public int? ScratchDays { get; init; }

    /// <summary>Free gigabytes on the repos' drive below which the node rings (<see cref="DiskAlertGbKey"/>); <see cref="DefaultDiskAlertGb"/> unless set.</summary>
    public int? DiskAlertGb { get; init; }

    /// <summary>Runner-hours of CI a flight may spend (<see cref="FlightCiBudgetKey"/>); <see cref="DefaultFlightCiBudget"/> unless set.</summary>
    public int? FlightCiBudget { get; init; }

    /// <summary>The staleness bounds (<see cref="StaleKeys"/>); each its default unless set.</summary>
    public int? StaleFlightHours { get; init; }
    public int? StalePrHours { get; init; }
    public int? StaleRecheckHours { get; init; }
    public int? StaleCardHours { get; init; }
    public int? StaleHolds { get; init; }
    public int? StaleUnreachableHours { get; init; }

    public double? JobMemoryGigabytes { get; init; }
    public Pilot.Node.Flights.JobPriority? JobPriority { get; init; }

    public string? MergeMethod { get; init; }

    public double? StartLimit { get; init; }

    public double? PriorityStartLimit { get; init; }

    /// <summary>The 7-day window's share past which a start rings, once per window, and goes ahead.</summary>
    public double? WeekLimit { get; init; }

    /// <summary>The wait between the pull request watch's reads while no client shows the Pull requests page (<see cref="PrWatchIdleKey"/>); a minute is read while one does.</summary>
    public TimeSpan? PrWatchIdle { get; init; }
    /// <summary>The margin, in points, a start tolerates between the 7-day window's used share and its elapsed share; null is off.</summary>
    public double? WeekPace { get; init; }

    /// <summary>The share of the 7-day window, in percent, a leg's rounds may spend before the next round rings; 3 when unset.</summary>
    public double? SpendAlert { get; init; }

    /// <summary>The share of its window, in percent, past which an idle live watchtower is compacted (<see cref="WatchtowerCompactShareKey"/>); 60 when unset.</summary>
    public double? WatchtowerCompactShare { get; init; }

    /// <summary>The person messages in a watchtower chat, since its last compaction, after which it is compacted once idle (<see cref="WatchtowerChatMessagesKey"/>); 40 when unset, 0 is off.</summary>
    public int? WatchtowerChatMessages { get; init; }

    /// <summary>How many minutes an idle watchtower (the brain) sits before it is compacted, when its context is over <see cref="WatchtowerCompactAbove"/> (<see cref="WatchtowerCompactIdleKey"/>); 50 when unset.</summary>
    public int? WatchtowerCompactIdle { get; init; }

    /// <summary>The context, in thousands of tokens, an idle watchtower must hold for the idle compaction to be worth its turn (<see cref="WatchtowerCompactAboveKey"/>); 100 when unset.</summary>
    public int? WatchtowerCompactAbove { get; init; }

    /// <summary>The repos whose flights have runway: they go on past <see cref="StartLimit"/>, up to <see cref="PriorityStartLimit"/>.</summary>
    public IReadOnlyList<string>? PriorityRepos { get; init; }

    /// <summary>Per repo (its folder name), the tracker a bare number means (<see cref="RepoTrackersKey"/>), each entry <c>repo=shortcut</c> or <c>repo=github</c>; a repo left out means GitHub.</summary>
    public IReadOnlyList<string>? RepoTrackers { get; init; }

    /// <summary>The repos (folder names) whose PRs the node never merges by itself (<see cref="NoAutoMergeReposKey"/>), autopilot or not: an assistant's merge raises a card, and a passed flight rings.</summary>
    public IReadOnlyList<string>? NoAutoMergeRepos { get; init; }

    /// <summary>The repos (folder names) whose flights write their legs to the work item as sub-tasks (<see cref="SubTaskReposKey"/>); a repo left out writes nothing to its tracker.</summary>
    public IReadOnlyList<string>? SubTaskRepos { get; init; }

    /// <summary>The roles' ranges by key (<c>worker-range</c>: <c>sonnet..opus-xhigh</c>); a key left out takes the role's built-in range.</summary>
    public IReadOnlyDictionary<string, string>? Roles { get; init; }

    /// <summary>The file's object holding the fleet layer of the role ranges: not a setting a person sets by key, but what <c>set node &lt;range&gt; --fleet</c> on any node saved here.</summary>
    public const string FleetRangesField = "fleet-ranges";

    /// <summary>The fleet's role ranges by key, kept apart from this node's own (<see cref="Roles"/>), which win over them.</summary>
    public IReadOnlyDictionary<string, string>? FleetRoles { get; init; }

    /// <summary>These settings with the fleet's range for <paramref name="key"/> set to <paramref name="values"/> (none, or <c>default</c>, takes it out), or why not.</summary>
    public static (NodeSettings? Settings, string? Error) WithFleet(NodeSettings settings, string key, IReadOnlyList<string> values)
    {
        if (!IsRoleKey(key))
        {
            return (null, $"{key} has no fleet value; the fleet holds {string.Join(", ", RoleModels.Roles.Select(RoleModels.RangeKey))}");
        }
        // Checked as an own value is, then the own layer is put back as it was.
        var (checkedRange, error) = With(new NodeSettings(), key, values);
        if (checkedRange is null)
        {
            return (null, error);
        }
        var fleet = new Dictionary<string, string>(settings.FleetRoles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (checkedRange.Roles?.GetValueOrDefault(key) is { } range)
        {
            fleet[key] = range;
        }
        else
        {
            fleet.Remove(key);
        }
        return (settings with { FleetRoles = fleet.Count == 0 ? null : fleet }, null);
    }

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
        if (root[FleetRangesField] is JsonObject fleetRanges)
        {
            foreach (var role in RoleModels.Roles)
            {
                var key = RoleModels.RangeKey(role);
                if (fleetRanges[key] is JsonValue v && v.TryGetValue<string>(out var text) && WithFleet(settings, key, [text]) is { Settings: { } withFleet })
                {
                    settings = withFleet;
                }
            }
        }
        return Migrated(settings, root);
    }

    // A reviewer pinned to Sonnet (reviewer-model) with no reviewer-deep-* keys migrates to sonnet..opus-high: its deep reviews
    // now run on the built-in max, on purpose, as the settlement of the sonnet-versus-opus mismatch.
    // Settings from before ranges (#599) held a model and an effort per role, a worker max and a deep review's own: each role that
    // has any becomes the nearest range, its max raised to its default when that sits above, effort max read as xhigh.
    private static NodeSettings Migrated(NodeSettings settings, JsonObject root)
    {
        string? Old(string key) => root[key] is { } v && Values(v) is [{ } first, ..] ? first.Trim().ToLowerInvariant() : null;
        var roles = new Dictionary<string, string>(settings.Roles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var role in RoleModels.Roles)
        {
            var key = RoleModels.RangeKey(role);
            if (roles.ContainsKey(key))
            {
                continue;
            }
            var (model, effort) = (Old(role + "-model"), Old(role + "-effort"));
            var (maxModel, maxEffort) = role switch
            {
                RoleModels.Worker => (Old("worker-model-max"), Old("worker-effort-max")),
                RoleModels.Reviewer => (Old("reviewer-deep-model"), Old("reviewer-deep-effort")),
                _ => (null, null),
            };
            if (model is null && effort is null && maxModel is null && maxEffort is null)
            {
                continue;
            }
            var built = RoleModels.BuiltIn(role);
            var def = RoleRange.Nearest(model, effort, built.Default);
            var max = RoleRange.Nearest(maxModel, maxEffort, built.Max);
            if (max.Order < def.Order)
            {
                max = def;
            }
            roles[key] = new RoleRange(def, max).ToString();
        }
        return roles.Count == 0 ? settings : settings with { Roles = roles };
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
                    ReviewLimitKey => JsonValue.Create(ReviewLimit),
                    AcceptFleetFlightsKey => JsonValue.Create(AcceptFleetFlights),
                    AutopilotKey => JsonValue.Create(Autopilot),
                    ReviewDeepFirstKey => JsonValue.Create(ReviewDeepFirst),
                    UserSkillsKey => JsonValue.Create(UserSkills),
                    MergeBatchKey => JsonValue.Create(MergeBatch),
                    WorkerLadderKey => JsonValue.Create(WorkerLadder),
                    JobPlaneOutputKey => JsonValue.Create(JobPlaneOutput),
                    LanPortKey => JsonValue.Create(LanPort),
                    MaxJobsKey => JsonValue.Create(MaxJobs),
                    MaxGoAroundsKey => JsonValue.Create(MaxGoArounds),
                    ScratchDaysKey => JsonValue.Create(ScratchDays),
                    DiskAlertGbKey => JsonValue.Create(DiskAlertGb),
                    FlightCiBudgetKey => JsonValue.Create(FlightCiBudget),
                    StaleFlightHoursKey or StalePrHoursKey or StaleRecheckHoursKey or StaleCardHoursKey or StaleHoldsKey or StaleUnreachableHoursKey => JsonValue.Create(StaleValue(key)),
                    JobMemoryKey => JsonValue.Create(JobMemoryGigabytes),
                    JobPriorityKey => JsonValue.Create(Pilot.Node.Flights.JobPriorities.Name(JobPriority!.Value)),
                    RepoRootsKey or PriorityReposKey or RepoTrackersKey or NoAutoMergeReposKey or SubTaskReposKey => new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]),
                    _ => JsonValue.Create(values[0]),
                };
            }
        }
        if (FleetRoles is { Count: > 0 })
        {
            var fleet = new JsonObject();
            foreach (var (key, range) in FleetRoles.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                fleet[key] = JsonValue.Create(range);
            }
            root[FleetRangesField] = fleet;
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
        ReviewLimitKey => ReviewLimit is { } reviews ? [reviews.ToString(CultureInfo.InvariantCulture)] : null,
        AcceptFleetFlightsKey => AcceptFleetFlights is { } accept ? [accept ? "on" : "off"] : null,
        AutopilotKey => Autopilot is { } autopilot ? [autopilot ? "on" : "off"] : null,
        ReviewDeepFirstKey => ReviewDeepFirst is { } deepFirst ? [deepFirst ? "on" : "off"] : null,
        UserSkillsKey => UserSkills is { } skills ? [skills ? "on" : "off"] : null,
        MergeBatchKey => MergeBatch is { } batch ? [batch ? "on" : "off"] : null,
        WorkerLadderKey => WorkerLadder is { } ladder ? [ladder ? "on" : "off"] : null,
        JobPlaneOutputKey => JobPlaneOutput is { } planeOutput ? [planeOutput ? "on" : "off"] : null,
        LanPortKey => LanPort is { } port ? [port.ToString(CultureInfo.InvariantCulture)] : null,
        ShortcutTokenKey => ShortcutToken is { } token ? [token] : null,
        MachineKey => Machine is { } machine ? [machine] : null,
        RepoRootsKey => RepoRoots,
        UpdatePolicyKey => UpdatePolicy is { } policy ? [policy.Name()] : null,
        UpdateChannelKey => UpdateChannel is { } channel ? [channel.Name()] : null,
        MaxJobsKey => MaxJobs is { } jobs ? [jobs.ToString(CultureInfo.InvariantCulture)] : null,
        MaxGoAroundsKey => MaxGoArounds is { } rounds ? [rounds.ToString(CultureInfo.InvariantCulture)] : null,
        ScratchDaysKey => ScratchDays is { } days ? [days.ToString(CultureInfo.InvariantCulture)] : null,
        DiskAlertGbKey => DiskAlertGb is { } alertGb ? [alertGb.ToString(CultureInfo.InvariantCulture)] : null,
        FlightCiBudgetKey => FlightCiBudget is { } ciBudget ? [ciBudget.ToString(CultureInfo.InvariantCulture)] : null,
        StaleFlightHoursKey or StalePrHoursKey or StaleRecheckHoursKey or StaleCardHoursKey or StaleHoldsKey or StaleUnreachableHoursKey => StaleValue(key) is { } stale ? [stale.ToString(CultureInfo.InvariantCulture)] : null,
        JobPriorityKey => JobPriority is { } priority ? [Pilot.Node.Flights.JobPriorities.Name(priority)] : null,
        JobMemoryKey => JobMemoryGigabytes is { } memory ? [memory.ToString(CultureInfo.InvariantCulture)] : null,
        MergeMethodKey => MergeMethod is { } merge ? [merge] : null,
        StartLimitKey => StartLimit is { } limit ? [limit.ToString(CultureInfo.InvariantCulture)] : null,
        PriorityStartLimitKey => PriorityStartLimit is { } high ? [high.ToString(CultureInfo.InvariantCulture)] : null,
        WeekLimitKey => WeekLimit is { } week ? [week.ToString(CultureInfo.InvariantCulture)] : null,
        PrWatchIdleKey => PrWatchIdle is { } idle ? [DurationText(idle)] : null,
        WeekPaceKey => WeekPace is { } pace ? [pace.ToString(CultureInfo.InvariantCulture)] : null,
        SpendAlertKey => SpendAlert is { } alert ? [alert.ToString(CultureInfo.InvariantCulture)] : null,
        WatchtowerCompactShareKey => WatchtowerCompactShare is { } wcs ? [wcs.ToString(CultureInfo.InvariantCulture)] : null,
        WatchtowerChatMessagesKey => WatchtowerChatMessages is { } wcm ? [wcm.ToString(CultureInfo.InvariantCulture)] : null,
        WatchtowerCompactIdleKey => WatchtowerCompactIdle is { } wci ? [wci.ToString(CultureInfo.InvariantCulture)] : null,
        WatchtowerCompactAboveKey => WatchtowerCompactAbove is { } wca ? [wca.ToString(CultureInfo.InvariantCulture)] : null,
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
            case ReviewLimitKey:
                if (clear)
                {
                    return (settings with { ReviewLimit = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var reviewLimit) && reviewLimit is >= 0 and <= 16
                    ? (settings with { ReviewLimit = reviewLimit }, null)
                    : (null, "review-limit takes the most reviews and fixers this node runs at once, 0 to 16 (0 for no limit)");
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
            case UserSkillsKey:
                if (clear)
                {
                    return (settings with { UserSkills = null }, null);
                }
                return OnOff(One()) is { } userSkills
                    ? (settings with { UserSkills = userSkills }, null)
                    : (null, "user-skills takes on or off");
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
            case JobPlaneOutputKey:
                if (clear)
                {
                    return (settings with { JobPlaneOutput = null }, null);
                }
                return OnOff(One()) is { } planeOut
                    ? (settings with { JobPlaneOutput = planeOut }, null)
                    : (null, "job-plane-output takes on or off");
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
            case MaxGoAroundsKey:
                if (clear)
                {
                    return (settings with { MaxGoArounds = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var goArounds) && goArounds is >= 1 and <= 50
                    ? (settings with { MaxGoArounds = goArounds }, null)
                    : (null, "max-go-arounds takes a number, 1 to 50");
            case ScratchDaysKey:
                if (clear)
                {
                    return (settings with { ScratchDays = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var scratchDays) && scratchDays is >= 1 and <= 365
                    ? (settings with { ScratchDays = scratchDays }, null)
                    : (null, "scratch-days takes a number, 1 to 365");
            case StaleFlightHoursKey:
            case StalePrHoursKey:
            case StaleRecheckHoursKey:
            case StaleCardHoursKey:
            case StaleHoldsKey:
            case StaleUnreachableHoursKey:
                if (clear)
                {
                    return (settings.WithStale(key, null), null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var staleBound) && staleBound is >= 1 and <= 1000
                    ? (settings.WithStale(key, staleBound), null)
                    : (null, $"{key} takes a number, 1 to 1000");
            case DiskAlertGbKey:
                if (clear)
                {
                    return (settings with { DiskAlertGb = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var diskAlertGb) && diskAlertGb is >= 1 and <= 10000
                    ? (settings with { DiskAlertGb = diskAlertGb }, null)
                    : (null, "disk-alert-gb takes a number, 1 to 10000");
            case FlightCiBudgetKey:
                if (clear)
                {
                    return (settings with { FlightCiBudget = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var ciBudget) && ciBudget is >= 1 and <= 1000
                    ? (settings with { FlightCiBudget = ciBudget }, null)
                    : (null, "flight-ci-budget takes a number of runner-hours, 1 to 1000");
            case JobPriorityKey:
                if (clear)
                {
                    return (settings with { JobPriority = null }, null);
                }
                return Pilot.Node.Flights.JobPriorities.Parse(One()) is { } jobPriority
                    ? (settings with { JobPriority = jobPriority }, null)
                    : (null, $"job-priority takes {string.Join(", ", Pilot.Node.Flights.JobPriorities.Names)}");
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
            case PrWatchIdleKey:
                if (clear)
                {
                    return (settings with { PrWatchIdle = null }, null);
                }
                return ParseDuration(One()) is { } idle && idle >= TimeSpan.FromMinutes(1) && idle <= TimeSpan.FromHours(24)
                    ? (settings with { PrWatchIdle = idle }, null)
                    : (null, "pr-watch-idle takes how long to wait between reads while no one looks at the Pull requests page, 1 minute to 24 hours, as 15m or 1h (a bare number is minutes)");
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
            case WatchtowerCompactShareKey:
                if (clear)
                {
                    return (settings with { WatchtowerCompactShare = null }, null);
                }
                return double.TryParse(One(), NumberStyles.Float, CultureInfo.InvariantCulture, out var compactShare) && compactShare is > 0 and <= 100
                    ? (settings with { WatchtowerCompactShare = compactShare }, null)
                    : (null, "watchtower-compact-share takes the share of its window in percent, above 0 and up to 100");
            case WatchtowerChatMessagesKey:
                if (clear)
                {
                    return (settings with { WatchtowerChatMessages = null }, null);
                }
                return int.TryParse(One(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatMessages) && chatMessages >= 0
                    ? (settings with { WatchtowerChatMessages = chatMessages }, null)
                    : (null, "watchtower-chat-messages takes a count of person messages, 0 or more (0 is off)");
            case WatchtowerCompactIdleKey:
                if (clear)
                {
                    return (settings with { WatchtowerCompactIdle = null }, null);
                }
                return int.TryParse(One(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var compactIdle) && compactIdle > 0
                    ? (settings with { WatchtowerCompactIdle = compactIdle }, null)
                    : (null, "watchtower-compact-idle takes the minutes an idle watchtower waits before it is compacted, 1 or more");
            case WatchtowerCompactAboveKey:
                if (clear)
                {
                    return (settings with { WatchtowerCompactAbove = null }, null);
                }
                return int.TryParse(One(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var compactAbove) && compactAbove >= 0
                    ? (settings with { WatchtowerCompactAbove = compactAbove }, null)
                    : (null, "watchtower-compact-above takes the context in thousands of tokens an idle watchtower must hold to be compacted, 0 or more");
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
                if (!RoleRange.TryParse(given, out var range, out var why))
                {
                    return (null, $"{role} {why}");
                }
                roles[role] = range!.ToString();
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

    /// <summary>A duration as <c>set node</c> spells it: <c>90s</c>, <c>15m</c>, <c>2h</c>, a bare number meaning minutes; null for anything else.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (text is null || text.Length == 0)
        {
            return null;
        }
        var unit = char.ToLowerInvariant(text[^1]);
        var number = char.IsAsciiDigit(unit) ? text : text[..^1];
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0 || double.IsInfinity(n))
        {
            return null;
        }
        return unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'h' => TimeSpan.FromHours(n),
            'm' or >= '0' and <= '9' => TimeSpan.FromMinutes(n),
            _ => null,
        };
    }

    /// <summary>A duration the way <see cref="ParseDuration"/> reads it: whole hours as <c>2h</c>, whole minutes as <c>15m</c>, else seconds.</summary>
    public static string DurationText(TimeSpan span) =>
        span.TotalSeconds % 3600 == 0 ? $"{(long)span.TotalHours}h"
        : span.TotalSeconds % 60 == 0 ? $"{(long)span.TotalMinutes}m"
        : $"{(long)span.TotalSeconds}s";

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
            JobPriority = saved.JobPriority is { } savedPriority ? savedPriority : options.JobPriority,
            Saved = saved,
            CommandLineKeys = given,
        };
    }
}
