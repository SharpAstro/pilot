using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// One node of the Pull requests tree: an org, a repo of it, or a PR of the repo. Org and repo nodes are kept by name across reads
/// (their data is replaced each build), so <see cref="TreeExpansion{T}"/>, which keys by reference, keeps their fold; PR nodes are leaves
/// made afresh.
/// </summary>
internal sealed class PullRequestNode
{
    public PullRequestOrgGroup? Org { get; set; }
    public PullRequestGroup? Repo { get; set; }
    public PullRequestRow? Pr { get; set; }
    public IReadOnlyList<PullRequestNode> Children { get; set; } = [];

    /// <summary>What selection follows when the rows reorder or are read again: the PR's URL, or the header's kind and name.</summary>
    public string Key => Pr is { } pr ? pr.Url : Repo is { } repo ? "repo:" + repo.Repo : Org is { } org ? "org:" + org.Org : "";
}
