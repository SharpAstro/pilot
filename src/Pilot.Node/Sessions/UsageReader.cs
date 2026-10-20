using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// Reads the account's usage once a minute from <c>rate-limits.json</c> in the Claude config folder, which the
/// controller's status line writes from the <c>rate_limits</c> Claude Code hands it: no login is read, no API called.
/// Past <see cref="HighPercent"/> of the 5-hour window it raises a For-you card, once per window, since workers stop
/// there and a stop the controller saw coming is no surprise.
/// </summary>
public sealed class UsageReader(NodeState state, string file, TimeProvider time) : BackgroundService
{
    public const string HighKind = "usage-high";

    /// <summary>The 5-hour share past which a card says workers will stop soon.</summary>
    public double HighPercent { get; init; } = 90;

    private DateTimeOffset? _warnedFor;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            Read();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), time, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One read: puts the figures and warns when the 5-hour window is nearly spent; nothing when the file is missing or unreadable.</summary>
    public void Read()
    {
        if (Parse(file) is not { } read)
        {
            return;
        }
        state.PutUsage(read);
        // The merged figures, not this read: a stale writer must not raise, or miss, the warning either.
        var usage = state.Usage()!;

        if (usage.FiveHour is { UsedPercent: var used, ResetsAt: var resets } && used >= HighPercent && resets is { } at && at > time.GetUtcNow() && _warnedFor != at)
        {
            _warnedFor = at;
            state.RaiseCard(CardBin.ForYou, HighKind, Verbs.NodeTarget, $"5-hour usage is at {used:0}%; it resets at {at.ToLocalTime():HH:mm}",
                "workers stop when it runs out; the node resumes them once it resets");
        }
    }

    /// <summary>The usage in <paramref name="path"/>, or null when it is missing, half written or of another shape.</summary>
    public static UsageRow? Parse(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var readAt = root.TryGetProperty("written_at", out var written) && written.ValueKind == JsonValueKind.Number && written.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return new UsageRow { FiveHour = Window(limits, "five_hour"), SevenDay = Window(limits, "seven_day"), ReadAt = readAt };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>One window: its <c>used_percentage</c>, and its <c>resets_at</c> in epoch seconds (or ISO 8601, which the status line also takes).</summary>
    private static UsageWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        DateTimeOffset? resets = null;
        if (window.TryGetProperty("resets_at", out var at))
        {
            if (at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var epoch))
            {
                resets = DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            else if (at.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                resets = parsed;
            }
        }
        return new UsageWindow { UsedPercent = used.GetDouble(), ResetsAt = resets };
    }
}
