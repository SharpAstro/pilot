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
        for (var i = 0; i < 100 && called.Count < 2; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
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

    private static readonly NodeMark Se1 = new("se1", "http://se1", "SE1", Here: false);
    private static readonly NodeMark Se2 = new("se2", "http://se2", "SE2", Here: false);

    private static ChatView<RgbaImage> NewMultiChat(RgbaImageRenderer renderer, Func<NodeMark, string, Task<string?>> sendOn)
    {
        var chat = NewChat(renderer, _ => Task.FromResult<string?>(null));
        chat.LoadOn = (_, _, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
        chat.OnSendOn = sendOn;
        return chat;
    }

    [Fact]
    public async Task AMessageQueuedForANodeIsStillDeliveredToItAfterSwitchingAndIsNotDrawnOnTheOtherPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var hold = Held();
        var called = new List<(string Node, string Text)>();
        var chat = NewMultiChat(renderer, (node, text) =>
        {
            called.Add((node.Where, text));
            return called.Count == 1 ? hold.Task : Task.FromResult<string?>(null);
        });
        chat.SwitchTo(Se1);
        chat.Input.InsertText("alpha");
        var first = chat.SendAsync();
        chat.Input.InsertText("bravo");
        var second = chat.SendAsync();

        chat.SwitchTo(Se2);
        Described(chat).ShouldNotContain("alpha");
        Described(chat).ShouldNotContain("bravo");

        hold.SetResult(null);
        await first;
        await second;
        called.ShouldBe([(Se1.Where, "alpha"), (Se1.Where, "bravo")]);
    }

    [Fact]
    public async Task AFailedLineForANodeIsNotDrawnOnAnotherPageAndIsBackWhenReturning()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewMultiChat(renderer, (_, _) => Task.FromResult<string?>("down"));
        chat.SwitchTo(Se1);
        chat.Input.InsertText("alpha");
        await chat.SendAsync();
        chat.Failures.Count.ShouldBe(1);

        chat.SwitchTo(Se2);
        Described(chat).ShouldNotContain("you · failed");
        Actions(chat).ShouldNotContain(a => a.StartsWith("chat:retry:", StringComparison.Ordinal));

        chat.SwitchTo(Se1);
        Described(chat).ShouldContain("you · failed");
        Actions(chat).ShouldContain(a => a.StartsWith("chat:edit:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASendThatIsCancelledByATimeoutEndsFailedAndTheNextOneStillGoes()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var called = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            called.Add(text);
            return text == "alpha"
                ? throw new TaskCanceledException("timed out", new TimeoutException())
                : Task.FromResult<string?>(null);
        });
        chat.Input.InsertText("alpha");
        var first = chat.SendAsync();
        chat.Input.InsertText("bravo");
        var second = chat.SendAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        called.ShouldBe(["alpha", "bravo"]);
        Described(chat).ShouldContain("you · failed");
        var actions = Actions(chat);
        actions.ShouldContain(a => a.StartsWith("chat:retry:", StringComparison.Ordinal));
        actions.ShouldContain(a => a.StartsWith("chat:edit:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedAndAThrownDeliveryStayFailedWithRetryAndEditAndTheNextOneStillGoes()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var called = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            called.Add(text);
            return text switch
            {
                "alpha" => Task.FromResult<string?>("the watchtower is down"),
                "bravo" => throw new HttpRequestException("no route"),
                _ => Task.FromResult<string?>(null),
            };
        });

        chat.Input.InsertText("alpha");
        var first = chat.SendAsync();
        chat.Input.InsertText("bravo");
        var second = chat.SendAsync();
        chat.Input.InsertText("charlie");
        var third = chat.SendAsync();
        await first;
        await second;
        await third;

        called.ShouldBe(["alpha", "bravo", "charlie"]);
        var text = Described(chat);
        text.ShouldContain("you · failed");
        text.ShouldContain("the watchtower is down");
        text.ShouldContain("no route");
        chat.Failures.Count.ShouldBe(2);
        var actions = Actions(chat);
        actions.Count(a => a.StartsWith("chat:retry:", StringComparison.Ordinal)).ShouldBe(2);
        actions.Count(a => a.StartsWith("chat:edit:", StringComparison.Ordinal)).ShouldBe(2);
        chat.Notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task RetrySendsTheSameTextToTheSameTargetAtTheEndOfTheQueue()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var told = new List<(string Card, string Words)>();
        var fail = true;
        var hold = Held();
        var plain = new List<string>();
        var chat = NewChat(renderer, text =>
        {
            plain.Add(text);
            return plain.Count == 1 ? hold.Task : Task.FromResult<string?>(null);
        });
        chat.OnSendAboutCard = (card, _, words) =>
        {
            told.Add((card.Id, words));
            return Task.FromResult<string?>(fail ? "no" : null);
        };

        chat.Attach(Card("c1"), null);
        chat.Input.InsertText("why c1?");
        await chat.SendAsync();
        chat.Failures.ShouldBe(["no"]);

        chat.Input.InsertText("plain");
        var second = chat.SendAsync();
        chat.Input.InsertText("later");
        var third = chat.SendAsync();
        fail = false;
        Click(chat, Actions(chat).Single(a => a.StartsWith("chat:retry:", StringComparison.Ordinal)));
        Described(chat).ShouldNotContain("you · failed");
        var text = Described(chat);
        text.IndexOf("later", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("why c1?", StringComparison.Ordinal));

        hold.SetResult(null);
        await second;
        await third;
        for (var i = 0; i < 100 && told.Count < 2; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        told.ShouldBe([("c1", "why c1?"), ("c1", "why c1?")]);
        plain.ShouldBe(["plain", "later"]);
    }

    [Fact]
    public async Task EditPutsTheTextBackInTheBoxAndTheCardInTheQuoteAndTheFailedLineGoes()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, _ => Task.FromResult<string?>("down"));
        chat.OnSendAboutCard = (_, _, _) => Task.FromResult<string?>("no");

        chat.Attach(Card("c1"), null);
        chat.Input.InsertText("why c1?");
        await chat.SendAsync();
        chat.Input.Text.ShouldBe("");
        chat.QuotedCard.ShouldBeNull();

        Click(chat, Actions(chat).Single(a => a.StartsWith("chat:edit:", StringComparison.Ordinal)));
        chat.Input.Text.ShouldBe("why c1?");
        chat.QuotedCard.ShouldNotBeNull().Id.ShouldBe("c1");
        chat.Failures.ShouldBeEmpty();
        Described(chat).ShouldNotContain("you · failed");

        // A box with text keeps it: the edited words follow on a new line.
        chat.Detach();
        await chat.SendAsync();
        chat.Input.Text.ShouldBe("");
        chat.Input.InsertText("draft");
        Click(chat, Actions(chat).Single(a => a.StartsWith("chat:edit:", StringComparison.Ordinal)));
        chat.Input.Text.ShouldBe("draft\nwhy c1?");
    }

    [Fact]
    public async Task AFailedLineOverlapsNoOtherText()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, _ => Task.FromResult<string?>("refused: the watchtower is not running and a long reason follows so that it wraps onto more lines than one"));
        chat.Input.InsertText("a message that did not go, long enough to need some room on the page " + new string('x', 40));
        await chat.SendAsync();
        chat.Input.InsertText("second");
        await chat.SendAsync();

        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f);
        var arranged = Layout.Engine.Arrange(chat.Tree(), new Rect<float>(0, 0, 1100, 900), ctx);
        arranged.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "you · failed" } }).ShouldBe(2);
        TextOverlap.Faults(arranged).Where(f => f.Bounds.Y > 70f).Select(f => f.Line).ShouldBeEmpty();
    }
}
