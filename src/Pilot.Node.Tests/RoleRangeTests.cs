using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A role is a range of model and effort pairs (#599): a default and a max, never effort max, with old settings read as the nearest range.</summary>
public sealed class RoleRangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pilot-range-" + Guid.NewGuid().ToString("N"));

    public RoleRangeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestDirs.DeleteTree(_root);

    private NodeSettings ReadOld(string json)
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        File.WriteAllText(file, json);
        return NodeSettings.Read(file);
    }

    [Theory]
    [InlineData("sonnet-high..opus-medium", "sonnet-high..opus-medium")]
    [InlineData("opus", "opus..opus")]
    [InlineData("sonnet-low", "sonnet-low..sonnet-low")]
    [InlineData("SONNET..Opus-XHigh", "sonnet..opus-xhigh")]
    public void ARangeParsesAndFormatsInOneSpelling(string text, string formatted)
    {
        RoleRange.TryParse([text], out var range, out var error).ShouldBeTrue(error);
        range!.ToString().ShouldBe(formatted);
    }

    [Fact]
    public void TwoValuesAreADefaultAndAMax()
    {
        RoleRange.TryParse(["sonnet-high", "opus-medium"], out var range, out _).ShouldBeTrue();
        (range!.Default.ToString(), range.Max.ToString()).ShouldBe(("sonnet-high", "opus-medium"));
    }

    [Theory]
    [InlineData("sonnet-max")]
    [InlineData("sonnet..opus-max")]
    [InlineData("opus-max..opus-max")]
    public void EffortMaxIsRefusedOnAnyModel(string text)
    {
        RoleRange.TryParse([text], out _, out var error).ShouldBeFalse();
        error.ShouldBe("never takes effort max");
        NodeSettings.With(new NodeSettings(), "worker-range", [text]).Error.ShouldBe("worker-range never takes effort max");
    }

    [Theory]
    [InlineData("opus-high..sonnet-high", "takes a max not below its default")]
    [InlineData("sonnet-high..sonnet-low", "takes a max not below its default")]
    [InlineData("haiku", null)]
    [InlineData("sonnet-huge", null)]
    public void AMaxBelowTheDefaultOrAnUnknownPairIsRefused(string text, string? error)
    {
        RoleRange.TryParse([text], out _, out var why).ShouldBeFalse();
        if (error is not null)
        {
            why.ShouldBe(error);
        }
        NodeSettings.With(new NodeSettings(), "reviewer-range", [text]).Error.ShouldNotBeNull().ShouldStartWith("reviewer-range takes");
    }

    [Fact]
    public void TheLineIsEveryPairInOrderWithNoEffortMax()
    {
        RoleRange.Line.ShouldBe(["sonnet-low", "sonnet-medium", "sonnet-high", "sonnet-xhigh", "opus-low", "opus-medium", "opus-high", "opus-xhigh"]);
    }

    [Fact]
    public void TheRoleKeysAreRangesAndTheOldKeysAreGone()
    {
        NodeSettings.Keys.Where(k => k.EndsWith("-range", StringComparison.Ordinal)).ShouldBe(["watchtower-range", "worker-range", "planner-range", "reviewer-range"]);
        NodeSettings.Keys.ShouldNotContain(k => k.EndsWith("-model", StringComparison.Ordinal) || k.EndsWith("-effort", StringComparison.Ordinal) || k.Contains("-max", StringComparison.Ordinal) || k.StartsWith("reviewer-deep", StringComparison.Ordinal));
        NodeSettings.With(new NodeSettings(), "worker-model", ["opus"]).Error.ShouldNotBeNull().ShouldStartWith("there is no setting worker-model");
    }

    [Fact]
    public void OldSettingsReadAsTheNearestRange()
    {
        var settings = ReadOld("""
            {
              "worker-model": "opus", "worker-effort": "high", "worker-model-max": "opus", "worker-effort-max": "max",
              "reviewer-model": "sonnet", "reviewer-effort": "medium", "reviewer-deep-model": "opus", "reviewer-deep-effort": "max",
              "planner-effort": "max"
            }
            """);
        var models = new RoleModels(settings);

        models.Range(RoleModels.Worker).ToString().ShouldBe("opus-high..opus-xhigh");
        models.Range(RoleModels.Reviewer).ToString().ShouldBe("sonnet-medium..opus-xhigh");
        models.Range(RoleModels.Planner).ToString().ShouldBe("opus-xhigh..opus-xhigh");
        models.Range(RoleModels.Watchtower).ShouldBe(RoleModels.BuiltIn(RoleModels.Watchtower));
    }

    [Theory]
    [InlineData("""{ "worker-model-max": "sonnet" }""", "sonnet..sonnet-xhigh")]
    [InlineData("""{ "worker-effort-max": "high" }""", "sonnet..opus-high")]
    [InlineData("""{ "worker-effort-max": "max" }""", "sonnet..opus-xhigh")]
    [InlineData("""{ "worker-model": "opus", "worker-effort-max": "low" }""", "opus..opus-low")]
    public void EachOldWorkerKeyBecomesTheMaxOrTheDefault(string json, string range)
        => new RoleModels(ReadOld(json)).Range(RoleModels.Worker).ToString().ShouldBe(range);

    [Fact]
    public void AnOldDeepReviewKeyBecomesTheReviewersMax()
    {
        new RoleModels(ReadOld("""{ "reviewer-deep-model": "opus" }""")).Range(RoleModels.Reviewer).Max.ToString().ShouldBe("opus-high");
        new RoleModels(ReadOld("""{ "reviewer-deep-effort": "xhigh" }""")).Range(RoleModels.Reviewer).Max.ToString().ShouldBe("opus-xhigh");
    }

    [Fact]
    public void AMigratedFileIsWrittenWithRangesOnly()
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        ReadOld("""{ "worker-model": "opus", "worker-effort-max": "max" }""").Write(file);

        var text = File.ReadAllText(file);
        text.ShouldContain("worker-range");
        text.ShouldNotContain("worker-model");
        text.ShouldNotContain("worker-effort-max");
        new RoleModels(NodeSettings.Read(file)).Range(RoleModels.Worker).ToString().ShouldBe("opus..opus-xhigh");
    }

    [Fact]
    public void TheLadderAndADeepReviewStopAtTheMaxAndNeverStepToEffortMax()
    {
        var worker = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { ["worker-range"] = "sonnet-low..opus-medium" } });
        worker.WorkerFor(null, null, 1).ShouldBe(("sonnet", "low"));
        worker.WorkerFor(null, null, 3).ShouldBe(("opus", "low"));
        worker.WorkerFor(null, null, 4).ShouldBe(("opus", "medium"));
        worker.WorkerFor(null, null, 9).ShouldBe(("opus", "medium"));

        var reviewer = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { ["reviewer-range"] = "sonnet-high..opus-medium" } });
        (reviewer.ReviewerModel(ReviewDepth.Deep), reviewer.ReviewerEffort(ReviewDepth.Deep)).ShouldBe(("opus", "medium"));

        foreach (var role in RoleModels.Roles)
        {
            RoleModels.BuiltIn(role).Max.Effort.ShouldNotBe("max");
        }
        for (var attempt = 1; attempt < 10; attempt++)
        {
            new RoleModels(null).WorkerFor(null, null, attempt).Effort.ShouldNotBe("max");
        }
    }

    [Fact]
    public async Task EveryDefaultRowShowsItsValueItsSourceAndWhyItIsWhatItIs()
    {
        await using var node = await NodeHarness.StartAsync(TestContext.Current.CancellationToken);

        var page = (await node.Client.GetSettingsAsync(TestContext.Current.CancellationToken)).Response.ShouldNotBeNull();

        foreach (var role in RoleModels.Roles)
        {
            var row = page.Settings.Single(s => s.Key == RoleModels.RangeKey(role));
            row.Value.ShouldBe(RoleModels.BuiltIn(role).ToString());
            row.Source.ShouldBe(SettingSources.BuiltIn);
            row.Detail.ShouldContain("Built in: " + RoleModels.BuiltIn(role));
            row.Detail.ShouldContain(RoleModels.BuiltInWhy(role));
            row.Detail.ShouldContain("effort max is never allowed");
            row.Choices.ShouldBe(RoleRange.Line);
        }
    }
}
