using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>The node's host: Kestrel on the socket and nothing else, and the three routes of the wire.</summary>
public static class NodeApp
{
    /// <summary>
    /// Builds the node for the socket <paramref name="held"/> admits it to. The caller runs it, and keeps the lock until
    /// it has stopped.
    /// </summary>
    /// <param name="configure">Registers services last, over the node's own (a test's journal, its logging).</param>
    public static WebApplication Build(NodeOptions options, NodeLock held, Action<WebApplicationBuilder>? configure = null)
    {
        Directory.CreateDirectory(options.DataFolder);

        // No args: the command line is the node's own (NodeOptions), not configuration for the host to read.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenOnNodeSocket(held));
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, PilotJsonContext.Default));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<INodeJournal, InMemoryJournal>();
        builder.Services.AddSingleton(services => new NodeState(
            services.GetRequiredService<INodeJournal>(), services.GetRequiredService<TimeProvider>(), options.RetainedDeltas));
        builder.Services.AddSingleton<EventHub>();
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseWebSockets();
        app.RestrictNodeSocketToItsOwner(held);
        MapRoutes(app);
        return app;
    }

    private static void MapRoutes(WebApplication app)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var version = typeof(NodeApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        app.MapGet(NodeWire.NodeRoute, (NodeOptions options, NodeState state) => Results.Json(
            ResponseEnvelope<NodeInfo>.Ok(new NodeInfo
            {
                WireVersion = NodeWire.Version,
                Version = version,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                StartedAt = startedAt,
                DataFolder = options.DataFolder,
                SocketPath = options.SocketPath,
                Stream = state.Stream,
            }),
            PilotJsonContext.Default.ResponseEnvelopeNodeInfo));

        app.MapGet(NodeWire.EventsRoute, async (HttpContext context, EventHub hub, IHostApplicationLifetime lifetime) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("The event stream is a WebSocket");
                return;
            }

            long? since = null;
            if (context.Request.Query["since"] is { Count: > 0 } sinceValue)
            {
                if (!long.TryParse(sinceValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("since is the number of the last event applied");
                    return;
                }
                since = n;
            }
            var stream = context.Request.Query["stream"].ToString();

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            await hub.ServeAsync(socket, since, stream is { Length: > 0 } ? stream : null, stopping.Token);
        });

        // Read by hand rather than bound, so a body that is not a command gets the envelope like any other refusal.
        app.MapPost(NodeWire.CommandsRoute, async (HttpContext context, NodeState state) =>
        {
            CommandRequest? command;
            try
            {
                command = await JsonSerializer.DeserializeAsync(context.Request.Body, PilotJsonContext.Default.CommandRequest, context.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Refuse($"The body is not a command: {ex.Message}");
            }

            if (command is null || string.IsNullOrWhiteSpace(command.Verb) || string.IsNullOrWhiteSpace(command.Target) || string.IsNullOrWhiteSpace(command.Who))
            {
                return Refuse("A command needs a verb, a target and who sends it");
            }

            var result = state.Execute(command);
            return result.Ok
                ? Results.Json(ResponseEnvelope<CommandResult>.Ok(result), PilotJsonContext.Default.ResponseEnvelopeCommandResult)
                : Results.Json(ResponseEnvelope<CommandResult>.Fail(result.Error ?? "refused", 400, result), PilotJsonContext.Default.ResponseEnvelopeCommandResult, statusCode: 400);
        });
    }

    private static IResult Refuse(string error) =>
        Results.Json(ResponseEnvelope<CommandResult>.Fail(error), PilotJsonContext.Default.ResponseEnvelopeCommandResult, statusCode: 400);
}
