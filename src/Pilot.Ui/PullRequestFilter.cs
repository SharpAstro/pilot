using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The pills of the Pull requests page: which of the PRs the list narrows to. No pill lit shows every PR.</summary>
internal enum PullRequestFilter
{
    /// <summary>The person's own (<see cref="PullRequestsRow.IsMine"/>).</summary>
    Mine,

    /// <summary>The PRs of a flight.</summary>
    Flights,

    /// <summary>The PRs that wait on the person: ready, changes requested or conflicts.</summary>
    NeedsMe,
}
