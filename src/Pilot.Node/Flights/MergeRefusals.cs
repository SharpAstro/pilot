using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A merge the node refused itself, carrying its class (<see cref="MergeRefusal"/>) given at the throw. Its message keeps its words.
/// </summary>
public sealed class MergeBlockedException(MergeRefusal refusal, string message) : FlightException(message)
{
    public override MergeRefusal? Refusal { get; } = refusal;
}

/// <summary>
/// The classes a merge refusal can have, and the one place that reads one. The class is set where the refusal is made (a throw site
/// gives it, <see cref="Of(MergeAnswerKind)"/> maps GitHub's sorted answer, whose words only <see cref="MergeAnswers"/> matches), so
/// nothing here reads a message. Anything unclassified is <see cref="Unclassified"/>, Persons, so nothing new is hidden.
/// </summary>
public static class MergeRefusals
{
    /// <summary>The node's own wait: who acts is nobody, it goes on when the wait ends.</summary>
    public static readonly MergeRefusal NodeWait = new(MergeRefusalClass.NodeWait);

    /// <summary>A refusal that passes by itself: the node asks again or brings the branch up to date.</summary>
    public static readonly MergeRefusal Passing = new(MergeRefusalClass.Passing);

    /// <summary>A person's, with nothing to press: a fact to read. What nothing classified falls to.</summary>
    public static readonly MergeRefusal Unclassified = new(MergeRefusalClass.Persons);

    /// <summary>A person's: branch protection needs an approval on GitHub.</summary>
    public static readonly MergeRefusal NeedsApproval = new(MergeRefusalClass.Persons, MergeAction.ApproveOnGitHub);

    /// <summary>A person's: a review on GitHub asks for changes.</summary>
    public static readonly MergeRefusal ChangesRequested = new(MergeRefusalClass.Persons, MergeAction.ReadChanges);

    /// <summary>A person's: a required check failed.</summary>
    public static readonly MergeRefusal FailedCheck = new(MergeRefusalClass.Persons, MergeAction.RerunFailedCheck);

    /// <summary>A person's: the review's design question is open.</summary>
    public static readonly MergeRefusal DesignQuestion = new(MergeRefusalClass.Persons, MergeAction.AnswerDesignQuestion);

    /// <summary>A person's: the plane holds commits past the PR's head.</summary>
    public static readonly MergeRefusal Unpushed = new(MergeRefusalClass.Persons, MergeAction.OpenPrAgain);

    /// <summary>
    /// GitHub's merge queue is busy with other entries: passes by itself, the node asks again. A throw site gives this where the queue
    /// is what refused.
    /// </summary>
    public static readonly MergeRefusal QueueBusy = Passing;

    /// <summary>The refusal GitHub's sorted answer comes to (a kind <see cref="MergeAnswers"/> decided, never its words read again).</summary>
    public static MergeRefusal Of(MergeAnswerKind kind) => kind switch
    {
        // GitHub 5xx or a timeout: asked again.
        MergeAnswerKind.Transient => Passing,
        // Behind or conflicting: the node brings the branch up to date (and the watchtower resolves a real conflict in the plane).
        MergeAnswerKind.Behind or MergeAnswerKind.Conflicts => Passing,
        // The head moved past the one named: the node starts a review of the new head.
        MergeAnswerKind.HeadMoved => NodeWait,
        // Done is no refusal; "can't be rebased" after every fallback and GitHub's other words are a person's, the text kept whole.
        _ => Unclassified,
    };

    /// <summary>The class a thrown refusal carries; <see cref="Unclassified"/> for any that was given none.</summary>
    public static MergeRefusal Of(Exception exception) => exception is FlightException { Refusal: { } refusal } ? refusal : Unclassified;

    /// <summary>The class a merge verb's refusal carries; null for a command that was done. <see cref="Unclassified"/> when none was given.</summary>
    public static MergeRefusal? Of(CommandResult result) => result.Ok ? null : result.Refusal ?? Unclassified;
}
