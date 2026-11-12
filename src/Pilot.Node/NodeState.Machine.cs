using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>The account's usage as last read; null before any.</summary>
    public UsageRow? Usage()
    {
        lock (_gate)
        {
            return _usage;
        }
    }

    /// <summary>
    /// Puts the usage read, merged with the held figures so a stale writer never wins (<see cref="UsageRow.Newest"/>),
    /// when the figures changed, or when the same ones were written again 10 minutes after those held: a status line
    /// rewrites them each minute, and a delta for each would fill the journal with nothing.
    /// </summary>
    public void PutUsage(UsageRow read)
    {
        lock (_gate)
        {
            var usage = UsageRow.Newest(_usage, read);
            if (_usage is { } held && held.Account == usage.Account && held.FiveHour == usage.FiveHour && held.SevenDay == usage.SevenDay
                && usage.ReadAt - held.ReadAt < TimeSpan.FromMinutes(10))
            {
                return;
            }
            Publish(new UsageChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Usage = usage });
        }
    }

    /// <summary>The machine as last sampled; null before any.</summary>
    public MachineRow? Machine()
    {
        lock (_gate)
        {
            return _machine;
        }
    }

    /// <summary>The admission as last put; null before any.</summary>
    public AdmissionRow? Admission()
    {
        lock (_gate)
        {
            return _admission;
        }
    }

    /// <summary>Puts the admission when it differs from the one held, so a sweep that finds the same count adds no delta.</summary>
    public void PutAdmission(AdmissionRow row)
    {
        lock (_gate)
        {
            // Nothing held and nothing to say (no session counted, no priority flight, no peer left out, no idle session waiting on a person or a job):
            // a node that has just started and swept adds no event, so a client's first snapshot is the same as before the sweep ran at start. A node
            // whose only sessions are waiting ones has something to say (the "not counted" line is for exactly that case), so it is published.
            if (_admission is null && row.OpusCount == 0 && row.Counted.Length == 0 && row.Priority.Length == 0 && row.PeersNotCounted is null
                && row.WaitingOnPerson.Length == 0)
            {
                return;
            }
            // Who waits is compared as written, like who is counted: the count staying put while a worker's job ends or a session asks a card is a change.
            if (_admission is { } held && held.OpusCount == row.OpusCount && held.OpusCap == row.OpusCap && held.OneFlightPerRepo == row.OneFlightPerRepo
                && held.PeersNotCounted == row.PeersNotCounted && held.Counted.SequenceEqual(row.Counted) && held.Priority.SequenceEqual(row.Priority)
                && held.WaitingOnPerson.SequenceEqual(row.WaitingOnPerson))
            {
                return;
            }
            Publish(new AdmissionChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Admission = row });
        }
    }

    /// <summary>Every open PR of the repos pilot works in as last read; null before any.</summary>
    public PullRequestsRow? PullRequests()
    {
        lock (_gate)
        {
            return _pullRequests;
        }
    }

    /// <summary>
    /// Puts the machine sampled when it changed meaningfully (<see cref="Pilot.Node.Flights.MachineSampler.Differs"/>): a sample every few
    /// seconds would otherwise put a delta for each, and the journal would fill with CPU jitter.
    /// </summary>
    public void PutMachine(MachineRow sampled)
    {
        lock (_gate)
        {
            // The sampler knows no strays: what the stray sweep last said rides on every row, so a sample never wipes it.
            sampled = sampled with { Strays = _strays ?? _machine?.Strays ?? [] };
            if (_machine is { } held && !Pilot.Node.Flights.MachineSampler.Differs(held, sampled))
            {
                return;
            }
            Publish(new MachineChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Machine = sampled });
        }
    }

    // What the stray sweep last named (pilot #909); null before its first sweep.
    private MachineStray[]? _strays;

    /// <summary>The strays the sweep last named, as the machine row carries them; none before the first sweep.</summary>
    public MachineStray[] Strays()
    {
        lock (_gate)
        {
            return _strays ?? _machine?.Strays ?? [];
        }
    }

    /// <summary>
    /// Puts the strays the sweep named, into the machine row, when they differ from the ones held (compared as written, ages aside: a row
    /// that grew a minute older is no news). Nothing is published before the first machine sample; the next sample carries them.
    /// </summary>
    public void PutStrays(IReadOnlyList<MachineStray> strays)
    {
        lock (_gate)
        {
            var held = _strays ?? _machine?.Strays ?? [];
            _strays = [.. strays];
            if (_machine is not { } machine
                || held.Select(h => (h.Pid, h.StartedAt, h.Reason)).SequenceEqual(_strays.Select(h => (h.Pid, h.StartedAt, h.Reason))))
            {
                return;
            }
            Publish(new MachineChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Machine = machine with { Strays = _strays } });
        }
    }

    // Compared as written, read times aside: a status holds arrays (worktrees, PRs, ideas), which a record compares by reference.
    private static bool SameRepos(ReposRow held, ReposRow read) =>
        held.Repos.Length == read.Repos.Length
        && held.Repos.Zip(read.Repos).All(pair => System.Text.Json.JsonSerializer.Serialize(pair.First with { UpdatedAt = default }, PilotJsonContext.Default.RepoStatus)
            == System.Text.Json.JsonSerializer.Serialize(pair.Second with { UpdatedAt = default }, PilotJsonContext.Default.RepoStatus));

    /// <summary>What the node last said it is (<see cref="NodeRow"/>); null before any.</summary>
    public NodeRow? Node()
    {
        lock (_gate)
        {
            return _node;
        }
    }

    /// <summary>
    /// Puts what the node says it is when it differs meaningfully (<see cref="NodeRow.Differs"/>): the watchtower's clock-driven
    /// figures alone would otherwise put a delta each tick and fill the journal.
    /// </summary>
    public void PutNode(NodeRow said)
    {
        lock (_gate)
        {
            if (_node is { } held && !NodeRow.Differs(held, said))
            {
                return;
            }
            Publish(new NodeChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Row = said });
        }
    }

    /// <summary>
    /// Puts the PRs read, when they differ from those held, or when the held read is 2 minutes old: a read every minute that finds
    /// nothing new would fill the journal with a row of every PR, yet a client dates the row by its read and marks one over 5
    /// minutes old stale, so the read time is renewed well within that. The per-repo read stamps are what a peer takes a read by (#834),
    /// so a read that only renews them is kept in the state at once, which is what a peer asks for, and is put on the stream when a
    /// client is attached to hear it (a peer following this node is one); with none, the journal gets no delta for it.
    /// </summary>
    public void PutPullRequests(PullRequestsRow read)
    {
        lock (_gate)
        {
            read = WithoutForgotten(read);
            if (_pullRequests is { } held && held.Orgs.SequenceEqual(read.Orgs) && held.Errors.SequenceEqual(read.Errors) && held.Note == read.Note
                && held.PullRequests.SequenceEqual(read.PullRequests) && read.ReadAt - held.ReadAt < TimeSpan.FromMinutes(2))
            {
                if (StampsRenewed(held, read))
                {
                    if (_sinks.Count > 0)
                    {
                        Publish(new PullRequestsChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Row = read });
                    }
                    else
                    {
                        _pullRequests = read;
                    }
                }
                return;
            }
            Publish(new PullRequestsChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Row = read });
        }
    }

    /// <summary>How long a PR the node saw merge stays out of every PR row put (a lagging search or a peer's older row may still list it).</summary>
    public static readonly TimeSpan MergedForgetWindow = TimeSpan.FromMinutes(10);

    private readonly List<(string Repo, int Number, DateTimeOffset At)> _forgotten = [];

    private static bool SameRepo(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // A flight names its repo by folder (tianwen), a PR row as owner/name.
        var an = a.Contains('/') ? a[(a.LastIndexOf('/') + 1)..] : a;
        var bn = b.Contains('/') ? b[(b.LastIndexOf('/') + 1)..] : b;
        return (!a.Contains('/') || !b.Contains('/')) && string.Equals(an, bn, StringComparison.OrdinalIgnoreCase);
    }

    // Caller holds the gate. Drops the aged, then the listed PRs the node saw merge.
    private PullRequestsRow WithoutForgotten(PullRequestsRow read)
    {
        var now = _time.GetUtcNow();
        _forgotten.RemoveAll(f => now - f.At >= MergedForgetWindow);
        if (_forgotten.Count == 0)
        {
            return read;
        }
        var kept = read.PullRequests.Where(p => !_forgotten.Any(f => f.Number == p.Number && SameRepo(f.Repo, p.Repo))).ToArray();
        return kept.Length == read.PullRequests.Length ? read : read with { PullRequests = kept };
    }

    /// <summary>
    /// The node saw <paramref name="repo"/>#<paramref name="number"/> merge: it leaves the held PR row at once (published), and for
    /// <see cref="MergedForgetWindow"/> every PR row put is cleared of it, so a lagging read never brings it back. In memory only.
    /// </summary>
    public void ForgetPullRequest(string repo, int number, DateTimeOffset at)
    {
        lock (_gate)
        {
            _forgotten.RemoveAll(f => f.Number == number && SameRepo(f.Repo, repo));
            _forgotten.Add((repo, number, at));
            if (_pullRequests is { } held)
            {
                var row = WithoutForgotten(held);
                if (!ReferenceEquals(row, held))
                {
                    Publish(new PullRequestsChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Row = row });
                }
            }
        }
    }

    // Some repo's read stamp is newer than the held one by more than a few seconds, or the stamps are other repos'.
    private static bool StampsRenewed(PullRequestsRow held, PullRequestsRow read) =>
        read.RepoReads.Length != held.RepoReads.Length
        || read.RepoReads.Any(s => held.RepoReads.FirstOrDefault(h => string.Equals(h.Repo, s.Repo, StringComparison.OrdinalIgnoreCase)) is not { } was
            || s.By != was.By || s.Flying != was.Flying || s.ReadAt - was.ReadAt > StampLag);

    /// <summary>How far a peer may see a repo's read stamp behind the read.</summary>
    public static readonly TimeSpan StampLag = TimeSpan.FromSeconds(5);

    /// <summary>Where every repo the node knows stands as last read, or marked not read yet; null before any.</summary>
    public ReposRow? Repos()
    {
        lock (_gate)
        {
            return _repos;
        }
    }

    /// <summary>
    /// Puts the repos read, when their statuses differ from those held (their own read times aside), or when the held read is 2
    /// minutes old: a read every few minutes that finds nothing new would fill the journal with a row of every repo, yet a
    /// client dates the row by its read, so the read time is renewed well within that.
    /// </summary>
    public void PutRepos(ReposRow read)
    {
        lock (_gate)
        {
            // A node that knows no repos and has said nothing has nothing to say.
            if (_repos is null && read.Repos.Length == 0)
            {
                return;
            }
            if (_repos is { } held && held.ReadAt is { } heldAt && read.ReadAt is { } readAt && readAt - heldAt < TimeSpan.FromMinutes(2)
                && SameRepos(held, read))
            {
                return;
            }
            Publish(new ReposChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Row = read });
        }
    }
}
