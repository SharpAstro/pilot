using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// The model and effort each kind of session the node starts runs on (#114): the watchtower and workers on Sonnet, planners
/// and reviewers on Opus, unless the settings say otherwise. A leg's own <c>--model</c> or <c>--effort</c> still wins for
/// its worker. Read when a session starts, so <c>set node worker-model opus</c> applies to the next one, with no restart.
/// <c>none</c> passes no flag: the session takes Claude Code's own default.
/// </summary>
public sealed partial class RoleModels
{
    public const string Watchtower = "watchtower";
    public const string Worker = "worker";
    public const string Planner = "planner";
    public const string Reviewer = "reviewer";
    /// <summary>A deep review's own model and effort (#74), unset by default: it then runs on the reviewer's, a step higher in effort.</summary>
    public const string ReviewerDeep = "reviewer-deep";

    public const string NoneValue = "none";

    public static IReadOnlyList<string> Roles { get; } = [Watchtower, Worker, Planner, Reviewer, ReviewerDeep];

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

    public static string ModelKey(string role) => role + "-model";

    public static string EffortKey(string role) => role + "-effort";

    /// <summary>A role's model when the settings name none: Opus where judgement decides what follows, Sonnet elsewhere.</summary>
    public static string DefaultModel(string role) => role is Planner or Reviewer or ReviewerDeep ? "opus" : "sonnet";

    public void Apply(NodeSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
        }
    }

    /// <summary>The model a session of <paramref name="role"/> starts on, or null for Claude Code's own default.</summary>
    public string? Model(string role) => Pick(ModelKey(role), _defaults ? DefaultModel(role) : null);

    /// <summary>The effort a session of <paramref name="role"/> starts at, or null for Claude Code's own default.</summary>
    public string? Effort(string role) => Pick(EffortKey(role), null);

    private string? Pick(string key, string? fallback) => Raw(key) switch
    {
        null => fallback,
        var value => Named(value),
    };

    // The setting as saved, none included: a deep review tells "not set, follow the reviewer" from "set to none, pass no flag".
    private string? Raw(string key)
    {
        lock (_lock)
        {
            return _settings.Roles?.GetValueOrDefault(key);
        }
    }

    private static string? Named(string value) => value == NoneValue ? null : value;

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

    /// <summary>The model a reviewer of <paramref name="depth"/> starts on: the reviewer's own, and for a deep review the
    /// stronger <c>reviewer-deep-model</c> when the settings name one (<c>none</c> there passes no flag).</summary>
    public string? ReviewerModel(ReviewDepth depth) =>
        depth == ReviewDepth.Deep && Raw(ModelKey(ReviewerDeep)) is { } deep ? Named(deep) : Model(Reviewer);

    /// <summary>The effort a reviewer of <paramref name="depth"/> starts at: the reviewer's own for standard, a step below it for
    /// light (low when none is set), and for deep <c>reviewer-deep-effort</c>, else a step above it (high when none is set).
    /// A deep setting of <c>none</c> passes no flag, as it does for any role.</summary>
    public string? ReviewerEffort(ReviewDepth depth)
    {
        var own = Effort(Reviewer);
        return depth switch
        {
            ReviewDepth.Light => Step(own, -1, "low"),
            ReviewDepth.Deep => Raw(EffortKey(ReviewerDeep)) is { } deep ? Named(deep) : Step(own, 1, "high"),
            _ => own,
        };
    }

    public const string DefaultWorkerModelMax = "opus";
    public const string DefaultWorkerEffortMax = "max";

    /// <summary>The strongest model the worker ladder steps up to (<see cref="NodeSettings.WorkerModelMaxKey"/>).</summary>
    public string WorkerModelMax => Raw(NodeSettings.WorkerModelMaxKey) is { } v && Named(v) is { } model ? model : DefaultWorkerModelMax;

    /// <summary>The highest effort the worker ladder steps up to (<see cref="NodeSettings.WorkerEffortMaxKey"/>).</summary>
    public string WorkerEffortMax => Raw(NodeSettings.WorkerEffortMaxKey) is { } v && Named(v) is { } effort ? effort : DefaultWorkerEffortMax;

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
    /// leg's own (<paramref name="plannedModel"/>, <paramref name="plannedEffort"/>) or the worker role's, attempt 3 on opus at the
    /// same effort, attempt 4 and on opus at xhigh. Never past <see cref="WorkerModelMax"/> and <see cref="WorkerEffortMax"/>, and
    /// never below what the leg's plan names. A resumed or nudged session does not come here: it keeps the model it started with.
    /// </summary>
    public (string? Model, string? Effort) WorkerFor(string? plannedModel, string? plannedEffort, int attempt)
    {
        var model = plannedModel ?? Model(Worker);
        var effort = plannedEffort ?? Effort(Worker);
        if (!WorkerLadder || attempt < 3)
        {
            return (model, effort);
        }
        var max = WorkerModelMax;
        var step = ModelRank(max) < 0 || ModelRank(max) >= 2 ? "opus" : max;
        if (model is null || (ModelRank(model) >= 0 && ModelRank(step) > ModelRank(model)))
        {
            model = step;
        }
        if (attempt >= 4)
        {
            var efforts = Efforts.ToArray();
            var maxAt = Array.IndexOf(efforts, WorkerEffortMax);
            var target = Math.Min(Array.IndexOf(efforts, "xhigh"), maxAt < 0 ? efforts.Length - 1 : maxAt);
            if (Array.IndexOf(efforts, effort) < target)
            {
                effort = efforts[target];
            }
        }
        return (model, effort);
    }

    /// <summary>The redo choice for <paramref name="leg"/> (its next fresh worker is attempt go-arounds + 2), numbered as the redo it orders (go-arounds + 1) and naming that attempt's model once it is a second redo or later.</summary>
    public string RedoLabel(LegRow leg)
    {
        var (model, effort) = WorkerFor(leg.Model, leg.Effort, leg.GoArounds + 2);
        return Verbs.RedoLeg(leg.Name, leg.GoArounds + 1, model, effort);
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

    /// <summary>Whether <paramref name="value"/> names a model: an alias (sonnet, opus) or a full id (claude-opus-5-5[1m]).</summary>
    public static bool IsModel(string value) => ModelName().IsMatch(value);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]*(\[[a-z0-9]+\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelName();
}
