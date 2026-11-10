namespace Pilot.Node.Tests;

/// <summary>
/// Told of every change of a <see cref="NodeState"/> (subscribe it as a sink), so a test waits for the state's next change instead
/// of looking again after a pause on the wall clock (#933). A wait is bounded by a ceiling a busy machine does not reach, never by
/// a pace.
/// </summary>
internal sealed class StateChanges : INodeEventSink
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes at the next change; taken before a look at the state, so a change between the look and the wait is not missed.</summary>
    public Task Next => Volatile.Read(ref _next).Task;

    public int Capacity => 10_000;

    public bool TryWrite(byte[] message)
    {
        Changed();
        return true;
    }

    public void Overflowed() => Changed();

    public void Changed() => Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Subscribes a new <see cref="StateChanges"/> to <paramref name="state"/>; dispose the subscription when the test is done with it.</summary>
    public static (StateChanges Changes, IDisposable Subscription) Watch(NodeState state)
    {
        var changes = new StateChanges();
        return (changes, state.Subscribe(null, null, changes));
    }

    /// <summary>
    /// Returns once <paramref name="condition"/> holds, looking again at each change of the state. Throws a <see cref="TimeoutException"/>
    /// naming <paramref name="what"/> (and what <paramref name="seen"/> says, when given) if it does not within <paramref name="ceiling"/>.
    /// </summary>
    public async Task UntilAsync(Func<bool> condition, TimeSpan ceiling, CancellationToken cancellationToken, string what, Func<string>? seen = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ceiling);
        try
        {
            while (true)
            {
                var next = Next;
                if (condition())
                {
                    return;
                }
                await next.WaitAsync(limit.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{what} did not come within {ceiling}{(seen is null ? "" : ": " + seen())}");
        }
    }
}
