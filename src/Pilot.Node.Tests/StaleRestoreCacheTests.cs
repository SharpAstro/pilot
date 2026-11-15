using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A restore that fails NU1102 on a version nuget.org's flat container lists read a stale HTTP cache (pilot #1064). Fakes only:
/// <see cref="FakeNuGetFeed"/>, no network.
/// </summary>
public sealed class StaleRestoreCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Nu1102 = "error NU1102: Unable to find package Drawboard.Core with version (>= 1.2.7) [/w/App.csproj]";

    [Fact]
    public async Task AMissWhoseVersionTheFeedListsIsStale()
    {
        var feed = new FakeNuGetFeed();
        feed.Publish("Drawboard.Core", "1.2.7");

        var stale = await StaleRestoreCache.ListedMissesAsync(Nu1102, feed, Ct);

        stale.ShouldHaveSingleItem().Id.ShouldBe("Drawboard.Core");
    }

    [Fact]
    public async Task ALaterVersionListedCountsToo()
    {
        var feed = new FakeNuGetFeed();
        feed.Publish("Drawboard.Core", "1.2.9");

        (await StaleRestoreCache.ListedMissesAsync(Nu1102, feed, Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AVersionTheFeedDoesNotListYetIsNotStale()
    {
        var feed = new FakeNuGetFeed();
        feed.Publish("Drawboard.Core", "1.2.5");

        (await StaleRestoreCache.ListedMissesAsync(Nu1102, feed, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFeedThatCannotBeReadGivesNone()
    {
        var feed = new FakeNuGetFeed { Offline = true };
        feed.Publish("Drawboard.Core", "1.2.7");

        (await StaleRestoreCache.ListedMissesAsync(Nu1102, feed, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANu1101WithNoVersionAskedIsNeverStale()
    {
        var feed = new FakeNuGetFeed();
        feed.Publish("Drawboard.Core", "1.2.7");

        (await StaleRestoreCache.ListedMissesAsync("error NU1101: Unable to find package Drawboard.Core. No packages exist with this id in source(s): nuget.org", feed, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public void TheEnvironmentBypassesTheHttpCacheOfAnyDotnetRestore()
    {
        StaleRestoreCache.Environment["RestoreNoHttpCache"].ShouldBe("true");
        StaleRestoreCache.Environment["RestoreNoCache"].ShouldBe("true");
    }
}
