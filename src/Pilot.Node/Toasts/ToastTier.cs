namespace Pilot.Node.Toasts;

/// <summary>
/// How loudly a Ring card's desktop toast shows (#1356): <see cref="Urgent"/> stays until dismissed (<c>scenario="urgent"</c>), <see cref="Normal"/>
/// shows and goes to the notification centre. An enum-like class so the one table sits on the type: <see cref="Of"/>.
/// Every Ring kind is listed, the normal ones too, so a new Ring kind must be decided here: <see cref="Of"/> says null for a kind not
/// listed, and <c>ToastTierTests</c> fails for a Ring kind of <c>CardKindCatalog</c> without a tier.
/// </summary>
public sealed class ToastTier
{
    private ToastTier(string name) => Name = name;

    /// <summary>Something burns now, work is about to be lost, or pilot can no longer watch itself.</summary>
    public static ToastTier Urgent { get; } = new("Urgent");

    /// <summary>A decision or an answer that waits for the person.</summary>
    public static ToastTier Normal { get; } = new("Normal");

    /// <summary>The tier's name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => Name;

    private static readonly string[] UrgentKinds =
    [
        // burning the machine or money now
        "busy-process", "poller-caller", "over-budget", "stop-unconfirmed", "job-bench-busy",
        // work about to be lost
        "stopped-uncommitted", "closed-with-work", "push-blocked", "move-failed",
        // pilot can no longer watch itself
        "watchtower-down", "watchtower-stopped", "store", "worker-missing",
    ];

    private static readonly string[] NormalKinds =
    [
        "worker-stopped", "merged-hold", "worker-idle", "worker-waiting", "worker-failed", "worker-never-turned", "review-stalled",
        "validation-stalled", "plan-stalled", "session-waiting", "leg-wait-late", "leg-failed", "leg-no-go", "start-failed", "next-step",
        "overlap", "pilot-md-too-long", "package-late", "go-around-cap", "conflict-decision", "holding-timeout", "pr-ready-restore-failed",
        "review-notes-cap", "notes-unmade", "review-loop", "stray-session", "review-changes", "drop-unexplained", "pr-ready",
        "pr-checks-failed", "not-landed", "failure-alert", "autopilot-merge-refused", "pr-review-design", "pr-review-no-fixer",
        "pr-fix-capped", "watchtower-refused", "investigator-ring", "investigator-bound", "grant-asked", "update",
        "job-told-unanswered", "job-long", "job-untold",
    ];

    private static readonly Dictionary<string, ToastTier> Table = Build();

    private static Dictionary<string, ToastTier> Build()
    {
        var table = new Dictionary<string, ToastTier>(StringComparer.Ordinal);
        foreach (var kind in NormalKinds) table.Add(kind, Normal);
        foreach (var kind in UrgentKinds) table.Add(kind, Urgent);
        return table;
    }

    /// <summary>The tier of the card kind <paramref name="kind"/>, or null for a kind the table does not list.</summary>
    public static ToastTier? Of(string kind) => Table.TryGetValue(kind, out var tier) ? tier : null;
}
