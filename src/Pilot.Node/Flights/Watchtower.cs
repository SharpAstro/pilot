using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's one watchtower session, <c>&lt;machine&gt;-watchtower</c>, kept running: started with Remote Control on so
/// the controller reaches it from claude.ai or a phone, briefed from the node's state, and fed every Ring and For-you
/// card as it opens. One that ended or failed is started again; one the usage limit stopped is resumed once the limit
/// resets; one whose context has grown past <see cref="ContextLimit"/> is replaced, idle, by a fresh one briefed anew,
/// so it never drags a long conversation along (#36). A watchtower that cannot be started rings, never stays silent.
/// <para>
/// Idle for <see cref="RoundEvery"/>, it is sent on a round: it looks at where everything stands and acts on what
/// stalled. That turn also keeps its prompt cache warm: the cache lives an hour, and a watchtower woken after that
/// reads its whole context again at full price, which is what made v1's long-lived sessions dear.
/// </para>
/// </summary>
/// <remarks>
/// Which cards it was told is kept in memory: a node restarted finds its watchtower by name and takes the open cards
/// as told, since that session saw them; a fresh one has them in its brief.
/// </remarks>
public sealed class Watchtower(
    NodeState state, IWorkerLauncher launcher, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, NodeOptions options, TimeProvider time) : BackgroundService
{
    public const string Down = "watchtower-down";
    public const string Stopped = "watchtower-stopped";

    /// <summary>What its own raises are: a card it raised is never said back to it.</summary>
    public const string RaisedKind = "watchtower";

    /// <summary>How often it looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The share of its context window past which an idle watchtower is replaced by a fresh one.</summary>
    public double ContextLimit { get; init; } = 0.7;

    /// <summary>How long it sits idle before a round: inside the prompt cache's hour, so the round finds it warm.</summary>
    public TimeSpan RoundEvery { get; init; } = TimeSpan.FromMinutes(50);

    public const string RoundText = "Round: look at where everything stands (status and cards). Act on anything stalled or waiting that is yours, "
        + "ring for what needs the controller, and otherwise end your turn with one word: quiet.";

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

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly HashSet<string> _told = new HashSet<string>(StringComparer.Ordinal);
    private string? _job;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _nextTry = DateTimeOffset.MinValue;
    private DateTimeOffset? _resumedAfter;
    private DateTimeOffset? _roundAt;
    private bool _adopted;

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

    /// <summary>One look: keeps it running and tells it the cards it has not heard of.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var row = Current();
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
                if (now >= _nextTry)
                {
                    await StartAsync(row is null ? "none was running" : $"the last one {row.State.ToString().ToLowerInvariant()}", cancellationToken);
                }
                return;
            }

            state.CloseCardsAbout(Verbs.WatchtowerTarget, "it is running", Down);
            if (row.State == SessionState.Idle && row.ContextWindow > 0 && row.ContextTokens > ContextLimit * row.ContextWindow)
            {
                await StartAsync($"its context reached {100.0 * row.ContextTokens / row.ContextWindow:0}% of its window", cancellationToken, row);
                return;
            }
            if (!await TellNewCardsAsync(row, cancellationToken) && row.State == SessionState.Idle
                && now - (row.LastResponseAt ?? row.StartedAt) >= RoundEvery && (_roundAt is null || now - _roundAt >= RoundEvery))
            {
                _roundAt = now;
                await run(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = [RoundText], Who = LegWatch.Who }, cancellationToken);
            }
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
        try
        {
            Directory.CreateDirectory(folder);
            var brief = Briefs.Watchtower(Name, state.Flights(), state.Legs(), state.Cards(openOnly: true), Pilot);
            _job = await launcher.StartAsync(new WorkerStart(Name, folder, null, brief, Model: null, Effort: null, RemoteControl: true), cancellationToken);
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
        TellAllOpen();
        state.CloseCardsAbout(Verbs.WatchtowerTarget, "a fresh one started", Down, Stopped);
        state.RaiseCard(CardBin.Handled, "watchtower-started", Verbs.WatchtowerTarget, $"Started a fresh watchtower: {why}");
        return true;
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
