using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Gui;

/// <summary>
/// What a flight's page reads from the node's decisions route: the flight's decisions, oldest first. A read runs on the pool and is
/// asked for again when the page opens, once it is <see cref="Every"/> old, and when a decision for that flight arrives on the stream.
/// </summary>
internal sealed class DecisionReads(NodeClient client, Action redraw)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (DecisionRow[] Rows, DateTimeOffset At)> _flights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _busy = new(StringComparer.Ordinal);
    private long _seen;

    /// <summary>The decisions read so far, by flight code.</summary>
    public IReadOnlyDictionary<string, DecisionRow[]> Flights => _flights.ToDictionary(p => p.Key, p => p.Value.Rows, StringComparer.Ordinal);

    /// <summary>Reads <paramref name="code"/>'s decisions, unless they were read less than <see cref="Every"/> ago.</summary>
    public void AskFlight(string code, bool force = false)
    {
        if (!force && _flights.TryGetValue(code, out var held) && DateTimeOffset.UtcNow - held.At < Every)
        {
            return;
        }
        if (!_busy.TryAdd(code, 1))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await client.GetDecisionsAsync(code, null, null, CancellationToken.None).ConfigureAwait(false);
                if (answer.Response is { } report)
                {
                    _flights[code] = (report.Decisions, DateTimeOffset.UtcNow);
                    redraw();
                }
            }
            catch (Exception)
            {
                // The node did not answer: the flight's page shows what it had.
            }
            finally
            {
                _busy.TryRemove(code, out _);
            }
        });
    }

    /// <summary>A decision arrived on the stream: reads again the flight it belongs to, when that is the open page.</summary>
    public void Saw(DecisionRow? latest, string? openCode)
    {
        if (latest is null || latest.Seq == Interlocked.Exchange(ref _seen, latest.Seq))
        {
            return;
        }
        if (openCode is not null && latest.Subject == $"flight/{openCode}")
        {
            AskFlight(openCode, force: true);
        }
    }
}
