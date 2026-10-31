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

    private readonly TaskCompletionSource _firstSweep = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the sweep at start is over (it ran, failed, or the node stopped). A test node awaits it before it hands out
    /// its state, so that sweep never runs against a test's: on CI it told the keeper of the Opus count that its node ran no session
    /// after the test had taken a slot, and freed it (AdmissionFleetTests on windows, #546's run 41183059493).
    /// </summary>
    public Task FirstSweep => _firstSweep.Task;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        // The first sweep is at node start, not a minute on: a flight an update left with a head no review read is taken at once.
        var first = true;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    if (!first)
                    {
                        await Task.Delay(Interval, time, stopping);
                    }
                    first = false;
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
                _firstSweep.TrySetResult();
            }
        }
        finally
        {
            _firstSweep.TrySetResult();
        }
    }
}
