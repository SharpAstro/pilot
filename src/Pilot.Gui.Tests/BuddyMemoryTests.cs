using System.Text.Json;
using Pilot.Buddy;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1381 section 11 on fakes (no window, no process read, no GPU): the shape of the <c>buddy_memory</c> report, that the MCP tool relays it, and that the
/// cat's mirror keeps only what the cat shows.
/// </summary>
public sealed class BuddyMemoryTests
{
    private sealed class Relayed(BuddyRelay relay) : IBuddyLink
    {
        public Task<BuddyAnswer> CallAsync(BuddyCall call, CancellationToken cancellationToken) => Task.FromResult(relay.Handle(call));
    }

    private static BuddyMemory Fake(BuddyGpuFacts? gpu = null) => new(
        () => gpu ?? new BuddyGpuFacts(618, 831, null, false, 1024, 61, "LayeredWindow"),
        () => new BuddyMirrorCounts(3, 2, 1, 4, 1, 0, 0),
        () => (40_000_000, 90_000_000));

    [Fact]
    public void TheReportNamesEveryPart()
    {
        using var doc = JsonDocument.Parse(Fake().Report());
        var root = doc.RootElement;

        root.GetProperty("privateBytes").GetInt64().ShouldBe(40_000_000);
        root.GetProperty("workingSetBytes").GetInt64().ShouldBe(90_000_000);
        var heap = root.GetProperty("managedHeap");
        heap.GetProperty("heapSizeBytes").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        heap.GetProperty("committedBytes").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        heap.TryGetProperty("fragmentedBytes", out _).ShouldBeTrue();
        var gpu = root.GetProperty("gpu");
        gpu.GetProperty("fontAtlas").GetProperty("bytes").GetInt64().ShouldBe(1024L * 1024 + 512L * 512 * 4);
        gpu.GetProperty("fontAtlas").GetProperty("glyphsDrawn").GetInt32().ShouldBe(61);
        gpu.GetProperty("swapchain").GetProperty("width").GetInt32().ShouldBe(618);
        gpu.GetProperty("swapchain").GetProperty("height").GetInt32().ShouldBe(831);
        gpu.GetProperty("swapchain").GetProperty("bytesPerImage").GetInt64().ShouldBe(618L * 831 * 4);
        gpu.GetProperty("swapchain").GetProperty("images").ValueKind.ShouldBe(JsonValueKind.Null, "the renderer does not say how many images it made");
        gpu.GetProperty("depthBuffer").GetBoolean().ShouldBeFalse();
        var mirror = root.GetProperty("mirror");
        mirror.GetProperty("cards").GetInt32().ShouldBe(3);
        mirror.GetProperty("sessions").GetInt32().ShouldBe(2);
        mirror.GetProperty("jobs").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void WithNoWindowYetTheGpuIsNull()
    {
        var report = new BuddyMemory(() => null, () => new BuddyMirrorCounts(0, 0, 0, 0, 0, 0, 0), () => (1, 2)).Report();

        using var doc = JsonDocument.Parse(report);
        doc.RootElement.GetProperty("gpu").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheMcpToolRelaysTheReportAndWithoutOneRefuses()
    {
        var clock = TimeProvider.System;
        var model = new BuddyModel(clock);
        var relay = new BuddyRelay(model, new BuddyFeed(model, clock, () => new BuddySettings()), clock);
        var server = new BuddyMcpServer(new Relayed(relay));
        const string call = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"buddy_memory","arguments":{}}}""";

        using (var refused = JsonDocument.Parse((await server.HandleAsync(call, CancellationToken.None))!))
        {
            refused.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        }

        relay.MemoryReport = Fake().Report;
        using var answered = JsonDocument.Parse((await server.HandleAsync(call, CancellationToken.None))!);
        var result = answered.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        using var report = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        report.RootElement.GetProperty("mirror").GetProperty("cards").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task TheMcpServerListsTheTool()
    {
        var server = new BuddyMcpServer(new Relayed(new BuddyRelay(new BuddyModel(TimeProvider.System), new BuddyFeed(new BuddyModel(TimeProvider.System), TimeProvider.System, () => new BuddySettings()), TimeProvider.System)));

        var listed = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CancellationToken.None);

        listed.ShouldNotBeNull().ShouldContain("buddy_memory");
    }

    [Fact]
    public void ASlimMirrorKeepsWhatTheCatShowsAndReadsPastTheRest()
    {
        var slim = new NodeMirror { Slim = true };
        var full = new NodeMirror();
        var job = new JobRow { Id = "j1", Command = "dotnet test", Cwd = "/r", State = JobState.Running, LogPath = "/l", Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UnixEpoch };
        var snapshot = new SnapshotEvent { Stream = "s", Seq = 5, At = DateTimeOffset.UnixEpoch, Sessions = [], Notes = [new NodeNote { Id = "n1", At = DateTimeOffset.UnixEpoch, Who = "t", Text = "x" }], Jobs = [job] };
        slim.Apply(snapshot);
        full.Apply(snapshot);

        full.Jobs.Count.ShouldBe(1);
        slim.Jobs.Count.ShouldBe(0);
        slim.Notes.Count.ShouldBe(0);

        // A delta the cat does not show still moves the position, so the next one is accepted.
        slim.Apply(new JobAddedEvent { Seq = 6, At = DateTimeOffset.UnixEpoch, Job = job });
        slim.Seq.ShouldBe(6);
        slim.Jobs.Count.ShouldBe(0);
        slim.Apply(new JobChangedEvent { Seq = 7, At = DateTimeOffset.UnixEpoch, Job = job });
        slim.Seq.ShouldBe(7);
    }
}
