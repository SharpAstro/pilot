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
