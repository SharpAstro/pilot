using System.Collections.Concurrent;
using Pilot.Contracts;
using Pilot.Ui;

namespace Pilot.Gui;

/// <summary>
/// What the Spend page and a flight's page read from the node's spend routes: the window's report and waste, and a flight's spend so
/// far. A read runs on the pool, is kept for <see cref="Every"/> and asked for again once it is older, so the page shows the ledger
/// as it grows without a read on every frame.
/// </summary>
internal sealed class SpendReads(NodeClient client, Action redraw)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private SpendScreen? _screen;
    private DateTimeOffset _screenAt;
    private int _screenBusy;
    private readonly ConcurrentDictionary<string, (SpendFlightReport Report, DateTimeOffset At)> _flights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _flightBusy = new(StringComparer.Ordinal);

    /// <summary>The page as last read; null before the first answer.</summary>
    public SpendScreen? Screen => Volatile.Read(ref _screen);

    private SpendReport? _today;
    private DateTimeOffset _todayAt;
    private int _todayBusy;

    /// <summary>Today's report as last read, for the Home page's line of the flights that spent the most; null before the first answer.</summary>
    public SpendReport? Today => Volatile.Read(ref _today);

    /// <summary>Reads today's report, unless it was read less than <see cref="Every"/> ago.</summary>
    public void AskToday()
    {
        if (DateTimeOffset.UtcNow - _todayAt < Every || Interlocked.Exchange(ref _todayBusy, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var report = await client.GetSpendAsync("today", CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _today, report.Response);
                redraw();
            }
            catch (Exception)
            {
                // The node did not answer: Home keeps the line it had and asks again after Every.
            }
            finally
            {
                _todayAt = DateTimeOffset.UtcNow;
                Volatile.Write(ref _todayBusy, 0);
            }
        });
    }

    private IReadOnlyDictionary<string, long>? _tokens;
    private DateTimeOffset _tokensAt;
    private int _tokensBusy;

    /// <summary>Every flight's tokens so far as last read; null before the first answer, when the snapshot's figures stand.</summary>
    public IReadOnlyDictionary<string, long>? Tokens => Volatile.Read(ref _tokens);

    /// <summary>Reads every flight's tokens in one call, unless that was done less than <see cref="Every"/> ago.</summary>
    public void AskTokens()
    {
        if (DateTimeOffset.UtcNow - _tokensAt < Every || Interlocked.Exchange(ref _tokensBusy, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await client.GetSpendTokensAsync(CancellationToken.None).ConfigureAwait(false);
                if (answer.Response is { } tokens)
                {
                    Volatile.Write(ref _tokens, tokens);
                    redraw();
                }
            }
            catch (Exception)
            {
                // The node did not answer: the rows keep the figures they had and ask again after Every.
            }
            finally
            {
                _tokensAt = DateTimeOffset.UtcNow;
                Volatile.Write(ref _tokensBusy, 0);
            }
        });
    }

    /// <summary>The flights read so far, for the flight page.</summary>
    public IReadOnlyList<SpendFlightReport> Flights => [.. _flights.Values.Select(v => v.Report)];

    /// <summary>Reads <paramref name="window"/>'s report and waste, unless they were read for it less than <see cref="Every"/> ago.</summary>
    public void Ask(string window, string source, bool force = false)
    {
        var held = Screen;
        if (!force && held?.Window == window && DateTimeOffset.UtcNow - _screenAt < Every)
        {
            return;
        }
        if (Interlocked.Exchange(ref _screenBusy, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var report = await client.GetSpendAsync(window, CancellationToken.None).ConfigureAwait(false);
                var waste = await client.GetSpendWasteAsync(window, CancellationToken.None).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                Volatile.Write(ref _screen, new SpendScreen(window, report.Response, waste.Response, source, now));
                _screenAt = now;
                redraw();
            }
            catch (Exception)
            {
                // The node did not answer: the page keeps what it had and asks again after Every.
                _screenAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                Volatile.Write(ref _screenBusy, 0);
            }
        });
    }

    /// <summary>Reads <paramref name="code"/>'s spend so far, unless it was read less than <see cref="Every"/> ago.</summary>
    public void AskFlight(string code, bool force = false)
    {
        if (!force && _flights.TryGetValue(code, out var held) && DateTimeOffset.UtcNow - held.At < Every)
        {
            return;
        }
        if (!_flightBusy.TryAdd(code, 1))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await client.GetSpendFlightAsync(code, CancellationToken.None).ConfigureAwait(false);
                if (answer.Response is { } report)
                {
                    _flights[code] = (report, DateTimeOffset.UtcNow);
                    redraw();
                }
            }
            catch (Exception)
            {
                // The node did not answer: the flight's page shows no spend until it does.
            }
            finally
            {
                _flightBusy.TryRemove(code, out _);
            }
        });
    }
}
