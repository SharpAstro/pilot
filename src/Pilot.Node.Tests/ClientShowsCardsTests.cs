using Microsoft.Extensions.DependencyInjection;
using Pilot.Node;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The wire that makes the Toaster's skip rule work (#1356): a client says over its event stream that it shows the cards, <see cref="PageViews"/>
/// keeps it, and it is forgotten when the stream closes. A broken link would silently turn normal toasts back on over an open For you bar.
/// </summary>
public sealed class ClientShowsCardsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            (DateTime.UtcNow < until).ShouldBeTrue("the node never showed what the test waits for");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task AClientSayingItShowsTheCardsIsKeptAndForgottenWhenItsStreamCloses()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var views = node.App.Services.GetRequiredService<PageViews>();
        views.AnyShowsCards.ShouldBeFalse();

        var stream = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        await stream.ReadAsync(Ct);
        await stream.SayPageAsync(null, Ct, showsCards: true);
        await Until(() => views.AnyShowsCards);

        await stream.SayPageAsync(null, Ct, showsCards: false);
        await Until(() => !views.AnyShowsCards);

        await stream.SayPageAsync(null, Ct, showsCards: true);
        await Until(() => views.AnyShowsCards);
        await stream.DisposeAsync();
        await Until(() => !views.AnyShowsCards);
    }

    [Fact]
    public async Task SayingOnlyAPageLeavesWhetherItShowsTheCardsAlone()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var views = node.App.Services.GetRequiredService<PageViews>();
        var stream = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        await stream.ReadAsync(Ct);
        await stream.SayPageAsync(null, Ct, showsCards: true);
        await Until(() => views.AnyShowsCards);

        await stream.SayPageAsync(Pilot.Contracts.ClientPages.PullRequests, Ct);
        await Until(() => views.Count(Pilot.Contracts.ClientPages.PullRequests) == 1);
        views.AnyShowsCards.ShouldBeTrue();
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task ATestNodeShowsNoRealToastAndWritesNoRealRegistry()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.App.Services.GetRequiredService<Pilot.Node.Toasts.IToastRunner>().ShouldBeOfType<RecordingToastRunner>();
        node.App.Services.GetRequiredService<Pilot.Node.Toasts.ISchemeRegistry>().ShouldBeOfType<MemorySchemeRegistry>();
    }
}
