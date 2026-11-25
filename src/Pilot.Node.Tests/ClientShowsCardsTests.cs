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
    public async Task TheNodesToasterSkipsANormalCardWhileAClientShowsTheCardsAndShowsAnUrgentOne()
    {
        // The last link of the skip rule: the Toaster the node's own services build reads PageViews, not a lambda of a test's.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node toasts on Windows only (AddToasts adds nothing elsewhere)");
        await using var node = await NodeHarness.StartAsync(Ct);
        var views = node.App.Services.GetRequiredService<PageViews>();
        var runner = (RecordingToastRunner)node.App.Services.GetRequiredService<Pilot.Node.Toasts.IToastRunner>();
        var state = node.App.Services.GetRequiredService<NodeState>();
        var stream = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        await stream.ReadAsync(Ct);
        await stream.SayPageAsync(null, Ct, showsCards: true);
        await Until(() => views.AnyShowsCards);

        state.RaiseCard(Pilot.Contracts.CardBin.Ring, "worker-waiting", "flight/SKIP", "a normal ring", "detail");
        state.RaiseCard(Pilot.Contracts.CardBin.Ring, "push-blocked", "flight/URGENT", "an urgent ring", "detail");
        await Until(() => runner.Shown.Any(s => s.Tag == "flight/URGENT"));

        // The urgent card was raised after the normal one, so the Toaster has passed the normal one by now.
        runner.Shown.ShouldNotContain(s => s.Tag == "flight/SKIP");
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
