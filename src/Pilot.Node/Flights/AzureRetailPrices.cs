using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>The spot price of a VM size, for the cost of a cloud bench (#823); a test says what it answers.</summary>
public interface IRetailPrices
{
    /// <summary>The price of one spot VM hour of <paramref name="vmSize"/> in <paramref name="region"/> in USD, or null when it cannot be read.</summary>
    Task<double?> SpotPerHourAsync(string region, string vmSize, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IRetailPrices"/> from Azure's public retail prices API (<c>prices.azure.com</c>, no sign-in): the Linux spot meter for the size
/// and region. Any answer short of one price (no network, an error, no spot meter for the size) is null, and the caller then takes the
/// settings' max price as an upper bound.
/// </summary>
public sealed partial class AzureRetailPrices(HttpClient http) : IRetailPrices
{
    public static readonly Uri Endpoint = new("https://prices.azure.com/api/retail/prices");

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    // A region and an SKU are plain words; anything else would be a quote inside the filter below.
    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex Plain();

    public async Task<double?> SpotPerHourAsync(string region, string vmSize, CancellationToken cancellationToken)
    {
        if (!Plain().IsMatch(region) || !Plain().IsMatch(vmSize))
        {
            return null;
        }
        var filter = $"serviceName eq 'Virtual Machines' and armRegionName eq '{region}' and armSkuName eq '{vmSize}' and priceType eq 'Consumption' and contains(meterName, 'Spot')";
        var uri = new Uri($"{Endpoint}?api-version=2023-01-01-preview&$filter={Uri.EscapeDataString(filter)}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Budget);
        try
        {
            using var response = await http.GetAsync(uri, limit.Token);
            return response.IsSuccessStatusCode ? Pick(await response.Content.ReadAsStringAsync(limit.Token)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// The price in an answer of the API: the lowest per-hour spot meter of a Linux product with a price above zero (the Windows meter of the
    /// same size is dearer, and a low-priority meter is another thing); null when there is none.
    /// </summary>
    public static double? Pick(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            double? best = null;
            foreach (var item in items.EnumerateArray())
            {
                var meter = Text(item, "meterName") ?? "";
                if (Text(item, "unitOfMeasure") != "1 Hour"
                    || !meter.Contains("Spot", StringComparison.OrdinalIgnoreCase)
                    || meter.Contains("Low Priority", StringComparison.OrdinalIgnoreCase)
                    || (Text(item, "productName") ?? "").Contains("Windows", StringComparison.OrdinalIgnoreCase)
                    || !item.TryGetProperty("retailPrice", out var price)
                    || price.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                var value = price.GetDouble();
                if (value > 0)
                {
                    best = best is { } known && known <= value ? known : value;
                }
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The price as a log line shows it.</summary>
    public static string Show(double perHour) => perHour.ToString("0.0####", CultureInfo.InvariantCulture);
}
