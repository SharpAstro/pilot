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

    private NodeHarness(WebApplication app, NodeLock held, string folder, NodeOptions options, FakeClaudeAgents agents)
    {
        Agents = agents;
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

    /// <param name="retainedDeltas">How many deltas the node holds for a resuming client.</param>
    public static async Task<NodeHarness> StartAsync(CancellationToken cancellationToken, int retainedDeltas = NodeState.DefaultRetainedDeltas)
    {
        // Short: a socket path must fit in 107 bytes, and the temp folder on Windows is already long.
        var folder = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var options = new NodeOptions(Path.Combine(folder, NodeSocket.DefaultFileName), Path.Combine(folder, "data"), retainedDeltas);
        if (!NodeSocket.TryValidate(options.SocketPath, out var tooLong))
        {
            throw new InvalidOperationException(tooLong);
        }
        if (!NodeLock.TryAcquire(options.SocketPath, out var held, out var refusal))
        {
            throw new InvalidOperationException($"The test node could not take the lock on {options.SocketPath}", refusal);
        }

        var agents = new FakeClaudeAgents();
        try
        {
            var app = NodeApp.Build(options, held, builder =>
            {
                builder.Logging.ClearProviders();
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                // A test node never runs the claude CLI or reads this machine's sessions: it tracks what the test gives it.
                builder.Services.AddSingleton<IClaudeAgents>(agents);
                builder.Services.AddSingleton(new SessionTrackerOptions(
                    new ClaudeProfile(Path.Combine(folder, "claude")),
                    Path.Combine(folder, "v1"),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100),
                    SessionTrackerOptions.DefaultEndedRetention));
            });
            await app.StartAsync(cancellationToken);
            return new NodeHarness(app, held, folder, options, agents);
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
        catch (IOException)
        {
            // A socket file still closing; the temp folder is the OS's to clear.
        }
    }
}
