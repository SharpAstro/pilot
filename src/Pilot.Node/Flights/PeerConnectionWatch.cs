namespace Pilot.Node.Flights;

/// <summary>
/// The node's watch on the flights held for a flight of another node (<see cref="Pilot.Contracts.FlightRow.PeerConnections"/>, #375): on a
/// cycle it asks each peer what it flies, drops each wait whose flight landed (or whose PR merged) so the flight flies, and keeps a flight
/// held, with the reason on the wait, while its node cannot be asked. See <see cref="FlightCommander.LookAtPeerConnectionsAsync"/>.
/// </summary>
public sealed class PeerConnectionWatch(FlightCommander flights, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>How often the peers are asked while a flight waits on one.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await flights.LookAtPeerConnectionsAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Outlive anything one look throws: a dead watch holds a flight for good, silently.
                await Error.WriteLineAsync($"peer connection watch: one look failed, looking again: {ex.Message}");
            }
        }
    }
}
