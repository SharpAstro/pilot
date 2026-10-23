using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A job on another node, selected on the Background tasks page, shows its log and follows it: the log is read from that
/// node (a harness node on TCP, reached through <see cref="FleetClients"/> as the window wires it) and its lines are in the
/// cells, a later line after the log grows, never left at "reading…".
/// </summary>
public sealed class RemoteJobLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobRow Job(string id) => new()
    {
        Id = id, Command = $"dotnet test {id}", Cwd = "/r/orbit", Owner = null, State = JobState.Running, LogPath = $"/data/jobs/{id}.log",
        Timeout = TimeSpan.FromHours(1), StartedAt = Live.At.AddMinutes(-11),
    };

    [Fact]
    public async Task AJobOnAnotherNodeShowsItsLogFromThatNodeAndFollowsItWhileItGrows()
    {
        using var node = new LogNode("j43", ["Restore complete"]);
        var le1 = new NodeMark(node.Endpoint, node.Endpoint, "LE1", Here: false);
        using var here = NodeClient.Remote("127.0.0.1:1");
        using var clients = new FleetClients(here, _ => false, e => NodeClient.Remote(e));
        using var vessel = new RgbaImageRenderer(1, 1);
        var jobs = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Jobs;
        var changed = new SemaphoreSlim(0);
        // As pilot-gui's Program wires it.
        jobs.LoadLog = async (node, id, ct) => (await clients.For(node, command: false)!.GetJobLogAsync(id, ct)) is { Success: true, Response: { } log }
            ? log
            : throw new InvalidOperationException($"the node did not send {id}'s log");
        jobs.Changed = () => changed.Release();
        var screen = new JobsScreen([JobLine.Build([Job("j43")], Live.At).Single() with { Node = le1 }], "test", Live.At);

        string Cells() => CellLayout.Describe(Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored));

        Cells().ShouldContain("reading…");
        (await changed.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        node.Requests.ShouldContain("/api/v1/jobs/j43/log");
        var first = Cells();
        first.ShouldContain("Restore complete");
        first.ShouldNotContain("reading…");

        node.Append("Passed Parser.NestedGroups");
        (await changed.WaitAsync(JobsView<RgbaImage>.Every + TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        var later = Cells();
        later.ShouldContain("Restore complete");
        later.ShouldContain("Passed Parser.NestedGroups");
    }

    /// <summary>A node on loopback that answers <c>GET /api/v1/jobs/{id}/log</c> with the lines it holds now.</summary>
    private sealed class LogNode : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly string _jobId;
        private readonly List<string> _lines;

        public LogNode(string jobId, string[] lines)
        {
            _jobId = jobId;
            _lines = [.. lines];
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = $"127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                    var path = context.Request.Url!.AbsolutePath;
                    JobLog log;
                    lock (_lines)
                    {
                        Requests.Add(path);
                        log = new JobLog { Lines = [.. _lines], Length = _lines.Sum(l => l.Length + 1) };
                    }
                    var found = path == NodeWire.JobLogRoute(_jobId);
                    context.Response.StatusCode = found ? 200 : 404;
                    var answer = found
                        ? ResponseEnvelope<JobLog>.Ok(log)
                        : ResponseEnvelope<JobLog>.Fail($"There is no job at {path}", 404);
                    await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(answer, PilotJsonContext.Default.ResponseEnvelopeJobLog));
                    context.Response.Close();
                }
            });
        }

        public string Endpoint { get; }

        /// <summary>The path of each request the node was sent.</summary>
        public List<string> Requests { get; } = [];

        public void Append(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
