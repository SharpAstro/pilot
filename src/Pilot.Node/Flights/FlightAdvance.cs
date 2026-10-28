namespace Pilot.Node.Flights;

/// <summary>
/// Takes every flying flight's next step once a minute (<see cref="LegCommander.AdvanceAllAsync"/>): a leg done starts the
/// next, the last opens the PR and its review. A leg's finish takes the step at once; this is what takes one the account's
/// 5-hour window held, once it resets, and one a node restart came between.
/// </summary>
public sealed class FlightAdvance(LegCommander legs, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await legs.AdvanceAllAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that failed once is tried again a minute later: a flight that never advances is the stall itself.
                await Error.WriteLineAsync($"flight advance: one sweep failed, sweeping again: {ex.Message}");
            }
        }
    }
}
