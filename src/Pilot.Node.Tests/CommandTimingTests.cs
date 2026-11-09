using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node times every command (wait and run), keeps an hour of them per verb with p50 and p95, and logs slow process starts (PIL794).</summary>
public sealed class CommandTimingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommandRequest Ring(string title, string? requestId = null) =>
        new() { Verb = Verbs.Raise, Target = "node", Args = ["ring", title], Who = "tests", RequestId = requestId };

    private static string NodeLogText(NodeHarness node)
    {
        var logs = Path.Combine(node.Options.DataFolder, "logs");
        if (!Directory.Exists(logs))
        {
            return "";
        }
        return string.Concat(Directory.EnumerateFiles(logs, "node*").Select(f =>
        {
            using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }

    [Fact]
    public async Task ACommandRunThroughTheNodeLeavesOneTimingLineAndARowOnTheInfo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        (await node.Client.SendAsync(Ring("plain"), Ct)).Success.ShouldBeTrue();
        (await node.Client.SendAsync(Ring("with id", Guid.NewGuid().ToString("N")), Ct)).Success.ShouldBeTrue();

        var lines = NodeLogText(node).Split('\n').Where(l => l.Contains("timing: command raise node by tests waited ", StringComparison.Ordinal)).ToArray();
        lines.Length.ShouldBe(2);
        lines[0].ShouldMatch(@"timing: command raise node by tests waited \d+ ms, ran \d+ ms");
        var row = (await node.Client.GetNodeAsync(Ct)).CommandTimings.ShouldNotBeNull().Single(r => r.Verb == Verbs.Raise);
        row.Count.ShouldBe(2);
        row.P95Ms.ShouldBeGreaterThanOrEqualTo(row.P50Ms);
    }

    [Fact]
    public void TheRowsHavePerVerbCountP50AndP95AndEntriesOlderThanAnHourDrop()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var log = new StringWriter();
        var timings = new CommandTimings(clock, log);
        timings.Record("say", "session/a", "cli", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));

        clock.Now = clock.Now.AddMinutes(30);
        for (var i = 1; i <= 20; i++)
        {
            timings.Record("say", "session/a", "cli", TimeSpan.FromMilliseconds(i * 5), TimeSpan.FromMilliseconds(i * 5));
        }
        timings.Record("merge", "flight/x", "gui", TimeSpan.Zero, TimeSpan.FromSeconds(2));

        log.ToString().ShouldContain("timing: command merge flight/x by gui waited 0 ms, ran 2000 ms");

        clock.Now = clock.Now.AddMinutes(31);
        var rows = timings.Rows().ShouldNotBeNull();
        rows.Select(r => r.Verb).ShouldBe(["merge", "say"]);
        rows.Single(r => r.Verb == "say").ShouldBe(new CommandTimingRow { Verb = "say", Count = 20, P50Ms = 100, P95Ms = 190 });

        clock.Now = clock.Now.AddMinutes(60);
        timings.Rows().ShouldBeNull();
    }

    [Fact]
    public void StatusPrintsTheCommandTimingsLine()
    {
        CommandTimingRow[] rows = [new() { Verb = "say", Count = 40, P50Ms = 300, P95Ms = 1200 }];
        var snapshot = new SnapshotEvent { Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [] };

        PilotCommand.Status(snapshot, commandTimings: rows).ShouldContain("commands  say: p50 300 ms, p95 1.2 s (40)");
        StatusMarkdown.Build(snapshot, commandTimings: rows).ShouldContain("**commands** say: p50 300 ms, p95 1.2 s (40)");
        PilotCommand.Status(snapshot, commandTimings: []).ShouldContain("commands  none in the last hour");
    }

    [Fact]
    public void TheRowsSurviveTheNodeInfoJsonRoundTrip()
    {
        var info = new NodeInfo
        {
            WireVersion = NodeWire.Version, Version = "1", MachineName = "m", ProcessId = 1, StartedAt = DateTimeOffset.UnixEpoch, DataFolder = "d", SocketPath = "s", Stream = "x",
            CommandTimings = [new CommandTimingRow { Verb = "say", Count = 1, P50Ms = 5, P95Ms = 5 }],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(info, PilotJsonContext.Default.NodeInfo);
        System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.NodeInfo)!.CommandTimings.ShouldBe(info.CommandTimings);
    }

    [Fact]
    public async Task AStartOverASecondIsLoggedAndAFastOneIsNot()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var log = new StringWriter();
        var wait = TimeSpan.Zero;
        // The seam hands back this very process, so nothing is started; the clock moves by how long the start "took".
        var traffic = new NodeTraffic { Time = clock, Starter = _ => { clock.Now += wait; return Process.GetCurrentProcess(); } };
        traffic.Log = log;
        var start = new ProcessStartInfo("git");

        wait = TimeSpan.FromMilliseconds(200);
        (await traffic.StartProcessAsync("git", start, Ct)).Dispose();
        log.ToString().ShouldBeEmpty();

        wait = TimeSpan.FromMilliseconds(2500);
        (await traffic.StartProcessAsync("claude", start, Ct)).Dispose();
        log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ShouldHaveSingleItem().ShouldContain("timing: process start claude waited 2500 ms");

        // A claude start is not a git or gh run.
        traffic.GitRunsSinceStart.ShouldBe(0);
        traffic.GhRunsSinceStart.ShouldBe(0);
    }
}
