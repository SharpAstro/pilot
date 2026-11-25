using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pilot.Contracts;

namespace Pilot.Node.Toasts;

/// <summary>
/// Shows a desktop toast for every open Ring card (#1356): one per subject (the tag), replaced when the card is raised again, withdrawn when
/// the card closes or leaves the Ring. More than <see cref="BurstAbove"/> normal toasts within <see cref="BurstWindow"/> become one
/// "N cards need you" toast (<see cref="ToastXml.BurstTag"/>), urgent ones always singly. Normal toasts are skipped while a client shows the
/// cards (<see cref="PageViews.AnyShowsCards"/>), and the <c>toasts</c> setting (all, urgent, off) is read at each card.
/// </summary>
/// <remarks>
/// It learns of a change without a loop on the CPU: a sink on <see cref="NodeState.Subscribe"/> only signals a channel (the state calls it
/// under its lock, so it touches nothing else), and one consumer awaits that channel and then diffs the open Ring cards, off the state's lock,
/// calling the runner. <see cref="Sync"/> is that diff, public so a test drives it with no timing at all.
/// </remarks>
public sealed class Toaster : IAsyncDisposable, Microsoft.Extensions.Hosting.IHostedService
{
    /// <summary>The span in which normal toasts count towards a burst.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(1);

    /// <summary>More normal toasts than this within <see cref="BurstWindow"/> become one burst toast.</summary>
    public const int BurstAbove = 3;

    private readonly NodeState _state;
    private readonly IToastRunner _runner;
    private readonly TimeProvider _time;
    private readonly Func<string> _mode;
    private readonly Func<bool> _clientShowsCards;
    private readonly ILogger? _logger;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    // What a card was toasted as: the tag it shows under and what it said, so a raise again (new ring time, title or body) shows again.
    private readonly Dictionary<string, Shown> _shown = new(StringComparer.Ordinal);
    // Cards seen and not toasted (skipped while a client looked, or a tier the setting mutes), by what they said: they never toast later.
    private readonly Dictionary<string, string> _skipped = new(StringComparer.Ordinal);
    // Tags of cards found open at start (taken as seen, not toasted): their toast, if one is still up from before a restart, goes when they leave.
    private readonly Dictionary<string, string> _quietTags = new(StringComparer.Ordinal);
    private IReadOnlyCollection<string> _tags = [];
    private readonly List<(DateTimeOffset At, string Id)> _recent = [];
    private readonly HashSet<string> _burst = new(StringComparer.Ordinal);
    private IDisposable? _subscription;
    private Task? _run;
    private int _disposed;

    private sealed record Shown(string Tag, string Signature, bool InBurst);

    /// <summary>
    /// A toaster over <paramref name="state"/>. <paramref name="mode"/> says the <c>toasts</c> setting now (all, urgent or off);
    /// <paramref name="clientShowsCards"/> whether any client shows the cards now.
    /// </summary>
    public Toaster(NodeState state, IToastRunner runner, TimeProvider time, Func<string> mode, Func<bool> clientShowsCards, ILogger? logger = null)
    {
        _state = state;
        _runner = runner;
        _time = time;
        _mode = mode;
        _clientShowsCards = clientShowsCards;
        _logger = logger;
    }

    /// <summary>The tags shown now (the set a test reads); a card id maps to its tag, so two cards of one subject show one.</summary>
    public IReadOnlyCollection<string> Tags => Volatile.Read(ref _tags);

    /// <summary>Starts listening: the cards open in Ring now are taken as seen (a restart must not toast them again), then every change is toasted.</summary>
    public void Start()
    {
        if (_subscription is not null) return;
        // What is open now is noted at once, before the consumer starts, so a card raised after Start is never taken for one that was there.
        Sync(quiet: true).GetAwaiter().GetResult();
        _subscription = _state.Subscribe(since: null, stream: null, new Sink(this));
        _run = Task.Run(() => RunAsync(_stop.Token));
    }

    private void Signal() => _signal.Writer.TryWrite(true);

    private async Task RunAsync(CancellationToken cancel)
    {
        try
        {
            while (await _signal.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                _signal.Reader.TryRead(out _);
                await Sync(quiet: false, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// Brings the toasts to the open Ring cards: shows what is new or raised again, withdraws what closed or left the Ring. With
    /// <paramref name="quiet"/> a card found is only noted as seen, not toasted.
    /// </summary>
    public async Task Sync(bool quiet = false, CancellationToken cancel = default)
    {
        await _sync.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            try
            {
                await SyncLocked(quiet, cancel).ConfigureAwait(false);
            }
            finally
            {
                // An immutable snapshot, so a reader never enumerates what the consumer is changing.
                Volatile.Write(ref _tags, [.. _shown.Values.Select(s => s.Tag).Distinct(StringComparer.Ordinal)]);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task SyncLocked(bool quiet, CancellationToken cancel)
    {
        {
            var ring = _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring && c.State == CardState.Open).ToDictionary(c => c.Id, StringComparer.Ordinal);
            await WithdrawGone(ring, cancel).ConfigureAwait(false);
            foreach (var card in ring.Values.OrderBy(c => c.RungAt))
            {
                var signature = SignatureOf(card);
                if (_shown.TryGetValue(card.Id, out var shown) && shown.Signature == signature) continue;
                if (_skipped.TryGetValue(card.Id, out var skipped) && skipped == signature) continue;
                if (quiet)
                {
                    _skipped[card.Id] = signature;
                    _quietTags[card.Id] = ToastXml.TagOf(card.Subject);
                    continue;
                }
                await Raise(card, signature, cancel).ConfigureAwait(false);
            }
        }
    }

    private static string SignatureOf(CardRow card) => $"{card.RungAt.UtcTicks}|{card.Title}|{card.Summary}|{card.Detail}";

    private async Task WithdrawGone(Dictionary<string, CardRow> ring, CancellationToken cancel)
    {
        foreach (var id in _skipped.Keys.Where(id => !ring.ContainsKey(id)).ToList())
        {
            _skipped.Remove(id);
        }
        // A card open when the node started may still have its toast up from before the restart: withdraw it (a missing tag is harmless).
        foreach (var (id, tag) in _quietTags.Where(p => !ring.ContainsKey(p.Key)).ToList())
        {
            _quietTags.Remove(id);
            if (!_shown.Values.Any(s => !s.InBurst && s.Tag == tag)) await RemoveTag(tag, cancel).ConfigureAwait(false);
        }
        foreach (var (id, shown) in _shown.Where(p => !ring.ContainsKey(p.Key)).ToList())
        {
            _shown.Remove(id);
            _burst.Remove(id);
            // A tag another card of the subject still holds stays; the burst toast goes with its last card.
            if (shown.InBurst)
            {
                if (_burst.Count == 0) await RemoveTag(ToastXml.BurstTag, cancel).ConfigureAwait(false);
            }
            else if (!_shown.Values.Any(s => !s.InBurst && s.Tag == shown.Tag))
            {
                await RemoveTag(shown.Tag, cancel).ConfigureAwait(false);
            }
        }
    }

    private async Task Raise(CardRow card, string signature, CancellationToken cancel)
    {
        var tier = ToastTier.Of(card.Kind) ?? ToastTier.Normal;
        var mode = _mode();
        // A card muted now (the setting, or a client showing the cards) is not toasted; one toasted before keeps its old toast as it was.
        var muted = mode == "off" || tier == ToastTier.Normal && (mode == "urgent" || _clientShowsCards());
        if (muted)
        {
            _skipped[card.Id] = signature;
            return;
        }
        _skipped.Remove(card.Id);
        _quietTags.Remove(card.Id);

        var tag = ToastXml.TagOf(card.Subject);
        if (tier == ToastTier.Normal)
        {
            var now = _time.GetUtcNow();
            _recent.RemoveAll(r => now - r.At > BurstWindow || !_shown.ContainsKey(r.Id) && r.Id != card.Id);
            _recent.RemoveAll(r => r.Id == card.Id);
            _recent.Add((now, card.Id));
            if (_recent.Count > BurstAbove)
            {
                await Fold(card, signature, cancel).ConfigureAwait(false);
                return;
            }
        }
        // A card moved from a burst to a single (raised again after the window) leaves the burst's count.
        if (_shown.TryGetValue(card.Id, out var before) && before.InBurst)
        {
            _burst.Remove(card.Id);
            if (_burst.Count == 0) await RemoveTag(ToastXml.BurstTag, cancel).ConfigureAwait(false);
        }
        _shown[card.Id] = new Shown(tag, signature, InBurst: false);
        await ShowTag(tag, ToastXml.ForCard(card.Id, card.Title, card.Summary ?? card.Detail, tier), cancel).ConfigureAwait(false);
    }

    // The window holds more than BurstAbove normal rings: they are one toast now, the singles shown in it withdrawn.
    private async Task Fold(CardRow card, string signature, CancellationToken cancel)
    {
        foreach (var (_, id) in _recent)
        {
            if (!_shown.TryGetValue(id, out var single) || single.InBurst) continue;
            _shown[id] = single with { InBurst = true };
            _burst.Add(id);
            if (!_shown.Values.Any(s => !s.InBurst && s.Tag == single.Tag)) await RemoveTag(single.Tag, cancel).ConfigureAwait(false);
        }
        // The card itself may have a single toast up from its earlier ring: it is replaced by the burst, so that goes too (the loop above did it,
        // since the card is in _recent). A card not yet in _shown has nothing to withdraw.
        _shown[card.Id] = new Shown(ToastXml.TagOf(card.Subject), signature, InBurst: true);
        _burst.Add(card.Id);
        await ShowTag(ToastXml.BurstTag, ToastXml.Burst(_burst.Count), cancel).ConfigureAwait(false);
    }

    private async Task ShowTag(string tag, string xml, CancellationToken cancel)
    {
        try
        {
            await _runner.Show(tag, xml, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "toast {Tag} was not shown", tag);
        }
    }

    private async Task RemoveTag(string tag, CancellationToken cancel)
    {
        try
        {
            await _runner.Remove(tag, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "toast {Tag} was not withdrawn", tag);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

    /// <summary>Stops listening and ends the consumer.</summary>
    public async ValueTask DisposeAsync()
    {
        // The host stops it (StopAsync) and then disposes it, so a second call does nothing.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _subscription?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is { } run)
        {
            try { await run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _sync.Dispose();
    }

    // The state calls a sink under its lock, so this only signals; the diff runs on the consumer.
    private sealed class Sink(Toaster owner) : INodeEventSink
    {
        public int Capacity => int.MaxValue;

        public bool TryWrite(byte[] message)
        {
            owner.Signal();
            return true;
        }

        bool INodeEventSink.TryWriteSnapshot(SnapshotEvent snapshot)
        {
            owner.Signal();
            return true;
        }

        public void Overflowed()
        {
        }
    }
}
