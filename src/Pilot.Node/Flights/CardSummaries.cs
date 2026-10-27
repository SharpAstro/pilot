using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The plain sentence every card carries (<see cref="CardRow.Summary"/>): what happened and what the choices do, kept apart from
/// the raw detail. A raiser that wrote one passes it; any other card gets one here from its kind and subject, and a kind this
/// table does not know gets a generic line built from the title and the choices. Text that looks like a log is never one.
/// </summary>
public static partial class CardSummaries
{
    private const int MaxLength = 240;

    /// <summary>Whether <paramref name="text"/> can stand as a summary: short, at most two lines, and nothing like a log.</summary>
    public static bool Usable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return false;
        }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length is > 0 and <= 2 && !LooksLikeLog().IsMatch(text);
    }

    /// <summary>The summary to keep: the raiser's when it is usable and not just the detail, else the table's.</summary>
    public static string Resolve(string? given, string? detail, string kind, string subject, string title, IReadOnlyList<CardChoice> choices) =>
        Usable(given) && !string.Equals(given!.Trim(), detail?.Trim(), StringComparison.Ordinal) ? given!.Trim() : For(kind, subject, title, choices);

    /// <summary>The sentence for a card of <paramref name="kind"/> about <paramref name="subject"/>.</summary>
    public static string For(string kind, string subject, string title, IReadOnlyList<CardChoice> choices)
    {
        var what = Name(subject);
        return kind switch
        {
            LegCommander.ReviewChanges => $"The review of {what} asked for changes. Redo leg sends a fresh worker to make them; Ready anyway lands it as it is.",
            LegCommander.PrReady => $"{what} is reviewed and its PR is ready. Merge lands it; dismissing leaves the PR open.",
            LegCommander.MergeFailed => $"The merge of {what} did not go through. Redo leg sends a fresh worker to fix it; Merge tries once more.",
            "leg-failed" => $"The worker of {what} gave up. Redo leg sends a fresh worker; Mark done anyway accepts the leg as it is.",
            "pr-checks-failed" => $"The PR of {what} fails its checks. The node could not send the last leg back; redo it from the flight page or with pilot leg go-around, or fix them by hand, then dismiss this.",
            "pr-conflicts" => $"The PR of {what} conflicts with its base. The node could not rebase it or send the last leg back; redo the last leg from the flight page or with pilot leg go-around, or resolve it by hand, then dismiss this.",
            LegWatch.Stopped => $"The worker of {what} hit the usage limit. It resumes when the window resets; nothing to do.",
            LegWatch.Idle or LegWatch.Waiting => $"The worker of {what} stopped with its leg unfinished. Nudge tells it to carry on; Redo leg starts a fresh one.",
            LegWatch.Failed or LegWatch.Missing => $"The worker of {what} is gone. Resume brings it back; Redo leg starts a fresh one.",
            LegWatch.OverBudget => $"The worker of {what} is past its budget but keeps working. Extend gives it more; Redo leg starts a fresh worker.",
            LegWatch.ReviewStalled => $"The review of {what} stalled. Retry it, or dismiss this once it moves.",
            LegWatch.ValidationStalled => $"The validation of {what} stalled. Resume it, or Redo leg to start over.",
            LegCommander.NoGoKind => $"The worker of {what} reports the tree is not as its brief assumes. Redo leg sends a fresh worker once the brief is fixed.",
            LegCommander.WaitLateKind => $"{what} waits on something that has not come in time. Look at what it waits for, or Redo leg.",
            LegCommander.PlannedKind => $"{what} has a plan of legs. Start it to fly them, or change the plan first.",
            "landed" => $"{what} landed: its PR is merged and its legs are done. Nothing to do; dismiss it when read.",
            "not-landed" => $"The PR of {what} merged on GitHub, but the node could not land the flight. Land it by hand, or dismiss it.",
            CardKinds.SessionWaiting => "A session waits on you for an answer. Open it and reply; it closes by itself once the session moves on.",
            CardKinds.AssistantAsk => "An assistant asks to run a command only you may run. Allow runs it as you; Deny refuses it.",
            _ => Fallback(title, choices),
        };
    }

    private static string Fallback(string title, IReadOnlyList<CardChoice> choices)
    {
        var line = title.Trim().TrimEnd('.', ':', ';');
        var labels = choices.Select(c => c.Label).Where(l => l.Length > 0).ToArray();
        var choose = labels.Length == 0 ? "Dismiss it once read" : $"Choose {string.Join(" or ", labels)}, or dismiss it";
        return $"{line}. {choose}.";
    }

    /// <summary>The thing a subject names, as a person says it: <c>PIL355/card-summary</c> for <c>flight/PIL355/card-summary</c>.</summary>
    private static string Name(string subject)
    {
        var parts = subject.Split('/');
        return parts.Length >= 2 && parts[0] == "flight" ? string.Join('/', parts.Skip(1)) : subject;
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}|\b\d{1,2}:\d{2}:\d{2}\b|^\s+at\s+\S+|\b[A-Za-z]:\\|(?:/[\w.-]+){3,}|\\[\w.-]+\\[\w.-]+", RegexOptions.Multiline)]
    private static partial Regex LooksLikeLog();
}
