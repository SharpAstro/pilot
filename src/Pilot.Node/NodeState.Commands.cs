using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>
    /// Does a command, or says why not, and puts its result on the stream either way, after whatever it changed. Its
    /// shape (a verb, a target, who) is the endpoint's to check; what it means is decided here.
    /// </summary>
    public CommandResult Execute(CommandRequest command)
    {
        lock (_gate)
        {
            // A body without args leaves them null: the generated deserialiser skips a missing property's initialiser.
            var args = command.Args ?? [];
            string? error = null;
            switch (command.Verb, command.Target)
            {
                case (Verbs.Note, Verbs.NodeTarget) when string.Join(' ', args).Trim() is { Length: > 0 } text:
                    Publish(new NoteAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Note = new NodeNote { At = _time.GetUtcNow(), Who = command.Who, Text = text } });
                    break;
                case (Verbs.Note, Verbs.NodeTarget):
                    error = "note node needs the note's text";
                    break;
                case (Verbs.Note, _):
                    error = $"note takes the target node, not {command.Target}";
                    break;
                default:
                    error = $"The node knows no verb {command.Verb}";
                    break;
            }

            return CompleteLocked(command, error, way: null, sessionId: null);
        }
    }

    /// <summary>
    /// Puts the result of a command done outside <see cref="Execute"/> on the stream: one whose work is too long to do
    /// under the lock, as a <c>say</c> is (<see cref="Sessions.SessionMessenger"/>).
    /// </summary>
    /// <param name="error">Why it was not done, or null when it was.</param>
    /// <param name="way">Which way it went (<see cref="CommandResult.Way"/>).</param>
    /// <param name="sessionId">The target's new session id (<see cref="CommandResult.SessionId"/>).</param>
    public CommandResult Complete(CommandRequest command, string? error, string? way = null, string? sessionId = null, string? made = null, SettingRow? setting = null, string? waiting = null, IdeaTidyProposal? tidy = null, MilestoneProposal? milestones = null, ReadinessResult? readiness = null, MilestoneSuggestions? suggestions = null, IdeaRow? idea = null, BreakUpProposal? breakUp = null)
    {
        lock (_gate)
        {
            return CompleteLocked(command, error, way, sessionId, made, setting, waiting, tidy, milestones, readiness, suggestions, idea, breakUp);
        }
    }

    private CommandResult CompleteLocked(CommandRequest command, string? error, string? way, string? sessionId, string? made = null, SettingRow? setting = null, string? waiting = null, IdeaTidyProposal? tidy = null, MilestoneProposal? milestones = null, ReadinessResult? readiness = null, MilestoneSuggestions? suggestions = null, IdeaRow? idea = null, BreakUpProposal? breakUp = null)
    {
        var result = new CommandResult
        {
            Verb = command.Verb,
            Target = command.Target,
            Who = command.Who,
            Ok = error is null,
            Error = error,
            Way = error is null ? way : null,
            SessionId = error is null ? sessionId : null,
            Made = error is null ? made : null,
            Setting = error is null ? setting : null,
            Tidy = error is null ? tidy : null,
            Milestones = error is null ? milestones : null,
            Readiness = error is null ? readiness : null,
            Suggestions = error is null ? suggestions : null,
            Idea = error is null ? idea : null,
            BreakUp = error is null ? breakUp : null,
            Autopilot = command.Autopilot,
            Why = command.Why is { } why && why.Trim().Length > 0 ? why.Trim() : null,
            Waiting = error is null ? waiting : null,
            Seq = _seq + 1,
        };
        Publish(new CommandResultEvent { Seq = result.Seq, At = _time.GetUtcNow(), Result = result });
        return result;
    }

    /// <summary>
    /// Records a decision: a <see cref="DecisionEvent"/> on the stream, kept whole by the journal. Under the lock, as
    /// <see cref="Complete"/> is.
    /// </summary>
    public DecisionRow RecordDecision(string subject, string kind, string question, string[] options, string choice, string by, string byKind, string reason)
    {
        lock (_gate)
        {
            var at = _time.GetUtcNow();
            var decision = new DecisionRow
            {
                Subject = subject, Kind = kind, Question = question, Options = options, Choice = choice,
                By = by, ByKind = byKind, Reason = reason.Trim(), At = at, Seq = _seq + 1,
            };
            Publish(new DecisionEvent { Seq = decision.Seq, At = at, Decision = decision });
            return decision;
        }
    }

    /// <summary>
    /// The decisions kept, oldest first: of a flight (its subject is the flight, or a card or PR of it, <c>flight/PIL583</c>
    /// and anything below it), of a kind, made at or after <paramref name="since"/>.
    /// </summary>
    public DecisionRow[] Decisions(string? flight = null, string? kind = null, DateTimeOffset? since = null)
    {
        lock (_gate)
        {
            return [.. _journal.Decisions()
                .Where(d => flight is not { Length: > 0 } || SubjectIsOf(d.Subject, flight))
                .Where(d => kind is not { Length: > 0 } || string.Equals(d.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Where(d => since is not { } from || d.At >= from)
                .OrderBy(d => d.At).ThenBy(d => d.Seq)];
        }
    }

    private static bool SubjectIsOf(string subject, string flight)
    {
        var prefix = $"flight/{flight}";
        return subject.Equals(flight, StringComparison.OrdinalIgnoreCase)
            || subject.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || subject.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}
