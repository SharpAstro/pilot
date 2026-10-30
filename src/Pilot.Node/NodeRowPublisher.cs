using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Puts what the node says it is (<see cref="NodeRow"/>) every couple of seconds; <see cref="NodeState.PutNode"/> publishes only
/// a meaningful change, so a quiet node adds nothing to the journal. A tick on the node's own clock, rather than a hook in
/// <see cref="RoleModels"/> and the watchtower, because the watchtower's health moves with the clock as well as with its events
/// and the settings have no change event of their own.
/// </summary>
public sealed class NodeRowPublisher(NodeState state, Func<NodeRow> read, TimeProvider time) : BackgroundService
{
    /// <summary>How long between looks; the poll it replaces looked every 3 s.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    public ILogger<NodeRowPublisher>? Logger { get; init; }

    /// <summary>Looks once and puts the row; false when reading it failed.</summary>
    public bool Tick()
    {
        try
        {
            state.PutNode(read());
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogWarning(ex, "Reading what the node says it is failed; trying again");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            Tick();
            try
            {
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
