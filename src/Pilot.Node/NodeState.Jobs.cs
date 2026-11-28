using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    // The jobs newest first, sorted once and kept until a job is added or changes (ApplyToState drops it). Jobs() sorted every job at
    // every call, and JobRunner calls it at every tick and for each job it looks at: on DE1 (9,288 jobs, 2026-11-28) each sort put a
    // key array of 150 KB on the large object heap, 42 MB in 30 s (pilot #1443, #1365's second point). The array is shared: no caller
    // changes it, and a change of the jobs makes a new one.
    private JobRow[]? _jobsNewestFirst;

    /// <summary>The jobs, newest first. The array is shared and must not be changed.</summary>
    public JobRow[] Jobs()
    {
        lock (_gate)
        {
            return _jobsNewestFirst ??= [.. _jobs.Values.OrderByDescending(j => j.StartedAt)];
        }
    }

    /// <summary>The next job id, <c>j&lt;n&gt;</c>, never one used before.</summary>
    public string NextJobId()
    {
        lock (_gate)
        {
            return $"j{(_jobs.Count == 0 ? 1 : _jobs.Keys.Max(k => int.TryParse(k.AsSpan(1), out var n) ? n : 0) + 1)}";
        }
    }

    /// <summary>Puts a job: added when new, changed when it differs, nothing when it is the same.</summary>
    public void PutJob(JobRow job)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(job.Id, out var held))
            {
                if (held != job)
                {
                    Publish(new JobChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Job = job });
                }
            }
            else
            {
                Publish(new JobAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Job = job });
            }
        }
    }

    /// <summary>The PR reviews, newest start first.</summary>
    public PrReviewRow[] PrReviews()
    {
        lock (_gate)
        {
            return [.. _prReviews.Values.OrderByDescending(r => r.StartedAt)];
        }
    }

    /// <summary>Puts a PR review, by its repo and number: added when new, changed when it differs, nothing when it is the same.</summary>
    public void PutPrReview(PrReviewRow review)
    {
        lock (_gate)
        {
            if (_prReviews.TryGetValue(Verbs.PrTarget(review.Repo, review.Number), out var held))
            {
                if (held != review)
                {
                    Publish(new PrReviewChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Review = review });
                }
            }
            else
            {
                Publish(new PrReviewAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Review = review });
            }
        }
    }
}
