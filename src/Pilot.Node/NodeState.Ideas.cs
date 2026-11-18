using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>The ideas of every repo the node reads, newest first.</summary>
    public IdeaRow[] Ideas()
    {
        lock (_gate)
        {
            return _ideas;
        }
    }

    /// <summary>Every idea moved to another repo, oldest first: an old id finds the new one through <see cref="IdeaIds.Follow"/>.</summary>
    public IdeaMoveRow[] IdeaMoves()
    {
        lock (_gate)
        {
            return _ideaMoves;
        }
    }

    /// <summary>Remembers that an idea was moved, so its old id keeps resolving; a move already remembered is no delta.</summary>
    public void PutIdeaMove(IdeaMoveRow move)
    {
        lock (_gate)
        {
            if (!_ideaMoves.Contains(move))
            {
                Publish(new IdeaMovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Move = move });
            }
        }
    }

    /// <summary>The open milestones of every repo the node reads, the earliest due first within a repo.</summary>
    public MilestoneRow[] Milestones()
    {
        lock (_gate)
        {
            return _milestones;
        }
    }

    /// <summary>The open issues of every repo the node reads, in a milestone or in none, the newest change first.</summary>
    public IssueRow[] Issues()
    {
        lock (_gate)
        {
            return _issues;
        }
    }

    /// <summary>
    /// Puts the open milestones of one repo (<paramref name="repo"/>, its folder name) as just read, leaving every other repo's as they are:
    /// a delta with all the milestones when they differ, nothing when they are the same.
    /// </summary>
    public void PutMilestones(string repo, IEnumerable<MilestoneRow> read)
    {
        lock (_gate)
        {
            var next = IssueIds.OrderMilestones([.. _milestones.Where(m => !string.Equals(m.Repo, repo, StringComparison.OrdinalIgnoreCase)), .. read]).ToArray();
            if (!next.SequenceEqual(_milestones))
            {
                Publish(new MilestonesChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Milestones = next });
            }
        }
    }

    /// <summary>
    /// Puts the open issues of one repo (<paramref name="repo"/>, its folder name) as just read, leaving every other repo's as they are:
    /// a delta with all the issues when they differ, nothing when they are the same. A row read keeps the readiness the issue's row had
    /// (<see cref="PutReadiness"/>): GitHub does not know it, so a read never takes it away.
    /// </summary>
    public void PutIssues(string repo, IEnumerable<IssueRow> read)
    {
        lock (_gate)
        {
            var kept = _issues.Where(i => i.Readiness is not null && string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase)).ToDictionary(i => i.Number, i => i.Readiness);
            var next = IssueIds.OrderIssues([.. _issues.Where(i => !string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase)),
                .. read.Select(i => i.Readiness is null && kept.TryGetValue(i.Number, out var readiness) ? i with { Readiness = readiness } : i)]).ToArray();
            // Age moves with the clock alone: a read that changes nothing else is no delta.
            if (next.Length != _issues.Length || next.Zip(_issues).Any(pair => pair.First with { Age = "" } != pair.Second with { Age = "" }))
            {
                Publish(new IssuesChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Issues = next });
            }
        }
    }

    /// <summary>
    /// Keeps <paramref name="readiness"/> on the row of issue <paramref name="number"/> of <paramref name="repo"/> (a delta); false when the node has
    /// no row for it. The answer lives on the row, so it goes when the issue leaves the open ones.
    /// </summary>
    public bool PutReadiness(string repo, int number, IssueReadiness readiness)
    {
        lock (_gate)
        {
            var at = Array.FindIndex(_issues, i => i.Number == number && string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                return false;
            }
            if (_issues[at].Readiness != readiness)
            {
                var next = (IssueRow[])_issues.Clone();
                next[at] = next[at] with { Readiness = readiness };
                Publish(new IssuesChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Issues = next });
            }
            return true;
        }
    }

    /// <summary>The summaries made, one per scope: the fleet's first, then each flight's by code.</summary>
    public SummaryRow[] Summaries()
    {
        lock (_gate)
        {
            return [.. _summaries.Values.OrderBy(s => s.Scope != SummaryRow.FleetScope).ThenBy(s => s.Scope, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Puts the ideas of one repo (<paramref name="repo"/>, its folder name) as just read, leaving every other repo's as they are:
    /// a delta with all the ideas when they differ, nothing when they are the same.
    /// </summary>
    public void PutIdeas(string repo, IEnumerable<IdeaRow> read)
    {
        lock (_gate)
        {
            PutIdeasLocked([.. _ideas.Where(i => !string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase)), .. read]);
        }
    }

    /// <summary>
    /// Puts the ideas of every repo read at once: the repos named in <paramref name="repos"/> take the rows of <paramref name="read"/>
    /// (so an idea gone from GitHub goes), every other repo's rows stay.
    /// </summary>
    public void PutIdeas(IReadOnlyCollection<string> repos, IEnumerable<IdeaRow> read)
    {
        lock (_gate)
        {
            PutIdeasLocked([.. _ideas.Where(i => !repos.Contains(i.Repo, StringComparer.OrdinalIgnoreCase)), .. read]);
        }
    }

    /// <summary>The designs the node keeps (PIL1152): an idea row names the design started from it and its current version, read from here, not from GitHub.</summary>
    public Func<DesignRow[]>? Designs { get; set; }

    /// <summary>The idea key a design remembers: the repo and the idea's id, e.g. <c>pilot I381</c>.</summary>
    public static string IdeaKey(string repo, string id) => $"{repo} {id}";

    /// <summary>Reads the ideas again with the designs as they are now (a design was started or published): a delta only when a row changes.</summary>
    public void RefreshIdeaDesigns()
    {
        lock (_gate)
        {
            PutIdeasLocked([.. _ideas]);
        }
    }

    private IdeaRow[] WithDesigns(IEnumerable<IdeaRow> all)
    {
        var rows = all.ToArray();
        DesignRow[] designs;
        try
        {
            designs = Designs?.Invoke() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            designs = [];
        }
        // List() is newest first; an idea lists its designs oldest first. A link follows its idea through a move or a fold (IdeaIds.Follow), so it is looked up by where the idea is now.
        var moves = _ideaMoves;
        var byIdea = new Dictionary<string, List<DesignRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in designs.Reverse())
        {
            foreach (var key in d.Ideas())
            {
                var followed = key;
                if (IdeaIds.Parse(key).Ref is { Repo: { } repo } parsed)
                {
                    var at = IdeaIds.Follow(moves, repo, parsed.Number);
                    followed = IdeaKey(at.Repo, IdeaIds.Id(at.Number));
                }
                if (!byIdea.TryGetValue(followed, out var list))
                {
                    byIdea[followed] = list = [];
                }
                if (!list.Any(x => x.Id == d.Id))
                {
                    list.Add(d);
                }
            }
        }
        return [.. rows.Select(r =>
        {
            var linked = byIdea.TryGetValue(IdeaKey(r.Repo, r.Id), out var list) ? list : [];
            var links = linked.Select(d => new IdeaDesignLink(d.Id, d.Title, d.Current)).ToArray();
            return r with { Designs = links, Design = links.Length > 0 ? links[0].Id : null, DesignVersion = links.Length > 0 ? links[0].Version : null };
        })];
    }

    private void PutIdeasLocked(IEnumerable<IdeaRow> all)
    {
        var next = IdeaIds.Order(WithDesigns(all)).ToArray();
        // Age moves with the clock alone: a read that changes nothing else is no delta.
        if (next.Length != _ideas.Length || next.Zip(_ideas).Any(pair => pair.First with { Age = "", Images = [], Designs = [] } != pair.Second with { Age = "", Images = [], Designs = [] } || !pair.First.Designs.SequenceEqual(pair.Second.Designs)))
        {
            Publish(new IdeasChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Ideas = next });
        }
    }

    /// <summary>
    /// Keeps a summary, replacing the older of its scope: one delta, journaled and sent to every client. Always a delta, even for
    /// the same words, as the row says when it was made and what that cost.
    /// </summary>
    public void PutSummary(SummaryRow summary)
    {
        lock (_gate)
        {
            Publish(new SummaryChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Summary = summary });
        }
    }
}
