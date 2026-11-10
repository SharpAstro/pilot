using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// FakeGitHub is called from many tasks at once by the node (searches, merges, labels), so every recording it writes into
/// from a call is a concurrent collection that keeps each call exactly once (PIL743).
/// </summary>
public sealed class FakeGitHubTests
{
    private const int Workers = 8;
    private const int CallsEach = 200;
    private const int Calls = Workers * CallsEach;
    private const string Repo = "/work/repo";

    private sealed record Case(Func<FakeGitHub, int, Task> Call, Func<FakeGitHub, IEnumerable<object>> Entries, Action<FakeGitHub, int>? Seed = null);

    private static Case Of<T>(Func<FakeGitHub, int, Task> call, Func<FakeGitHub, IEnumerable<T>> entries, Action<FakeGitHub, int>? seed = null) =>
        new(call, g => entries(g).Select(e => (object)e!), seed);

    /// <summary>Issues the guarded writes act on, made in a milestone before the calls begin, each as GitHub holds it.</summary>
    private static void SeedIssues(FakeGitHub github, int count)
    {
        for (var i = 0; i < count; i++)
        {
            github.AddIssue(Repo, $"issue {i}", "m");
        }
    }

    private static readonly Dictionary<string, Case> Cases = new()
    {
        [nameof(FakeGitHub.Searches)] = Of(
            (g, i) => g.OpenPullRequestsInAsync([$"repo{i}"], CancellationToken.None),
            g => g.Searches.Select(s => s[0])),
        [nameof(FakeGitHub.IssuesCreated)] = Of(
            (g, i) => g.CreateIssueAsync(Repo, $"t{i}", null, CancellationToken.None),
            g => g.IssuesCreated.Select(c => c.Title)),
        [nameof(FakeGitHub.ClosedIssues)] = Of(
            (g, i) => g.CloseIssueAsync(Repo, i, "done", CancellationToken.None),
            g => g.ClosedIssues.Select(c => c.Number)),
        [nameof(FakeGitHub.MilestonesMade)] = Of(
            (g, i) => g.CreateMilestoneAsync(Repo, $"m{i}", "d", CancellationToken.None),
            g => g.MilestonesMade.Select(m => m.Title)),
        [nameof(FakeGitHub.Moves)] = Of(
            (g, i) => g.MoveIssueAsync(Repo, i + 1, "other", g.MilestoneIssuesOf[Path.GetFullPath(Repo)][i + 1].UpdatedAt, CancellationToken.None),
            g => g.Moves.Select(m => m.Number),
            SeedIssues),
        [nameof(FakeGitHub.ClosedNotPlanned)] = Of(
            (g, i) => g.CloseIssueNotPlannedAsync(Repo, i + 1, "no", g.MilestoneIssuesOf[Path.GetFullPath(Repo)][i + 1].UpdatedAt, CancellationToken.None),
            g => g.ClosedNotPlanned.Select(c => c.Number),
            SeedIssues),
        [nameof(FakeGitHub.Edits)] = Of(
            (g, i) => g.EditIssueAsync(Repo, i + 1, $"t{i}", "b", g.MilestoneIssuesOf[Path.GetFullPath(Repo)][i + 1].UpdatedAt, CancellationToken.None),
            g => g.Edits.Select(e => e.Number),
            SeedIssues),
        [nameof(FakeGitHub.Transfers)] = Of(
            (g, i) => g.TransferIssueAsync(Repo, i + 1, "owner/elsewhere", g.MilestoneIssuesOf[Path.GetFullPath(Repo)][i + 1].UpdatedAt, CancellationToken.None),
            g => g.Transfers.Select(t => t.Number),
            SeedIssues),
        [nameof(FakeGitHub.ImageUploads)] = Of(
            (g, i) => g.UploadIdeaImageAsync(Repo, $"i{i}", "png", [1], CancellationToken.None),
            g => g.ImageUploads.Select(u => u.Id)),
        [nameof(FakeGitHub.ImageReads)] = Of(
            (g, i) => g.IdeaImageAsync(Repo, $"i{i}", "png", CancellationToken.None),
            g => g.ImageReads.Select(r => r.Id)),
        [nameof(FakeGitHub.Merged)] = Of(
            (g, i) => g.MergeAsync(Repo, i, "head", "squash", CancellationToken.None),
            g => g.Merged.Select(m => m.Number)),
        [nameof(FakeGitHub.AutoMergeArmed)] = Of(
            (g, i) => g.ArmAutoMergeAsync(Repo, i, "head", "squash", CancellationToken.None),
            g => g.AutoMergeArmed.Select(a => a.Number)),
        [nameof(FakeGitHub.AutoMergeDisarmed)] = Of(
            (g, i) => g.DisarmAutoMergeAsync(Repo, i, CancellationToken.None),
            g => g.AutoMergeDisarmed),
        [nameof(FakeGitHub.ProtectionAsked)] = Of(
            (g, i) => g.BranchUnprotectedAsync(Repo, $"b{i}", CancellationToken.None),
            g => g.ProtectionAsked),
        [nameof(FakeGitHub.Reruns)] = Of(
            (g, i) => g.RerunFailedChecksAsync(Repo, i, CancellationToken.None),
            g => g.Reruns),
        [nameof(FakeGitHub.Dequeued)] = Of(
            (g, i) => g.DequeueAsync(Repo, i, CancellationToken.None),
            g => g.Dequeued),
        [nameof(FakeGitHub.DraftCalls)] = Of(
            (g, i) => g.SetDraftAsync(Repo, i, true, CancellationToken.None),
            g => g.DraftCalls.Select(d => d.Number)),
        [nameof(FakeGitHub.Created)] = Of(
            (g, i) => g.CreateAsync(Repo, $"b{i}", "main", "t", "b", CancellationToken.None),
            g => g.Created.Select(c => c.Branch)),
        [nameof(FakeGitHub.Drafts)] = Of(
            (g, i) => g.OpenDraftAsync(Repo, $"b{i}", "main", "t", "b", CancellationToken.None),
            g => g.Drafts.Select(d => d.Number)),
        [nameof(FakeGitHub.ClosedPrs)] = Of(
            (g, i) => g.ClosePullRequestAsync(Repo, i, "c", CancellationToken.None),
            g => g.ClosedPrs),
        [nameof(FakeGitHub.Statuses)] = Of(
            (g, i) => g.SetCommitStatusAsync(Repo, $"s{i}", "success", "d", null, CancellationToken.None),
            g => g.Statuses.Select(s => s.Sha)),
        [nameof(FakeGitHub.LabelCalls)] = Of(
            (g, i) => g.SetPilotLabelAsync(Repo, i, "pilot: passed", CancellationToken.None),
            g => g.LabelCalls.Select(l => l.Number)),
        [nameof(FakeGitHub.RemoveCalls)] = Of(
            (g, i) => g.RemoveLabelAsync(Repo, i, "pilot: passed", CancellationToken.None),
            g => g.RemoveCalls.Select(l => l.Number)),
        [nameof(FakeGitHub.LabelsCreated)] = Of(
            (g, i) => g.AddIssueLabelAsync(Repo, i, $"l{i}", CancellationToken.None),
            g => g.LabelsCreated.Keys),
        [nameof(FakeGitHub.IssueComments)] = Of(
            (g, i) => g.CommentIssueAsync(Repo, i, $"c{i}", CancellationToken.None),
            g => g.IssueComments.Select(c => c.Number)),
        [nameof(FakeGitHub.PrConversation)] = Of(
            (g, i) => g.CommentIssueAsync(Repo, i, $"c{i}", CancellationToken.None),
            g => g.PrConversation.Select(c => c.Number)),
        [nameof(FakeGitHub.LabelsEnsured)] = Of(
            (g, i) => g.EnsureLabelAsync(Repo, $"l{i}", CancellationToken.None),
            g => g.LabelsEnsured.Select(l => l.Label)),
        [nameof(FakeGitHub.RepoLabels)] = Of(
            (g, i) => g.EnsureLabelAsync(Repo, $"l{i}", CancellationToken.None),
            g => g.RepoLabels.Keys),
        [nameof(FakeGitHub.LabelsDeleted)] = Of(
            (g, i) => g.DeleteRepoLabelAsync(Repo, $"l{i}", CancellationToken.None),
            g => g.LabelsDeleted),
        [nameof(FakeGitHub.PrioritySets)] = Of(
            (g, i) => g.SetIssuePriorityAsync(Repo, i, IssuePriority.High, CancellationToken.None),
            g => g.PrioritySets.Select(p => p.Number)),
        [nameof(FakeGitHub.IssuePriorities)] = Of(
            (g, i) => g.SetIssuePriorityAsync(Repo, i, IssuePriority.High, CancellationToken.None),
            g => g.IssuePriorities.Keys),
        [nameof(FakeGitHub.SubIssuesCreated)] = Of(
            (g, i) => g.CreateSubIssueAsync(Repo, i, "t", null, CancellationToken.None),
            g => g.SubIssuesCreated.Select(s => s.Parent)),
        [nameof(FakeGitHub.SubIssues)] = Of(
            (g, i) => g.CreateSubIssueAsync(Repo, i, "t", null, CancellationToken.None),
            g => g.SubIssues.SelectMany(p => p.Value.Keys.Select(id => (p.Key, id)))),
        [nameof(FakeGitHub.Issues)] = Of(
            (g, i) => g.CreateIssueAsync(Repo, $"t{i}", null, CancellationToken.None),
            g => g.Issues.Keys),
        [nameof(FakeGitHub.MilestonesOf)] = Of(
            (g, i) => g.CreateMilestoneAsync(Repo, $"m{i}", "d", CancellationToken.None),
            g => g.MilestonesOf.SelectMany(r => r.Value.Keys.Select(n => (r.Key, n)))),
        [nameof(FakeGitHub.MilestoneIssuesOf)] = Of(
            (g, i) => g.CreateIssueInMilestoneAsync(Repo, $"t{i}", null, "m", CancellationToken.None),
            g => g.MilestoneIssuesOf.SelectMany(r => r.Value.Keys.Select(n => (r.Key, n)))),
    };

    public static TheoryData<string> Recordings => [.. Cases.Keys];

    [Theory]
    [Trait("Category", "Invariant")]
    [MemberData(nameof(Recordings))]
    public async Task EveryCallMadeInParallelIsRecordedExactlyOnce(string recording)
    {
        var (call, entries, seed) = Cases[recording];
        var github = new FakeGitHub();
        seed?.Invoke(github, Calls);

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            for (var k = 0; k < CallsEach; k++)
            {
                await call(github, w * CallsEach + k);
            }
        })));

        var held = entries(github).ToArray();
        held.Length.ShouldBe(Calls, recording);
        held.Distinct().Count().ShouldBe(Calls, recording);
    }

    [Fact]
    [Trait("Category", "Invariant")]
    [Trait("Category", "Slow")] // a real git: a clone runs git against a bare repo made here, which is what the call does
    public async Task ClonesMadeInParallelAreEachRecordedExactlyOnce()
    {
        // A clone runs git, so this one is 8 tasks x 3 calls, against one empty bare repo made here.
        var root = Path.Combine(Path.GetTempPath(), "pilot-fakegithub-" + Guid.NewGuid().ToString("N"));
        try
        {
            var origin = Path.Combine(root, "origin.git");
            Directory.CreateDirectory(origin);
            (await new GitCli().RunAsync(["init", "-q", "--bare"], origin, CancellationToken.None)).Ok.ShouldBeTrue();
            var github = new FakeGitHub();
            github.Origins["acme/gadget"] = origin;

            await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
            {
                for (var k = 0; k < 3; k++)
                {
                    await github.CloneAsync("acme/gadget", Path.Combine(root, "clones", $"c{w}-{k}"), CancellationToken.None);
                }
            })));

            github.Cloned.Count.ShouldBe(Workers * 3);
            github.Cloned.Select(c => c.Path).Distinct().Count().ShouldBe(Workers * 3);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Git may still hold a pack file for a moment on Windows; a temp folder left behind harms nothing.
            }
        }
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public async Task ReviewsPostedInParallelAreEachRecordedExactlyOnceWithTheirOwnId()
    {
        var payload = Path.Combine(Path.GetTempPath(), "pilot-fakegithub-review-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(payload, """{"event":"COMMENT"}""", TestContext.Current.CancellationToken);
        try
        {
            var github = new FakeGitHub();
            var ids = new ConcurrentBag<long>();

            await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
            {
                for (var k = 0; k < CallsEach; k++)
                {
                    ids.Add((await github.PostReviewAsync("acme/gadget", w * CallsEach + k, payload, CancellationToken.None)).Id);
                }
            })));

            github.Posted.Count.ShouldBe(Calls);
            github.Posted.Select(p => p.Number).Distinct().Count().ShouldBe(Calls);
            ids.Distinct().Count().ShouldBe(Calls);
        }
        finally
        {
            File.Delete(payload);
        }
    }

    /// <summary>
    /// A seeded setting is only ever written by a test before the node runs, so a plain collection is safe there. Anything
    /// else a call writes into must be a concurrent collection, so a List, Dictionary or ImmutableArray property that comes
    /// back is a recording that loses calls (or throws) under parallel callers.
    /// </summary>
    private static readonly Dictionary<string, string> Seeded = new()
    {
        [nameof(FakeGitHub.OpenPrs)] = "seeded by a test: each repo's open PRs, only read by a call",
        [nameof(FakeGitHub.PrFiles)] = "seeded by a test: the files of each PR, only read by a call",
        [nameof(FakeGitHub.PrOf)] = "seeded by a test: one PR per branch, only read by a call",
        [nameof(FakeGitHub.SummaryOf)] = "seeded by a test: the checks summary of each PR, only read by a call",
        [nameof(FakeGitHub.Runs)] = "seeded by a test: the workflow runs of each commit, only read by a call",
        [nameof(FakeGitHub.MergeOf)] = "seeded by a test: the merge state of each PR, only read by a call",
        [nameof(FakeGitHub.Origins)] = "seeded by a test: where each repo clones from, only read by a call",
        [nameof(FakeGitHub.FailedRuns)] = "seeded by a test: the failed run of each PR, only read by a call",
        [nameof(FakeGitHub.CiRuns)] = "seeded by a test: the CI runs of each branch, only read by a call",
    };

    [Fact]
    [Trait("Category", "Invariant")]
    public void NoPlainListDictionaryOrImmutableArrayHoldsAnythingACallWritesInto()
    {
        static bool Plain(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() is var d
            && (d == typeof(List<>) || d == typeof(Dictionary<,>) || d == typeof(ImmutableArray<>));

        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance;
        var members = typeof(FakeGitHub).GetProperties(all).Select(p => (p.Name, p.PropertyType))
            .Concat(typeof(FakeGitHub).GetFields(all).Select(f => (f.Name, f.FieldType)))
            .Where(m => Plain(m.Item2))
            .Select(m => m.Name)
            .ToArray();

        members.Where(n => !Seeded.ContainsKey(n)).ShouldBeEmpty("a recording written from a call must be a ConcurrentQueue<T> or ConcurrentDictionary<,>; add a seeded setting to the allow list only with its why");
        Seeded.Keys.Where(n => !members.Contains(n)).ShouldBeEmpty("an allow-list entry that is no longer a plain collection must be dropped");
    }
}
