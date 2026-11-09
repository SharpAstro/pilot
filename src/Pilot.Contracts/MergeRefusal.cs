namespace Pilot.Contracts;

/// <summary>Who acts on a refused merge: decided where the refusal is made, never by matching its words afterwards (PIL864).</summary>
public enum MergeRefusalClass
{
    /// <summary>
    /// The node's own wait: legs not done, a holding pattern, a review not passed, checks running, the repo's merge turn held, a merge
    /// under way. The node goes on by itself when it ends, so nothing rings and nobody is asked.
    /// </summary>
    NodeWait,

    /// <summary>
    /// A refusal that passes by itself: GitHub did not answer, the branch is behind or conflicts and the node brings it up to date,
    /// the merge queue is busy. The node asks again; a person is told only if it does not pass.
    /// </summary>
    Passing,

    /// <summary>
    /// A refusal only a person can clear, with the one thing to do (<see cref="MergeAction"/>). Anything the node did not classify is
    /// this, so nothing new is hidden.
    /// </summary>
    Persons,
}

/// <summary>The one thing a person does about a <see cref="MergeRefusalClass.Persons"/> refusal.</summary>
public enum MergeAction
{
    /// <summary>Nothing to press: the refusal is a fact to read (a bad ask, GitHub's own text). What nothing classified falls to.</summary>
    None,

    /// <summary>The PR was closed unmerged: nothing to merge, and nothing to press. A fact to read, so it is a For-you card, not a ring.</summary>
    PrClosed,

    /// <summary>The PR is a draft: mark it ready for review on GitHub.</summary>
    MarkReadyOnGitHub,

    /// <summary>
    /// With a <see cref="MergeRefusalClass.Passing"/> refusal: the PR conflicts with its base. The node brings the branch up to date
    /// and asks again, but a conflict that is real does not pass, so it is said when the tries run out.
    /// </summary>
    ResolveConflict,

    /// <summary>Branch protection needs an approval: approve the PR on GitHub.</summary>
    ApproveOnGitHub,

    /// <summary>A review on GitHub asks for changes: read them, then the merge is offered again.</summary>
    ReadChanges,

    /// <summary>A required check failed: re-run it, or redo the leg if it fails again with no cause of its own.</summary>
    RerunFailedCheck,

    /// <summary>The review asked a question of design nobody has answered: answer it or dismiss it.</summary>
    AnswerDesignQuestion,

    /// <summary>The plane holds commits the PR does not: open the PR again so they are pushed and reviewed.</summary>
    OpenPrAgain,
}

/// <summary>A merge refusal's class, and for <see cref="MergeRefusalClass.Persons"/> the one action to take.</summary>
public sealed record MergeRefusal(MergeRefusalClass Class, MergeAction Action = MergeAction.None);
