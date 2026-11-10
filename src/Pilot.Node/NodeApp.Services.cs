using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>The node log, and what writes to it: no console provider, stderr goes to the same file.</summary>
    private static LogWriter AddLogging(WebApplicationBuilder builder, NodeOptions options)
    {
        // The host's own start and stop lines stay; a line per request does not, as every client polls and streams.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // The node writes to logs/node.log and nowhere else (pilot #416): no console provider, and what was written to stderr
        // goes to the same file through LogWriter. Stderr is left for a catastrophic error the log cannot take.
        builder.Logging.ClearProviders();
        var log = NodeLog(options);
        var logWriter = new LogWriter(log);
        builder.Logging.AddProvider(new FileLoggerProvider(log));
        DetachedShell.Error = logWriter;
        NodeTraffic.Shared.Log = logWriter;
        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton(logWriter);
        // First, so its start line leads and its stop line (hosts stop in reverse) closes the log of this run.
        builder.Services.AddHostedService(_ => new NodeLifecycleLog(options, log));
        // A reboot made ready (#916): maintenance mode, every live session told to save its state, the node collecting the files.
        builder.Services.AddSingleton(services =>
        {
            var messenger = services.GetRequiredService<SessionMessenger>();
            return new RebootPrep(services.GetRequiredService<NodeState>(), services.GetRequiredService<TimeProvider>(), RebootPrep.FolderIn(options.DataFolder))
            {
                Say = async (id, text, ct) => (await messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = "node" }, ct)).Ok,
                End = async (id, why, ct) => await messenger.EndAsync(new CommandRequest { Verb = Verbs.EndSession, Target = Verbs.SessionTarget(id), Args = [why], Who = Flights.LegWatch.Who }, ct),
                SetMaintenance = on => services.GetRequiredService<NodeSettingsCommands>().Set(new CommandRequest
                    { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.MaintenanceKey, on ? "on" : "off"], Who = "node" }),
            };
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<RebootPrep>());
        return logWriter;
    }

    /// <summary>The options, the clock, the node's traffic count and the JSON the host speaks.</summary>
    private static void AddCore(WebApplicationBuilder builder, NodeOptions options)
    {
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, PilotJsonContext.Default));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(NodeTraffic.Shared);
        // What GET /node reads the heap with: the live figures, unless a test host registers a fixed reading after this (a live
        // working set moves between any two calls, so two `pilot status` outputs of one node would never be equal).
        builder.Services.AddSingleton<Func<NodeHeapRow>>(_ => () => NodeHeap.Read());
        builder.Services.AddHostedService(services => new TrafficLog(services.GetRequiredService<NodeTraffic>(), services.GetRequiredService<RollingLog>(), services.GetRequiredService<TimeProvider>()));
    }

    /// <summary>The store (the journal, the node's state) and what shows it: the page views and the event hub.</summary>
    private static void AddState(WebApplicationBuilder builder, NodeOptions options, LogWriter logWriter)
    {
        // The store: the node's events as files in its data folder, read back on the next start (FileJournal).
        builder.Services.AddSingleton<INodeJournal>(_ => new FileJournal(Path.Combine(options.DataFolder, "store")));
        // The state is built on the store here, and a store that cannot be read is set aside and started again from a backup (StoreRecovery).
        // A store from a newer node still throws StoreTooNewException from here, which NodeProgram turns into its refusal.
        builder.Services.AddSingleton(services =>
        {
            var time = services.GetRequiredService<TimeProvider>();
            var opened = StoreRecovery.Open(
                services.GetRequiredService<INodeJournal>(),
                journal =>
                {
                    var state = new NodeState(journal, time, options.RetainedDeltas)
                    {
                        Error = logWriter,
                        TokensByFlight = () => services.GetRequiredService<SpendLedger>().TokensByFlight(),
                    };
                    // A card about to ring goes to the brain first (PIL855); resolved at the first ring, as the brain needs the watchtower, which needs this state.
                    state.RingJudge = new RingJudge(state, () => services.GetRequiredService<IBrain>()) { Log = line => logWriter.WriteLine(line) };
                    return state;
                },
                time);
            return opened;
        });
        // The line in node.log and node-start.log and the card, when the start was from a backup: said when the host starts, with every service
        // there to resolve (the card goes to the brain before it rings, which needs the watchtower, which needs the state built just above).
        builder.Services.AddHostedService(services => new StoreRecoveryReport(services.GetRequiredService<StoreRecovery>(), logWriter,
            line => NodeProgram.AppendStartLog(options, $"pilot-node {FileJournal.RunningVersion} {line}")));
        builder.Services.AddSingleton(services => services.GetRequiredService<StoreRecovery>().State);
        builder.Services.AddSingleton<PageViews>();
        builder.Services.AddSingleton<EventHub>();
    }

    /// <summary>What the node knows of Claude Code: its agents, the hooks and the transcripts.</summary>
    private static void AddSessionBooks(WebApplicationBuilder builder, NodeOptions options)
    {
        builder.Services.AddSingleton<IClaudeAgents, ClaudeCliAgents>();
        var relayFolder = RelayFolder(options);
        builder.Services.AddSingleton(SessionTrackerOptions.FromEnvironment() with { HiddenFolders = [relayFolder] });
        builder.Services.AddSingleton<ReportBook>();
        builder.Services.AddSingleton(services => new HookBook { Reports = services.GetRequiredService<ReportBook>() });
        builder.Services.AddSingleton(services => new TranscriptBook(services.GetRequiredService<SessionTrackerOptions>().Profile));
    }

    /// <summary>Git, the planes (worktrees) and what removes them, the stop gate and the attachments.</summary>
    private static void AddPlanes(WebApplicationBuilder builder, NodeOptions options, LogWriter logWriter)
    {
        builder.Services.AddSingleton<IGit, GitCli>();
        builder.Services.AddSingleton(services => new RemovalQueue(RemovalQueue.FileIn(options.DataFolder), services.GetRequiredService<TimeProvider>()) { Error = logWriter });
        // First: tried once at the start, before any service touches the paths it holds.
        builder.Services.AddHostedService(services => new RemovalQueueStart(services.GetRequiredService<RemovalQueue>(), services.GetRequiredService<NodeState>(), services.GetRequiredService<TimeProvider>()));
        builder.Services.AddHostedService(services => new DiskSweep(
            Path.Combine(services.GetRequiredService<SessionTrackerOptions>().Profile.Root, "jobs"), Path.GetTempPath(),
            services.GetRequiredService<NodeState>(), services.GetRequiredService<TimeProvider>())
        {
            ScratchDays = () => services.GetRequiredService<RoleModels>().ScratchDays,
            DiskAlertGb = () => services.GetRequiredService<RoleModels>().DiskAlertGb,
            Planes = services.GetRequiredService<Planes>(),
            GitHub = services.GetService<IGitHub>(),
            Queue = services.GetRequiredService<RemovalQueue>(),
            Repos = () => [.. services.GetRequiredService<RepoCatalog>().Repos().Where(r => r.GitHub is not null).Select(r => r.Path)],
        });
        // The Claude Code jobs pilot started and has as ended or replaced are stopped and removed once the tracker has listed, then hourly (#891).
        builder.Services.AddSingleton<IClaudeJobs>(services => new ClaudeCliJobs(Path.Combine(services.GetRequiredService<SessionTrackerOptions>().Profile.Root, "jobs")));
        // The same removal, at once, when the node ends a session it started (a worker's stop, a session end).
        builder.Services.AddSingleton(services => new ClaudeJobRemoval(services.GetRequiredService<IClaudeJobs>(), logWriter));
        builder.Services.AddHostedService(services => new ClaudeJobSweep(
            services.GetRequiredService<NodeState>(), services.GetRequiredService<SessionLedger>(), services.GetRequiredService<IClaudeJobs>(),
            services.GetRequiredService<IClaudeAgents>(), options.Machine, services.GetRequiredService<TimeProvider>())
        {
            Ready = () => services.GetRequiredService<SessionTracker>().FirstPoll,
            CurrentWatchtower = () => services.GetRequiredService<Watchtower>().Current(),
            Error = logWriter,
        });
        builder.Services.AddSingleton(services => new Planes(services.GetRequiredService<IGit>(), services.GetRequiredService<RemovalQueue>()));
        builder.Services.AddSingleton<StopGate>();
        builder.Services.AddSingleton<PermissionGate>();
        builder.Services.AddSingleton(new Attachments(options.DataFolder));
    }

    /// <summary>What a flight connected to a releasing flight waits for: nuget.org, the releases and the watches on them.</summary>
    private static void AddPackages(WebApplicationBuilder builder, LogWriter logWriter)
    {
        // nuget.org's public flat container: what a flight connected to a releasing flight waits for.
        builder.Services.AddSingleton<INuGetFeed>(_ => new NuGetOrgFeed(new HttpClient(new CountingHandler(NodeTraffic.Shared) { InnerHandler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate } }) { Timeout = TimeSpan.FromSeconds(30) }));
        builder.Services.AddSingleton<PackageReleases>();
        builder.Services.AddSingleton(services =>
        {
            var watch = ActivatorUtilities.CreateInstance<PackageWatch>(services);
            watch.Error = logWriter;
            return watch;
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<PackageWatch>());
        // A flight connected to a flight another node flies is held until that node says it landed.
        builder.Services.AddHostedService(services =>
        {
            var commander = services.GetRequiredService<FlightCommander>();
            commander.Error = logWriter;
            return new PeerConnectionWatch(commander, services.GetRequiredService<TimeProvider>()) { Error = logWriter };
        });
    }

    /// <summary>The flight commander, with what it reaches: the trackers, the fleet, the ledger.</summary>
    private static void AddFlights(WebApplicationBuilder builder, NodeOptions options)
    {
        builder.Services.AddSingleton(services =>
        {
            var flights = ActivatorUtilities.CreateInstance<FlightCommander>(services);
            flights.Models = services.GetRequiredService<RoleModels>();
            flights.Machine = options.Machine;
            flights.Attachments = services.GetRequiredService<Attachments>();
            flights.Repos = services.GetRequiredService<RepoCatalog>();
            flights.Ledger = services.GetRequiredService<SessionLedger>();
            // A landing tells each live session of the flight to finish and report, then cancels the jobs of the flight and its sessions.
            flights.Say = async (id, text, ct) =>
                (await services.GetRequiredService<NodeCommands>().RunAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = LegWatch.Who }, ct)).Ok;
            flights.CancelJobs = (flight, owners, who) => services.GetRequiredService<JobRunner>().CancelFor(flight, owners, who);
            flights.CancelHeldJobs = (flight, who) => services.GetRequiredService<JobRunner>().CancelRunningFor(flight, who);
            var models = services.GetRequiredService<RoleModels>();
            var trackers = new List<ITracker> { new ShortcutTracker(new HttpClient(new CountingHandler(NodeTraffic.Shared) { InnerHandler = new HttpClientHandler() }) { Timeout = TimeSpan.FromSeconds(30) }, () => ShortcutTracker.TokenFrom(models.ShortcutToken)) };
            if (services.GetService<IGitHub>() is { } gitHub)
            {
                trackers.Add(new GitHubTracker(gitHub));
            }
            flights.Trackers = new Trackers(trackers);
            // The nodes this user's own grants reach (pilot connect): the file beside the node's data, as the clients keep it.
            flights.Fleet = new HeldTokenFleet(Environment.GetEnvironmentVariable("PILOT_CLIENT_GRANTS") is { Length: > 0 } grants
                ? grants
                : Path.Combine(options.DataFolder, "client-grants.json"),
                // Where each held node announces itself now, when this node runs LAN discovery; else the last address it answered at.
                services.GetService<LAN.Lib.LanDiscovery>() is { } lan ? () => lan.PeersOf(NodeWire.LanService) : null);
            // The last copy of each peer's flights and legs, so a flight of a node that went down can still be read (#894).
            flights.Copy = services.GetRequiredService<FleetCopy>();
            return flights;
        });
        builder.Services.AddSingleton(services => new FleetCopy(FleetCopy.FileIn(options.DataFolder), services.GetRequiredService<TimeProvider>()) { Error = services.GetRequiredService<LogWriter>() });
        builder.Services.AddHostedService(services => new FleetCopyWatch(services.GetRequiredService<FlightCommander>(), services.GetRequiredService<TimeProvider>()) { Error = services.GetRequiredService<LogWriter>() });
    }

    /// <summary>The repos: the catalog, the watches on them and on their PRs, the sync and the commander.</summary>
    private static void AddRepos(WebApplicationBuilder builder, NodeOptions options, LogWriter logWriter)
    {
        builder.Services.AddSingleton<RepoCatalog>();
        builder.Services.AddSingleton(services => new RepoWatch(
            services.GetRequiredService<NodeState>(), services.GetRequiredService<RepoCatalog>(), services.GetRequiredService<Planes>(),
            services.GetRequiredService<IGitHub>(), services.GetRequiredService<TimeProvider>()) { Machine = options.Machine, Error = logWriter });
        builder.Services.AddHostedService(services => services.GetRequiredService<RepoWatch>());
        builder.Services.AddSingleton(services =>
        {
            var models = services.GetRequiredService<RoleModels>();
            var watch = new PullRequestWatch(services.GetRequiredService<NodeState>(), services.GetRequiredService<RepoCatalog>(), services.GetRequiredService<IGitHub>(), services.GetRequiredService<TimeProvider>())
            {
                Views = services.GetRequiredService<PageViews>(),
                Idle = () => models.PrWatchIdle,
            };
            watch.Error = logWriter;
            // A saved pr-watch-idle counts at once, also for a wait already running.
            models.Applied += watch.PaceChanged;
            return watch;
        });
        // The recheck listens to the watch's reads: it is built before the watch can make its first.
        builder.Services.AddHostedService(services =>
        {
            services.GetRequiredService<PrRecheck>();
            return services.GetRequiredService<PullRequestWatch>();
        });
        builder.Services.AddSingleton(services =>
        {
            var sync = ActivatorUtilities.CreateInstance<RepoSync>(services);
            sync.Error = logWriter;
            return sync;
        });
        builder.Services.AddSingleton<RepoCommander>();
    }

    /// <summary>The ideas, and the models that merge, tidy and sort them.</summary>
    private static void AddIdeas(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton<IIdeaMerger, ClaudeIdeaMerger>();
        builder.Services.AddSingleton<IIdeaTidier, ClaudeIdeaTidier>();
        builder.Services.AddSingleton<IIdeaMilestoner, ClaudeIdeaMilestoner>();
        builder.Services.AddSingleton<IIssueReadinessChecker, ClaudeIssueReadinessChecker>();
        builder.Services.AddSingleton<IMilestoneSuggester, ClaudeMilestoneSuggester>();
        builder.Services.AddSingleton<IIssueSplitter>(services => new ClaudeIssueSplitter(services.GetService<Attachments>()));
        builder.Services.AddSingleton<IIdeaRepoChooser, ClaudeIdeaRepoChooser>();
        builder.Services.AddSingleton(services =>
        {
            var ideas = ActivatorUtilities.CreateInstance<IdeaCommander>(services);
            ideas.Error = logWriter;
            ideas.Attachments = services.GetRequiredService<Attachments>();
            return ideas;
        });
    }

    /// <summary>Starting workers: the trust, the ledgers, the launcher, GitHub, the models and the start gate.</summary>
    private static void AddWorkers(WebApplicationBuilder builder, NodeOptions options, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services => new ClaudeTrust(ClaudeAccount.FileFor(services.GetRequiredService<SessionTrackerOptions>().Profile.Root)));
        builder.Services.AddSingleton(_ => new StartOptionsBook(StartOptionsBook.FileIn(options.DataFolder)) { Error = logWriter });
        builder.Services.AddSingleton(services => new SessionLedger(SessionLedger.FileIn(options.DataFolder), services.GetRequiredService<TimeProvider>()) { Error = logWriter });
        builder.Services.AddSingleton(_ => new SpendLedger(SpendLedger.FileIn(options.DataFolder)) { Error = logWriter });
        builder.Services.AddSingleton(services => new ClaudeWorkerLauncher(services.GetRequiredService<ClaudeTrust>(), services.GetRequiredService<StartOptionsBook>(),
            services.GetRequiredService<SessionLedger>(), options, services.GetRequiredService<SendTimings>(), ClaudeWorkerLauncher.BriefFolderIn(options.DataFolder),
            services.GetRequiredService<ClaudeJobRemoval>()));
        builder.Services.AddSingleton(services => new LoggingWorkerLauncher(
            services.GetRequiredService<ClaudeWorkerLauncher>(), logWriter,
            new ClaudeJobState(Path.Combine(services.GetRequiredService<SessionTrackerOptions>().Profile.Root, "jobs")), services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<IWorkerLauncher>(services => services.GetRequiredService<LoggingWorkerLauncher>());
        builder.Services.AddHostedService(services => new StartWatch(services.GetRequiredService<IWorkerLauncher>()));
        builder.Services.AddSingleton<ICheckRunner, JobCheckRunner>();
        builder.Services.AddSingleton<IGitHub, GhCli>();
        builder.Services.AddSingleton(_ => new RoleModels(options.Saved));
        builder.Services.AddSingleton(services => new StartGate(services.GetRequiredService<NodeState>(), services.GetRequiredService<TimeProvider>())
        {
            Outage = services.GetRequiredService<TurnOutage>(),
        });
    }

    /// <summary>The admission gate, counting this node's sessions and the fleet's.</summary>
    private static void AddAdmission(WebApplicationBuilder builder, NodeOptions options)
    {
        builder.Services.AddSingleton(services =>
        {
            var slots = new AdmissionGate(services.GetRequiredService<NodeState>(), services.GetRequiredService<RoleModels>, options.Machine, services.GetRequiredService<TimeProvider>());
            // The peers this node holds grants for count in admission; a setting the keeper of the count is sent is applied as this machine's own.
            slots.Fleet.Fleet = services.GetRequiredService<FlightCommander>().Fleet;
            slots.SettingApplier = args => services.GetRequiredService<NodeSettingsCommands>().Set(new CommandRequest
            {
                Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = args, Who = "fleet", Grant = "fleet",
            }).Error;
            return slots;
        });
    }

    /// <summary>
    /// The failures looked into: a failure in a test the PR does not touch is filed as an investigation. Its ledger is kept in the
    /// node's data folder, so a restart keeps which failure already has its investigation.
    /// </summary>
    private static void AddFailureInvestigations(WebApplicationBuilder builder, LogWriter logWriter, NodeOptions options)
    {
        builder.Services.AddSingleton(_ => new FailureLedger(FailureLedger.FileIn(options.DataFolder)) { Error = logWriter });
        builder.Services.AddSingleton(services => new FailureInvestigations(services.GetRequiredService<NodeState>(), services.GetRequiredService<IGitHub>(),
            services.GetRequiredService<FailureLedger>(), services.GetRequiredService<TimeProvider>())
        {
            Error = logWriter,
            CreateFlight = (command, token) => services.GetRequiredService<FlightCommander>().RunAsync(command, token),
        });
        // What a failure under investigation does to its PR: retried once, then the tower on alert and the flights held. Its alerts are kept
        // in the data folder too, so a restart keeps the holds.
        builder.Services.AddSingleton(services => new FailureAlerts(services.GetRequiredService<NodeState>(), services.GetRequiredService<IGitHub>(),
            services.GetRequiredService<FailureInvestigations>(), services.GetRequiredService<TimeProvider>(), FailureAlerts.FileIn(options.DataFolder))
        {
            Error = logWriter,
        });
    }

    /// <summary>The leg commander.</summary>
    private static void AddLegs(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(services =>
        {
            var legs = ActivatorUtilities.CreateInstance<LegCommander>(services);
            legs.Models = services.GetRequiredService<RoleModels>();
            legs.Gate = services.GetRequiredService<StartGate>();
            legs.DropJobs = (owners, why) => services.GetRequiredService<JobRunner>().DropQueuedOf(owners, why);
            legs.Slots = services.GetRequiredService<AdmissionGate>();
            legs.Attachments = services.GetRequiredService<Attachments>();
            legs.Releases = services.GetRequiredService<PackageReleases>();
            legs.Ledger = services.GetRequiredService<SpendLedger>();
            legs.SiblingSync = services.GetRequiredService<RepoSync>();
            // Read as it is when a finish's check ends: a finish that outlasts the command route's deadline was answered queued.
            legs.ReplyDeadline = () => services.GetRequiredService<NodeCommands>().ReplyDeadline;
            legs.Error = services.GetRequiredService<LogWriter>();
            legs.Investigations = services.GetRequiredService<FailureInvestigations>();
            // The alerts run a held leg check through the commander, which refuses the go-around of a held flight: set here, as it needs both.
            legs.Alerts = services.GetRequiredService<FailureAlerts>();
            legs.Alerts.Legs = legs;
            // A PR's conflict with its base is put to the flight's flying worker as a line from the node, as a landing tells its sessions.
            legs.Say = async (id, text, ct) =>
                (await services.GetRequiredService<NodeCommands>().RunAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = LegWatch.Who }, ct)).Ok;
            // A flight another node hands over goes on by the normal advance, as the sweep takes every flying flight's.
            services.GetRequiredService<FlightCommander>().Advance = legs.AdvanceFlightAsync;
            // A flight made here is planned at once: the create starts its planner, so no further plan-flight is needed.
            services.GetRequiredService<FlightCommander>().Plan = (code, who, token) =>
                legs.RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(code), Who = who }, token);
            return legs;
        });
    }

    /// <summary>The reviews of any PR.</summary>
    private static void AddPrReviews(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services =>
        {
            var reviews = ActivatorUtilities.CreateInstance<PrReviews>(services);
            reviews.Models = services.GetRequiredService<RoleModels>();
            reviews.Slots = services.GetRequiredService<AdmissionGate>();
            reviews.Error = logWriter;
            reviews.Investigations = services.GetRequiredService<FailureInvestigations>();
            reviews.Alerts = services.GetRequiredService<FailureAlerts>();
            reviews.DropJobs = (owners, why) => services.GetRequiredService<JobRunner>().DropQueuedOf(owners, why);
            reviews.TakeFlightVerdict = services.GetRequiredService<LegCommander>().TakePrVerdictAsync;
            reviews.SendFlightRound = services.GetRequiredService<LegCommander>().AnsweredDesignAsync;
            return reviews;
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<PrReviews>());
    }

    /// <summary>The commands the node takes, the autopilot, and the LAN: the grants and, when announced, its discovery.</summary>
    private static void AddCommands(WebApplicationBuilder builder, NodeOptions options)
    {
        builder.Services.AddSingleton<Autopilot>();
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
    }

    /// <summary>The watch on every leg's worker.</summary>
    private static void AddLegWatch(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services =>
        {
            var legs = services.GetRequiredService<LegCommander>();
            var commands = services.GetRequiredService<NodeCommands>();
            return new LegWatch(services.GetRequiredService<NodeState>(), commands.RunAsync, services.GetRequiredService<TimeProvider>())
            {
                Pilot = Briefs.Run(legs.Cli, legs.Socket),
                Models = services.GetRequiredService<RoleModels>(),
                Ledger = services.GetRequiredService<SessionLedger>(),
                Gate = services.GetRequiredService<StartGate>(),
                Outage = services.GetRequiredService<TurnOutage>(),
                Error = logWriter,
                Ready = services.GetRequiredService<JobRunner>().Recovered,
                StartedPlanner = legs.StartedPlanner,
                RestartPlanner = legs.RestartPlannerAsync,
                StopPlanner = legs.StopPlannerAsync,
                RestartWorker = legs.RestartWorkerAsync,
                RestartValidator = legs.RestartValidatorAsync,
                PlaneHead = legs.PlaneHeadOfAsync,
                StartTicker = legs.StartTickerAsync,
                StopSession = legs.StopSessionAsync,
                ChangedFiles = async (flight, ct) =>
                {
                    var diff = await services.GetRequiredService<IGit>().RunAsync(["diff", "--name-only", $"{flight.Base}...HEAD"], flight.Worktree, ct);
                    return diff.Ok ? diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
                },
                TellEndedJobs = (owners, ct) => services.GetRequiredService<JobRunner>().TellUntoldOf(owners, ct),
            };
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<LegWatch>());
    }

    /// <summary>The watchtower, its chat and the compactor.</summary>
    private static void AddWatchtower(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services =>
        {
            var legs = services.GetRequiredService<LegCommander>();
            var commands = services.GetRequiredService<NodeCommands>();
            return new Watchtower(services.GetRequiredService<NodeState>(), services.GetRequiredService<IWorkerLauncher>(), commands.RunAsync,
                services.GetRequiredService<NodeOptions>(), services.GetRequiredService<TimeProvider>())
            {
                Pilot = Briefs.Run(legs.Cli, legs.Socket),
                Ready = () => services.GetRequiredService<SessionTracker>().FirstPoll,
                Models = services.GetRequiredService<RoleModels>(),
                Gate = services.GetRequiredService<StartGate>(),
                UiConnected = () => services.GetRequiredService<PageViews>().Any,
                Error = logWriter,
                JobRecord = new ClaudeJobState(Path.Combine(services.GetRequiredService<SessionTrackerOptions>().Profile.Root, "jobs")).Read,
                Transcripts = services.GetRequiredService<TranscriptBook>(),
                // The watchtower is one long-lived claude -p stream-json process the node owns (PIL855).
                Streams = new ClaudeStreamProcessLauncher(),
                Ledger = services.GetRequiredService<SessionLedger>(),
                Prepare = start => services.GetRequiredService<ClaudeWorkerLauncher>().WithMcp(start),
            };
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<Watchtower>());
        // The node's thinking (summaries, the verdict before a ring, the plain wording of a card) is asked of the watchtower's own stream (PIL855).
        builder.Services.AddSingleton<IBrain>(services => new BrainClient(() => services.GetRequiredService<Watchtower>().CurrentStream(), services.GetRequiredService<TimeProvider>())
        {
            Log = line => logWriter.WriteLine(line),
        });
        builder.Services.AddSingleton<SendLog>();
        builder.Services.AddSingleton(services => new SendTimings(services.GetRequiredService<TimeProvider>(), logWriter));
        builder.Services.AddSingleton(services => new CommandTimings(services.GetRequiredService<TimeProvider>(), logWriter));
        builder.Services.AddSingleton(services => new WatchtowerChat(services.GetRequiredService<Watchtower>(), services.GetRequiredService<TranscriptBook>(), services.GetRequiredService<SendLog>()));
        builder.Services.AddSingleton(services => new Compactor(services.GetRequiredService<NodeState>(), services.GetRequiredService<Watchtower>(),
            services.GetRequiredService<NodeCommands>().RunAsync, services.GetRequiredService<TimeProvider>())
        {
            Models = services.GetRequiredService<RoleModels>(),
            Gate = services.GetRequiredService<StartGate>(),
            Ledger = services.GetRequiredService<SessionLedger>(),
            Error = logWriter,
            PersonMessages = row => services.GetRequiredService<TranscriptBook>().PersonMessages(row.SessionId),
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<Compactor>());
    }

    /// <summary>The usage the account reports.</summary>
    private static void AddUsage(WebApplicationBuilder builder)
    {
        // From the profile the tracker reads, so a test node, on a profile of its own, never reads the machine's.
        builder.Services.AddSingleton(services => new UsageReader(
            services.GetRequiredService<NodeState>(),
            Path.Combine(services.GetRequiredService<SessionTrackerOptions>().Profile.Root, "rate-limits.json"),
            services.GetRequiredService<TimeProvider>())
        {
            AccountFile = ClaudeAccount.FileFor(services.GetRequiredService<SessionTrackerOptions>().Profile.Root),
            Outage = services.GetRequiredService<TurnOutage>(),
        });
        builder.Services.AddSingleton(services => new TurnOutage(services.GetRequiredService<NodeState>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<UsageReader>());
    }

    /// <summary>The machine's sampling, and the node's own row, which rides the same clock.</summary>
    private static void AddMachine(WebApplicationBuilder builder, NodeOptions options)
    {
        builder.Services.AddSingleton(services =>
        {
            var state = services.GetRequiredService<NodeState>();
            var tracker = services.GetRequiredService<SessionTracker>();
            var jobs = services.GetRequiredService<JobRunner>();
            return new MachineSampler(state, services.GetRequiredService<TimeProvider>())
            {
                // A session's name, or the watchtower's role for the node's own; a job's id. A job's tree is walked from its root.
                Owners = () =>
                {
                    var sessions = state.Sessions().ToDictionary(s => s.Id, StringComparer.Ordinal);
                    var owners = new List<(string, int)>();
                    foreach (var (id, pid) in tracker.LivePids())
                    {
                        if (sessions.TryGetValue(id, out var row))
                        {
                            owners.Add((row.Role == SessionRole.Watchtower ? MachineSampler.WatchtowerOwner : row.Name, pid));
                        }
                    }
                    foreach (var (id, pid) in jobs.RunningPids())
                    {
                        owners.Add((id, pid));
                    }
                    return owners;
                },
                // The repos' drive, else the Claude profile's (one and the same on most machines).
                DiskPath = () => options.RepoRoots.FirstOrDefault(Directory.Exists) ?? services.GetRequiredService<SessionTrackerOptions>().Profile.Root,
                // Every 5 s while a GUI or TUI shows Home, where the machine is drawn; every 15 s otherwise.
                Views = services.GetRequiredService<PageViews>(),
                Logger = services.GetService<ILogger<MachineSampler>>(),
            };
        });
        // Off for a test node, whose stream a sample every few seconds would number into the deltas the test counts.
        if (options.MachineSampling)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<MachineSampler>());
            // One sweep, both the hosted look and the handler of a stray card's End (end-stray), which only ends what it named.
            builder.Services.AddSingleton(services => new StraySweep(services.GetRequiredService<NodeState>(), OsProcessTable.Instance, services.GetRequiredService<TimeProvider>()) { LivePids = () => services.GetRequiredService<SessionTracker>().LivePids().Select(p => (p.Id, p.Pid)).ToList(), KnownOf = services.GetRequiredService<JobRunner>().KnownOf });
            builder.Services.AddHostedService(services => services.GetRequiredService<StraySweep>());
            // The node's own row rides the same tick of the clock (a settings change has no event to hook).
            builder.Services.AddHostedService(services =>
            {
                var state = services.GetRequiredService<NodeState>();
                var models = services.GetRequiredService<RoleModels>();
                var watchtower = services.GetRequiredService<Watchtower>();
                return new NodeRowPublisher(state, () => new NodeRow
                {
                    Maintenance = models.Maintenance,
                    Autopilot = models.Autopilot,
                    AcceptsFleetFlights = models.AcceptsFleetFlights,
                    Account = state.Usage()?.Account,
                    Watchtower = watchtower.Health(),
                }, services.GetRequiredService<TimeProvider>())
                {
                    Logger = services.GetService<ILogger<NodeRowPublisher>>(),
                };
            });
        }
    }

    /// <summary>The jobs the node runs for a plane.</summary>
    private static void AddJobs(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(services =>
        {
            var commands = services.GetRequiredService<NodeCommands>();
            var nodeOptions = services.GetRequiredService<NodeOptions>();
            return new JobRunner(services.GetRequiredService<NodeState>(), nodeOptions, services.GetRequiredService<TimeProvider>())
            {
                // The real shell launcher unless a test registered another (an in-memory one, #933).
                Launcher = services.GetService<IJobLauncher>() ?? RealJobLauncher.Instance,
                Say = commands.RunAsync,
                Log = services.GetRequiredService<LogWriter>(),
                Ledger = services.GetRequiredService<SessionLedger>(),
                MaxRunning = Math.Max(1, nodeOptions.MaxJobs),
                JobsPerRepo = nodeOptions.Saved?.JobsPerRepo ?? NodeSettings.JobsPerRepoDefault,
                MinFreeMemory = (long)(Math.Max(0, nodeOptions.JobMemoryGigabytes) * (1L << 30)),
                Priority = nodeOptions.JobPriority,
                PlaneOutput = nodeOptions.Saved?.JobPlaneOutput ?? NodeSettings.JobPlaneOutputDefault,
                BuildCache = nodeOptions.Saved?.JobBuildCache ?? NodeSettings.JobBuildCacheDefault,
                EndedForGood = id => services.GetRequiredService<SessionTracker>().IsEndedForGood(id),
                Outage = services.GetRequiredService<TurnOutage>(),
                EndedAfterTurn = id => services.GetRequiredService<SessionTracker>().EndedAfterFinishedTurn(id),
                // A job's end is told to its owner only once the tracker has listed the sessions (pilot #810), as the watchtower waits.
                Ready = () => services.GetRequiredService<SessionTracker>().FirstPoll,
                Lifetime = services.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            };
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<JobRunner>());
        builder.Services.AddSingleton(services => new BenchRun(
            services.GetRequiredService<NodeState>(), services.GetRequiredService<NodeOptions>(), services.GetRequiredService<RepoCatalog>(), services.GetRequiredService<Planes>(),
            services.GetRequiredService<IGit>(), services.GetRequiredService<IGitHub>(), services.GetRequiredService<JobRunner>()));
    }

    /// <summary>The sweep that advances every flying flight.</summary>
    private static void AddFlightAdvance(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services =>
        {
            var advance = ActivatorUtilities.CreateInstance<FlightAdvance>(services);
            advance.Error = logWriter;
            return advance;
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<FlightAdvance>());
    }

    /// <summary>The recheck of a PR and the PR checks that listen to the watch's reads.</summary>
    private static void AddPrChecks(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services =>
        {
            var recheck = ActivatorUtilities.CreateInstance<PrRecheck>(services);
            recheck.Fleet = services.GetRequiredService<FlightCommander>().Fleet;
            recheck.Error = logWriter;
            return recheck;
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<PrRecheck>());
        // A failed check is run again once before the node sends a leg round on it.
        builder.Services.AddSingleton(services =>
        {
            var checks = ActivatorUtilities.CreateInstance<PrChecks>(services);
            checks.RerunFirst = true;
            checks.WatchtowerLive = () => services.GetRequiredService<Watchtower>().IsLive();
            checks.Recheck = services.GetRequiredService<PrRecheck>();
            checks.Investigations = services.GetRequiredService<FailureInvestigations>();
            checks.Alerts = services.GetRequiredService<FailureAlerts>();
            checks.Error = logWriter;
            return checks;
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<PrChecks>());
    }

    /// <summary>The summaries and the digest.</summary>
    private static void AddSummaries(WebApplicationBuilder builder, NodeOptions options, LogWriter logWriter)
    {
        // The brain answers a summary; the one-off call of the summary model (sonnet by default) is what runs when it is down (PIL855).
        builder.Services.AddSingleton<ISummaryModel>(services => new BrainSummaryModel(services.GetRequiredService<IBrain>(),
            new ClaudeSummaryModel(Path.Combine(options.DataFolder, "summary"), services.GetRequiredService<RoleModels>()),
            line => logWriter.WriteLine(line)));
        builder.Services.AddSingleton(services => new Summaries(services.GetRequiredService<NodeState>(), services.GetRequiredService<ISummaryModel>(), services.GetRequiredService<TimeProvider>())
        {
            Machine = options.Machine,
            Bounds = () => services.GetRequiredService<RoleModels>().Stale,
            // What a card the call proposes as resolved is checked against.
            GitHub = services.GetRequiredService<IGitHub>(),
            // The nodes this user's grants reach, the same a create-flight in maintenance may route to.
            Fleet = services.GetRequiredService<FlightCommander>().Fleet,
        });
        // The fleet summary follows changes through the watchtower while a client is attached (PIL903).
        builder.Services.AddSingleton(services => new SummaryFollower(services.GetRequiredService<NodeState>(), services.GetRequiredService<Summaries>(), services.GetRequiredService<TimeProvider>())
        {
            Bounds = () => services.GetRequiredService<RoleModels>().Stale,
            Error = logWriter,
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<SummaryFollower>());
        builder.Services.AddSingleton(services => new Digest(services.GetRequiredService<NodeState>(), services.GetRequiredService<TimeProvider>())
        {
            Bounds = () => services.GetRequiredService<RoleModels>().Stale,
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<Digest>());
        builder.Services.AddSingleton(services => new StaleWatch(services.GetRequiredService<NodeState>(), services.GetRequiredService<TimeProvider>())
        {
            Bounds = () => services.GetRequiredService<RoleModels>().Stale,
            Trackers = () => services.GetRequiredService<FlightCommander>().Trackers,
            TellEndedJobs = (owners, ct) => services.GetRequiredService<JobRunner>().TellUntoldOf(owners, ct),
            Say = async (id, text, ct) =>
                (await services.GetRequiredService<NodeCommands>().RunAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = LegWatch.Who }, ct)).Ok,
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<StaleWatch>());
    }

    /// <summary>Releases of pilot itself.</summary>
    private static void AddUpdates(WebApplicationBuilder builder, NodeOptions options)
    {
        // Releases of pilot itself: by --update-policy, for an installed release only (NodeUpdater).
        builder.Services.AddSingleton<IReleaseFeed>(_ => options.ReleaseFeed is { } feed
            ? new FolderReleaseFeed(feed, NodeUpdater.Layout.Product)
            : new GhReleaseFeed(NodeUpdater.Repo, channel: options.UpdateChannel));
        builder.Services.AddSingleton<NodeUpdater>();
        builder.Services.AddHostedService(services => services.GetRequiredService<NodeUpdater>());
    }

    /// <summary>The Settings page: the restart, the setup checklist and the settings commands.</summary>
    private static void AddSettings(WebApplicationBuilder builder, NodeOptions options)
    {
        // The Settings page: a saved setting restarts the node to run with it, and the setup checklist's fixes.
        builder.Services.AddSingleton<NodeRestarter>();
        builder.Services.AddSingleton(services => new NodeSetup(options, Environment.ProcessPath ?? "pilot-node",
            services.GetRequiredService<SessionTrackerOptions>().Profile.Root)
        {
            ShortcutToken = () => Flights.ShortcutTracker.TokenFrom(services.GetRequiredService<RoleModels>().ShortcutToken),
        });
        builder.Services.AddSingleton<NodeSettingsCommands>();
    }

    /// <summary>The session tracker, and what delivers a message to a session.</summary>
    private static void AddSessions(WebApplicationBuilder builder, NodeOptions options, LogWriter logWriter)
    {
        var relayFolder = RelayFolder(options);
        builder.Services.AddSingleton(services => new SessionTracker(
            services.GetRequiredService<NodeState>(),
            services.GetRequiredService<IClaudeAgents>(),
            services.GetRequiredService<SessionTrackerOptions>(),
            () => services.GetRequiredService<EventHub>().ClientCount > 0,
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<SessionTracker>>())
        {
            Hooks = services.GetRequiredService<HookBook>(),
            Reports = services.GetRequiredService<ReportBook>(),
            Attachments = services.GetRequiredService<Attachments>(),
            Starts = services.GetRequiredService<StartOptionsBook>(),
            Launched = services.GetRequiredService<SessionLedger>(),
            Ledger = services.GetRequiredService<SpendLedger>(),
            Error = logWriter,
            IsWatchtowerId = id => services.GetRequiredService<Watchtower>().KnowsId(id),
            IsStreamSession = id => services.GetRequiredService<Watchtower>().OwnsSession(id),
            JobRecord = new ClaudeJobState(Path.Combine(services.GetRequiredService<SessionTrackerOptions>().Profile.Root, "jobs")).Read,
            // A session that ends on its own takes its queued jobs off the queue (#327).
            DropJobs = (owners, why) => services.GetRequiredService<JobRunner>().DropQueuedOf(owners, why),
            IsBeingReplaced = id => services.GetRequiredService<LegCommander>().IsBeingReplaced(id),
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<SessionTracker>());
        // The relay's throwaway session runs in a folder of the node's own, as v1's runs in its state folder.
        builder.Services.AddSingleton<ISessionDelivery>(services => new ClaudeCliDelivery(
            services.GetRequiredService<IClaudeAgents>(), relayFolder, services.GetRequiredService<SessionTrackerOptions>().Profile) { RosterFile = SessionRoster.FileIn(options.DataFolder) });
        builder.Services.AddSingleton(services => new SessionMessenger(
            services.GetRequiredService<NodeState>(),
            services.GetRequiredService<SessionTracker>(),
            services.GetRequiredService<ISessionDelivery>(),
            logWriter,
            services.GetRequiredService<StartOptionsBook>(),
            services.GetRequiredService<SessionLedger>())
        {
            // Ending a session cancels the jobs it owns.
            CancelJobs = (owners, who) => services.GetRequiredService<JobRunner>().CancelOwnedBy(owners, who),
            Sends = services.GetRequiredService<SendLog>(),
            Timings = services.GetRequiredService<SendTimings>(),
            Removal = services.GetRequiredService<ClaudeJobRemoval>(),
            Streams = () => services.GetRequiredService<Watchtower>(),
        });
    }

    /// <summary>The relay's throwaway session runs in this folder of the node's own, as v1's runs in its state folder.</summary>
    private static string RelayFolder(NodeOptions options) => Path.Combine(options.DataFolder, "relay");
}
