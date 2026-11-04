using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// What the node's decision sources share (PIL583): which kind a choice's verb makes, which verbs are decisions when given
/// straight, and the one line a choice is recorded as.
/// </summary>
internal static class DecisionSources
{
    private static readonly string[] AcceptWords = ["accepted", "verified", "pushed-with-lease", "merged-with-origin"];

    /// <summary>The <see cref="DecisionKinds"/> a command makes, or null when its verb is no decision.</summary>
    public static string? KindOf(string verb, string[]? args)
    {
        var first = args is { Length: > 0 } ? args[0] : "";
        return verb switch
        {
            Verbs.Merge => DecisionKinds.Merge,
            Verbs.GoAround => DecisionKinds.GoAround,
            Verbs.CreateFlight when args?.Contains(Verbs.AnywayFlag, StringComparer.Ordinal) == true => DecisionKinds.CreateAnyway,
            Verbs.FinishLeg when AcceptWords.Contains(first, StringComparer.OrdinalIgnoreCase) => DecisionKinds.AcceptLeg,
            Verbs.Review when string.Equals(first, "pass", StringComparison.OrdinalIgnoreCase) => DecisionKinds.PassWithNotes,
            Verbs.Fold or Verbs.Separate or Verbs.ConnectOverlap => DecisionKinds.Overlap,
            Verbs.FlightPriority => DecisionKinds.Priority,
            Verbs.FixPr => DecisionKinds.Design,
            _ => null,
        };
    }

    /// <summary>Whether a command given straight is a decision to keep (a card's choice is kept by the card's answer, and the design answer by the review fix).</summary>
    public static bool IsDirect(string verb, string[]? args) =>
        KindOf(verb, args) is { } kind && kind != DecisionKinds.Design && kind != DecisionKinds.PassWithNotes;

    /// <summary>What a command given straight was, in a few words.</summary>
    public static string ChoiceOf(CommandRequest command) => command.Verb switch
    {
        Verbs.Merge => "Merge",
        Verbs.GoAround => "Go around",
        Verbs.CreateFlight => "Create it anyway",
        Verbs.FinishLeg => "Mark done anyway",
        Verbs.Fold => $"Fold into {command.Args.FirstOrDefault()}".Trim(),
        Verbs.Separate => "Plan on its own",
        Verbs.ConnectOverlap => $"Connect to {command.Args.FirstOrDefault()}".Trim(),
        Verbs.FlightPriority => $"Priority {command.Args.FirstOrDefault()}".Trim(),
        _ => command.Verb,
    };

    /// <summary>The question a command given straight answers.</summary>
    public static string QuestionOf(CommandRequest command) => command.Verb switch
    {
        Verbs.Merge => $"Merge {command.Target}?",
        Verbs.GoAround => $"Send {command.Target} round again?",
        Verbs.CreateFlight => $"Create a flight in {command.Target} although another node's label is on its work item?",
        Verbs.FinishLeg => $"Take {command.Target} as done as it stands?",
        Verbs.FlightPriority => $"What priority is {command.Target}?",
        _ => $"What of {command.Target}, overlapping another flight?",
    };

    /// <summary>
    /// Whether a command is a gate override (PIL702): leg finish accepted, create-flight --anyway, a review pass from anyone but
    /// the flight's reviewer (<paramref name="reviewer"/>, null when it has no review), and a merge as-is over a holding pattern.
    /// </summary>
    public static bool IsOverride(CommandRequest command, string? reviewer)
    {
        var args = command.Args ?? [];
        var first = args.Length > 0 ? args[0] : "";
        return command.Verb switch
        {
            Verbs.FinishLeg => string.Equals(first, "accepted", StringComparison.OrdinalIgnoreCase),
            Verbs.CreateFlight => args.Contains(Verbs.AnywayFlag, StringComparer.Ordinal),
            Verbs.Review => reviewer is not null && string.Equals(first, "pass", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command.Who, reviewer, StringComparison.Ordinal),
            Verbs.Merge => args.Length > 2 && args[2] == Flights.LegCommander.MergeAsIs,
            _ => false,
        };
    }

    /// <summary>
    /// The reason the sending session gave of its own: its Why, else, given straight, the words after the outcome of an accepted
    /// finish. A card choice's canned Args never count (<paramref name="fromCard"/>). Empty when none.
    /// </summary>
    public static string OwnReasonOf(CommandRequest command, bool fromCard)
    {
        if (command.Why?.Trim() is { Length: > 0 } why)
        {
            return why;
        }
        return !fromCard && command.Verb == Verbs.FinishLeg ? string.Join(' ', (command.Args ?? []).Skip(1)).Trim() : "";
    }

    /// <summary>The reason a command given straight gave: its Why, else its words (a go-around's are why it goes round).</summary>
    public static string ReasonOf(CommandRequest command)
    {
        if (OwnReasonOf(command, fromCard: false) is { Length: > 0 } own)
        {
            return own;
        }
        var words = command.Verb == Verbs.GoAround ? string.Join(' ', command.Args ?? []).Trim() : "";
        if (words.Length > 0)
        {
            return words;
        }
        return IsOverride(command, null) ? $"pressed by {command.Who}, no words given" : "no reason given";
    }
}
