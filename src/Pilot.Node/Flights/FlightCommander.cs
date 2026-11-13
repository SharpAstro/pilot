using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The flight verbs: <c>create-flight</c>, <c>connect</c> and <c>land</c>. Git work takes seconds, so each runs off the
/// state's lock, one at a time (two creates must not claim one code), and puts its result on the stream when done. Nor does git
/// or GitHub work hold the commander's own lock (PIL338): a create runs on its own lock, apart from every other verb's, and a
/// landing lets the lock go for its calls and takes it again to record, so a connect or a land of another flight goes through at once.
/// </summary>
/// <param name="workers">Stops the flight's sessions before its plane goes; null in a test that starts none.</param>
/// <param name="github">Gives a flight made without a title its issue's, and the commit a landed PR merged as; null in a test that names its titles.</param>
/// <param name="packages">Reads what a landing flight releases, for the flights connected to it to wait for; null in a test that has none wait for a package.</param>
public sealed partial class FlightCommander(NodeState state, Planes planes, TimeProvider time, IWorkerLauncher? workers = null, IGitHub? github = null, PackageReleases? packages = null)
{
    // The one lock of the flight verbs, which says what holds it (a command waiting past LockWait.Patience is told).
    private readonly NamedLock _one = new(time);

    /// <summary>The lock the flight verbs share, and what holds it.</summary>
    public NamedLock Lock => _one;

    // Two creates must not claim one code, but a create's git work (the checkout, the worktree) holds no other verb: it has its own lock.
    private readonly SemaphoreSlim _creating = new(1, 1);

    // The flights being landed, whose plane and sessions are being taken down with the lock let go: a second land of one is refused.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _landing = new(StringComparer.Ordinal);

    /// <summary>Says text to a session row (a node say), true when it was delivered; null where there is none (a test that tells no one).</summary>
    public Func<string, string, CancellationToken, Task<bool>>? Say { get; set; }

    /// <summary>Cancels the queued and running jobs of a flight and of its sessions (flight, session row ids, who); null where there is no job runner.</summary>
    public Func<string, IReadOnlyCollection<string>, string, int>? CancelJobs { get; set; }

    /// <summary>Cancels a held flight's running jobs other than its leg's check (a cancelled check reads as a failed one); returns how many.</summary>
    public Func<string, string, int>? CancelHeldJobs { get; set; }

    /// <summary>Has the job runner look at its queue again (a hold lifted is a reason: its queued jobs may start); null where there is no runner.</summary>
    public Action? PumpJobs { get; set; }

    /// <summary>The node's ledger of launched sessions, whose live entries of a flight are wound down with it; null in a test with none.</summary>
    public Sessions.SessionLedger? Ledger { get; set; }

    /// <summary>How long the sessions of a landing flight are given to finish and report after being told, before they are stopped.</summary>
    public TimeSpan FinishGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The settings in force: whether the node is in maintenance. A test's commander takes every flight.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>Where the images its legs' artifacts name are kept; a landed flight's copies go. Null keeps none.</summary>
    public Attachments? Attachments { get; set; }

    /// <summary>The trackers a work item's title is read from; null reads GitHub issues only, through <c>github</c>, and refuses any other tracker's item given without a title.</summary>
    public Trackers? Trackers
    {
        get => _trackers ?? (github is null ? null : _trackers = new Trackers([new GitHubTracker(github)]));
        set => _trackers = value;
    }

    private Trackers? _trackers;

    /// <summary>The machine's prefix, as a refusal names it.</summary>
    public string Machine { get; set; } = "this node";

    /// <summary>The other nodes of the fleet, which a create-flight in maintenance is routed to; null where there are none (a test's commander).</summary>
    public IFleet? Fleet { get; set; }

    /// <summary>This machine's checkouts, by which a flight handed over is given its own repo; null where there is none (a receive-flight is then refused for want of a checkout).</summary>
    public RepoCatalog? Repos { get; set; }

    /// <summary>
    /// Takes a flight's next step, as the sweep does (<see cref="LegCommander.AdvanceFlightAsync"/>): what a flight handed over goes on
    /// with once it is here, through the normal advance and no path of its own. Null where there is none (a test's commander).
    /// </summary>
    public Func<string, CancellationToken, Task>? Advance { get; set; }

    /// <summary>
    /// Starts the planner of a flight made here (plan-flight on it, taking who made it), off the reply, so a flight made by the CLI,
    /// MCP or plan-idea is planned with no further command. A planner held by the window or admission waits and the sweep starts it.
    /// Null where there is none (a test's commander).
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? Plan { get; set; }

    /// <summary>The quiet card a flight folded into another leaves in place of a landed one.</summary>
    public const string FoldedKind = "folded";

    /// <summary>The Handled card that says a flight went to another node, as this one is in maintenance.</summary>
    public const string RoutedKind = "routed";

    /// <summary>The Handled card that says who undid a flight's connection.</summary>
    public const string DisconnectedKind = "disconnected";

    /// <summary>The Handled card that says a flight came from another node.</summary>
    public const string ReceivedKind = "received";

    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.CreateFlight or Verbs.ReceiveFlight or Verbs.Connect or Verbs.Disconnect or Verbs.Land or Verbs.FlyAnyway or Verbs.FlightPriority or Verbs.AdoptFlight;

    /// <summary>Runs <paramref name="command"/>; one that waits for the lock past <see cref="LockWait.Patience"/> is answered as queued, saying what holds it, and runs once it frees.</summary>
    public Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken) =>
        command.Verb is Verbs.CreateFlight or Verbs.ReceiveFlight
            ? CreateOneAtATimeAsync(command, cancellationToken)
            : command.Verb == Verbs.AdoptFlight
                ? AdoptOneAtATimeAsync(command, cancellationToken)
                :LockWait.RunAsync(_one, state, time, command, token => RunUnderLockAsync(command, token), cancellationToken);

    private async Task<CommandResult> RunUnderLockAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            string? error = null;
            string? made = null;
            string? way = null;
            try
            {
                switch (command.Verb)
                {
                    case Verbs.Connect:
                        made = await ConnectAsync(command, cancellationToken);
                        break;
                    case Verbs.Disconnect:
                        made = Disconnect(command);
                        break;
                    case Verbs.Land:
                        await LandAsync(command, cancellationToken);
                        break;
                    case Verbs.FlyAnyway:
                        FlyAnyway(command);
                        break;
                    case Verbs.FlightPriority:
                        made = await SetPriorityAsync(command, cancellationToken);
                        break;
                }
            }
            catch (FlightException ex)
            {
                error = ex.Message;
            }
            return state.Complete(command, error, way: way, made: made);
        }
        finally
        {
            _one.Release();
        }
    }

    private FlightRow Find(string target)
    {
        if (!target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            throw new FlightException($"this verb takes a flight target, {Verbs.FlightTargetPrefix}<code>, not {target}");
        }
        var code = target[Verbs.FlightTargetPrefix.Length..];
        return state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlightException($"There is no flight {code}");
    }
}
