using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A picture pasted into the Chat page's box (PIL1366): kept by Ctrl+V, sent with the words to the watchtower the box sends to, never dropped without a word.</summary>
public sealed class ChatPictureSendTests
{
    private static readonly NodeMark Se1 = new("se1", "http://se1", "SE1", Here: false);

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _renderer = new(1280, 900);

        public Rig()
        {
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chat.Load = (_, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
            Chat.OnSend = text => { Plain.Add(text); return Task.FromResult<string?>(null); };
            Chat.GetClipboardImage = () => ([1, 2, 3], "png");
            Chat.OnSendAboutCard = (card, node, words) => { Plain.Add($"card {card.Id}: {words}"); return Task.FromResult<string?>(null); };
            Chat.OnSendOn = (node, text) => { Plain.Add($"{node.Name}: {text}"); return Task.FromResult<string?>(null); };
        }

        public PilotChrome<RgbaImage> Chrome { get; }
        public ChatView<RgbaImage> Chat => Chrome.Chat;
        public List<string> Plain { get; } = [];
        public List<(string Target, string Words, (string Name, byte[] Bytes)? Picture)> Sent { get; } = [];
        public string? Refuse { get; set; }

        public void WireAllTargets()
        {
            Chat.OnSendWithPicture = (words, picture) => Told("this node", words, picture);
            Chat.OnSendOnWithPicture = (node, words, picture) => Told(node.Name, words, picture);
            Chat.OnSendAboutCardWithPicture = (card, node, words, picture) => Told($"card {card.Id}", words, picture);
        }

        private Task<string?> Told(string target, string words, (string Name, byte[] Bytes)? picture)
        {
            Sent.Add((target, words, picture));
            return Task.FromResult(Refuse);
        }

        // Ctrl+V as the box handles it: the key override first, the text paste when it says no.
        public bool CtrlV()
        {
            Chat.Tree();
            return Chat.Input.OnKeyOverride!(TextInputKey.Paste);
        }

        public string Described() =>
            string.Join(' ', DIR.Lib.Layout.Engine.Arrange(Chat.Tree(), new Rect<int>(0, 0, 160, 80), Console.Lib.CellMeasureContext.PixelAuthored)
                .Select(a => a.Node).OfType<DIR.Lib.Layout.Node.Leaf>().Select(l => l.Content).OfType<DIR.Lib.Layout.Content.Text>().Select(t => t.Value.Trim()));

        public void Dispose() => _renderer.Dispose();
    }

    private static CardRow Card(string id) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/TIA1611", Title = $"card {id}", At = Live.At, State = CardState.Open,
    };

    [Fact]
    public async Task ACtrlVPictureIsKeptAndEnterSendsItWithTheWordsToThisNodesWatchtower()
    {
        using var rig = new Rig();
        rig.WireAllTargets();

        rig.CtrlV().ShouldBeTrue();
        rig.Chat.PendingPicture.ShouldNotBeNull().Name.ShouldBe("pasted.png");
        rig.Described().ShouldContain("picture: pasted.png");
        rig.Described().ShouldContain("Ctrl+V pastes a picture");

        rig.Chat.Input.InsertText("what is this?");
        await rig.Chat.SendAsync();

        var (target, words, picture) = rig.Sent.ShouldHaveSingleItem();
        target.ShouldBe("this node");
        words.ShouldBe("what is this?");
        picture.ShouldNotBeNull().Bytes.ShouldBe([1, 2, 3]);
        rig.Chat.Input.Text.ShouldBe("");
        rig.Chat.PendingPicture.ShouldBeNull();
        rig.Plain.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithNoPictureOnTheClipboardCtrlVPastesTextAndEveryTargetSendsAsItDid()
    {
        using var rig = new Rig();
        rig.WireAllTargets();
        rig.Chat.GetClipboardImage = () => null;

        rig.CtrlV().ShouldBeFalse();
        rig.Chat.PendingPicture.ShouldBeNull();

        rig.Chat.Input.InsertText("plain words");
        await rig.Chat.SendAsync();

        // This node's own send takes a picture, so the words go through it with none, as a design chat's do.
        rig.Plain.ShouldBeEmpty();
        var (_, words, picture) = rig.Sent.ShouldHaveSingleItem();
        words.ShouldBe("plain words");
        picture.ShouldBeNull();
    }

    [Fact]
    public async Task AnotherNodesWatchtowerTakesThePictureThroughItsOwnSend()
    {
        using var rig = new Rig();
        rig.WireAllTargets();
        rig.Chat.SwitchTo(Se1);

        rig.CtrlV().ShouldBeTrue();
        rig.Chat.Input.InsertText("look at this");
        await rig.Chat.SendAsync();

        var (target, words, picture) = rig.Sent.ShouldHaveSingleItem();
        target.ShouldBe("SE1");
        words.ShouldBe("look at this");
        picture.ShouldNotBeNull();
        rig.Chat.PendingPicture.ShouldBeNull();
    }

    [Fact]
    public async Task AQuotedCardGetsThePictureThroughItsTell()
    {
        using var rig = new Rig();
        rig.WireAllTargets();
        rig.Chat.Attach(Card("c1"), null);

        rig.CtrlV().ShouldBeTrue();
        rig.Chat.Input.InsertText("why?");
        await rig.Chat.SendAsync();

        var (target, words, picture) = rig.Sent.ShouldHaveSingleItem();
        target.ShouldBe("card c1");
        words.ShouldBe("why?");
        picture.ShouldNotBeNull();
        rig.Chat.QuotedCard.ShouldBeNull();
    }

    [Fact]
    public async Task AnotherNodesCardAskedAboutInTheFleetChatGoesToTheShownWatchtowersPictureSend()
    {
        using var rig = new Rig();
        rig.WireAllTargets();
        rig.Chat.Attach(Card("c9"), Se1);

        rig.CtrlV().ShouldBeTrue();
        rig.Chat.Input.InsertText("and this?");
        await rig.Chat.SendAsync();

        var (target, words, picture) = rig.Sent.ShouldHaveSingleItem();
        target.ShouldBe("this node");
        words.ShouldContain("c9");
        words.ShouldContain("SE1");
        picture.ShouldNotBeNull();
    }

    [Fact]
    public async Task ATargetWithNoPictureSendKeepsTheWordsAndThePictureAndSaysWhy()
    {
        using var rig = new Rig();
        rig.Chat.OnSendWithPicture = (words, picture) => Task.FromResult<string?>(null);
        rig.Chat.SwitchTo(Se1);
        // Another node's watchtower has no picture send: Ctrl+V takes none and the box does not hint at one.
        rig.CtrlV().ShouldBeFalse();
        rig.Described().ShouldNotContain("Ctrl+V pastes a picture");

        // A picture held for this node's chat, then the person moves to a target that cannot take it: SwitchTo leaves a held picture alone (only Restart clears it), so hold one on the card instead.
        rig.Chat.SwitchTo(null);
        rig.CtrlV().ShouldBeTrue();
        rig.Chat.Attach(Card("c1"), null);
        rig.Chat.Input.InsertText("about the card");
        await rig.Chat.SendAsync();

        rig.Plain.ShouldBeEmpty();
        rig.Chat.Input.Text.ShouldBe("about the card");
        rig.Chat.PendingPicture.ShouldNotBeNull();
        rig.Chat.Notices.ShouldHaveSingleItem().ShouldContain("card c1 cannot take a picture: Drop sends the words alone");

        // Drop, and the words go alone.
        rig.Chat.DropPicture();
        await rig.Chat.SendAsync();
        rig.Plain.ShouldBe(["card c1: about the card"]);
    }

    [Fact]
    public async Task ARefusedSendKeepsThePictureOnItsFailedLineAndEditPutsItBack()
    {
        using var rig = new Rig();
        rig.WireAllTargets();
        rig.Refuse = "that node runs an older pilot";

        rig.CtrlV().ShouldBeTrue();
        rig.Chat.Input.InsertText("see this");
        await rig.Chat.SendAsync();

        rig.Chat.Failures.ShouldBe(["that node runs an older pilot"]);
        rig.Chat.PendingPicture.ShouldBeNull();

        var regions = new List<ClickableRegion>();
        rig.Chat.Render(new RectF32(0, 0, 1280, 900));
        rig.Chat.CollectPaintedRegions(regions);
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("chat:edit:", StringComparison.Ordinal)).OnClick!.Invoke(InputModifier.None);

        rig.Chat.Input.Text.ShouldBe("see this");
        rig.Chat.PendingPicture.ShouldNotBeNull().Name.ShouldBe("pasted.png");
        rig.Chat.Failures.ShouldBeEmpty();
    }

    [Fact]
    public void ABoxWithNoClipboardImageHasNoHintAndLeavesCtrlVToTheText()
    {
        using var rig = new Rig();
        rig.WireAllTargets();
        rig.Chat.GetClipboardImage = null;

        rig.Chat.Tree();
        rig.Chat.Input.OnKeyOverride.ShouldBeNull();
        rig.Described().ShouldNotContain("Ctrl+V pastes a picture");
    }
}
