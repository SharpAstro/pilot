using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>GitHub's answers to a merge, an enqueue and an auto-merge arm, sorted in one table (MergeAnswers) that GhCli acts on.</summary>
public sealed class MergeAnswerTests
{
    private const string QueueJson = """{"data":{"repository":{"pullRequest":{"id":"PR_1","isMergeQueueEnabled":true}}}}""";

    [Theory]
    [InlineData(0, "", "", MergeAnswerKind.Done)]
    [InlineData(1, "", "gh: Pull request is already in the queue", MergeAnswerKind.Done)]
    [InlineData(1, "", "GraphQL: Pull request is already in the queue (enqueuePullRequest)", MergeAnswerKind.Done)]
    [InlineData(1, "", "GraphQL: Auto-merge is already enabled for this pull request (enablePullRequestAutoMerge)", MergeAnswerKind.Done)]
    [InlineData(1, "", "X Pull request #5 was already merged", MergeAnswerKind.Done)]
    [InlineData(1, "", "GraphQL: Pull request is not in the merge queue (dequeuePullRequest)", MergeAnswerKind.Refused)]
    [InlineData(1, "", "GraphQL: Pull request is not currently mergeable by this actor (mergePullRequest)", MergeAnswerKind.Refused)]
    [InlineData(1, "", "GraphQL: Merging is not currently allowed (mergePullRequest)", MergeAnswerKind.Refused)]
    [InlineData(1, "", "gh: Pull Request is not mergeable", MergeAnswerKind.Conflicts)]
    [InlineData(1, "", "GraphQL: This branch can't be rebased (mergePullRequest)", MergeAnswerKind.NotRebaseable)]
    [InlineData(1, "", "GraphQL: Head branch was modified. Review and try the merge again. (mergePullRequest)", MergeAnswerKind.HeadMoved)]
    [InlineData(1, "", "GraphQL: Base branch was modified. Review and try the merge again. (mergePullRequest)", MergeAnswerKind.Behind)]
    [InlineData(1, "", "gh: HTTP 502: Bad Gateway (https://api.github.com/graphql)", MergeAnswerKind.Transient)]
    [InlineData(1, "", "HTTP 503: Service Unavailable", MergeAnswerKind.Transient)]
    [InlineData(1, "", "HTTP 504: Gateway Timeout", MergeAnswerKind.Transient)]
    [InlineData(-1, "", "gh timed out", MergeAnswerKind.Transient)]
    [InlineData(1, "", "GraphQL: Pull request has merge conflicts", MergeAnswerKind.Conflicts)]
    [InlineData(1, "", "gh: Required status check build is expected", MergeAnswerKind.Refused)]
    [InlineData(1, "", "", MergeAnswerKind.Refused)]
    public void EveryAnswerHasItsRow(int exit, string stdout, string stderr, MergeAnswerKind expected) =>
        MergeAnswers.Classify(exit, stdout, stderr).Kind.ShouldBe(expected);

    [Theory]
    [InlineData("GraphQL: Pull request is not in the merge queue (dequeuePullRequest)")]
    [InlineData("GraphQL: Pull request is not queued")]
    [InlineData("GraphQL: Pull request is not currently in a merge queue")]
    public void LeavingTheQueueWhenNotInItIsDoneForADequeueOnly(string said)
    {
        MergeAnswers.Classify(1, "", said, leavingQueue: true).Kind.ShouldBe(MergeAnswerKind.Done);
        MergeAnswers.Classify(1, "", said).Kind.ShouldNotBe(MergeAnswerKind.Done);
    }

    [Fact]
    public void AMergeAnswerSayingNotCurrentlyIsNoMerge() =>
        MergeAnswers.Classify(1, "", "GraphQL: Merging is not currently allowed for this pull request").Kind.ShouldNotBe(MergeAnswerKind.Done);

    [Fact]
    public void AGraphQlErrorOnExitZeroIsAnAnswerToo() =>
        MergeAnswers.Classify(0, """{"errors":[{"message":"Pull request is already queued"}]}""", "").Kind.ShouldBe(MergeAnswerKind.Done);

    [Fact]
    public void ALongMultiLineRefusalIsKeptWhole()
    {
        var said = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}: Required status check \"build ({i})\" is expected."));
        var answer = MergeAnswers.Classify(1, "", said + "\n");
        answer.Kind.ShouldBe(MergeAnswerKind.Refused);
        answer.Message.ShouldBe(said);
    }

    private sealed class Script
    {
        public Queue<(int Exit, string Out, string Err)> Answers { get; } = new();
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<TimeSpan> Waits { get; } = [];

        public GhCli Gh() => new()
        {
            Runner = (args, _, _) =>
            {
                Calls.Add(args);
                if (args.Contains("api") && args.Any(a => a.StartsWith("query=query", StringComparison.Ordinal) || a == "owner={owner}"))
                {
                    return Task.FromResult((0, QueueJson, ""));
                }
                return Task.FromResult(Answers.Dequeue());
            },
            Delay = (wait, _) =>
            {
                Waits.Add(wait);
                return Task.CompletedTask;
            },
        };

        public int Merges => Calls.Count(c => !c.Contains("owner={owner}"));
    }

    [Fact]
    public async Task AQueueAnswerAlreadyQueuedIsDone()
    {
        var script = new Script();
        script.Answers.Enqueue((1, "", "GraphQL: Pull request is already in the queue (enqueuePullRequest)"));
        (await script.Gh().MergeAsync("repo", 7, "abc", "squash", CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task ATransientAnswerIsRetriedWithBackoffThenSucceeds()
    {
        var script = new Script();
        script.Answers.Enqueue((1, "", "HTTP 502: Bad Gateway"));
        script.Answers.Enqueue((-1, "", "gh timed out"));
        script.Answers.Enqueue((0, "{}", ""));
        await script.Gh().MergeAsync("repo", 7, "abc", "squash", CancellationToken.None);
        script.Merges.ShouldBe(3);
        script.Waits.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);
    }

    [Fact]
    public async Task ATransientAnswerThatNeverClearsIsRefusedAfterThreeRetries()
    {
        var script = new Script();
        for (var i = 0; i < 4; i++)
        {
            script.Answers.Enqueue((1, "", "HTTP 503: Service Unavailable"));
        }
        var refused = await Should.ThrowAsync<MergeRefusedException>(() => script.Gh().MergeAsync("repo", 7, "abc", "squash", CancellationToken.None));
        refused.Kind.ShouldBe(MergeAnswerKind.Transient);
        script.Merges.ShouldBe(4);
        script.Waits.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ARefusalCarriesItsKindAndGitHubsWholeMessage()
    {
        var script = new Script();
        var said = "GraphQL: This branch can't be rebased (mergePullRequest)\nsecond line\nthird line";
        script.Answers.Enqueue((1, "", said));
        var refused = await Should.ThrowAsync<MergeRefusedException>(() => script.Gh().MergeAsync("repo", 7, "abc", "rebase", CancellationToken.None));
        refused.Kind.ShouldBe(MergeAnswerKind.NotRebaseable);
        refused.Said.ShouldBe(said);
        refused.Message.ShouldContain(said);
        script.Waits.ShouldBeEmpty();
    }

    [Fact]
    public async Task AllowedMergeMethodsAreReadFromTheRepoAndKept()
    {
        var script = new Script();
        script.Answers.Enqueue((0, """{"allow_squash_merge":true,"allow_rebase_merge":false,"allow_merge_commit":false}""", ""));
        var gh = script.Gh();
        var methods = await gh.AllowedMergeMethodsAsync("repo", CancellationToken.None);
        methods.ShouldBe(new AllowedMergeMethods(true, false, false));
        (await gh.AllowedMergeMethodsAsync("repo", CancellationToken.None)).ShouldBe(methods);
        script.Calls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"allow_squash_merge":false,"allow_rebase_merge":true,"allow_merge_commit":true}""", false, true, true)]
    [InlineData("""{"name":"x"}""", true, true, true)]
    public void TheMethodFlagsParse(string json, bool squash, bool rebase, bool merge) =>
        AllowedMergeMethods.Parse(json).ShouldBe(new AllowedMergeMethods(squash, rebase, merge));

    [Fact]
    public void NoJsonIsNoAnswer() => AllowedMergeMethods.Parse("not json").ShouldBeNull();
}
