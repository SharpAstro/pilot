using System.Text;

namespace Pilot.Node;

/// <summary>
/// Where a bench may run in Azure, read from the node's settings with the defaults filled in (#823): what the legs that make, price and
/// delete the VM consume, so none of them re-reads a key. It exists only when a subscription and a resource group are saved; without
/// them <see cref="From"/> says why not, and every bench runs on the node's own machine as before.
/// </summary>
public sealed record BenchCloud
{
    public required string Subscription { get; init; }

    public required string ResourceGroup { get; init; }

    /// <summary>The region, as <c>az account list-locations</c> names it.</summary>
    public required string Region { get; init; }

    /// <summary>The VM size (an Azure SKU): fixed, so two bench runs compare.</summary>
    public required string VmSize { get; init; }

    /// <summary>The image, a marketplace URN <c>publisher:offer:sku:version</c>.</summary>
    public required string Image { get; init; }

    /// <summary>The most to pay per VM hour, in USD.</summary>
    public required double SpotPricePerHour { get; init; }

    /// <summary>The most cloud benches may cost in a calendar month, in USD.</summary>
    public required double MonthlyCap { get; init; }

    /// <summary>Whether the node signs in as a service principal; false means the machine's own <c>az login</c>.</summary>
    public required bool ServicePrincipal { get; init; }

    public string? AppId { get; init; }

    public string? Tenant { get; init; }

    /// <summary>The service principal's secret: held for the one call that signs in, never printed (see <see cref="PrintMembers"/>).</summary>
    public string? Secret { get; init; }

    // The record's own text leaves the secret out, so a log line or a failed assertion that prints it shows nothing.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Subscription = {Subscription}, ResourceGroup = {ResourceGroup}, Region = {Region}, VmSize = {VmSize}, Image = {Image}, ");
        builder.Append($"SpotPricePerHour = {SpotPricePerHour}, MonthlyCap = {MonthlyCap}, ServicePrincipal = {ServicePrincipal}, AppId = {AppId}, Tenant = {Tenant}, Secret = {(Secret is { Length: > 0 } ? "set" : "unset")}");
        return true;
    }

    /// <summary>
    /// The cloud a bench may use as <paramref name="settings"/> say it, or why it may not: a missing subscription or resource group
    /// (cloud benches are off), or a service principal short of its app id, tenant or secret.
    /// </summary>
    public static (BenchCloud? Cloud, string? Why) From(NodeSettings settings)
    {
        var missing = new List<string>();
        if (settings.BenchSubscription is not { Length: > 0 })
        {
            missing.Add(NodeSettings.BenchSubscriptionKey);
        }
        if (settings.BenchResourceGroup is not { Length: > 0 })
        {
            missing.Add(NodeSettings.BenchResourceGroupKey);
        }
        if (missing.Count > 0)
        {
            return (null, $"cloud benches are off: {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} not set, so a bench runs on this machine");
        }
        var servicePrincipal = settings.BenchAuth == NodeSettings.AuthServicePrincipal;
        if (servicePrincipal)
        {
            var absent = new List<string>();
            if (settings.BenchSpAppId is not { Length: > 0 })
            {
                absent.Add(NodeSettings.BenchSpAppIdKey);
            }
            if (settings.BenchSpTenant is not { Length: > 0 })
            {
                absent.Add(NodeSettings.BenchSpTenantKey);
            }
            if (settings.BenchSpSecret is not { Length: > 0 })
            {
                absent.Add(NodeSettings.BenchSpSecretKey);
            }
            if (absent.Count > 0)
            {
                return (null, $"cloud benches are off: {NodeSettings.BenchAuthKey} is {NodeSettings.AuthServicePrincipal} but {string.Join(", ", absent)} {(absent.Count == 1 ? "is" : "are")} not set, so a bench runs on this machine");
            }
        }
        return (new BenchCloud
        {
            Subscription = settings.BenchSubscription!,
            ResourceGroup = settings.BenchResourceGroup!,
            Region = settings.BenchRegion ?? NodeSettings.DefaultBenchRegion,
            VmSize = settings.BenchVmSize ?? NodeSettings.DefaultBenchVmSize,
            Image = settings.BenchImage ?? NodeSettings.DefaultBenchImage,
            SpotPricePerHour = settings.BenchSpotPrice ?? NodeSettings.DefaultBenchSpotPrice,
            MonthlyCap = settings.BenchMonthlyCap ?? NodeSettings.DefaultBenchMonthlyCap,
            ServicePrincipal = servicePrincipal,
            AppId = servicePrincipal ? settings.BenchSpAppId : null,
            Tenant = servicePrincipal ? settings.BenchSpTenant : null,
            Secret = servicePrincipal ? settings.BenchSpSecret : null,
        }, null);
    }
}
