using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A feed's pages are built from the lists an event changed and kept otherwise (pilot #1443): the page builder asks for them several
/// times a look and many looks a second, and every list used to be copied at every event and every page built at every ask.
/// </summary>
public sealed class FeedKeptPagesTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNode _node = new();
    private readonly NodeFeed _feed;

    public FeedKeptPagesTests() => _feed = new NodeFeed(_node.Open, "node.sock", TimeSpan.FromMilliseconds(20));

    public async ValueTask DisposeAsync() => await _feed.DisposeAsync();

    private async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10, Ct);
        }
        done().ShouldBeTrue();
    }

    private async Task Send(NodeEvent next)
    {
        var before = _feed.Version;
        _node.Send(next);
        await Until(() => _feed.Version > before);
    }

    [Fact]
    public async Task AnEventThatTouchesNoSessionKeepsTheSessionsPageAndOneThatDoesBuildsItAgain()
    {
        _feed.Start();
        await Until(() => _node.OpenCount == 1);
        await Send(Live.Snapshot(1, "s-1", Live.Row("a", "alpha")));

        var first = _feed.Screen(Live.At);
        first.Lines.Single().Row.Name.ShouldBe("alpha");
        _feed.Screen(Live.At).ShouldBeSameAs(first);

        // A card is not a session: the sessions page is the one already built, and the cards panel shows the new card.
        await Send(new CardAddedEvent
        {
            Seq = 2, At = Live.At,
            Card = new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "session-waiting", Subject = "session/a", Title = "alpha asks you", At = Live.At, State = CardState.Open },
        });
        _feed.Screen(Live.At).ShouldBeSameAs(first);
        _feed.Cards().Open.Select(c => c.Id).ShouldBe(["c1"]);

        // A session's change builds the page again, with the change in it.
        await Send(Live.Changed(3, Live.Row("a", "alpha", SessionState.Working)));
        var changed = _feed.Screen(Live.At);
        changed.ShouldNotBeSameAs(first);
        changed.Lines.Single().Row.State.ShouldBe(SessionState.Working);

        // The page counts its "ago" from the clock: the next second builds it again.
        _feed.Screen(Live.At.AddSeconds(1)).ShouldNotBeSameAs(changed);
    }

    [Fact]
    public async Task TheFlightsAndJobsPagesAreKeptWhileOnlySessionsOutsideThemMove()
    {
        _feed.Start();
        await Until(() => _node.OpenCount == 1);
        await Send(Live.Snapshot(1, "s-1", Live.Row("a", "alpha")));

        var jobs = _feed.JobsPage(Live.At);
        var cards = _feed.Cards();
        await Send(new CardAddedEvent
        {
            Seq = 2, At = Live.At,
            Card = new CardRow { Id = "c1", Bin = CardBin.ForYou, Kind = "digest", Subject = "node", Title = "Digest", At = Live.At, State = CardState.Open },
        });

        // The jobs did not change; the cards did.
        _feed.JobsPage(Live.At).ShouldBeSameAs(jobs);
        _feed.Cards().ShouldNotBeSameAs(cards);
    }
}

public sealed class KeptTests
{
    [Fact]
    public void AValueIsBuiltAgainOnlyWhenAnInputChangesOrItsSecondTurnsOver()
    {
        var kept = new Kept<string>();
        var builds = 0;
        var list = new[] { 1, 2 };
        var at = new DateTimeOffset(2026, 11, 28, 10, 0, 0, 100, TimeSpan.Zero);
        string Build() => $"built {++builds}";

        kept.Get(at, [list, "status"], Build).ShouldBe("built 1");
        // Later in the same second, the same list and an equal string: kept.
        kept.Get(at.AddMilliseconds(800), [list, new string("status".ToCharArray())], Build).ShouldBe("built 1");
        // An equal list that is another array is another list: a feed swaps its arrays only when an event changes them.
        kept.Get(at, [new[] { 1, 2 }, "status"], Build).ShouldBe("built 2");
        kept.Get(at.AddSeconds(1), [list, "status"], Build).ShouldBe("built 3");

        // A value that does not count from the clock stays across seconds.
        var timeless = new Kept<string>();
        timeless.Get(null, [list], Build).ShouldBe("built 4");
        timeless.Get(null, [list], Build).ShouldBe("built 4");
    }
}
