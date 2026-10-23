using System.Text.RegularExpressions;

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

    public static string ModelKey(string role) => role + "-model";

    public static string EffortKey(string role) => role + "-effort";

    /// <summary>A role's model when the settings name none: Opus where judgement decides what follows, Sonnet elsewhere.</summary>
    public static string DefaultModel(string role) => role is Planner or Reviewer ? "opus" : "sonnet";

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

    private string? Pick(string key, string? fallback)
    {
        string? value;
        lock (_lock)
        {
            value = _settings.Roles?.GetValueOrDefault(key);
        }
        return value switch
        {
            null => fallback,
            NoneValue => null,
            _ => value,
        };
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
            return _settings.PriorityRepos?.Contains(repo, StringComparer.OrdinalIgnoreCase) == true
                ? _settings.PriorityStartLimit ?? 98
                : _settings.StartLimit ?? 90;
        }
    }

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

    /// <summary>Whether <paramref name="value"/> names a model: an alias (sonnet, opus) or a full id (claude-opus-5-5[1m]).</summary>
    public static bool IsModel(string value) => ModelName().IsMatch(value);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]*(\[[a-z0-9]+\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelName();
}
