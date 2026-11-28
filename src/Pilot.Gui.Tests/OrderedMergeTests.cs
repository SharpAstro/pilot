using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A window of several nodes merges each node's lines, already in order and marked once, instead of marking and sorting them all
/// again at every look (pilot #1443): the merge must give exactly what the sort of their concatenation gave.
/// </summary>
public sealed class OrderedMergeTests
{
    private static readonly NodeMark De1 = new("de1", "192.168.0.79:47731", "DE1", Here: true);
    private static readonly NodeMark Se1 = new("se1", "192.168.0.70:47731", "SE1", Here: false);

    private static IReadOnlyList<SessionLine> Sessions(Random random, string node, int count, DateTimeOffset now)
    {
        var states = Enum.GetValues<SessionState>();
        var roles = Enum.GetValues<SessionRole>();
        var rows = Enumerable.Range(0, count).Select(i => Live.Row($"{node}{i:x4}", $"session {random.Next(40)}", states[random.Next(states.Length)]) with
        {
            Role = roles[random.Next(roles.Length)],
            Flight = random.Next(3) == 0 ? null : $"PIL{random.Next(5)}",
            Leg = random.Next(2) == 0 ? null : $"leg{random.Next(3)}",
        });
        return SessionLine.Build(rows, now);
    }

    private static IReadOnlyList<JobLine> Jobs(Random random, string node, int count, DateTimeOffset now)
    {
        var states = new[] { JobState.Running, JobState.Queued, JobState.Succeeded, JobState.Failed };
        var rows = Enumerable.Range(0, count).Select(i =>
        {
            var started = now - TimeSpan.FromMinutes(random.Next(600));
            var state = states[random.Next(states.Length)];
            return new JobRow
            {
                Id = $"{node}j{i}", Command = "dotnet test", Cwd = "/repo", State = state, LogPath = $"/logs/{node}{i}.log", Timeout = TimeSpan.FromMinutes(30),
                StartedAt = started,
                QueuedAt = state == JobState.Queued && random.Next(2) == 0 ? started - TimeSpan.FromMinutes(random.Next(10)) : null,
                EndedAt = state is JobState.Succeeded or JobState.Failed && random.Next(4) != 0 ? started + TimeSpan.FromMinutes(random.Next(60)) : null,
            };
        });
        return JobLine.Build(rows, now);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MergingEachNodesSessionsGivesTheOrderTheSortOfAllOfThemGave(int seed)
    {
        var random = new Random(seed);
        var now = Live.At;
        var de1 = Sessions(random, "a", 400, now).Select(l => l with { Node = De1 }).ToList();
        var se1 = Sessions(random, "b", 300, now).Select(l => l with { Node = Se1 }).ToList();

        var merged = Ordered.Merge([de1, se1], SessionLine.Ordering);

        merged.ShouldBe(SessionLine.Order(de1.Concat(se1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MergingEachNodesJobsGivesTheOrderTheSortOfAllOfThemGave(int seed)
    {
        var random = new Random(seed);
        var now = Live.At;
        var de1 = Jobs(random, "a", 400, now).Select(l => l with { Node = De1 }).ToList();
        var se1 = Jobs(random, "b", 300, now).Select(l => l with { Node = Se1 }).ToList();

        var merged = Ordered.Merge([de1, se1], JobLine.Ordering);

        merged.ShouldBe(JobLine.Order(de1.Concat(se1)));
    }
}
