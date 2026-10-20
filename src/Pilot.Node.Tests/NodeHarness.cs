using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;

namespace Pilot.Node.Tests;

/// <summary>
/// A real node on a socket and data folder of its own, over the real socket: what the stream and lock tests share.
/// Adapted from tianwen's functional <c>NodeHarness</c>.
/// </summary>
internal sealed class NodeHarness : IAsyncDisposable
{
    private readonly NodeLock _held;

    private NodeHarness(WebApplication app, NodeLock held, string folder, NodeOptions options, FakeClaudeAgents agents, FakeSessionDelivery delivery)
    {
        Agents = agents;
        Delivery = delivery;
        App = app;
        _held = held;
        Folder = folder;
        Options = options;
        Client = new NodeClient(options.SocketPath);
    }

    public WebApplication App { get; }

    /// <summary>The temp folder holding the socket and the data folder, deleted on dispose.</summary>
    public string Folder { get; }

    public NodeOptions Options { get; }

    public string SocketPath => Options.SocketPath;

    public NodeClient Client { get; }

    public NodeState State => App.Services.GetRequiredService<NodeState>();

    /// <summary>The <c>claude agents</c> listing the node's session tracker reads: empty until a test sets it.</summary>
    public FakeClaudeAgents Agents { get; }

    /// <summary>The <c>claude</c> calls behind <c>say</c>: recorded, never run.</summary>
    public FakeSessionDelivery Delivery { get; }

    /// <summary>Leg workers: recorded, never started.</summary>
    public FakeWorkers Workers { get; private init; } = new();

    /// <summary>Leg checks: what they say is the test's.</summary>
    public FakeChecks Checks { get; private init; } = new();

    /// <summary>GitHub: what it says is the test's.</summary>
    public FakeGitHub GitHub { get; private init; } = new();

    /// <param name="retainedDeltas">How many deltas the node holds for a resuming client.</param>
    /// <param name="holdFirstListing">Keeps the session tracker's first poll waiting until the test releases it.</param>
    /// <param name="lanPort">Listens on the LAN too, on this port, announcing nothing: a client on loopback stands in for one on another machine.</param>
    /// <param name="watchtower">Keeps a watchtower, started through the fake workers; off by default, as it would count among them.</param>
    public static async Task<NodeHarness> StartAsync(CancellationToken cancellationToken, int retainedDeltas = NodeState.DefaultRetainedDeltas, bool holdFirstListing = false, bool watchtower = false, int? lanPort = null)
    {
        // Short: a socket path must fit in 107 bytes, and the temp folder on Windows is already long.
        var folder = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var options = new NodeOptions(Path.Combine(folder, NodeSocket.DefaultFileName), Path.Combine(folder, "data"), retainedDeltas, Watchtower: watchtower,
            Lan: lanPort is not null, LanPort: lanPort ?? NodeWire.DefaultLanPort, LanAnnounce: false);
        if (!NodeSocket.TryValidate(options.SocketPath, out var tooLong))
        {
            throw new InvalidOperationException(tooLong);
        }
        if (!NodeLock.TryAcquire(options.SocketPath, out var held, out var refusal))
        {
            throw new InvalidOperationException($"The test node could not take the lock on {options.SocketPath}", refusal);
        }

        var agents = holdFirstListing ? new FakeClaudeAgents().HoldFirstListing() : new FakeClaudeAgents();
        var delivery = new FakeSessionDelivery(agents);
        var workers = new FakeWorkers();
        var checks = new FakeChecks();
        var github = new FakeGitHub();
        try
        {
            var app = NodeApp.Build(options, held, builder =>
            {
                builder.Logging.ClearProviders();
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                // A test node never runs the claude CLI or reads this machine's sessions: it tracks what the test gives it.
                builder.Services.AddSingleton<IClaudeAgents>(agents);
                builder.Services.AddSingleton<ISessionDelivery>(delivery);
                // Nor starts a worker, runs a check or calls GitHub: git, on a repo of the test's own, is real.
                builder.Services.AddSingleton<Pilot.Node.Flights.IWorkerLauncher>(workers);
                builder.Services.AddSingleton<Pilot.Node.Flights.ICheckRunner>(checks);
                builder.Services.AddSingleton<Pilot.Node.Flights.IGitHub>(github);
                builder.Services.AddSingleton(new SessionTrackerOptions(
                    new ClaudeProfile(Path.Combine(folder, "claude")),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100),
                    SessionTrackerOptions.DefaultEndedRetention));
            });
            await app.StartAsync(cancellationToken);
            return new NodeHarness(app, held, folder, options, agents, delivery) { Workers = workers, Checks = checks, GitHub = github };
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        _held.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A socket file still closing or held by a scanner; the temp folder is the OS's to clear.
        }
    }
}
