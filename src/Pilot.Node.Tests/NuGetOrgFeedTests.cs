using System.IO.Compression;
using System.Net;
using System.Text;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The real nuget.org feed, against a stub that answers as nuget.org does: SharpAstro versions carry <c>+sha</c> build metadata
/// (SemVer 2), so their registration leaf is only in the gzipped SemVer 2 hive and the SemVer 1 hive answers 404. A feed read
/// from the wrong hive says "not published" for every version, which the fake feed never shows.
/// </summary>
public sealed class NuGetOrgFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Leaf = """
        {
          "@id": "https://api.nuget.org/v3/registration5-gz-semver2/dir.lib/11.37.4851.json",
          "catalogEntry": "https://api.nuget.org/v3/catalog0/data/2026.10.26.09.00.00/dir.lib.11.37.4851+abc1234.json",
          "listed": true,
          "published": "2026-10-26T08:15:30.123+00:00",
          "packageContent": "https://api.nuget.org/v3-flatcontainer/dir.lib/11.37.4851/dir.lib.11.37.4851.nupkg"
        }
        """;

    private static readonly DateTimeOffset LeafPublished = new(2026, 10, 26, 8, 15, 30, 123, TimeSpan.Zero);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        return output.ToArray();
    }

    /// <summary>nuget.org's SemVer 2 hive only: the SemVer 1 hive has no leaf for a version with build metadata.</summary>
    private static HttpResponseMessage SemVer2Only(HttpRequestMessage request, byte[] body) =>
        request.RequestUri!.AbsoluteUri.Contains("registration5-gz-semver2/dir.lib/11.37.4851.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    [Fact]
    public void TheLeafIsReadFromTheGzippedSemVer2HiveByLowerCaseIdAndVersionWithoutBuildMetadata() =>
        NuGetOrgFeed.RegistrationLeaf("DIR.Lib", "11.37.4851+Abc1234").AbsoluteUri
            .ShouldBe("https://api.nuget.org/v3/registration5-gz-semver2/dir.lib/11.37.4851.json");

    [Fact]
    public async Task APublishedTimeIsReadFromAGzipBodyTheHandlerLeftCompressed()
    {
        var handler = new StubHandler(r => SemVer2Only(r, Gzip(Leaf)));
        var feed = new NuGetOrgFeed(new HttpClient(handler));

        var published = await feed.PublishedAtAsync("DIR.Lib", "11.37.4851+abc1234", Ct);

        published.ShouldBe(LeafPublished);
        handler.Requests.ShouldHaveSingleItem().AbsoluteUri.ShouldBe("https://api.nuget.org/v3/registration5-gz-semver2/dir.lib/11.37.4851.json");
    }

    [Fact]
    public async Task APublishedTimeIsReadFromABodyTheHttpClientInflatedItself()
    {
        // A handler with AutomaticDecompression sees Content-Encoding: gzip and hands on plain JSON.
        var handler = new StubHandler(r => SemVer2Only(r, Encoding.UTF8.GetBytes(Leaf)));
        var feed = new NuGetOrgFeed(new HttpClient(handler));

        (await feed.PublishedAtAsync("DIR.Lib", "11.37.4851", Ct)).ShouldBe(LeafPublished);
    }

    [Fact]
    public async Task AVersionTheHiveDoesNotKnowHasNoPublishedTime()
    {
        var feed = new NuGetOrgFeed(new HttpClient(new StubHandler(r => SemVer2Only(r, Gzip(Leaf)))));

        (await feed.PublishedAtAsync("DIR.Lib", "11.37.9999", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ALeafWithoutAPublishedTimeSaysNothing()
    {
        var feed = new NuGetOrgFeed(new HttpClient(new StubHandler(r => SemVer2Only(r, Gzip("""{"listed": true}""")))));

        (await feed.PublishedAtAsync("DIR.Lib", "11.37.4851", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task AFeedThatErrorsRaisesInsteadOfSayingNotPublished()
    {
        var feed = new NuGetOrgFeed(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

        await Should.ThrowAsync<HttpRequestException>(() => feed.PublishedAtAsync("DIR.Lib", "11.37.4851", Ct));
    }
}
