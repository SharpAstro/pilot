using System.Globalization;
using System.Reflection;
using System.Text.Json;
using LAN.Lib;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

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
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenOnNodeSocket(held);
            if (options.Lan)
            {
                // Every address, as a LAN client may come by any of this machine's: what it may do there is NodeLan's.
                kestrel.ListenAnyIP(options.LanPort);
            }
        });
        // The host's own start and stop lines stay; a line per request does not, as every client polls and streams.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, PilotJsonContext.Default));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        // The store: the node's events as files in its data folder, read back on the next start (FileJournal).
        builder.Services.AddSingleton<INodeJournal>(_ => new FileJournal(Path.Combine(options.DataFolder, "store")));
        builder.Services.AddSingleton(services => new NodeState(
            services.GetRequiredService<INodeJournal>(), services.GetRequiredService<TimeProvider>(), options.RetainedDeltas));
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton<IClaudeAgents, ClaudeCliAgents>();
        var relayFolder = Path.Combine(options.DataFolder, "relay");
        builder.Services.AddSingleton(SessionTrackerOptions.FromEnvironment() with { HiddenFolders = [relayFolder] });
        builder.Services.AddSingleton<HookBook>();
        builder.Services.AddSingleton(services => new TranscriptBook(services.GetRequiredService<SessionTrackerOptions>().Profile));
        builder.Services.AddSingleton<IGit, GitCli>();
        builder.Services.AddSingleton(services => new Planes(services.GetRequiredService<IGit>()));
        builder.Services.AddSingleton<FlightCommander>();
        builder.Services.AddSingleton<RepoCatalog>();
        builder.Services.AddSingleton<IWorkerLauncher, ClaudeWorkerLauncher>();
        builder.Services.AddSingleton<ICheckRunner, ShellCheckRunner>();
        builder.Services.AddSingleton<IGitHub, GhCli>();
        builder.Services.AddSingleton<LegCommander>();
        builder.Services.AddSingleton<NodeCommands>();
        builder.Services.AddSingleton<NodeLan>();
        if (options.Lan && options.LanAnnounce)
        {
            builder.Services.AddLanDiscovery(lan =>
            {
                lan.ServiceName = NodeWire.LanService;
                lan.ServicePort = options.LanPort;
                lan.NodeName = $"{options.Machine} {Environment.MachineName}";
                lan.StableNodeIdPath = Path.Combine(options.DataFolder, "lan-node-id");
                lan.Properties["machine"] = options.Machine;
                lan.Properties["wire"] = NodeWire.Version.ToString(CultureInfo.InvariantCulture);
            });
        }
        builder.Services.AddSingleton(services =>
        {
            var legs = services.GetRequiredService<LegCommander>();
            var commands = services.GetRequiredService<NodeCommands>();
            return new LegWatch(services.GetRequiredService<NodeState>(), commands.RunAsync, services.GetRequiredService<TimeProvider>())
            {
                Pilot = Briefs.Run(legs.Cli, legs.Socket),
            };
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<LegWatch>());
        builder.Services.AddSingleton(services =>
        {
            var legs = services.GetRequiredService<LegCommander>();
            var commands = services.GetRequiredService<NodeCommands>();
            return new Watchtower(services.GetRequiredService<NodeState>(), services.GetRequiredService<IWorkerLauncher>(), commands.RunAsync,
                services.GetRequiredService<NodeOptions>(), services.GetRequiredService<TimeProvider>())
            {
                Pilot = Briefs.Run(legs.Cli, legs.Socket),
                Ready = () => services.GetRequiredService<SessionTracker>().FirstPoll,
            };
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<Watchtower>());
        // From the profile the tracker reads, so a test node, on a profile of its own, never reads the machine's.
        builder.Services.AddSingleton(services => new UsageReader(
            services.GetRequiredService<NodeState>(),
            Path.Combine(services.GetRequiredService<SessionTrackerOptions>().Profile.Root, "rate-limits.json"),
            services.GetRequiredService<TimeProvider>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<UsageReader>());
        builder.Services.AddSingleton(services =>
        {
            var commands = services.GetRequiredService<NodeCommands>();
            var nodeOptions = services.GetRequiredService<NodeOptions>();
            return new JobRunner(services.GetRequiredService<NodeState>(), nodeOptions, services.GetRequiredService<TimeProvider>())
            {
                Say = commands.RunAsync,
                MaxRunning = Math.Max(1, nodeOptions.MaxJobs),
                MinFreeMemory = (long)(Math.Max(0, nodeOptions.JobMemoryGigabytes) * (1L << 30)),
            };
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<JobRunner>());
        builder.Services.AddSingleton<PrChecks>();
        builder.Services.AddHostedService(services => services.GetRequiredService<PrChecks>());
        builder.Services.AddSingleton<Digest>();
        builder.Services.AddHostedService(services => services.GetRequiredService<Digest>());
        // Releases of pilot itself: by --update-policy, for an installed release only (NodeUpdater).
        builder.Services.AddSingleton<IReleaseFeed>(_ => options.ReleaseFeed is { } feed
            ? new FolderReleaseFeed(feed, NodeUpdater.Layout.Product)
            : new GhReleaseFeed(NodeUpdater.Repo));
        builder.Services.AddSingleton<NodeUpdater>();
        builder.Services.AddHostedService(services => services.GetRequiredService<NodeUpdater>());
        builder.Services.AddSingleton(services => new SessionTracker(
            services.GetRequiredService<NodeState>(),
            services.GetRequiredService<IClaudeAgents>(),
            services.GetRequiredService<SessionTrackerOptions>(),
            () => services.GetRequiredService<EventHub>().ClientCount > 0,
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<SessionTracker>>())
        {
            Hooks = services.GetRequiredService<HookBook>(),
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<SessionTracker>());
        // The relay's throwaway session runs in a folder of the node's own, as v1's runs in its state folder.
        builder.Services.AddSingleton<ISessionDelivery>(services => new ClaudeCliDelivery(
            services.GetRequiredService<IClaudeAgents>(),
            services.GetRequiredService<SessionTrackerOptions>().Profile,
            relayFolder));
        builder.Services.AddSingleton(services => new SessionMessenger(
            services.GetRequiredService<NodeState>(),
            services.GetRequiredService<SessionTracker>(),
            services.GetRequiredService<ISessionDelivery>(),
            Console.Error));
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseWebSockets();
        app.RestrictNodeSocketToItsOwner(held);
        app.UseNodeAccessGate(LanPolicy);
        MapRoutes(app);
        return app;
    }

    /// <summary>
    /// What a request from the LAN may do: watch (the node, the sessions, the event stream), ask for a grant and read its
    /// ticket; and, with a grant's token, send commands, which go out under the grant's label. Hooks and the list of
    /// grants are this machine's alone. A request on the socket passes untouched: its owner is this machine's person.
    /// Node.Lib's gate applies it.
    /// </summary>
    private static readonly NodeAccessPolicy LanPolicy = new NodeAccessPolicy
    {
        Classify = context =>
        {
            var path = context.Request.Path.Value ?? "";
            var get = HttpMethods.IsGet(context.Request.Method);
            var post = HttpMethods.IsPost(context.Request.Method);
            var watching = get && (path is NodeWire.NodeRoute or NodeWire.SessionsRoute or NodeWire.EventsRoute || path.StartsWith(NodeWire.GrantsRoute + "/", StringComparison.Ordinal));
            // The repos and their issues name this machine's folders, and a transcript is a session's words: read for a
            // grant, as a command is made with one.
            var granted = get && (path is NodeWire.ReposRoute or NodeWire.IssuesRoute
                || path.StartsWith(NodeWire.SessionsRoute + "/", StringComparison.Ordinal) && path.EndsWith("/transcript", StringComparison.Ordinal));
            return watching || (post && path == NodeWire.GrantsRoute) ? NodeRouteAccess.Open
                : (post && path == NodeWire.CommandsRoute) || granted ? NodeRouteAccess.Command
                : NodeRouteAccess.ThisMachineOnly;
        },
        MayCommand = async context =>
        {
            if (await context.RequestServices.GetRequiredService<NodeLan>().VerifyAsync(context) is not { } grant)
            {
                return false;
            }
            context.Items[NodeLan.GrantLabelKey] = grant.Label;
            return true;
        },
        RefuseAsync = (context, access) =>
        {
            var why = access is NodeRouteAccess.Command
                ? "a command from the LAN needs a grant: pilot connect <host:port> asks this node's person for one"
                : "this route is for this machine only";
            return Results.Json(ResponseEnvelope<CommandResult>.Fail(why, 403), PilotJsonContext.Default.ResponseEnvelopeCommandResult, statusCode: 403).ExecuteAsync(context);
        },
    };

    /// <summary>How long <c>GET /api/v1/sessions</c> waits for the tracker's first poll.</summary>
    private static readonly TimeSpan FirstPollBudget = TimeSpan.FromSeconds(10);

    private static void MapRoutes(WebApplication app)
    {
        // A hook's stdin, forwarded by `pilot-node hook`: the session it names takes the hook's state, and the tracker
        // looks again now. Always 204, so a hook never learns anything worth failing over.
        app.MapPost(NodeWire.HooksRoute, async (HttpContext context, HookBook hooks, SessionTracker tracker, TimeProvider time, ILogger<HookBook> logger) =>
        {
            try
            {
                using var hook = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (hooks.Apply(hook.RootElement, time.GetUtcNow()) is { } session)
                {
                    // Debug only: PreToolUse fires on every tool call. Logging__LogLevel__Pilot.Node=Debug shows them.
                    logger.LogDebug("hook {Event} for {Session}", ClaudeAgentsJson.Text(hook.RootElement, "hook_event_name"), session[..Math.Min(8, session.Length)]);
                    tracker.Kick();
                }
            }
            catch (JsonException)
            {
            }
            return Results.NoContent();
        });

        var startedAt = DateTimeOffset.UtcNow;
        var version = typeof(NodeApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        app.MapGet(NodeWire.NodeRoute, (NodeOptions options, NodeState state) => Results.Json(
            ResponseEnvelope<NodeInfo>.Ok(new NodeInfo
            {
                WireVersion = NodeWire.Version,
                Version = version,
                MachineName = Environment.MachineName,
                MachinePrefix = options.Machine,
                ProcessId = Environment.ProcessId,
                StartedAt = startedAt,
                DataFolder = options.DataFolder,
                SocketPath = options.SocketPath,
                Stream = state.Stream,
            }),
            PilotJsonContext.Default.ResponseEnvelopeNodeInfo));

        // Answers once the tracker's first poll is done, so a client asking right after starting the node gets every
        // session, not the first few a poll had put; a poll that never ends leaves it the rows there are after 10 s.
        app.MapGet(NodeWire.SessionsRoute, async (NodeState state, SessionTracker tracker, CancellationToken cancellationToken) =>
        {
            try
            {
                await tracker.FirstPoll.WaitAsync(FirstPollBudget, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
            return Results.Json(ResponseEnvelope<SessionRow[]>.Ok(state.Sessions()), PilotJsonContext.Default.ResponseEnvelopeSessionRowArray);
        });

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

        MapGrantRoutes(app);

        // A session's transcript, for the transcript page: by its row, read on from where the last read stopped.
        app.MapGet(NodeWire.TranscriptPattern, (string id, int? after, NodeState state, TranscriptBook transcripts) =>
            state.Sessions().FirstOrDefault(r => r.Id == id) is not { } row
                ? Results.Json(ResponseEnvelope<TranscriptPage>.Fail($"There is no session {id}", 404), PilotJsonContext.Default.ResponseEnvelopeTranscriptPage, statusCode: 404)
                : transcripts.Read(row.SessionId, after ?? -1) is { } page
                    ? Results.Json(ResponseEnvelope<TranscriptPage>.Ok(page), PilotJsonContext.Default.ResponseEnvelopeTranscriptPage)
                    : Results.Json(ResponseEnvelope<TranscriptPage>.Fail($"{row.Name} has no transcript yet", 404), PilotJsonContext.Default.ResponseEnvelopeTranscriptPage, statusCode: 404));

        // The new-flight wizard's reads: the repos a flight can be made in, and a repo's open issues (through gh).
        app.MapGet(NodeWire.ReposRoute, (RepoCatalog repos) =>
            Results.Json(ResponseEnvelope<RepoInfo[]>.Ok(repos.Repos()), PilotJsonContext.Default.ResponseEnvelopeRepoInfoArray));
        app.MapGet(NodeWire.IssuesRoute, async (string? path, IGitHub github, CancellationToken cancellationToken) =>
        {
            if (path is not { Length: > 0 } || !Directory.Exists(path))
            {
                return Results.Json(ResponseEnvelope<IssueInfo[]>.Fail("path names no checkout on this machine"), PilotJsonContext.Default.ResponseEnvelopeIssueInfoArray, statusCode: 400);
            }
            try
            {
                return Results.Json(ResponseEnvelope<IssueInfo[]>.Ok(await github.IssuesAsync(path, cancellationToken)), PilotJsonContext.Default.ResponseEnvelopeIssueInfoArray);
            }
            catch (FlightException ex)
            {
                return Results.Json(ResponseEnvelope<IssueInfo[]>.Fail(ex.Message, 502), PilotJsonContext.Default.ResponseEnvelopeIssueInfoArray, statusCode: 502);
            }
        });

        // Read by hand rather than bound, so a body that is not a command gets the envelope like any other refusal.
        app.MapPost(NodeWire.CommandsRoute, async (HttpContext context, NodeState state, NodeCommands commands, IHostApplicationLifetime lifetime) =>
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

            // From the LAN, under its grant: named by it, so it can pass for nobody here (the watchtower least), and never
            // handing out or taking away grants, which this machine's person does.
            if (context.Items[NodeLan.GrantLabelKey] is string grant)
            {
                if (command.Verb is Verbs.AnswerGrant or Verbs.RevokeGrant)
                {
                    return Refuse($"{command.Verb} is for this machine only");
                }
                command = command with { Who = $"{grant}/{command.Who}" };
            }

            // A say takes seconds and runs off the state's lock. It is not cut short when its client goes: a session
            // stopped and never resumed would be worse than a result nobody reads, which the stream carries anyway.
            // A flight verb runs git, off the lock too, and is not cut short either: a half-made worktree helps nobody.
            var result = await commands.RunAsync(command, lifetime.ApplicationStopping);
            return result.Ok
                ? Results.Json(ResponseEnvelope<CommandResult>.Ok(result), PilotJsonContext.Default.ResponseEnvelopeCommandResult)
                : Results.Json(ResponseEnvelope<CommandResult>.Fail(result.Error ?? "refused", 400, result), PilotJsonContext.Default.ResponseEnvelopeCommandResult, statusCode: 400);
        });
    }

    private static void MapGrantRoutes(WebApplication app)
    {
        app.MapPost(NodeWire.GrantsRoute, async (HttpContext context, NodeLan lan) =>
        {
            GrantRequest? ask;
            try
            {
                ask = await JsonSerializer.DeserializeAsync(context.Request.Body, PilotJsonContext.Default.GrantRequest, context.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(ResponseEnvelope<GrantTicket>.Fail($"The body is not an ask: {ex.Message}"), PilotJsonContext.Default.ResponseEnvelopeGrantTicket, statusCode: 400);
            }
            if (ask is not { Label.Length: > 0 and <= 60 })
            {
                return Results.Json(ResponseEnvelope<GrantTicket>.Fail("An ask needs a label of 1 to 60 characters"), PilotJsonContext.Default.ResponseEnvelopeGrantTicket, statusCode: 400);
            }
            return lan.Ask(ask.Label) is { } ticket
                ? Results.Json(ResponseEnvelope<GrantTicket>.Ok(ticket), PilotJsonContext.Default.ResponseEnvelopeGrantTicket)
                : Results.Json(ResponseEnvelope<GrantTicket>.Fail("Another ask is waiting for this node's person; try again shortly", 409), PilotJsonContext.Default.ResponseEnvelopeGrantTicket, statusCode: 409);
        });
        app.MapGet(NodeWire.GrantsRoute + "/{id}", (string id, NodeLan lan) =>
            Results.Json(ResponseEnvelope<GrantTicket>.Ok(lan.Ticket(id)), PilotJsonContext.Default.ResponseEnvelopeGrantTicket));
        app.MapGet(NodeWire.GrantsRoute, async (NodeLan lan, CancellationToken cancellationToken) =>
            Results.Json(ResponseEnvelope<GrantInfo[]>.Ok(await lan.GrantsAsync(cancellationToken)), PilotJsonContext.Default.ResponseEnvelopeGrantInfoArray));
    }

    private static IResult Refuse(string error) =>
        Results.Json(ResponseEnvelope<CommandResult>.Fail(error), PilotJsonContext.Default.ResponseEnvelopeCommandResult, statusCode: 400);
}
