using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Updates;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node counts its own network use: its HTTP exactly, its git and gh children as an estimate; an hourly line and a summary row say it.</summary>
public sealed class NodeTrafficTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Canned(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    [Fact]
    public async Task ACountingHandlerAddsAResponsesAndARequestsBytes()
    {
        var traffic = new NodeTraffic();
        var inner = new Canned(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(new string('x', 1000)) });
        using var invoker = new HttpMessageInvoker(new CountingHandler(traffic) { InnerHandler = inner });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.org/feed/index.json") { Content = new StringContent(new string('y', 300)) };
        using var response = await invoker.SendAsync(request, Ct);
        (await response.Content.ReadAsStringAsync(Ct)).Length.ShouldBe(1000);

        traffic.HttpInSinceStart.ShouldBeInRange(1000, 1300);
        traffic.HttpOutSinceStart.ShouldBeInRange(300, 600);
    }

    [Fact]
    public async Task AResponseWithoutALengthIsCountedByWhatArrived()
    {
        var traffic = new NodeTraffic();
        var inner = new Canned(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[5000])) });
        using var invoker = new HttpMessageInvoker(new CountingHandler(traffic) { InnerHandler = inner });

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), Ct);

        traffic.HttpInSinceStart.ShouldBeGreaterThanOrEqualTo(5000);
        traffic.HttpOutSinceStart.ShouldBeLessThan(300);
    }

    [Fact]
    public async Task AGitRunAndAGhRunAddToTheCountByTheJobObjectOrAsUnknown()
    {
        // Its own counter, so the git runs of the other test classes (they share NodeTraffic.Shared) cannot move the figure.
        var traffic = new NodeTraffic();

        (await new GitCli(traffic).RunAsync(["--version"], Path.GetTempPath(), Ct)).Ok.ShouldBeTrue();
        traffic.GitRunsSinceStart.ShouldBe(1);
        traffic.GhRunsSinceStart.ShouldBe(0);
        traffic.ChildBytesSinceStart.ShouldBe(0, "a local git run is a run with no bytes");

        // gh's helper starts through the same method; git stands in for the binary, which may not be installed.
        var start = new ProcessStartInfo("git", ["--version"]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        var (process, counted) = traffic.Start("gh", start);
        using (process)
        using (counted)
        {
            await process.StandardOutput.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
        }

        traffic.GhRunsSinceStart.ShouldBe(1);
        traffic.GitRunsSinceStart.ShouldBe(1);
        if (NodeTraffic.CanMeasureChildren)
        {
            traffic.ChildBytesSinceStart.ShouldBeGreaterThan(0);
        }
        else
        {
            traffic.ChildBytesSinceStart.ShouldBe(0);
        }
    }

    [Theory]
    [InlineData("fetch --quiet origin", true)]
    [InlineData("-C some/dir push origin HEAD", true)]
    [InlineData("-c user.name=x pull --ff-only", true)]
    [InlineData("ls-remote origin", true)]
    [InlineData("lfs pull", true)]
    [InlineData("remote update", true)]
    [InlineData("clone https://example.org/r.git", true)]
    [InlineData("status --porcelain", false)]
    [InlineData("log -n 3", false)]
    [InlineData("remote get-url origin", false)]
    [InlineData("--version", false)]
    public void OnlyAGitRunThatCanUseTheNetworkAddsBytes(string command, bool expected) =>
        GitCli.UsesNetwork(command.Split(' ')).ShouldBe(expected);

    [Fact]
    public async Task ARunThroughTheUpdateFeedsGhIsCounted()
    {
        var traffic = new NodeTraffic();
        // git stands in for gh: "git api ..." fails, which the feed answers with nothing, and the run is counted all the same.
        var feed = new GhReleaseFeed("SharpAstro/pilot", "git", traffic: traffic);

        (await feed.LatestAsync(Ct)).ShouldBeNull();

        traffic.GhRunsSinceStart.ShouldBe(1);
        traffic.GitRunsSinceStart.ShouldBe(0);
        if (NodeTraffic.CanMeasureChildren)
        {
            traffic.ChildBytesSinceStart.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void ANodeThatJustStartedHasNoLastHourRowUntilAHourItRanInHasEnded()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 30, 10, 20, 0, TimeSpan.Zero));
        var traffic = new NodeTraffic { Time = clock };
        traffic.AddHttp(100, 10);

        traffic.LastHourRow().ShouldBeNull();
        clock.Now = new DateTimeOffset(2026, 10, 30, 10, 59, 0, TimeSpan.Zero);
        traffic.LastHourRow().ShouldBeNull();

        clock.Now = new DateTimeOffset(2026, 10, 30, 11, 0, 1, TimeSpan.Zero);
        var row = traffic.LastHourRow().ShouldNotBeNull();
        row.Hour.ShouldBe(new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero));
        row.HttpIn.ShouldBe(100);
    }

    [Fact]
    public void TheHourlyRollWritesOneLineWithTheHoursInAndOut()
    {
        var folder = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var clock = new Clock(new DateTimeOffset(2026, 10, 30, 9, 20, 0, TimeSpan.Zero));
            var traffic = new NodeTraffic { Time = clock };
            var log = new RollingLog("node", folder: folder);
            traffic.AddHttp(received: 12_000, sent: 3_000);
            traffic.AddChild("git", NodeTraffic.CanMeasureChildren ? 7_000 : null);
            traffic.AddChild("gh", NodeTraffic.CanMeasureChildren ? 1_000 : null);

            clock.Now = new DateTimeOffset(2026, 10, 30, 10, 0, 5, TimeSpan.Zero);
            traffic.Roll(line => log.Write(line)).ShouldBeTrue();
            traffic.Roll(line => log.Write(line)).ShouldBeFalse("once per hour");

            var lines = File.ReadAllLines(Path.Combine(folder, "node.log"));
            lines.Length.ShouldBe(1);
            lines[0].ShouldContain("traffic 2026-10-30 09:00Z");
            lines[0].ShouldContain("http in 12000 bytes, out 3000 bytes");
            lines[0].ShouldContain("git runs 1, gh runs 1");
            lines[0].ShouldContain(NodeTraffic.CanMeasureChildren ? "8000 bytes (estimate, upper bound)" : "unknown");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TheLastHoursFigureReachesTheNodesSummaryRow()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 30, 9, 20, 0, TimeSpan.Zero));
        var traffic = new NodeTraffic { Time = clock };
        traffic.AddHttp(received: 4_000_000, sent: 1_000_000);
        traffic.AddChild("git", NodeTraffic.CanMeasureChildren ? 9_000_000 : null);
        clock.Now = new DateTimeOffset(2026, 10, 30, 10, 5, 0, TimeSpan.Zero);
        await using var node = await NodeHarness.StartAsync(Ct, traffic: traffic);

        var info = await node.Client.GetNodeAsync(Ct);

        info.Traffic.ShouldNotBeNull();
        info.Traffic.Hour.ShouldBe(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        info.Traffic.HttpIn.ShouldBe(4_000_000);
        info.Traffic.HttpOut.ShouldBe(1_000_000);
        info.Traffic.GitRuns.ShouldBe(1);
        info.Traffic.ChildBytes.ShouldBe(NodeTraffic.CanMeasureChildren ? 9_000_000 : null);
    }
}
