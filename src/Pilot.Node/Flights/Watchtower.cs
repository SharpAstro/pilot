using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's one watchtower session, <c>&lt;machine&gt;-watchtower</c>, kept running while there is something to watch
/// (<see cref="Watching"/>: a flight not landed, a job, a ring): started with Remote Control on so
/// the controller reaches it from claude.ai or a phone, briefed from the node's state, and fed every Ring and For-you
/// card as it opens. One that ended or failed is started again; one the usage limit stopped is resumed once the limit
/// resets. A watchtower that cannot be started rings, never stays silent.
/// <para>
/// It keeps its conversation: what it did and told the controller is worth more than a fresh brief. Long idle, or grown
/// past its share of the window, it is compacted instead (<see cref="Compactor"/>), so it wakes on a summary rather than
/// re-reading a long conversation whose prompt cache has gone cold. Stalls are the node's to find (<see cref="LegWatch"/>),
/// so it is not sent on rounds to keep its cache warm.
/// </para>
/// <para>
/// With nothing to watch it costs nothing: none is started, and one idle <see cref="QuietFor"/> after the last thing to
/// watch is stopped. A round every 50 minutes with no flight in the air spent 3.5M tokens in a day on "quiet". The next
/// flight, job or ring starts a fresh one, briefed from the state: cheaper than waking an old conversation whose cache
/// has gone cold.
/// </para>
/// </summary>
/// <remarks>
/// Which cards it was told is kept in memory: a node restarted finds its watchtower by name and takes the open cards
/// as told, since that session saw them; a fresh one has them in its brief.
/// <para>
/// Which brief it was started with is kept in a file (<see cref="BriefFile"/>), as a fingerprint of the brief's fixed part
/// (<see cref="Briefs.WatchtowerFingerprint"/>), because a say resumes the session and so keeps the brief it had: a node
/// updated to a changed brief finds one still running on the old, and starts a fresh one in its place before telling it
/// anything (#22). One whose start the node has no record of is taken as briefed by an older node.
/// </para>
/// </remarks>
public sealed class Watchtower(
    NodeState state, IWorkerLauncher launcher, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, NodeOptions options, TimeProvider time) : BackgroundService
{
    public const string Down = "watchtower-down";

    /// <summary>The model and effort the watchtower starts on; a test's watchtower passes none.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>What holds a start while the 5-hour window is spent: the node's one gate, shared with the leg sweep and the compactor.</summary>
    public StartGate Gate { get; set; } = new(state, time);

    public const string Stopped = "watchtower-stopped";

    /// <summary>The Handled card that says its start waits for the 5-hour window.</summary>
    public const string Waits = "watchtower-waits";

    /// <summary>What its own raises are: a card it raised is never said back to it.</summary>
    public const string RaisedKind = "watchtower";

    /// <summary>How often it looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long it is kept once there is nothing to watch, idle, before it is stopped.</summary>
    public TimeSpan QuietFor { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long after a failed start the next one is tried.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long after its limit resets a stopped watchtower is resumed.</summary>
    public TimeSpan ResetMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a started watchtower has to show up in the listing before it counts as gone.</summary>
    public TimeSpan ShowUpGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How it runs the CLI, socket and all: <see cref="Briefs.Run"/>.</summary>
    public string Pilot { get; init; } = "pilot";

    /// <summary>Completes when the session tracker has had its first look: before it, a running watchtower looks missing.</summary>
    public Func<Task> Ready { get; init; } = () => Task.CompletedTask;

    /// <summary>Its session name, <c>du1-watchtower</c>.</summary>
    public string Name => $"{options.Machine.ToLowerInvariant()}-watchtower";

    /// <summary>Where the fingerprint of the brief the running watchtower was started with is kept, in the node's data folder.</summary>
    public string BriefFile => Path.Combine(options.DataFolder, "watchtower-brief");

    private readonly SemaphoreSlim _one = new(1, 1);
    private string? _briefed;
    private bool _briefedRead;
    private readonly HashSet<string> _told = new HashSet<string>(StringComparer.Ordinal);
    private string? _job;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _nextTry = DateTimeOffset.MinValue;
    private DateTimeOffset? _resumedAfter;
    private bool _adopted;
    // When there was last something to watch; the node's start counts, so one running then is kept QuietFor first.
    private DateTimeOffset? _watchingAt;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Ready();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await LookAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // As with the leg watch: a keeper that died on one bad look would be the silent derail itself.
                await Console.Error.WriteLineAsync($"watchtower: one look failed, looking again: {ex.Message}");
            }
        }
    }

    /// <summary>The watchtower's row now: the one it started, else the newest of its name, live first.</summary>
    public SessionRow? Current()
    {
        var rows = state.Sessions().Where(s => s.Name == Name).ToList();
        return rows.FirstOrDefault(r => r.Id == _job)
            ?? rows.OrderByDescending(Live).ThenByDescending(r => r.StartedAt).FirstOrDefault();
    }

    private static bool Live(SessionRow row) => row.State is SessionState.Working or SessionState.Waiting or SessionState.Idle;

    /// <summary>
    /// What there is to watch, in words, or null for nothing: a flight not landed, a job running or waiting, a ring open
    /// (other than about the watchtower itself).
    /// </summary>
    public string? Watching()
    {
        if (state.Flights().FirstOrDefault(f => f.State != FlightState.Landed) is { } flight)
        {
            return $"{flight.Code} is {(flight.State == FlightState.Held ? "held" : "in the air")}";
        }
        if (state.Jobs().FirstOrDefault(j => j.State is JobState.Running or JobState.Queued) is { } job)
        {
            return $"job {job.Id} is {(job.State == JobState.Running ? "running" : "waiting")}";
        }
        return state.Cards(openOnly: true).FirstOrDefault(c => c.Bin == CardBin.Ring && c.Subject != Verbs.WatchtowerTarget) is { } ring
            ? $"{ring.Id} rings"
            : null;
    }

    /// <summary>One look: keeps it running while there is something to watch, and tells it the cards it has not heard of.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        if (!options.Watchtower)
        {
            return;
        }
        await _one.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var row = Current();
            var watching = Watching();
            if (watching is not null || _watchingAt is null)
            {
                _watchingAt = now;
            }
            if (!_adopted)
            {
                // A watchtower already running when the node starts saw the cards open now.
                _adopted = true;
                if (row is not null && Live(row))
                {
                    _job = row.Id;
                    TellAllOpen();
                }
            }

            if (watching is null)
            {
                // Nothing to watch: none is started, and an idle one goes once it has been quiet long enough.
                if (row is { State: SessionState.Idle } && now - _watchingAt >= QuietFor)
                {
                    await StopQuietAsync(row, cancellationToken);
                }
                return;
            }

            if (row is { ApiError: { } error } && row.State != SessionState.Working)
            {
                await StoppedAsync(row, error, now, cancellationToken);
                return;
            }
            if (row is null || !Live(row))
            {
                if (_startedAt is { } started && now - started < ShowUpGrace && (row is null || row.Id != _job))
                {
                    return;
                }
                // Kept by the node by itself, so held while the 5-hour window is spent, and started by the next look once it has reset;
                // a restart asked for (restart-watchtower, or the card's Try now) is a person's, and goes ahead.
                if (now >= _nextTry && !Gate.Hold("watchtower", Models.StartLimit, Models.WeekLimit, Waits, Verbs.WatchtowerTarget, "The watchtower waits to start"))
                {
                    await StartAsync($"{watching}, and {(row is null ? "none was running" : $"the last one {row.State.ToString().ToLowerInvariant()}")}", cancellationToken);
                }
                return;
            }

            state.CloseCardsAbout(Verbs.WatchtowerTarget, "it is running", Down);
            if (Briefed() != BriefFingerprint())
            {
                // A say would resume it on its old brief: a fresh one is told the new. A start that failed is tried again
                // after the retry time, as any other start is, not stopped and started on every look.
                if (now >= _nextTry)
                {
                    await StartAsync("its brief changed since the node started it", cancellationToken, row);
                }
                return;
            }
            await TellNewCardsAsync(row, cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary><c>restart-watchtower</c>: a fresh watchtower in place of the one running, if any.</summary>
    public async Task<string?> RestartAsync(string why, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var row = Current();
            return await StartAsync(why, cancellationToken, row is not null && Live(row) ? row : null) ? null : "the watchtower did not start; its card says why";
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The target a <c>tell</c> goes to: the watchtower's row, or why there is none to tell.</summary>
    public (string? Target, string? Error) TellTarget()
    {
        var row = Current();
        return row is not null && (Live(row) || row.State == SessionState.Ended)
            ? (Verbs.SessionTarget(row.Id), null)
            : (null, options.Watchtower ? "the watchtower is not running; the node is starting one" : "this node keeps no watchtower (--no-watchtower)");
    }

    private async Task StoppedAsync(SessionRow row, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (row.ApiErrorResetsAt is not { } resets)
        {
            state.RaiseCard(CardBin.Ring, Stopped, Verbs.WatchtowerTarget, "The watchtower was stopped by the API", $"{error}, with no time it clears",
                [new CardChoice { Label = "Restart it", Verb = Verbs.RestartWatchtower, Target = Verbs.WatchtowerTarget, Args = ["stopped by the API"] }]);
            return;
        }
        if (now < resets + ResetMargin)
        {
            state.RaiseCard(CardBin.ForYou, Stopped, Verbs.WatchtowerTarget, $"The watchtower hit the usage limit; it resumes at {resets.ToLocalTime():HH:mm}",
                $"{error}; until then no card reaches it, so rings come straight to you");
            return;
        }
        if (_resumedAfter is { } done && done >= resets)
        {
            // Resumed once for this reset to no effect: a fresh one instead.
            await StartAsync("it stayed stopped after its limit reset", cancellationToken, row);
            return;
        }

        _resumedAfter = resets;
        var result = await run(new CommandRequest
        {
            Verb = Verbs.Say,
            Target = Verbs.SessionTarget(row.Id),
            Args = ["The usage limit that stopped you has reset. Carry on; `" + Pilot + " cards` shows what opened meanwhile."],
            Who = LegWatch.Who,
        }, cancellationToken);
        if (result.Ok)
        {
            state.CloseCardsAbout(Verbs.WatchtowerTarget, "resumed after the limit reset", Stopped);
            state.RaiseCard(CardBin.Handled, "watchtower-resumed", Verbs.WatchtowerTarget, "The watchtower resumed after its usage limit reset");
        }
    }

    /// <summary>Starts a fresh watchtower, stopping <paramref name="replacing"/> first; false, with a ring, when it could not.</summary>
    private async Task<bool> StartAsync(string why, CancellationToken cancellationToken, SessionRow? replacing = null)
    {
        if (!options.Watchtower)
        {
            return false;
        }
        if (replacing is not null)
        {
            try
            {
                await launcher.StopAsync(replacing.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already: the fresh one starts all the same.
            }
        }

        var folder = options.WatchtowerFolder;
        string fingerprint;
        try
        {
            Directory.CreateDirectory(folder);
            var brief = Briefs.Watchtower(Name, state.Flights(), state.Legs(), state.Cards(openOnly: true), Pilot, Models.Model(RoleModels.Watchtower));
            fingerprint = BriefFingerprint();
            _job = await launcher.StartAsync(new WorkerStart(Name, folder, null, brief, Model: Models.Model(RoleModels.Watchtower), Effort: Models.Effort(RoleModels.Watchtower), RemoteControl: true), cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _nextTry = time.GetUtcNow() + RetryAfter;
            var untrusted = ex.Message.Contains("not trusted", StringComparison.OrdinalIgnoreCase);
            state.RaiseCard(CardBin.Ring, Down, Verbs.WatchtowerTarget, "The watchtower could not be started",
                untrusted
                    ? $"Claude Code starts no background session in a folder never trusted. Run `claude` in {folder} once and accept the prompt (or start the node with --watchtower-folder <a trusted folder>); the node tries again every {RetryAfter.TotalMinutes:0} minutes."
                    : $"{ex.Message}; the node tries again in {RetryAfter.TotalMinutes:0} minutes",
                [new CardChoice { Label = "Try now", Verb = Verbs.RestartWatchtower, Target = Verbs.WatchtowerTarget, Args = ["tried again from its card"] }]);
            return false;
        }

        _startedAt = time.GetUtcNow();
        _resumedAfter = null;
        RecordBrief(fingerprint);
        TellAllOpen();
        state.CloseCardsAbout(Verbs.WatchtowerTarget, "a fresh one started", Down, Stopped);
        state.RaiseCard(CardBin.Handled, "watchtower-started", Verbs.WatchtowerTarget, $"Started a fresh watchtower: {why}");
        return true;
    }

    /// <summary>Stops a watchtower with nothing left to watch, and says so; the next thing to watch starts a fresh one.</summary>
    private async Task StopQuietAsync(SessionRow row, CancellationToken cancellationToken)
    {
        try
        {
            await launcher.StopAsync(row.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already: there is nothing to stop.
        }
        _job = null;
        _startedAt = null;
        state.RaiseCard(CardBin.Handled, "watchtower-parked", Verbs.WatchtowerTarget,
            $"Stopped the watchtower: nothing to watch for {QuietFor.TotalMinutes:0} minutes; the next flight, job or ring starts a fresh one");
    }

    private string BriefFingerprint() => Briefs.WatchtowerFingerprint(Name, Pilot, Models.Model(RoleModels.Watchtower));

    /// <summary>The fingerprint of the brief the running watchtower was started with, or null when the node has no record of it.</summary>
    private string? Briefed()
    {
        if (!_briefedRead)
        {
            _briefedRead = true;
            try
            {
                _briefed = File.Exists(BriefFile) ? File.ReadAllText(BriefFile).Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable counts as no record: one fresh watchtower, rather than one on a brief nobody can vouch for.
            }
        }
        return _briefed;
    }

    private void RecordBrief(string fingerprint)
    {
        _briefed = fingerprint;
        _briefedRead = true;
        try
        {
            Directory.CreateDirectory(options.DataFolder);
            File.WriteAllText(BriefFile, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory all the same; a restarted node then finds no record and starts one afresh.
        }
    }

    /// <summary>Takes every open card as told: a fresh watchtower has them in its brief.</summary>
    private void TellAllOpen()
    {
        foreach (var card in state.Cards(openOnly: true))
        {
            _told.Add(card.Id);
        }
    }

    /// <summary>Says the cards it has not heard of to it, in one message; true when there were any to say.</summary>
    private async Task<bool> TellNewCardsAsync(SessionRow row, CancellationToken cancellationToken)
    {
        var fresh = state.Cards(openOnly: true)
            .Where(c => c.Bin != CardBin.Handled && c.Kind != RaisedKind && c.Subject != Verbs.WatchtowerTarget && !_told.Contains(c.Id))
            .OrderBy(c => c.At)
            .ToList();
        if (fresh.Count == 0)
        {
            return false;
        }

        var text = (fresh.Count == 1 ? "A card opened:" : $"{fresh.Count} cards opened:") + "\n" + string.Join("\n", fresh.Select(Briefs.CardLine))
            + "\nHandle what is yours; leave the rest open for the controller.";
        var result = await run(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = [text], Who = LegWatch.Who }, cancellationToken);
        if (result.Ok)
        {
            foreach (var card in fresh)
            {
                _told.Add(card.Id);
            }
        }
        return true;
    }
}
