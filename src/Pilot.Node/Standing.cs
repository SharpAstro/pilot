using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>Who a command is from, as the node sorts them (#208).</summary>
public enum StandingKind
{
    /// <summary>The person: a GUI click, or the <c>pilot</c> CLI run by a human (no <c>CLAUDECODE</c> in its environment). May do everything.</summary>
    Person,

    /// <summary>
    /// An assistant: a Claude session the person talks to (a terminal session, the GUI chat), known by its command being agent-run
    /// and from no session the node keeps for a flight. May do everything asked in words; your-only things by the autopilot rule.
    /// </summary>
    Assistant,

    /// <summary>The lookout: the watchtower on its own. Watches, rings, nudges and answers; nothing your-only.</summary>
    Lookout,

    /// <summary>A flight's session (planner, worker, validator, reviewer), or a PR review's: its own flight's legs, checks, artifacts and reviews, nothing outside it.</summary>
    FlightSession,

    /// <summary>Another node, under a grant: as before the standings, and never the settings, setup or grants of this machine.</summary>
    Grant,
}

/// <summary>
/// Who sends a command: the one place the node classifies a sender, from <see cref="CommandRequest.Who"/>, the
/// <see cref="CommandRequest.Agent"/> flag, the session rows and the flights' own workers, validators and reviewers. A
/// command from no known session that an agent ran is an assistant's; any other is the person's, which includes the node's
/// own steps (<c>Who = "node"</c>), since the node acts for the person.
/// </summary>
/// <param name="Flight">The flight a <see cref="StandingKind.FlightSession"/> flies; null for any other standing, and for a PR review's reviewer.</param>
/// <param name="Pr">The PR (as a <c>pr/</c> target) a PR review's reviewer reviews; null otherwise.</param>
public sealed record Standing(StandingKind Kind, string? Flight = null, string? Pr = null)
{
    public static readonly Standing Person = new(StandingKind.Person);

    /// <summary>
    /// The verbs that are the person's alone: merge and land, the node's settings and setup, a grant's answer and
    /// revocation, an update, and a watchtower restart. (<c>connect</c> is a flight's dependency, so not here; a card's
    /// dismissal is: see <see cref="YourOnly"/>.)
    /// </summary>
    public static readonly IReadOnlySet<string> YourOnlyVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        Verbs.Merge, Verbs.Land, Verbs.Set, Verbs.Setup, Verbs.AnswerGrant, Verbs.RevokeGrant, Verbs.Update, Verbs.RestartWatchtower,
    };

    /// <summary>
    /// What the lookout may not do beyond <see cref="YourOnlyVerbs"/>: a review's verdict or its posting to GitHub in the
    /// person's name, syncing the repos, an issue made in their name, and the answer to an overlap (fold retires a flight the
    /// way a landing does, separate restarts a planner on the person's answer).
    /// </summary>
    private static readonly IReadOnlySet<string> LookoutNever = new HashSet<string>(StringComparer.Ordinal)
    {
        Verbs.Review, Verbs.PostReview, Verbs.SyncRepos, Verbs.CreateIssue, Verbs.Fold, Verbs.Separate,
    };

    /// <summary>What a flight's session may not do beyond <see cref="YourOnlyVerbs"/>: the person's answers to an overlap and a review posted in their name.</summary>
    private static readonly IReadOnlySet<string> FlightSessionNever = new HashSet<string>(StringComparer.Ordinal)
    {
        Verbs.PostReview, Verbs.Fold, Verbs.Separate,
    };

    /// <summary>Sorts <paramref name="command"/>'s sender; <paramref name="lookout"/> is the watchtower's session name.</summary>
    public static Standing Of(CommandRequest command, NodeState state, string lookout)
    {
        if (command.Grant is not null)
        {
            return new Standing(StandingKind.Grant);
        }
        if (string.Equals(command.Who, lookout, StringComparison.Ordinal))
        {
            return new Standing(StandingKind.Lookout);
        }
        var who = command.Who;
        if (state.Legs().FirstOrDefault(l => string.Equals(l.Worker, who, StringComparison.Ordinal) || string.Equals(l.Validator, who, StringComparison.Ordinal)) is { } leg)
        {
            return new Standing(StandingKind.FlightSession, leg.Flight);
        }
        if (state.Flights().FirstOrDefault(f => f.Review is { } review && string.Equals(review.Reviewer, who, StringComparison.Ordinal)) is { } reviewed)
        {
            return new Standing(StandingKind.FlightSession, reviewed.Code);
        }
        if (state.PrReviews().FirstOrDefault(r => string.Equals(r.Reviewer, who, StringComparison.Ordinal)) is { } prReview)
        {
            return new Standing(StandingKind.FlightSession, Pr: Verbs.PrTarget(prReview.Repo, prReview.Number));
        }
        // A planner has no row of its own on a flight, only its session: the rows carry the role and the flight.
        if (state.Sessions().FirstOrDefault(s => s.Role is SessionRole.Planner or SessionRole.Worker or SessionRole.Reviewer && s.Flight is not null && string.Equals(s.Name, who, StringComparison.Ordinal)) is { } session)
        {
            return new Standing(StandingKind.FlightSession, session.Flight);
        }
        return command.Agent ? new Standing(StandingKind.Assistant) : Person;
    }

    /// <summary>
    /// Whether <paramref name="command"/> is the person's alone: a <see cref="YourOnlyVerbs"/> verb, or the dismissal of a
    /// card in the ring bin, which is the person's to close. (A card's answer is its chosen button's command, so is your-only
    /// exactly when that is.)
    /// </summary>
    public static bool YourOnly(CommandRequest command, NodeState state) =>
        YourOnlyVerbs.Contains(command.Verb) || command.Verb == Verbs.Dismiss && IsRing(command, state);

    private static bool IsRing(CommandRequest command, NodeState state) =>
        command.Target.StartsWith(Verbs.CardTargetPrefix, StringComparison.Ordinal)
        && state.Cards().FirstOrDefault(c => c.Id == command.Target[Verbs.CardTargetPrefix.Length..]) is { State: CardState.Open, Bin: CardBin.Ring };

    /// <summary>Why this standing may not send <paramref name="command"/>; null when it may, as far as who it is goes.</summary>
    public string? Refusal(CommandRequest command, NodeState state)
    {
        var yourOnly = YourOnly(command, state);
        // Autopilot is what lets an assistant do your-only things: only the person may change it, whoever asks and however.
        if (command.Verb == Verbs.Set && Kind != StandingKind.Person && command.Args is [{ } key, ..] && string.Equals(key.Trim(), NodeSettings.AutopilotKey, StringComparison.OrdinalIgnoreCase))
        {
            return "autopilot is the person's to change: set it from the GUI, or from a terminal you run yourself";
        }
        switch (Kind)
        {
            case StandingKind.Lookout when yourOnly || LookoutNever.Contains(command.Verb):
                return $"{command.Verb} is the controller's, not the watchtower's: leave its card open for them";
            case StandingKind.FlightSession when yourOnly || FlightSessionNever.Contains(command.Verb):
                return $"{command.Verb} is the controller's, not a flight session's: leave it to them";
            case StandingKind.FlightSession when OutsideItsFlight(command.Target):
                return Flight is { } code
                    ? $"{command.Target} is not {code}'s: a flight's session acts on its own flight only"
                    : $"{command.Target} is not the PR this review is of ({Pr}): a review acts on its own PR only";
            default:
                // An assistant's your-only command runs as it did before the autopilot rule; the confirm card (autopilot
                // off) and the direct run it records (autopilot on) are #208's next leg, which goes here.
                return null;
        }
    }

    /// <summary>Whether <paramref name="target"/> is a flight or a PR other than the one this session is for.</summary>
    private bool OutsideItsFlight(string target)
    {
        if (target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            var code = target[Verbs.FlightTargetPrefix.Length..];
            code = code[..(code.IndexOf('/') is var slash and >= 0 ? slash : code.Length)];
            return !string.Equals(code, Flight, StringComparison.OrdinalIgnoreCase);
        }
        return target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal) && !string.Equals(target, Pr, StringComparison.OrdinalIgnoreCase);
    }
}
