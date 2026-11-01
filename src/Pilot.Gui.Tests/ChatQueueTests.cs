using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Console.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The chat's outbox (PIL582): Enter moves the typed text out of the box into a pending line at once, messages sent while one is
/// with the node queue behind it in order and go one after another, each to the target it was sent to, and a queued one can be dropped.
/// </summary>
public sealed class ChatQueueTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 9, 40, 12, TimeSpan.Zero);

    private static CardRow Card(string id) => new() { Id = id, Bin = CardBin.Ring, Kind = "k", Subject = "flight/X", Title = "t" + id, At = At, State = CardState.Open };

    private static ChatView<RgbaImage> NewChat(RgbaImageRenderer renderer, Func<string, Task<string?>> send)
    {
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
        chat.OnSend = send;
        return chat;
    }

    private static string Described(ChatView<RgbaImage> chat) =>
        string.Join(' ', Layout.Engine.Arrange(chat.Tree(), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()));

    private static List<string> Actions(ChatView<RgbaImage> chat)
    {
        chat.Render(new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        return [.. regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action)];
    }

    private static void Click(ChatView<RgbaImage> chat, string action)
    {
        chat.Render(new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
    }

    private static TaskCompletionSource<string?> Held() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task TwoMessagesQueueInOrderAndTheHostIsCalledOnlyAfterThePreviousReturned()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var holds = new[] { Held(), Held() };
        var called = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            called.Add(text);
            return holds[called.Count - 1].Task;
        });

        chat.Input.InsertText("alpha");
        var first = chat.SendAsync();
        chat.Input.Text.ShouldBe("");
        chat.Input.InsertText("bravo");
        var second = chat.SendAsync();
        chat.Input.Text.ShouldBe("");

        called.ShouldBe(["alpha"]);
        var text = Described(chat);
        text.ShouldContain("you · sending…");
        text.ShouldContain("you · queued 1");
        text.IndexOf("alpha", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("bravo", StringComparison.Ordinal));
        text.ShouldContain("● sending…");

        holds[0].SetResult(null);
        await first;
        called.ShouldBe(["alpha", "bravo"]);
        Described(chat).ShouldNotContain("queued");

        holds[1].SetResult(null);
        await second;
        Described(chat).ShouldNotContain("sending…");
        chat.Notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task ThreeMessagesShowTheirPlacesAndAQueuedOneCanBeDroppedAndIsNeverSent()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var hold = Held();
        var called = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            called.Add(text);
            return called.Count == 1 ? hold.Task : Task.FromResult<string?>(null);
        });

        chat.Input.InsertText("alpha");
        var first = chat.SendAsync();
        chat.Input.InsertText("bravo");
        var second = chat.SendAsync();
        chat.Input.InsertText("charlie");
        var third = chat.SendAsync();
        var text = Described(chat);
        text.ShouldContain("you · queued 1");
        text.ShouldContain("you · queued 2");

        var drops = Actions(chat).Where(a => a.StartsWith("chat:drop:", StringComparison.Ordinal)).ToList();
        drops.Count.ShouldBe(2);
        Click(chat, drops[0]);
        await second;
        Described(chat).ShouldNotContain("bravo");
        Described(chat).ShouldContain("you · queued 1");

        hold.SetResult(null);
        await first;
        await third;
        called.ShouldBe(["alpha", "charlie"]);
    }

    [Fact]
    public async Task ARefusalDoesNotStopTheQueue()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var hold = Held();
        var called = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            called.Add(text);
            return called.Count == 1 ? hold.Task : Task.FromResult<string?>(null);
        });

        chat.Input.InsertText("alpha");
        var first = chat.SendAsync();
        chat.Input.InsertText("bravo");
        var second = chat.SendAsync();

        hold.SetResult("the watchtower is down");
        await first;
        await second;

        called.ShouldBe(["alpha", "bravo"]);
    }

    [Fact]
    public async Task AQueuedAboutCardMessageKeepsItsCardAfterTheQuoteIsDroppedOrAnotherAttached()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var hold = Held();
        var told = new List<(string Card, string Words)>();
        var plain = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            plain.Add(text);
            return hold.Task;
        });
        chat.OnSendAboutCard = (card, _, words) =>
        {
            told.Add((card.Id, words));
            return Task.FromResult<string?>(null);
        };

        chat.Input.InsertText("plain");
        var first = chat.SendAsync();

        chat.Attach(Card("c1"), null);
        chat.Input.InsertText("why c1?");
        var second = chat.SendAsync();
        chat.QuotedCard.ShouldBeNull();

        chat.Attach(Card("c2"), null);
        chat.Detach();
        chat.Attach(Card("c3"), null);
        chat.Input.InsertText("and c3?");
        var third = chat.SendAsync();

        told.ShouldBeEmpty();
        hold.SetResult(null);
        await first;
        await second;
        await third;

        plain.ShouldBe(["plain"]);
        told.ShouldBe([("c1", "why c1?"), ("c3", "and c3?")]);
    }

    [Fact]
    public async Task SwitchingNodeDropsWhatIsStillQueuedForTheOldOne()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var hold = Held();
        var called = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            called.Add(text);
            return hold.Task;
        });

        chat.Input.InsertText("alpha");
        var first = chat.SendAsync();
        chat.Input.InsertText("bravo");
        var second = chat.SendAsync();

        chat.SwitchTo(new NodeMark("se1", "http://se1", "SE1", Here: false));
        await second;

        hold.SetResult(null);
        await first;
        called.ShouldBe(["alpha"]);
    }
}
