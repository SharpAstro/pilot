using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A card's Chat about this (pilot #400): the button, the Chat page with the card quoted, the send through tell card/id, and the quoted feed.</summary>
public sealed class ChatAboutCardTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static CardRow Card(string id, CardState state = CardState.Open, CardBin bin = CardBin.ForYou, string subject = "flight/TIA1611") => new()
    {
        Id = id, Bin = bin, Kind = "landed", Subject = subject, Title = $"card {id}", At = Now, State = state,
        Detail = "The flight landed: its PR merged and its worktree went.",
        Choices = [new CardChoice { Label = "Thanks", Verb = Verbs.Dismiss, Target = Verbs.CardTarget(id) }],
    };

    private static PilotChrome<RgbaImage> NewChrome(out RgbaImageRenderer renderer, uint width = 1280, uint height = 900)
    {
        renderer = new RgbaImageRenderer(width, height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Cards.CanTell = true;
        chrome.Chat.Load = (_, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        return chrome;
    }

    private static List<string> Buttons(PilotChrome<RgbaImage> chrome, CardsScreen screen, int width = 1280, int height = 900)
    {
        chrome.Render(new SessionsScreen([], "test", Now), null, screen, new RectF32(0, 0, width, height));
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        return [.. regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action)];
    }

    private static void Press(PilotChrome<RgbaImage> chrome, string action)
    {
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
    }

    [Fact]
    public void EveryOpenCardOfThePanelHasTheButtonAndAHandledOrClosedOneHasNone()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        chrome.Cards.OnChatAbout.ShouldNotBeNull();
        var screen = CardsScreen.From([Card("c1"), Card("c2", bin: CardBin.Ring), Card("c3", CardState.Closed, CardBin.Handled)], [], Now);

        var buttons = Buttons(chrome, screen);

        buttons.ShouldContain("card:c1:chat");
        buttons.ShouldContain("card:c2:chat");
        buttons.ShouldNotContain("card:c3:chat");
    }

    [Fact]
    public void WithNoHookTheCardsHaveNoButtonAndWhileAPressIsWithTheNodeItIsDead()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        var screen = CardsScreen.From([Card("c1")], [], Now);
        chrome.Cards.OnChatAbout = null;
        Buttons(chrome, screen).ShouldNotContain("card:c1:chat");

        chrome.Cards.OnChatAbout = _ => { };
        chrome.Cards.NoteWorking("c1", "card:c1:1");
        Buttons(chrome, screen).ShouldNotContain("card:c1:chat");
        chrome.Cards.NoteWorking("c1", null);
        Buttons(chrome, screen).ShouldContain("card:c1:chat");
    }

    [Fact]
    public void AWatchOnlyCardHasNoLiveChatButton()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        chrome.Cards.CanSteer = node => node?.Here != false;
        var other = new NodeMark("de2", "http://de2", "DE2", Here: false);
        var card = Card("c1");
        var screen = CardsScreen.Merge([(other, CardsScreen.From([card], [], Now))], null);

        Buttons(chrome, screen).ShouldNotContain("card:c1:chat");
    }

    [Fact]
    public void TheFlightPageHasTheButtonForItsCard()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        chrome.Flights.CanCommand = true;
        var ready = new CardRow
        {
            Id = "c9", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/TIA1611", Title = "TIA1611's PR #7 is ready to merge",
            At = Now, State = CardState.Open, Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1611", Args = ["abc1234"] }],
        };
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail("TIA1611");

        chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([FlightLine.Build([Flight()], [], Now, [])[0]], "test", Now), CardsScreen.From([ready], [], Now), new RectF32(0, 0, 1280, 900));
        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:chat-card" }).OnClick!.Invoke(InputModifier.None);

        chrome.Page.ShouldBe(PilotPage.Chat);
        chrome.Chat.QuotedCard.ShouldBe(ready);
    }

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611", Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
    };

    [Fact]
    public void PressingItShowsTheChatWithTheCardQuotedAndTheBoxFocused()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        chrome.Chat.OnSendAboutCard = (_, _, _) => Task.FromResult<string?>(null);
        var screen = CardsScreen.From([Card("c1")], [], Now);
        Buttons(chrome, screen);

        Press(chrome, "card:c1:chat");

        chrome.Page.ShouldBe(PilotPage.Chat);
        chrome.Chat.QuotedCard!.Id.ShouldBe("c1");
        chrome.Chat.Tree();
        chrome.Chat.Ui.Focus.Current.ShouldBe(chrome.Chat.Input);
        Described(chrome.Chat).ShouldContain("about card c1: card c1");

        // Dropped, the card goes and the next message is a plain one.
        var regions = new List<ClickableRegion>();
        chrome.Chat.Render(new RectF32(0, 0, 1280, 900));
        chrome.Chat.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "chat:drop-card" }).OnClick!.Invoke(InputModifier.None);
        chrome.Chat.QuotedCard.ShouldBeNull();
    }

    private static string Described(ChatView<RgbaImage> chat) =>
        string.Join(' ', Layout.Engine.Arrange(chat.Tree(), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()));

    [Fact]
    public async Task EnterSendsTheTypedWordsAboutTheCardAndDropsIt()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        var sent = new List<(string Card, string? Node, string Words)>();
        chrome.Chat.OnSendAboutCard = (card, node, words) =>
        {
            sent.Add((card.Id, node?.Name, words));
            return Task.FromResult<string?>(null);
        };
        var plain = new List<string>();
        chrome.Chat.OnSend = text =>
        {
            plain.Add(text);
            return Task.FromResult<string?>(null);
        };
        chrome.Chat.Attach(Card("c1"), null);
        chrome.Chat.Tree();
        var tracker = new BackgroundTaskTracker();

        TextInputInteraction.HandleText(chrome.Chat.Input, "why did it land?");
        TextInputInteraction.HandleKey(InputKey.Enter, InputModifier.None, new TextInputInteraction.KeyContext(tracker, chrome.Chat.Ui.Focus, () => { }));
        await tracker.DrainAsync();

        sent.ShouldBe([("c1", null, "why did it land?")]);
        plain.ShouldBeEmpty();
        chrome.Chat.QuotedCard.ShouldBeNull();

        // The next message has no card to be about.
        TextInputInteraction.HandleText(chrome.Chat.Input, "and now?");
        TextInputInteraction.HandleKey(InputKey.Enter, InputModifier.None, new TextInputInteraction.KeyContext(tracker, chrome.Chat.Ui.Focus, () => { }));
        await tracker.DrainAsync();
        plain.ShouldBe(["and now?"]);
    }

    private static TranscriptEntry Sent(string words, CardRow card) => new()
    {
        Index = 0, Kind = TranscriptKind.User, At = Now,
        Text = CardContext.Compose(card, "DE1", null, words),
    };

    [Fact]
    public async Task ASentMessageShowsInTheChatAsTheCardsQuoteAndTheWordsNotTheWholeBlock()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var entry = Sent("why did it land?", Card("c1"));
        chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = after < 0 ? [entry] : [], Total = 1 });
        chat.OnSend = _ => Task.FromResult<string?>(null);
        chat.Open();
        for (var i = 0; i < 100 && chat.Entries.Count < 1; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        chat.Close();

        var text = Described(chat);

        text.ShouldContain("> card c1: card c1");
        text.ShouldContain("> The flight landed");
        text.ShouldContain("why did it land?");
        text.ShouldNotContain("[pilot card");
        text.ShouldNotContain("node:");
        text.ShouldNotContain("[end of card]");
    }

    [Fact]
    public void AnEntryThatIsNotACardMessageIsDrawnAsItIs()
    {
        var plain = new TranscriptEntry { Index = 0, Kind = TranscriptKind.User, Text = "hello" };

        ChatView<RgbaImage>.AsQuoted(plain).ShouldBeSameAs(plain);
    }

    // A fleet of fake clients: each node has its own chat feed and records what it is told; "no grant" nodes refuse.
    private sealed class FakeFleet
    {
        public readonly List<string> ToHere = [];
        public readonly Dictionary<string, List<string>> ToNode = [];
        public readonly List<string> Read = [];
        public readonly HashSet<string> Granted = [];

        public void Wire(PilotChrome<RgbaImage> chrome, TranscriptEntry hereEntry, TranscriptEntry otherEntry)
        {
            chrome.Chat.Load = (after, _) => { Read.Add("here"); return Task.FromResult(new ChatPage { Entries = after < 0 ? [hereEntry] : [], Total = 1 }); };
            chrome.Chat.OnSend = text => { ToHere.Add(text); return Task.FromResult<string?>(null); };
            chrome.Chat.LoadOn = (node, after, _) => { Read.Add(node.Name); return Task.FromResult(new ChatPage { Entries = after < 0 ? [otherEntry] : [], Total = 1 }); };
            chrome.Chat.OnSendOn = (node, text) => Told(node, text);
            chrome.Chat.OnSendAboutCard = (card, node, words) => Told(node, CardContext.Compose(card, node?.Name ?? "", null, words));
        }

        private Task<string?> Told(NodeMark? node, string text)
        {
            if (node is null or { Here: true })
            {
                ToHere.Add(text);
                return Task.FromResult<string?>(null);
            }
            if (!Granted.Contains(node.Where))
            {
                return Task.FromResult<string?>($"{node.Name} is watched, not commanded: Connect to {node.Name} to steer");
            }
            if (!ToNode.TryGetValue(node.Name, out var told))
            {
                ToNode[node.Name] = told = [];
            }
            told.Add(text);
            return Task.FromResult<string?>(null);
        }
    }

    private static readonly NodeMark Se1Node = new("se1", "http://se1", "SE1", Here: false);

    private static TranscriptEntry Said(string text) => new() { Index = 0, Kind = TranscriptKind.Assistant, At = Now, Text = text };

    private static async Task Typed(PilotChrome<RgbaImage> chrome, string words)
    {
        var tracker = new BackgroundTaskTracker();
        TextInputInteraction.HandleText(chrome.Chat.Input, words);
        TextInputInteraction.HandleKey(InputKey.Enter, InputModifier.None, new TextInputInteraction.KeyContext(tracker, chrome.Chat.Ui.Focus, () => { }));
        await tracker.DrainAsync();
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AnotherNodesCardIsChattedAboutWithThatNodeAndNotThisOne()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        var fleet = new FakeFleet();
        fleet.Granted.Add(Se1Node.Where);
        fleet.Wire(chrome, Said("this node's watchtower"), Said("SE1's watchtower"));
        chrome.Cards.CanSteer = _ => true;
        var screen = CardsScreen.Merge([(Se1Node, CardsScreen.From([Card("c1")], [], Now))], null);
        Buttons(chrome, screen).ShouldContain("card:c1:chat");

        Press(chrome, "card:c1:chat");
        await WaitForAsync(() => chrome.Chat.Entries.Count > 0);

        chrome.Page.ShouldBe(PilotPage.Chat);
        chrome.Chat.Node.ShouldBe(Se1Node);
        chrome.Chat.Entries.Single().Text.ShouldBe("SE1's watchtower");
        fleet.Read.ShouldNotContain("here");
        Described(chrome.Chat).ShouldContain("watchtower · SE1");
        Described(chrome.Chat).ShouldContain("This node's watchtower");

        await Typed(chrome, "why did it land?");

        fleet.ToHere.ShouldBeEmpty();
        fleet.ToNode["SE1"].Single().ShouldContain("why did it land?");
        fleet.ToNode["SE1"].Single().ShouldContain("card c1");
        chrome.Chat.Close();
    }

    [Fact]
    public async Task TheWayBackReadsThisNodesChatAndTheRailsChatOpensIt()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        var fleet = new FakeFleet();
        fleet.Wire(chrome, Said("this node's watchtower"), Said("SE1's watchtower"));
        chrome.Cards.CanSteer = _ => true;
        chrome.Chat.SwitchTo(Se1Node);
        chrome.Chat.Attach(Card("c1"), Se1Node);
        chrome.Show(PilotPage.Chat);
        chrome.Chat.Node.ShouldBeNull("the rail's Chat opens this node's chat");

        chrome.Chat.SwitchTo(Se1Node);
        chrome.Chat.Render(new RectF32(0, 0, 1280, 900));
        var regions = new List<ClickableRegion>();
        chrome.Chat.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "chat:back" }).OnClick!.Invoke(InputModifier.None);
        await WaitForAsync(() => chrome.Chat.Entries.Count > 0 && chrome.Chat.Entries[0].Text == "this node's watchtower");

        chrome.Chat.Node.ShouldBeNull();
        chrome.Chat.Entries.Single().Text.ShouldBe("this node's watchtower");
        chrome.Chat.QuotedCard.ShouldBeNull();
        await Typed(chrome, "hello");
        fleet.ToHere.ShouldBe(["hello"]);
        chrome.Chat.Close();
    }

    [Fact]
    public async Task ANodeThatRefusesIsSaidInTheChatsNotices()
    {
        var chrome = NewChrome(out var renderer);
        using var _ = renderer;
        var fleet = new FakeFleet();
        fleet.Wire(chrome, Said("here"), Said("there"));
        chrome.Cards.CanSteer = _ => true;
        var screen = CardsScreen.Merge([(Se1Node, CardsScreen.From([Card("c1")], [], Now))], null);
        Buttons(chrome, screen);
        Press(chrome, "card:c1:chat");
        chrome.Chat.Tree();

        await Typed(chrome, "anyone there?");

        chrome.Chat.Notices.Single().ShouldContain("Connect to SE1 to steer");
        fleet.ToNode.ShouldBeEmpty();
        fleet.ToHere.ShouldBeEmpty();
        chrome.Chat.Close();
    }

    [Fact]
    public async Task CapturesOfTheChatOnAnotherNodesCardAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var width in new[] { 900, 1800 })
        {
            var chrome = NewChrome(out var renderer, (uint)width, 700);
            using var _ = renderer;
            var fleet = new FakeFleet();
            fleet.Granted.Add(Se1Node.Where);
            var entry = Sent("why did it land before its review finished?", Card("c0"));
            fleet.Wire(chrome, Said("this node's watchtower"), entry);
            chrome.Cards.CanSteer = _ => true;
            var screen = CardsScreen.Merge([(Se1Node, CardsScreen.From([Card("c1", bin: CardBin.Ring)], [], Now))], null);
            Buttons(chrome, screen);
            Press(chrome, "card:c1:chat");
            await WaitForAsync(() => chrome.Chat.Entries.Count > 0);
            chrome.Render(new SessionsScreen([], "test", Now), null, screen, new RectF32(0, 0, width, 700));
            File.WriteAllBytes(Path.Combine(dir, $"chat-other-node-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, 700));
            chrome.Chat.Close();
        }
    }

    [Fact]
    public async Task CapturesOfThePanelWithTheButtonAndTheChatWithAQuotedCardAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var width in new[] { 900, 1800 })
        {
            var chrome = NewChrome(out var renderer, (uint)width, 700);
            using var _ = renderer;
            chrome.Chat.OnSendAboutCard = (_, _, _) => Task.FromResult<string?>(null);
            var entry = Sent("why did it land before its review finished?", Card("c0"));
            chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = after < 0 ? [entry] : [], Total = 1 });
            var screen = CardsScreen.From([Card("c1", bin: CardBin.Ring), Card("c2")], [], Now);
            chrome.Show(PilotPage.Home);
            chrome.Render(new SessionsScreen([], "test", Now), null, screen, new RectF32(0, 0, width, 700));
            File.WriteAllBytes(Path.Combine(dir, $"chat-about-panel-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, 700));

            Press(chrome, "card:c1:chat");
            for (var i = 0; i < 100 && chrome.Chat.Entries.Count < 1; i++)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            chrome.Render(new SessionsScreen([], "test", Now), null, screen, new RectF32(0, 0, width, 700));
            File.WriteAllBytes(Path.Combine(dir, $"chat-about-chat-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, 700));
        }
    }
}
