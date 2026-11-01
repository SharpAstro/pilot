using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// Reads the account's usage once a minute from <c>rate-limits.json</c> in the Claude config folder, which the
/// controller's status line writes from the <c>rate_limits</c> Claude Code hands it, and the account itself from Claude
/// Code's <c>.claude.json</c> (<see cref="ClaudeAccount"/>): no token is read, no API called.
/// Past <see cref="HighPercent"/> of the 5-hour window it raises a For-you card, once per window, since workers stop
/// there and a stop the controller saw coming is no surprise.
/// </summary>
public sealed class UsageReader(NodeState state, string file, TimeProvider time) : BackgroundService
{
    /// <summary>Claude Code's <c>.claude.json</c>, whose login names the account; null reads none.</summary>
    public string? AccountFile { get; init; }

    public const string HighKind = "usage-high";

    /// <summary>The 5-hour share past which a card says workers will stop soon.</summary>
    public double HighPercent { get; init; } = 90;

    private DateTimeOffset? _warnedFor;

    // The login last read, and when it was seen to change: the status line's file names no account, so after a switch it still
    // holds the last account's figures until the status line writes again, and those were put as the new account's (the owner
    // saw 87% of the week on an account that had used 6%, DE1 2026-10-30).
    private string? _email;
    private DateTimeOffset? _switchedAt;

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

    /// <summary>
    /// One read: puts the account and its figures, and warns when the 5-hour window is nearly spent. With no figures the
    /// account is put alone, so a node whose status line writes none still says whose it is; nothing when there is neither.
    /// </summary>
    public void Read()
    {
        var account = AccountFile is { } path ? ClaudeAccount.Read(path) : null;
        if (account is { Email: var email })
        {
            if ((_email ?? state.Usage()?.Account?.Email) is { } was && was != email)
            {
                _switchedAt = time.GetUtcNow();
            }
            _email = email;
        }
        var figures = Parse(file);
        // Written before the login changed: the last account's, not this one's. The account is put alone until fresh ones come.
        if (figures is not null && _switchedAt is { } switched && figures.ReadAt < switched)
        {
            figures = null;
        }
        if ((figures is not null ? figures with { Account = account } : account is null ? null : new UsageRow { Account = account, ReadAt = time.GetUtcNow() }) is not { } read)
        {
            return;
        }
        state.PutUsage(read);
        // The merged figures, not this read: a stale writer must not raise, or miss, the warning either.
        var usage = state.Usage()!;

        if (usage.FiveHour is { UsedPercent: var used, ResetsAt: var resets } && used >= HighPercent && resets is { } at && at > time.GetUtcNow() && _warnedFor != at)
        {
            _warnedFor = at;
            // A reset-only notice: nothing to decide, the node resumes the workers itself, so it is a Handled line.
            state.RaiseCard(CardBin.Handled, HighKind, Verbs.NodeTarget, $"5-hour usage is at {used:0}%; it resets at {at.ToLocalTime():HH:mm}",
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
