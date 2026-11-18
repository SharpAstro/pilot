using Microsoft.Extensions.Hosting;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Designs;

/// <summary>
/// The designer sessions (PIL1151): one per design at a time, named <c>&lt;machine&gt;-dsn&lt;n&gt;-designer</c>, started in the design's own folder
/// when the design gets its first brief and resumed with its context (by <see cref="Say"/>, the node's own path, never a bare
/// <c>claude --resume</c>) when the owner adds to the brief. Every start and every resume goes through the <see cref="StartGate"/>: with the usage
/// limit open the ask is held, the design says <c>held until &lt;time&gt;</c> and why, and <see cref="SweepAsync"/> takes it up once the limit has ended.
/// </summary>
/// <remarks>
/// Asks run one after the other (<see cref="Settled"/> is the end of the last), so two asks for one design never both find "no designer yet" and
/// start two. The designer's id, name, state and the pieces not yet delivered are on the design's record (<see cref="DesignRow"/>), so a restart finds
/// the session again. The designer's writes outside <c>draft/</c> are refused by <see cref="DesignerGuard"/>.
/// </remarks>
public sealed class Designers(NodeState state, DesignStore store, IWorkerLauncher workers, StartGate gate, RoleModels models, string machine, TimeProvider time)
{
    /// <summary>How long a start may go unlisted by the session tracker before it is taken for lost: pieces wait for it until then.</summary>
    public static readonly TimeSpan ListedWithin = TimeSpan.FromMinutes(2);

    /// <summary>The card kind of a held ask.</summary>
    public const string HeldKind = "designer-held";

    private readonly Lock _chain = new();
    private Task _settled = Task.CompletedTask;

    /// <summary>How the CLI is run in the designer's brief (<see cref="Briefs.Run"/>).</summary>
    public string Run { get; init; } = Briefs.Run(null, null);

    /// <summary>The <c>--settings</c> JSON a designer is started with (<see cref="HookCommands.DesignerSettings"/>), which makes the node's PreToolUse deny reach it; null where none is wired.</summary>
    public string? GuardSettings { get; init; }

    /// <summary>Says a command to a session through the node's messenger, which resumes an ended session with its context; null where none is wired (a design store without sessions).</summary>
    public Func<CommandRequest, CancellationToken, Task<CommandResult>>? Say { get; set; }

    /// <summary>
    /// How long a designer stays up, idle and warm, after its last turn before the node ends it (<see cref="NodeSettings.DesignerKeepKey"/>, minutes); the next brief
    /// piece resumes it with its context through <see cref="Say"/>. <see cref="TimeSpan.Zero"/> ends it at once. Asked at each sweep, so a saved setting counts.
    /// </summary>
    public Func<TimeSpan> Keep { get; init; } = () => TimeSpan.FromMinutes(NodeSettings.DefaultDesignerKeep);

    /// <summary>The label lock on the design's issue (PIL1171): taken when a turn that drafts starts, released when the designer's session ends. Null where none is wired.</summary>
    public DesignLock? Locks { get; init; }

    /// <summary>The end of the asks made so far, for a test to await.</summary>
    public Task Settled
    {
        get { lock (_chain) { return _settled; } }
    }

    /// <summary>The name of the designer of <paramref name="id"/>.</summary>
    public string NameOf(string id) => $"{machine.ToLowerInvariant()}-dsn{DesignStore.Normalise(id)[4..]}-designer";

    /// <summary>
    /// Asks for the designer of <paramref name="id"/> to work on the brief: <paramref name="piece"/> is the owner's new words, or null for a design's first
    /// brief (which the designer's own brief carries whole). Done in the background, after the asks before it; returns at once.
    /// </summary>
    public void Ask(string id, string? piece)
    {
        lock (_chain)
        {
            _settled = Chain(_settled, () => AskAsync(id, piece));
        }
    }

    private static async Task Chain(Task before, Func<Task> next)
    {
        try
        {
            await before.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An earlier ask's unexpected fault is its own: it must not stop every ask after it until a restart.
        }
        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the designer of <paramref name="id"/> what the owner typed into DESIGN.md (PIL1171) without waking it: the words wait on the record and go with its next say,
    /// resume or start (<see cref="Ask"/>); a designer working now is told at its next turn, never interrupted.
    /// </summary>
    public void Queue(string id, string part, string cell, string text)
    {
        var where = cell.Trim().Length == 0 ? part + " of the design" : $"{part} of {cell.Trim()}";
        var lead = $"The owner typed into DESIGN.md: {where} now reads: ";
        // Every autosave of one part is one more edit: the designer is told the part's last words once, not each save.
        store.Update(id, r => r with { DesignerTyped = [.. r.DesignerTyped.Where(l => !l.StartsWith(lead, StringComparison.Ordinal)), lead + text.Trim()] });
    }

    /// <summary>Takes up what waits: a held ask whose hold has ended, and pieces for a designer that is up now. Called on a timer by the node, and by a test.</summary>
    public void Sweep()
    {
        lock (_chain)
        {
            _settled = Chain(_settled, SweepAsync);
        }
    }

    private async Task SweepAsync()
    {
        foreach (var row in store.List())
        {
            if (row.DesignerState is DesignerStates.Held || row.DesignerPending.Length > 0)
            {
                await AskAsync(row.Id, null).ConfigureAwait(false);
            }
            else if (row.DesignerName is { Length: > 0 } name && state.Sessions().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } listed)
            {
                // The session id is kept as soon as the tracker lists it, so the design's chat finds the transcript after a restart.
                var done = row.DesignerState is DesignerStates.Working && listed.State is SessionState.Idle or SessionState.Ended;
                if (row.DesignerState is DesignerStates.Idle && listed.State is SessionState.Idle && !listed.Gone && row.DesignerIdleSince is { } since && time.GetUtcNow() - since >= Keep())
                {
                    await EndIdleAsync(row.Id, listed).ConfigureAwait(false);
                    continue;
                }
                if (row.DesignerState is DesignerStates.Idle && row.DesignerIdleSince is null && listed.State is SessionState.Idle && !listed.Gone)
                {
                    // Idle with no mark (a record an older release wrote, or a restart): the keep time counts from now.
                    store.Update(row.Id, r => r with { DesignerIdleSince = time.GetUtcNow() });
                }
                if (done || listed.SessionId is { Length: > 0 } known && !row.DesignerSessions.Contains(known))
                {
                    // Its turn is done: the design says idle, and the next brief piece resumes it. A session that has ended no longer has the design open.
                    if (listed.State is SessionState.Ended)
                    {
                        Locks?.Release(row.Id);
                    }
                    store.Update(row.Id, r => r with
                    {
                        DesignerState = done ? DesignerStates.Idle : r.DesignerState,
                        DesignerIdleSince = done && listed.State is SessionState.Idle ? time.GetUtcNow() : r.DesignerIdleSince,
                        DesignerTurnPublished = done ? r.DesignerTurnStartedAt is { } began && r.History.Any(v => v.At >= began) : r.DesignerTurnPublished,
                        DesignerSessions = WithSession(r.DesignerSessions, listed.SessionId),
                    });
                }
            }
        }
    }

    /// <summary>Ends an idle designer through the node's own path (recorded in the session ledger as <see cref="SessionEndReasons.DesignerIdle"/>); the design stays idle, and its next piece resumes the session.</summary>
    private async Task EndIdleAsync(string id, SessionRow listed)
    {
        try
        {
            await workers.StopAsync(listed.Id, SessionEndReasons.DesignerIdle, "node", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already: the ledger closes it, and the next piece resumes it either way.
        }
        store.Update(id, r => r with { DesignerIdleSince = null });
        Locks?.Release(id);
    }

    private async Task AskAsync(string id, string? piece)
    {
        try
        {
            await DoAsync(id, piece).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The piece is not lost: BRIEF.md has it, and the designer that does start reads the brief whole.
            store.Update(id, r => r with { DesignerState = DesignerStates.Failed, DesignerWhy = ex.Message });
        }
    }

    private async Task DoAsync(string id, string? piece)
    {
        var row = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        var name = NameOf(row.Id);
        string[] pending = piece is { Length: > 0 } ? [.. row.DesignerTyped, .. row.DesignerPending, piece] : [.. row.DesignerTyped, .. row.DesignerPending];

        if (gate.HoldBecause($"design-ask {row.Id}", models.StartLimitFor(row.Repo ?? ""), models.WeekLimit, HeldKind, Verbs.DesignTarget(row.Id),
                $"{row.Id} waits to start its designer", models.WeekPaceFor(row.Repo ?? "")) is { } held)
        {
            store.Update(row.Id, r => r with { DesignerName = name, DesignerState = DesignerStates.Held, DesignerHeldUntil = held.Resets, DesignerWhy = gate.HeldAnswer(held), DesignerPending = pending, DesignerTyped = [] });
            return;
        }

        var live = state.Sessions().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && s.State is not SessionState.Failed && !s.Gone);
        if (live is not null || row.DesignerJob is { Length: > 0 } && live is null && StartedRecently(row))
        {
            if (live is null)
            {
                // Started, and not listed yet: the pieces wait for it, and a sweep delivers them.
                store.Update(row.Id, r => r with { DesignerState = DesignerStates.Working, DesignerIdleSince = null, DesignerHeldUntil = null, DesignerWhy = null, DesignerPending = pending, DesignerTyped = [] });
                return;
            }
            if (pending.Length > 0)
            {
                if (Say is null)
                {
                    store.Update(row.Id, r => r with { DesignerPending = pending, DesignerTyped = [] });
                    return;
                }
                Locks?.Want(row.Id);
                var said = await Say(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(live.Id), Args = [Briefs.DesignerPiece(row.Id, string.Join("\n\n", pending))], Who = "node" }, CancellationToken.None).ConfigureAwait(false);
                if (!said.Ok)
                {
                    // Kept for the next sweep, with the reason on the design.
                    store.Update(row.Id, r => r with { DesignerState = DesignerStates.Failed, DesignerWhy = said.Error ?? "the say was refused", DesignerPending = pending, DesignerTyped = [] });
                    return;
                }
            }
            store.Update(row.Id, r => r with { DesignerJob = live.Id, DesignerState = DesignerStates.Working, DesignerIdleSince = null, DesignerHeldUntil = null, DesignerWhy = null, DesignerPending = [], DesignerTyped = [], DesignerSessions = WithSession(r.DesignerSessions, live.SessionId), DesignerPhase = pending.Length > 0 ? DesignerPhases.Drafting : r.DesignerPhase,
                DesignerTurnStartedAt = pending.Length > 0 ? time.GetUtcNow() : r.DesignerTurnStartedAt, DesignerTurnPublished = pending.Length > 0 ? null : r.DesignerTurnPublished });
            return;
        }

        // None up: start the one designer, in the design's folder, with the contract and the brief so far.
        var detail = store.Detail(row.Id);
        var prototype = detail.CurrentFolder is { } current && File.Exists(Path.Combine(current, DesignStore.PrototypeFile))
            ? DesignFiles.AssembledJson(Path.Combine(current, DesignStore.PrototypeFile), out _) ?? File.ReadAllText(Path.Combine(current, DesignStore.PrototypeFile))
            : DesignFiles.Join(DesignStore.StarterPrototype, [DesignStore.StarterScreen]);
        var notes = detail.CurrentFolder is { } folder && File.Exists(Path.Combine(folder, DesignStore.DocumentFile))
            ? File.ReadAllText(Path.Combine(folder, DesignStore.DocumentFile))
            : null;
        var brief = Briefs.Designer(detail, prototype, notes, Run);
        if (row.DesignerTyped.Length > 0)
        {
            brief += "\n" + string.Join("\n\n", row.DesignerTyped) + "\n";
        }
        Locks?.Want(row.Id);
        var job = await workers.StartAsync(new WorkerStart(name, store.FolderOf(row.Id), null, brief, models.Model(RoleModels.Designer), models.Effort(RoleModels.Designer), Reason: SessionStartReasons.Designer, Settings: GuardSettings), CancellationToken.None).ConfigureAwait(false);
        store.Update(row.Id, r => r with { DesignerName = name, DesignerJob = job, DesignerState = DesignerStates.Working, DesignerIdleSince = null, DesignerHeldUntil = null, DesignerWhy = null, DesignerPending = [], DesignerTyped = [], DesignerStartedAt = time.GetUtcNow(), DesignerTurnStartedAt = time.GetUtcNow(), DesignerTurnPublished = null, DesignerPhase = DesignerPhases.Drafting });
    }

    private static string[] WithSession(string[] have, string? session) =>
        session is { Length: > 0 } && !have.Contains(session, StringComparer.Ordinal) ? [.. have, session] : have;

    private bool StartedRecently(DesignRow row) => row.DesignerStartedAt is { } at && time.GetUtcNow() - at < ListedWithin;
}

/// <summary>Takes up what waits for a designer (<see cref="Designers.Sweep"/>) every half minute: a hold whose usage limit has ended, pieces for a designer that has come up.</summary>
public sealed class DesignerSweep(Designers designers) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                designers.Sweep();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
