using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// The range of model and effort each kind of session the node starts runs on (#114, #599): the watchtower and workers
/// start on Sonnet, planners and reviewers on Opus, unless the settings say otherwise. A leg's own <c>--model</c> or
/// <c>--effort</c> still wins for its worker. Read when a session starts, so <c>set node worker-range opus..opus-xhigh</c>
/// applies to the next one, with no restart.
/// </summary>
public sealed partial class RoleModels
{
    public const string Watchtower = "watchtower";
    public const string Worker = "worker";
    public const string Planner = "planner";
    public const string Reviewer = "reviewer";
    public const string NoneValue = "none";

    public static IReadOnlyList<string> Roles { get; } = [Watchtower, Worker, Planner, Reviewer];

    public static IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Passes no model or effort for any role: what a test's commander and watchtower run with.</summary>
    public static RoleModels Unset { get; } = new(null, defaults: false);

    private readonly bool _defaults;
    private readonly Lock _lock = new();
    private NodeSettings _settings = new();

    public RoleModels(NodeSettings? settings, bool defaults = true)
    {
        _defaults = defaults;
        Apply(settings ?? new NodeSettings());
    }

    /// <summary>The setting that holds a role's range (<c>worker-range</c>: <c>sonnet-high..opus-xhigh</c>).</summary>
    public static string RangeKey(string role) => role + "-range";

    /// <summary>A role's built-in range: its default model at Claude Code's own effort, up to the max a ladder or a deep review steps to.</summary>
    public static RoleRange BuiltIn(string role) => role switch
    {
        Worker => new RoleRange(new ModelPair("sonnet", null), new ModelPair("opus", "xhigh")),
        Watchtower => new RoleRange(new ModelPair("sonnet", null), new ModelPair("sonnet", "high")),
        _ => new RoleRange(new ModelPair("opus", null), new ModelPair("opus", "high")),
    };

    /// <summary>Why a role's built-in range is what it is, for the setting's detail.</summary>
    public static string BuiltInWhy(string role) => role switch
    {
        Worker => "sonnet by default as most legs are routine; a redone leg climbs to opus at xhigh",
        Watchtower => "sonnet by default as it is the longest-lived session and the costliest per turn; it has no ladder",
        Planner => "opus by default as it decides everything after it; effort is left to Claude Code",
        _ => "opus by default as review is judgement; a deep review runs on the max, opus at high",
    };

    /// <summary>The role's range: this node's own, else the fleet's, else the built-in.</summary>
    public RoleRange Range(string role) => RangeWithSource(role).Range;

    /// <summary>The role's range and where it comes from (<see cref="SettingSources.ThisNode"/>, <see cref="SettingSources.Fleet"/> or <see cref="SettingSources.BuiltIn"/>).</summary>
    public (RoleRange Range, string Source) RangeWithSource(string role)
    {
        if (Raw(RangeKey(role)) is { } own && RoleRange.TryParse([own], out var mine, out _))
        {
            return (mine!, SettingSources.ThisNode);
        }
        if (FleetRaw(RangeKey(role)) is { } fleet && RoleRange.TryParse([fleet], out var shared, out _))
        {
            return (shared!, SettingSources.Fleet);
        }
        return (BuiltIn(role), SettingSources.BuiltIn);
    }

    /// <summary>The fleet's range for the role, when one was set; null when none was.</summary>
    public RoleRange? FleetRange(string role) =>
        FleetRaw(RangeKey(role)) is { } v && RoleRange.TryParse([v], out var range, out _) ? range : null;

    /// <summary>This node's effective range for every role, with its source: what the node publishes.</summary>
    public RoleRangeInfo[] Ranges() => [.. Roles.Select(r =>
    {
        var (range, source) = RangeWithSource(r);
        return new RoleRangeInfo(r, range.ToString(), source);
    })];

    private string? FleetRaw(string key)
    {
        lock (_lock)
        {
            return _settings.FleetRoles?.GetValueOrDefault(key);
        }
    }

    /// <summary>Raised after <see cref="Apply"/>, for what runs on a setting it must not wait to be asked again (the pull request watch's pace).</summary>
    public event Action? Applied;

    public void Apply(NodeSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
        }
        Applied?.Invoke();
    }

    /// <summary>How long the pull request watch waits between reads while no client shows the Pull requests page
    /// (<see cref="NodeSettings.PrWatchIdleKey"/>); <see cref="NodeSettings.DefaultPrWatchIdle"/> unless the settings say otherwise.</summary>
    public TimeSpan PrWatchIdle
    {
        get
        {
            lock (_lock)
            {
                return _settings.PrWatchIdle ?? NodeSettings.DefaultPrWatchIdle;
            }
        }
    }

    /// <summary>The model a session of <paramref name="role"/> starts on, or null for Claude Code's own default.</summary>
    public string? Model(string role) => _defaults ? Range(role).Default.Model : null;

    /// <summary>The effort a session of <paramref name="role"/> starts at, or null for Claude Code's own default.</summary>
    public string? Effort(string role) => _defaults ? Range(role).Default.Effort : null;

    // The setting as saved.
    private string? Raw(string key)
    {
        lock (_lock)
        {
            return _settings.Roles?.GetValueOrDefault(key);
        }
    }

    /// <summary>How a flight's PR is merged when the merge names no method (<see cref="NodeSettings.MergeMethodKey"/>):
    /// rebase unless the settings say otherwise; a test's commander keeps squash, as it always did.</summary>
    public string MergeMethod
    {
        get
        {
            lock (_lock)
            {
                return _settings.MergeMethod ?? (_defaults ? "rebase" : "squash");
            }
        }
    }

    /// <summary>Whether a flight goes on by itself once a leg is done (its next leg, or its PR and review): always on a
    /// node; a test's commander does not, as it never did.</summary>
    public bool Advances => _defaults;

    /// <summary>The start limit for a flight of <paramref name="repo"/>: <see cref="NodeSettings.PriorityStartLimit"/> for a priority repo
    /// (<see cref="NodeSettings.PriorityReposKey"/>), whose flights have runway as the window runs out, else <see cref="StartLimit"/>.</summary>
    public double StartLimitFor(string repo)
    {
        lock (_lock)
        {
            return IsPriority(repo)
                ? _settings.PriorityStartLimit ?? 98
                : _settings.StartLimit ?? 90;
        }
    }

    /// <summary>Whether <paramref name="repo"/> is a priority repo (<see cref="NodeSettings.PriorityReposKey"/>): its reviews are never lightened below standard.</summary>
    public bool IsPriority(string repo)
    {
        lock (_lock)
        {
            return _settings.PriorityRepos?.Contains(repo, StringComparer.OrdinalIgnoreCase) == true;
        }
    }

    /// <summary>The model a reviewer of <paramref name="depth"/> starts on: the reviewer's default, and for a deep review its max.</summary>
    public string? ReviewerModel(ReviewDepth depth) =>
        !_defaults ? null : depth == ReviewDepth.Deep ? Range(Reviewer).Max.Model : Model(Reviewer);

    /// <summary>Whether the first review of a large diff runs on the deep model (<see cref="NodeSettings.ReviewDeepFirstKey"/>); on unless the settings say off.</summary>
    public bool ReviewDeepFirst
    {
        get
        {
            lock (_lock)
            {
                return _settings.ReviewDeepFirst ?? true;
            }
        }
    }

    /// <summary>
    /// The model a reviewer starts on in <paramref name="round"/> (1 for the first review of a flight): a re-review runs on the leg
    /// model <paramref name="legModel"/> (the worker's, Sonnet by default), as it only reads what changed; the first review is
    /// <see cref="ReviewerModel"/>, its deep model only when the diff is deep-sized and <see cref="ReviewDeepFirst"/> is on.
    /// </summary>
    public string? ReviewerModelFor(ReviewDepth depth, int round, string? legModel) =>
        round > 1 ? legModel ?? Model(Worker)
        : depth == ReviewDepth.Deep && !ReviewDeepFirst ? Model(Reviewer)
        : ReviewerModel(depth);

    /// <summary>The effort a reviewer of <paramref name="depth"/> starts at: the reviewer's default for standard, a step below it for
    /// light (low when none is set), and for deep the reviewer's max. Never effort max.</summary>
    public string? ReviewerEffort(ReviewDepth depth)
    {
        if (!_defaults)
        {
            return null;
        }
        var range = Range(Reviewer);
        return depth switch
        {
            ReviewDepth.Light => Step(range.Default.Effort, -1, "low"),
            ReviewDepth.Deep => range.Max.Effort,
            _ => range.Default.Effort,
        };
    }

    /// <summary>Whether a leg's fresh worker steps up the ladder by attempt (<see cref="NodeSettings.WorkerLadderKey"/>); on unless set off.</summary>
    public bool WorkerLadder
    {
        get
        {
            lock (_lock)
            {
                return _settings.WorkerLadder ?? true;
            }
        }
    }

    /// <summary>The rank of a model by its alias or a full id containing the name (haiku 0, sonnet 1, opus 2); -1 for any other.</summary>
    public static int ModelRank(string? model) =>
        model is null ? -1
        : model.Contains("opus", StringComparison.OrdinalIgnoreCase) ? 2
        : model.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ? 1
        : model.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? 0
        : -1;

    /// <summary>
    /// The model and effort a leg's fresh worker of <paramref name="attempt"/> (go-arounds + 1) starts on: attempts 1 and 2 on the
    /// leg's own (<paramref name="plannedModel"/>, <paramref name="plannedEffort"/>) or the worker role's default, attempt 3 on the
    /// range's max model at the same effort, attempt 4 and on at xhigh. Never past the worker range's max (model or effort), never to
    /// effort max by a step, and never below what the leg's plan names. A step that lands on the max's model is held to the max's
    /// effort only when the plan names neither model nor effort; a plan that names the model keeps the default's effort (the plan wins). A resumed or nudged session does not come here.
    /// </summary>
    public (string? Model, string? Effort) WorkerFor(string? plannedModel, string? plannedEffort, int attempt)
    {
        var model = plannedModel ?? Model(Worker);
        var effort = plannedEffort ?? Effort(Worker);
        if (!WorkerLadder || attempt < 3)
        {
            return (model, effort);
        }
        var max = Range(Worker).Max;
        if (model is null || (ModelRank(model) >= 0 && ModelRank(max.Model) > ModelRank(model)))
        {
            model = max.Model;
        }
        if (attempt >= 4 && max.Effort is { } cap)
        {
            var efforts = RoleRange.Efforts;
            var target = Math.Min(Array.IndexOf(efforts, "xhigh"), Array.IndexOf(efforts, cap));
            if (effort is null || (Array.IndexOf(efforts, effort) is >= 0 and var at && at < target))
            {
                effort = efforts[target];
            }
        }
        if (plannedEffort is null && plannedModel is null && max.Effort is { } top && effort is not null && ModelRank(model) >= 0 && ModelRank(model) >= ModelRank(max.Model))
        {
            // a step that lands on the max's model never runs above the max's effort, whatever the default's was
            var efforts = RoleRange.Efforts;
            if (Array.IndexOf(efforts, effort) > Array.IndexOf(efforts, top))
            {
                effort = top;
            }
        }
        return (model, effort);
    }

    /// <summary>
    /// The choice that starts a fresh worker on <paramref name="leg"/>, naming why the last one is let go (<paramref name="why"/> completes
    /// "the last one ...": <c>went idle</c>, <c>never showed up</c>); its next worker is attempt go-arounds + 2, numbered as the redo it
    /// orders (go-arounds + 1) and naming that attempt's model once it is a second redo or later.
    /// </summary>
    public string RedoLabel(LegRow leg, string why)
    {
        var (model, effort) = WorkerFor(leg.Model, leg.Effort, leg.GoArounds + 2);
        return Verbs.FreshWorker(leg.Name, why, leg.GoArounds + 1, model, effort);
    }

    /// <summary>A send-back button for <paramref name="leg"/> in <paramref name="words"/>, naming the next worker's round and model as <see cref="RedoLabel"/> does.</summary>
    public string SendBackLabel(LegRow leg, string words)
    {
        var (model, effort) = WorkerFor(leg.Model, leg.Effort, leg.GoArounds + 2);
        return Verbs.SendBack(words, leg.GoArounds + 1, model, effort);
    }

    private static string Step(string? effort, int by, string whenUnset) =>
        Array.IndexOf(Efforts.ToArray(), effort) is var at and >= 0 ? Efforts[Math.Clamp(at + by, 0, Efforts.Count - 1)] : whenUnset;

    /// <summary>The share of the account's 5-hour window, in percent, past which the node starts nothing by itself
    /// (<see cref="NodeSettings.StartLimitKey"/>); 90 unless the settings say otherwise.</summary>
    public double StartLimit
    {
        get
        {
            lock (_lock)
            {
                return _settings.StartLimit ?? 90;
            }
        }
    }

    /// <summary>The tracker a bare number means in <paramref name="repo"/> (<see cref="NodeSettings.RepoTrackersKey"/>), in force at once.</summary>
    public TrackerKind TrackerOf(string repo)
    {
        lock (_lock)
        {
            return _settings.TrackerOf(repo);
        }
    }

    /// <summary>Whether the node never merges <paramref name="repo"/>'s PRs by itself (<see cref="NodeSettings.NoAutoMergeReposKey"/>), in force at once.</summary>
    public bool NoAutoMerge(string repo)
    {
        lock (_lock)
        {
            return _settings.NoAutoMergeRepos?.Contains(repo, StringComparer.OrdinalIgnoreCase) == true;
        }
    }

    /// <summary>Whether <paramref name="repo"/>'s flights keep their legs as sub-tasks of the work item (<see cref="NodeSettings.SubTaskReposKey"/>), in force at once.</summary>
    public bool SubTasks(string repo)
    {
        lock (_lock)
        {
            return _settings.SubTaskRepos?.Contains(repo, StringComparer.OrdinalIgnoreCase) == true;
        }
    }

    /// <summary>Whether the node takes no new flight (<see cref="NodeSettings.MaintenanceKey"/>): on while the setting says so, at once.</summary>
    public bool Maintenance
    {
        get
        {
            lock (_lock)
            {
                return _settings.Maintenance ?? false;
            }
        }
    }

    /// <summary>The Shortcut token the settings hold (<see cref="NodeSettings.ShortcutTokenKey"/>), in force at once; null when none is saved.</summary>
    public string? ShortcutToken
    {
        get
        {
            lock (_lock)
            {
                return _settings.ShortcutToken;
            }
        }
    }

    /// <summary>Whether ready merge queue entries are batched (<see cref="NodeSettings.MergeBatchKey"/>): in force at once, and off unless the person saved it on.</summary>
    public bool MergeBatch
    {
        get
        {
            lock (_lock)
            {
                return _settings.MergeBatch ?? false;
            }
        }
    }

    /// <summary>Whether the autopilot is on (<see cref="NodeSettings.AutopilotKey"/>): in force at once, and off unless the person saved it on.</summary>
    public bool Autopilot
    {
        get
        {
            lock (_lock)
            {
                return _settings.Autopilot ?? false;
            }
        }
    }

    /// <summary>Whether the node is willing to take a flight another node routes to it (<see cref="NodeSettings.AcceptFleetFlightsKey"/>); off unless set.</summary>
    public bool AcceptsFleetFlights
    {
        get
        {
            lock (_lock)
            {
                return _settings.AcceptFleetFlights ?? false;
            }
        }
    }

    /// <summary>The share of the account's 7-day window, in percent, past which a start rings once per window
    /// (<see cref="NodeSettings.WeekLimitKey"/>); 90 unless the settings say otherwise. It rings and holds nothing.</summary>
    public double WeekLimit
    {
        get
        {
            lock (_lock)
            {
                return _settings.WeekLimit ?? 90;
            }
        }
    }

    /// <summary>The week-pace margin in points that holds a start of <paramref name="repo"/>; null when the setting is off or the repo is
    /// a priority repo (<see cref="NodeSettings.WeekPaceKey"/>).</summary>
    public double? WeekPaceFor(string repo)
    {
        lock (_lock)
        {
            return IsPriority(repo) ? null : _settings.WeekPace;
        }
    }

    /// <summary>How many go-arounds a leg may have before the node and the watchtower send it round no more (<see cref="NodeSettings.MaxGoAroundsKey"/>); 5 by default.</summary>
    public int MaxGoArounds
    {
        get
        {
            lock (_lock)
            {
                return _settings.MaxGoArounds ?? NodeSettings.DefaultMaxGoArounds;
            }
        }
    }

    /// <summary>Days a job's scratch may sit before the disk sweep deletes it (<see cref="NodeSettings.ScratchDaysKey"/>); 2 by default.</summary>
    public int ScratchDays
    {
        get
        {
            lock (_lock)
            {
                return _settings.ScratchDays ?? NodeSettings.DefaultScratchDays;
            }
        }
    }

    /// <summary>The staleness bounds (#736), each its default unless the settings say.</summary>
    public Pilot.Node.Flights.StaleBounds Stale
    {
        get
        {
            lock (_lock)
            {
                return new Pilot.Node.Flights.StaleBounds
                {
                    FlightHours = _settings.StaleFlightHours ?? NodeSettings.DefaultStaleFlightHours,
                    PrHours = _settings.StalePrHours ?? NodeSettings.DefaultStalePrHours,
                    RecheckHours = _settings.StaleRecheckHours ?? NodeSettings.DefaultStaleRecheckHours,
                    CardHours = _settings.StaleCardHours ?? NodeSettings.DefaultStaleCardHours,
                    Holds = _settings.StaleHolds ?? NodeSettings.DefaultStaleHolds,
                };
            }
        }
    }

    /// <summary>Free gigabytes on the repos' drive below which the disk sweep rings (<see cref="NodeSettings.DiskAlertGbKey"/>); 20 by default.</summary>
    public int DiskAlertGb
    {
        get
        {
            lock (_lock)
            {
                return _settings.DiskAlertGb ?? NodeSettings.DefaultDiskAlertGb;
            }
        }
    }

    /// <summary>The share of the 7-day window, in percent, a leg's rounds may spend before the next one rings (<see cref="NodeSettings.SpendAlertKey"/>); 3 by default.</summary>
    public double SpendAlert
    {
        get
        {
            lock (_lock)
            {
                return _settings.SpendAlert ?? 3;
            }
        }
    }

    /// <summary>The share of its window, in percent, past which an idle live watchtower is compacted (<see cref="NodeSettings.WatchtowerCompactShareKey"/>); 60 by default.</summary>
    public double WatchtowerCompactShare
    {
        get
        {
            lock (_lock)
            {
                return _settings.WatchtowerCompactShare ?? 60;
            }
        }
    }

    /// <summary>The person messages in a watchtower chat after which it is compacted once idle (<see cref="NodeSettings.WatchtowerChatMessagesKey"/>); 40 by default, 0 is off.</summary>
    public int WatchtowerChatMessages
    {
        get
        {
            lock (_lock)
            {
                return _settings.WatchtowerChatMessages ?? 40;
            }
        }
    }

    /// <summary>The most Opus sessions the fleet runs at once (<see cref="NodeSettings.OpusCapKey"/>); 3 unless the settings say otherwise, 0 for no cap.</summary>
    public int OpusCap
    {
        get
        {
            lock (_lock)
            {
                return _settings.OpusCap ?? 3;
            }
        }
    }

    /// <summary>Whether only one flight per repo flies at a time (<see cref="NodeSettings.OneFlightPerRepoKey"/>); off unless set.</summary>
    public bool OneFlightPerRepo
    {
        get
        {
            lock (_lock)
            {
                return _settings.OneFlightPerRepo ?? false;
            }
        }
    }
}
