using System.Collections.Concurrent;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// An <see cref="IWorkerLauncher"/> that says in node.log every session it starts (role, name, job, model, effort, brief
/// length) and every start it refuses, and then keeps an eye on the record Claude Code keeps of each job it started
/// (<see cref="ClaudeJobState"/>): a job whose record reads failed is logged once, with its state and detail, and the detail
/// stays on hand (<see cref="FailureOf"/>) for a card to quote.
/// </summary>
public sealed class LoggingWorkerLauncher(IWorkerLauncher inner, TextWriter log, ClaudeJobState jobs, TimeProvider time) : IWorkerLauncher
{
    /// <summary>How long a started job is looked at for a failure; one that has run this long is up.</summary>
    public static readonly TimeSpan LookFor = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (string Role, string Name, DateTimeOffset At)> _watching = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ClaudeJobRecord> _failed = new(StringComparer.Ordinal);

    public async Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
    {
        var role = Role(start.Name);
        string job;
        try
        {
            job = await inner.StartAsync(start, cancellationToken);
        }
        catch (FlightException ex)
        {
            await Say($"start refused: role={role} name={start.Name} {ex.Message}");
            throw;
        }
        _watching[job] = (role, start.Name, time.GetUtcNow());
        await Say($"start: role={role} name={start.Name} job={job} model={start.Model ?? "default"} effort={start.Effort ?? "default"} brief={start.Brief.Length} chars");
        return job;
    }

    public Task StopAsync(string job, CancellationToken cancellationToken)
    {
        _watching.TryRemove(job, out _);
        return inner.StopAsync(job, cancellationToken);
    }

    /// <summary>What Claude Code's record of job <paramref name="job"/> said when it failed, as this launcher saw it; null otherwise.</summary>
    public ClaudeJobRecord? FailureOf(string? job) => job is not null && _failed.TryGetValue(job, out var record) ? record : null;

    /// <summary>Looks at the record of each job started and not yet failed; logs a failed one once.</summary>
    public async Task LookAsync()
    {
        foreach (var (job, (role, name, at)) in _watching)
        {
            if (jobs.Read(job) is { Failed: true } record)
            {
                if (_watching.TryRemove(job, out _))
                {
                    _failed[job] = record;
                    await Say($"start failed: role={role} name={name} job={job} state={record.State} detail={record.Detail ?? "none"}");
                }
            }
            else if (time.GetUtcNow() - at > LookFor)
            {
                _watching.TryRemove(job, out _);
            }
        }
    }

    private static string Role(string name) => SessionRoles.Resolve(name).Role.ToString().ToLowerInvariant();

    private async Task Say(string line)
    {
        try
        {
            await log.WriteLineAsync(line);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>Has <see cref="LoggingWorkerLauncher"/> look at the records of the jobs it started, every few seconds.</summary>
public sealed class StartWatch(IWorkerLauncher launcher) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (launcher is not LoggingWorkerLauncher logging)
        {
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await logging.LookAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
