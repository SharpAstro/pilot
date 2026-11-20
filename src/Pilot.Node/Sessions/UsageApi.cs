using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// Asks the server for the account's 5-hour and 7-day figures, as Claude Code's <c>/usage</c> does (the owner's decision on #1193):
/// <c>GET /api/oauth/usage</c> with the account's own access token from <c>.credentials.json</c>. The token is read from disk for each
/// request and lives in no field; it is never logged, written or sent anywhere but that request. An expired token is skipped, never
/// refreshed (a refresh rotates the refresh token, and one made outside Claude Code's lock signs Claude Code out). A failure is logged
/// by status code only. At most one request per <see cref="Every"/>, <see cref="AfterFailure"/> after a failure.
/// </summary>
public sealed class UsageApi(HttpClient http, string credentialsFile, TimeProvider time, ILogger? log = null)
{
    public const string Url = "https://api.anthropic.com/api/oauth/usage";

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(10);

    /// <summary>The source name a server read carries on its row.</summary>
    public const string SourceName = "server";

    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    /// <summary>The figures from the server, or null when none came: paced, no usable token, or a failure.</summary>
    public async Task<UsageRow?> ReadAsync(CancellationToken cancel)
    {
        var now = time.GetUtcNow();
        if (now < _notBefore)
        {
            return null;
        }
        // A missing file, a token without a value or an expired one is no request and no failure: nothing is paced.
        if (Token(now) is not { } token)
        {
            return null;
        }
        _notBefore = now + AfterFailure;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
            {
                log?.LogWarning("Usage read failed: HTTP {Status}", (int)response.StatusCode);
                return null;
            }
            var row = Parse(await response.Content.ReadAsStringAsync(cancel), now);
            if (row is null)
            {
                log?.LogWarning("Usage read failed: answer of an unknown shape");
                return null;
            }
            _notBefore = now + Every;
            return row;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancel.IsCancellationRequested)
        {
            // Only the exception's type is logged: its text could carry a request's details.
            log?.LogWarning("Usage read failed: {Kind}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The access token, read now, or null when there is no file, no token or it has expired.</summary>
    private string? Token(DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(credentialsFile));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(token.GetString()))
            {
                return null;
            }
            if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var ms)
                && DateTimeOffset.FromUnixTimeMilliseconds(ms) <= now)
            {
                return null;
            }
            return token.GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The answer's <c>five_hour</c> and <c>seven_day</c> as a row read at <paramref name="now"/>, or null with neither.</summary>
    public static UsageRow? Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var five = Window(doc.RootElement, "five_hour");
        var seven = Window(doc.RootElement, "seven_day");
        return five is null && seven is null ? null : new UsageRow { FiveHour = five, SevenDay = seven, ReadAt = now, Source = SourceName };
    }

    private static UsageWindow? Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        DateTimeOffset? resets = window.TryGetProperty("resets_at", out var at) && at.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        return new UsageWindow { UsedPercent = used.GetDouble(), ResetsAt = resets };
    }
}
