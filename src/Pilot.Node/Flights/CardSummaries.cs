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
    // Well above the longest sentence a raiser writes (the pr-conflicts one, about 200 with a long code), so none is thrown away;
    // text taken from a detail is far longer than a sentence and still fails.
    private const int MaxLength = 600;

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
            LegCommander.ReviewChanges => $"The review of {what} found things to fix. Fix the review's findings has a worker fix them on the leg's work as it stands, nothing thrown away; Land it with the review's findings unfixed takes it as it is.",
            LegCommander.PrReady => $"{what} is reviewed and its PR is ready. Merge lands it; dismissing leaves the PR open.",
            LegCommander.MergeFailed => $"The merge of {what} did not go through. Start a fresh worker on its last leg sends one to fix it; Merge its PR again as it is tries once more.",
            "leg-failed" => $"The worker of {what} stopped. Send it round again starts a fresh worker told why; Accept it as it stands keeps the failure in its report and the flight goes on.",
            LegCommander.PushBlockedKind => $"{what} is done and its check passes, but its branch was rebased off the pushed one. Push the rebased branch with lease overwrites origin's only if it has not moved; Merge origin's branch into the plane keeps its commits and pushes plain.",
            "pr-checks-failed" => $"The PR of {what} fails its checks and the node could not send the last leg back to fix them. The first choice starts a fresh worker on that leg, told which checks failed and to push a fix; or fix them by hand, then dismiss this.",
            "pr-conflicts" => $"The PR of {what} conflicts with its base and the node could not merge the base in by itself. The watchtower resolves it in the plane (merge, check, plain push); Resolve the conflict has a worker on the last leg do it, for real work only.",
            LegWatch.Stopped => $"The worker of {what} was stopped by the API and the node cannot resume it by itself. Tell it to carry on, or start a fresh worker on the leg.",
            LegWatch.LimitWait => $"The worker of {what} hit the usage limit. The node resumes it by itself after the window resets; nothing to do.",
            LegWatch.Idle => $"The worker of {what} has been idle with its leg unfinished and the node's nudges did not help. Nudge it to finish or say why not, or start a fresh worker on the leg.",
            LegWatch.Waiting => $"The worker of {what} waits on you for an answer. Answer it in its own session, or start a fresh worker on the leg.",
            LegWatch.Failed or LegWatch.Missing => $"The worker of {what} is gone. Tell it to carry on if it is still there, or start a fresh worker on the leg.",
            LegWatch.OverBudget => $"The worker of {what} is past its budget but keeps working. Let it spend more, or start a fresh worker on the leg.",
            LegWatch.ReviewStalled => $"The reviewer of {what} stalled twice. Start a fresh reviewer, or dismiss this once it moves.",
            LegWatch.ValidationStalled => $"The validator of {what} stalled. Start the leg without its validation, or drop the leg.",
            LegCommander.NoGoKind => $"The validator of {what} reports the tree is not as its brief assumes. Start the leg as the tree is, drop it, or plan the flight again.",
            LegCommander.WaitLateKind => $"{what} waits on a file that has not come in time. Look at what it waits for, or start a fresh worker on the leg.",
            LegCommander.NextStepKind => $"The node tried {what}'s next step and was refused. Do the step now to ask it again.",
            LegCommander.StartFailedKind => $"A session of {what} did not start and the node does not retry it by itself. Start it again from the card.",
            LegCommander.OverlapKind => $"{what} overlaps another flight's files. Fold it in, connect it, or plan both in parallel.",
            StopGate.Kind => $"A session of {what} stopped with files not committed after being told once. Tell it again to commit or drop them.",
            LegCommander.PlannedKind => $"{what} has a plan of legs. Start it to fly them, or change the plan first.",
            "landed" => $"{what} landed: its PR is merged and its legs are done. Nothing to do; dismiss it when read.",
            "not-landed" => $"The PR of {what} merged on GitHub, but landing the flight was refused (the title says why). The branch is in the base; land the flight by hand, then dismiss this.",
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
