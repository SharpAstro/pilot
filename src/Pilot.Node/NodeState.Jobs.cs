using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>The jobs, newest first.</summary>
    public JobRow[] Jobs()
    {
        lock (_gate)
        {
            return [.. _jobs.Values.OrderByDescending(j => j.StartedAt)];
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
