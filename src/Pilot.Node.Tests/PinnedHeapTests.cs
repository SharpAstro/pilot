using System.Diagnostics;
using System.Net.WebSockets;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Run alone: the pinned object heap is the whole process's, so another test's sockets would read as this one's.</summary>
[CollectionDefinition(nameof(PinnedHeapTests), DisableParallelization = true)]
public sealed class PinnedHeapCollection;

/// <summary>
/// What kept about 700 MB on the live node's pinned object heap (#794): the suspects driven against the in-memory node, the
/// pinned heap's live bytes measured after a full blocking collection before and after.
/// </summary>
/// <remarks>
/// The culprit was the event stream's snapshot: Node.Lib's queue sent it in one <c>SendAsync</c>, which wrote the whole
/// message into Kestrel's output pipe, whose blocks come from Kestrel's pinned block pool, and the pool keeps them. With
/// that queue (EventHub put back as it was, 2026-11-07, on DE1) <see cref="ClientsThatReadSlowlyStopOrGoAwayLeaveNoSnapshotPinned"/>
/// read "pinned live: before 8 MB, during 33 MB, after 33 MB; snapshot about 7 MB": one client's whole read left 8 MB
/// pinned, three more left 25 MB more, and none of it went when they disconnected. With <see cref="EventStreamQueue"/>'s
/// fragments it read "before 192 KB, during 398 KB, after 398 KB". Scaled to the live node's 25 MB snapshot and its GUI,
/// TUI and CLI clients reconnecting after each restart or overflow, that is the 737 MB. 200 git starts through GitCli
/// read "before 119 KB, after 119 KB": process pipes pin nothing that outlives them.
/// </remarks>
[Collection(nameof(PinnedHeapTests))]
public sealed class PinnedHeapTests
{
    /// <summary>
    /// How much the pinned heap's live bytes may grow over a test. Kestrel's pool keeps some 4 KB blocks for the connections
    /// it has served, and its sockets pin a few buffers of their own: a few MB at most, where one snapshot is several.
    /// </summary>
    private const long Bound = 4L << 20;

    /// <summary>Each session's question, so the snapshot weighs about <see cref="Sessions"/> times it.</summary>
    private const int QuestionChars = 8 * 1024;

    private const int Sessions = 1000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientsThatReadSlowlyStopOrGoAwayLeaveNoSnapshotPinned()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var question = new string('q', QuestionChars);
        for (var i = 0; i < Sessions; i++)
        {
            node.State.PutSession(Row($"{i:x8}", question));
        }
        // One whole read first, so whatever Kestrel and the sockets keep for any connection is in the baseline.
        await using (var first = await node.Client.OpenEventsAsync(since: null, stream: null, Ct))
        {
            (await first.ReadAsync(Ct)).ShouldBeOfType<SnapshotEvent>().Sessions.Length.ShouldBeGreaterThanOrEqualTo(Sessions);
        }
        var before = LivePinned();

        // Three at once, as clients reconnecting together after a node restart: one reads slowly, one stops reading, one goes
        // away in the middle of its snapshot.
        using var handler = NodeSocket.CreateHandler(node.SocketPath);
        using var slow = await ConnectAsync(handler);
        using var stuck = await ConnectAsync(handler);
        using var gone = await ConnectAsync(handler);
        var buffer = new byte[4096];
        await gone.ReceiveAsync(buffer, Ct);
        gone.Abort();
        for (var i = 0; i < 50; i++)
        {
            await slow.ReceiveAsync(buffer, Ct);
            await Task.Delay(10, Ct);
        }
        // Mid-snapshot: what the node has sent and not had read sits in its pipe and the sockets' buffers.
        var during = LivePinned();
        slow.Abort();
        stuck.Abort();
        var after = LivePinned();

        var grew = Math.Max(during, after) - before;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"pinned live: before {NodeHeapRow.Size(before)}, during {NodeHeapRow.Size(during)}, after {NodeHeapRow.Size(after)}; snapshot about {NodeHeapRow.Size((long)Sessions * QuestionChars)}");
        grew.ShouldBeLessThan(Bound, $"the pinned heap grew by {NodeHeapRow.Size(grew)} for three clients of a {NodeHeapRow.Size((long)Sessions * QuestionChars)} snapshot");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TwoHundredGitStartsLeaveNothingPinned()
    {
        var repo = Path.Combine(Path.GetTempPath(), "p2t", "pin" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        try
        {
            var git = new GitCli();
            (await git.RunAsync(["init", "-q"], repo, Ct)).Ok.ShouldBeTrue();
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "a\n", Ct);
            (await git.RunAsync(["status", "--porcelain"], repo, Ct)).Ok.ShouldBeTrue();
            var before = LivePinned();

            // Four at a time, as RepoWatch and the PR polls overlap; 200 in all, each reading its stdout and stderr to the end.
            var started = Stopwatch.StartNew();
            using var four = new SemaphoreSlim(4);
            var runs = Enumerable.Range(0, 200).Select(async _ =>
            {
                await four.WaitAsync(Ct);
                try
                {
                    return await git.RunAsync(["status", "--porcelain"], repo, Ct);
                }
                finally
                {
                    four.Release();
                }
            }).ToList();
            var results = await Task.WhenAll(runs);
            results.ShouldAllBe(r => r.Ok);
            var after = LivePinned();

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"200 git starts in {started.Elapsed.TotalSeconds:0.0} s; pinned live: before {NodeHeapRow.Size(before)}, after {NodeHeapRow.Size(after)}");
            (after - before).ShouldBeLessThan(Bound, $"the pinned heap grew by {NodeHeapRow.Size(after - before)} over 200 git starts");
        }
        finally
        {
            try
            {
                Directory.Delete(repo, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only pack file on Windows; the temp folder is the OS's to clear.
            }
        }
    }

    [Fact]
    public void TheHeapRowSaysWhatADumpUsedTo()
    {
        var row = NodeHeap.Read();
        row.HeapBytes.ShouldBeGreaterThan(0);
        row.CommittedBytes.ShouldBeGreaterThan(0);
        row.WorkingSetBytes.ShouldBeGreaterThan(0);

        var dump = new NodeHeapRow
        {
            At = DateTimeOffset.UnixEpoch,
            ServerGc = true,
            HeapBytes = 3_000_000_000,
            LohBytes = 1_299_227_607,
            LohFragmentationBytes = 477L << 20,
            PinnedBytes = 737L << 20,
            PinnedFragmentationBytes = 12L << 20,
            CommittedBytes = 2_600_000_000,
            WorkingSetBytes = 2_684_354_560,
        };
        dump.Line().ShouldBe("heap 2.8 GB · LOH 1.2 GB (477 MB free) · pinned 737 MB (12 MB free) · committed 2.4 GB · working set 2.5 GB · server GC");
        PilotCommand.Status(new SnapshotEvent { Seq = 0, At = DateTimeOffset.UnixEpoch, Stream = "s", Sessions = [], Notes = [] }, heap: dump)
            .ShouldContain("heap  heap 2.8 GB · LOH 1.2 GB (477 MB free) · pinned 737 MB");
        StatusMarkdown.Build(new SnapshotEvent { Seq = 0, At = DateTimeOffset.UnixEpoch, Stream = "s", Sessions = [], Notes = [] }, heap: dump)
            .ShouldContain("**heap** heap 2.8 GB");
    }

    [Fact]
    public void TheHourlyTrafficLineEndsWithTheHeap()
    {
        var time = new Clock { Now = new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.Zero) };
        var traffic = new NodeTraffic
        {
            Time = time,
            Heap = () => new NodeHeapRow { At = time.GetUtcNow(), PinnedBytes = 3L << 20, LohBytes = 5L << 20, HeapBytes = 40L << 20, CommittedBytes = 60L << 20, WorkingSetBytes = 90L << 20 },
        };
        time.Now = time.Now.AddHours(1);
        string? line = null;
        traffic.Roll(l => line = l).ShouldBeTrue();
        line.ShouldNotBeNull().ShouldEndWith("; heap 40 MB · LOH 5 MB (0 KB free) · pinned 3 MB (0 KB free) · committed 60 MB · working set 90 MB · workstation GC");
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void TheNodeRunsWorkstationConcurrentGc()
    {
        // The node's own runtimeconfig, copied beside the tests: the Web SDK's server GC is what the csproj turns off.
        var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pilot-node.runtimeconfig.json"));
        config.ShouldContain("\"System.GC.Server\": false");
        config.ShouldContain("\"System.GC.Concurrent\": true");
    }

    /// <summary>The pinned object heap's live bytes (size less free space) after full blocking collections.</summary>
    private static long LivePinned()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var pinned = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[NodeHeap.PinnedIndex];
        return pinned.SizeAfterBytes - pinned.FragmentationAfterBytes;
    }

    private static async Task<ClientWebSocket> ConnectAsync(HttpMessageHandler handler)
    {
        var socket = new ClientWebSocket();
        // A small receive buffer, so the client's own side holds little and the node's side holds what is unread.
        socket.Options.SetBuffer(4096, 4096);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        await socket.ConnectAsync(new Uri("ws://localhost" + NodeWire.EventsRoute), invoker, Ct);
        return socket;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SessionRow Row(string id, string question) => new SessionRow
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = "session " + id,
        Kind = SessionKind.Background,
        Cwd = "/work/" + id,
        Role = SessionRole.Worker,
        ContextTokens = 0,
        ContextWindow = 200_000,
        State = SessionState.Waiting,
        WaitingFor = question,
        SpendWeighted = 0,
        StartedAt = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero),
    };
}
