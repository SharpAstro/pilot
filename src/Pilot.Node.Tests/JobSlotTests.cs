using System.Collections.Concurrent;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A job's slot goes to the next in line only once its row says it ended: the rows the node publishes never read more running than
/// max-jobs (JobTests2's sampling test read five at max-jobs four on main's CI after #985, as the runner started the next job before it
/// put the ended row).
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class JobSlotTests : JobTestsBase
{
    public JobSlotTests() : base(fakeLauncher: true)
    {
    }

    /// <summary>
    /// Follows every job row the state publishes, and how many read running after each: lock-free (the state publishes from several
    /// threads), every count kept and the most read once the run is over.
    /// </summary>
    private sealed class RunningRows : INodeEventSink
    {
        private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<int> _counts = new();

        public int Most => _counts.IsEmpty ? 0 : _counts.Max();

        public int Capacity => 10_000;

        public bool TryWrite(byte[] message)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("job", out var job) && job.TryGetProperty("id", out var id) && job.TryGetProperty("state", out var state))
            {
                _states[id.GetString()!] = state.GetString()!;
                _counts.Enqueue(_states.Values.Count(s => s == "running"));
            }
            return true;
        }

        public void Overflowed()
        {
        }
    }

    [Fact]
    public async Task TheNextJobStartsOnlyOnceTheEndedOnesRowSaysItEndedSoNoMoreThanMaxJobsRowsReadRunning()
    {
        // The test is the dispatcher (Idle): a dispatch is run inside the window between a job's ending and its ended row, the one a
        // signal on the hosted loop only rarely lands in.
        var (jobs, _, _, gone) = Idle();
        jobs.MaxRunning = 1;
        jobs.SiblingsOf = _ => [];
        jobs.OneHeavyAtATime = false;
        jobs.BeforeEndedRow = _ => jobs.Dispatch(JobRunner.LookAgain.Instance);
        var rows = new RunningRows();
        using var watch = _state.Subscribe(null, null, rows);

        var ids = Enumerable.Range(0, 4).Select(_ => Hand(jobs, Sleep(30))).ToList();
        foreach (var id in ids)
        {
            // Once the one before has ended (its row put, its slot free), a look starts this one.
            jobs.Dispatch(JobRunner.LookAgain.Instance);
            await Until(id, j => j.State == JobState.Running);
            await gone(id);
        }

        ids.ShouldAllBe(id => _state.Jobs().Single(j => j.Id == id).EndedAt != null);
        rows.Most.ShouldBe(1, "a row read running beside another at max-jobs 1: the next job started before the ended one's row was put");
    }
}
