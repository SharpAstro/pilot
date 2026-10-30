using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

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
    /// revocation, an update, a restart of the node, and a watchtower restart. (<c>connect</c> is a flight's dependency, so not here; a card's
    /// dismissal is: see <see cref="YourOnly"/>.)
    /// </summary>
    public static readonly IReadOnlySet<string> YourOnlyVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        Verbs.Merge, Verbs.RerunChecks, Verbs.MoveFlight, Verbs.Land, Verbs.Set, Verbs.Setup, Verbs.AnswerGrant, Verbs.RevokeGrant, Verbs.Update, Verbs.RestartNode, Verbs.RestartWatchtower,
    };

    /// <summary>
    /// What the lookout does as the person's assistant when the autopilot is on (see NodeCommands.RunAsync): a flight's merge,
    /// its landing, a rerun of its checks, a card's answer or dismissal, and a fixer on a PR whose review asked for changes, within
    /// the guards the node's own fixer keeps (PrReviews.FixAsync): no fourth round, no design question, and no first fixer on another
    /// author's PR, whose Fix it stays the person's. The node's settings, setup, grants and updates stay the person's.
    /// </summary>
    /// <remarks>
    /// <see cref="Verbs.FixPr"/> was the lookout's never: told overnight to get every PR with review findings fixed, the watchtower
    /// answered the Retry of #467's fixer that could not start (c2737) and was refused, and the PRs outside a flight waited on the
    /// person (DE1, 2026-10-29).
    /// </remarks>
    public static readonly IReadOnlySet<string> LookoutOnAutopilot = new HashSet<string>(StringComparer.Ordinal)
    {
        Verbs.Merge, Verbs.Land, Verbs.RerunChecks, Verbs.Answer, Verbs.Dismiss, Verbs.FixPr,
    };

    /// <summary>
    /// What the lookout may not do beyond <see cref="YourOnlyVerbs"/>: a review's verdict or its posting to GitHub in the
    /// person's name, syncing the repos, an issue made in their name, and the answer to an overlap (fold retires a flight the
    /// way a landing does, separate restarts a planner on the person's answer).
    /// </summary>
    private static readonly IReadOnlySet<string> LookoutNever = new HashSet<string>(StringComparer.Ordinal)
    {
        Verbs.Review, Verbs.PostReview, Verbs.FixPr, Verbs.SyncRepos, Verbs.CreateIssue, Verbs.Fold, Verbs.Separate, Verbs.ConnectOverlap, Verbs.PlanIdea, Verbs.DropIdea, Verbs.EditIdea, Verbs.TidyIdea, Verbs.ProposeMilestone, Verbs.SaveTidyIdea, Verbs.FoldIdea,
    };

    /// <summary>
    /// What a flight's session may not do beyond <see cref="YourOnlyVerbs"/>: the person's answers to an overlap, a review
    /// posted in their name, and what reaches outside its flight whatever the target (an issue, a new flight, the repos' sync).
    /// </summary>
    private static readonly IReadOnlySet<string> FlightSessionNever = new HashSet<string>(StringComparer.Ordinal)
    {
        Verbs.PostReview, Verbs.FixPr, Verbs.Fold, Verbs.Separate, Verbs.ConnectOverlap, Verbs.CreateIssue, Verbs.CreateFlight, Verbs.SyncRepos, Verbs.PlanIdea, Verbs.DropIdea, Verbs.EditIdea, Verbs.TidyIdea, Verbs.ProposeMilestone, Verbs.SaveTidyIdea, Verbs.FoldIdea,
    };

    /// <summary>
    /// Sorts <paramref name="command"/>'s sender; <paramref name="lookout"/> is the watchtower's session name. The watchtower
    /// is the lookout on its own and an assistant while it acts on a message the person sent it (<paramref name="lookoutForPerson"/>,
    /// from <see cref="Flights.Watchtower.ActingForPerson"/>): their words in its chat are theirs to have done, by the autopilot rule.
    /// </summary>
    public static Standing Of(CommandRequest command, NodeState state, string lookout, bool lookoutForPerson = false)
    {
        if (command.Grant is not null)
        {
            return new Standing(StandingKind.Grant);
        }
        if (string.Equals(command.Who, lookout, StringComparison.Ordinal))
        {
            return new Standing(lookoutForPerson ? StandingKind.Assistant : StandingKind.Lookout);
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
        // A session of a new flight is not polled yet, so no row: its name still says whose it is (<machine>-<flight>-plan, -review or -<leg>),
        // and one that names a flight is that flight's, not an assistant's, whatever autopilot says.
        if (command.Agent && SessionRoles.Resolve(who, background: false) is { Role: SessionRole.Planner or SessionRole.Worker or SessionRole.Reviewer, Flight: { } named })
        {
            return new Standing(StandingKind.FlightSession, named);
        }
        return command.Agent ? new Standing(StandingKind.Assistant) : Person;
    }

    /// <summary>
    /// Whether <paramref name="command"/> is the person's alone: a <see cref="YourOnlyVerbs"/> verb, or the dismissal or the
    /// answer of an open card in the ring bin, which is the person's to close. (The answer to any other card is its chosen
    /// button's command, so is your-only exactly when that is.)
    /// </summary>
    public static bool YourOnly(CommandRequest command, NodeState state) =>
        YourOnlyVerbs.Contains(command.Verb) && !OnlyReads(command) || command.Verb is Verbs.Dismiss or Verbs.Answer && IsRing(command, state) || IsAsk(command, state);

    /// <summary>
    /// Whether <paramref name="command"/> only reads, though its verb is one of <see cref="YourOnlyVerbs"/>: <c>update --check</c> says
    /// whether a release is out and installs nothing (as the updater reads it, <c>check</c> anywhere in the arguments), so anyone may ask.
    /// </summary>
    private static bool OnlyReads(CommandRequest command) =>
        command.Verb == Verbs.Update && command.Args.Contains(UpdateArgs.Check);

    private static bool IsRing(CommandRequest command, NodeState state) =>
        CardOf(command, state) is { State: CardState.Open, Bin: CardBin.Ring };

    /// <summary>
    /// Whether <paramref name="command"/> answers (<c>answer</c>, either choice) or dismisses an assistant's open ask
    /// (<see cref="CardKinds.AssistantAsk"/>): the person's alone, whatever the autopilot says, as an assistant that could press
    /// its own Allow would have no ask at all.
    /// </summary>
    public static bool IsAsk(CommandRequest command, NodeState state) =>
        command.Verb is Verbs.Answer or Verbs.Dismiss && CardOf(command, state) is { State: CardState.Open, Kind: CardKinds.AssistantAsk };

    private static CardRow? CardOf(CommandRequest command, NodeState state) =>
        command.Target.StartsWith(Verbs.CardTargetPrefix, StringComparison.Ordinal)
            ? state.Cards().FirstOrDefault(c => c.Id == command.Target[Verbs.CardTargetPrefix.Length..])
            : null;

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
            // The lookout may answer a ring card (a stall's Resume, Nudge or Go around): the chosen button's command is run again
            // under its own standing, so one that is the person's (Merge, Fold, Post) is refused there and the card stays open.
            case StandingKind.Lookout when yourOnly && !AnswersRing(command, state) || LookoutNever.Contains(command.Verb):
                return $"{command.Verb} is the controller's, not the watchtower's: leave its card open for them";
            case StandingKind.FlightSession when yourOnly || FlightSessionNever.Contains(command.Verb):
                return $"{command.Verb} is the controller's, not a flight session's: leave it to them";
            case StandingKind.FlightSession when OutsideItsFlight(command.Target):
                return Flight is { } code
                    ? $"{command.Target} is not {code}'s: a flight's session acts on its own flight only"
                    : $"{command.Target} is not the PR this review is of ({Pr}): a review acts on its own PR only";
            case StandingKind.FlightSession when command.Verb is Verbs.Stop or Verbs.Compact && !OwnsSession(command, state):
                return $"{command.Target} is not {OwnerName()} session: a flight's session stops and compacts its own flight's only";
            case StandingKind.FlightSession when command.Verb is Verbs.CancelJob or Verbs.PauseJob or Verbs.ResumeJob && !OwnsJob(command, state):
                return $"{command.Target} is not {OwnerName()} job: a flight's session cancels, pauses and resumes its own flight's only";
            case StandingKind.Assistant when IsAsk(command, state):
                return "answering an assistant's ask is the person's: Allow and Deny are theirs to press, on the card";
            default:
                // An assistant's other your-only commands are not refused here but sorted by the autopilot rule in
                // NodeCommands.RunAsync: a confirm card with it off, a direct run recorded as such with it on (see Autopilot).
                return null;
        }
    }

    /// <summary>Whether <paramref name="command"/> answers (any choice) an open card in the ring bin that is no assistant's ask.</summary>
    private static bool AnswersRing(CommandRequest command, NodeState state) => command.Verb == Verbs.Answer && IsRing(command, state) && !IsAsk(command, state);

    private string OwnerName() => Flight is { } code ? $"{code}'s" : "this review's";

    private bool Owns(string? flight, string? name, string who) =>
        string.Equals(name, who, StringComparison.OrdinalIgnoreCase) || Flight is not null && string.Equals(flight, Flight, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the session <paramref name="command"/> targets is one of this flight's (or this very session).</summary>
    private bool OwnsSession(CommandRequest command, NodeState state)
    {
        var id = command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.SessionTargetPrefix.Length..] : command.Target;
        return state.Sessions().FirstOrDefault(s => s.Id == id) is { } row && Owns(row.Flight, row.Name, command.Who);
    }

    /// <summary>Whether the job <paramref name="command"/> targets was started by this session or by one of this flight's.</summary>
    private bool OwnsJob(CommandRequest command, NodeState state)
    {
        var id = command.Target.StartsWith(Verbs.JobTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.JobTargetPrefix.Length..] : command.Target;
        return state.Jobs().FirstOrDefault(j => j.Id == id) is { } job && Owns(job.Flight, job.OwnerName, command.Who);
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
