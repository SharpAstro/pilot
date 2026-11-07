using System.Net;
using System.Net.Http;
using System.Text;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

// The Azure retail prices API (prices.azure.com), over an answer the test writes: which meter is the spot price, and what is asked.
public sealed partial class AzureBenchTests
{
    private const string PricesJson = """
        {
          "Items": [
            { "retailPrice": 0.12,   "unitOfMeasure": "1 Hour",  "productName": "Virtual Machines FSv2 Series Windows", "meterName": "F8s v2 Spot" },
            { "retailPrice": 0.0800, "unitOfMeasure": "1 Hour",  "productName": "Virtual Machines FSv2 Series",         "meterName": "F8s v2 Spot" },
            { "retailPrice": 0.0712, "unitOfMeasure": "1 Hour",  "productName": "Virtual Machines FSv2 Series",         "meterName": "F8s v2 Spot" },
            { "retailPrice": 0.34,   "unitOfMeasure": "1 Hour",  "productName": "Virtual Machines FSv2 Series",         "meterName": "F8s v2" },
            { "retailPrice": 0.05,   "unitOfMeasure": "1 Hour",  "productName": "Virtual Machines FSv2 Series",         "meterName": "F8s v2 Low Priority" },
            { "retailPrice": 0,      "unitOfMeasure": "1 Hour",  "productName": "Virtual Machines FSv2 Series",         "meterName": "F8s v2 Spot" },
            { "retailPrice": 5,      "unitOfMeasure": "1/Month", "productName": "Virtual Machines FSv2 Series",         "meterName": "F8s v2 Spot" }
          ],
          "NextPageLink": null,
          "Count": 7
        }
        """;

    [Fact]
    public void ThePriceIsTheLowestPerHourLinuxSpotMeterAndNoOtherMeterCounts()
    {
        AzureRetailPrices.Pick(PricesJson).ShouldBe(0.0712);
    }

    [Theory]
    [InlineData("{\"Items\": []}")]
    [InlineData("{\"Count\": 0}")]
    [InlineData("<html>not json</html>")]
    [InlineData("{\"Items\": [{\"retailPrice\": 0.3, \"unitOfMeasure\": \"1 Hour\", \"productName\": \"Virtual Machines\", \"meterName\": \"F8s v2\"}]}")]
    public void AnAnswerWithNoSpotHourMeterIsNoPrice(string json)
    {
        AzureRetailPrices.Pick(json).ShouldBeNull();
    }

    [Fact]
    public async Task TheApiIsAskedForTheSpotMeterOfTheSizeInTheRegionAndItsAnswerIsRead()
    {
        HttpRequestMessage? asked = null;
        using var http = new HttpClient(new FakeHttp((request, _) =>
        {
            asked = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PricesJson, Encoding.UTF8, "application/json") });
        }));

        var price = await new AzureRetailPrices(http).SpotPerHourAsync("westeurope", "Standard_F8s_v2", TestContext.Current.CancellationToken);

        price.ShouldBe(0.0712);
        var uri = asked.ShouldNotBeNull().RequestUri.ShouldNotBeNull();
        uri.Host.ShouldBe("prices.azure.com");
        uri.AbsolutePath.ShouldBe("/api/retail/prices");
        var filter = Uri.UnescapeDataString(uri.Query);
        filter.ShouldContain("serviceName eq 'Virtual Machines'");
        filter.ShouldContain("armRegionName eq 'westeurope'");
        filter.ShouldContain("armSkuName eq 'Standard_F8s_v2'");
        filter.ShouldContain("priceType eq 'Consumption'");
        filter.ShouldContain("contains(meterName, 'Spot')");
    }

    [Fact]
    public async Task AnErrorStatusANetworkFailureAndANameThatIsNotPlainAreNoPriceAndNotAnException()
    {
        var calls = 0;
        using var error = new HttpClient(new FakeHttp((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));
        using var down = new HttpClient(new FakeHttp((_, _) =>
        {
            calls++;
            throw new HttpRequestException("no route to host");
        }));
        using var slow = new HttpClient(new FakeHttp(async (_, ct) =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }) );

        (await new AzureRetailPrices(error).SpotPerHourAsync("westeurope", "Standard_F8s_v2", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await new AzureRetailPrices(down).SpotPerHourAsync("westeurope", "Standard_F8s_v2", TestContext.Current.CancellationToken)).ShouldBeNull();
        calls.ShouldBe(2);

        // A quote in a name would be a quote in the filter: no request is made for it.
        (await new AzureRetailPrices(error).SpotPerHourAsync("west'europe", "Standard_F8s_v2", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await new AzureRetailPrices(error).SpotPerHourAsync("westeurope", "x' or 1 eq 1 or 'y", TestContext.Current.CancellationToken)).ShouldBeNull();
        calls.ShouldBe(2);

        // The caller's own cancel is the caller's: it is not turned into "no price".
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var caught = await Record.ExceptionAsync(() => new AzureRetailPrices(slow).SpotPerHourAsync("westeurope", "Standard_F8s_v2", cancel.Token));
        caught.ShouldBeAssignableTo<OperationCanceledException>();
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request, cancellationToken);
    }
}
