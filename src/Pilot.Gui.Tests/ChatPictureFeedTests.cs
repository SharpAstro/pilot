using Console.Lib;
using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// A picture sent with a tell, in the Chat page's feed (PIL1366): the person's message draws its words and a thumbnail that opens
/// window-wide, never the path line the watchtower was told. A queued line and an outbox line show a short marker, and the terminal's
/// cells show a picture line. CPU-rendered from a held chat with no node.
/// </summary>
public sealed class ChatPictureFeedTests
{
    private const string Path = @"C:\Users\x\AppData\Local\pilot2\attachments\ab12cd34.png";
    private static readonly DateTimeOffset At = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    internal static AttachmentPicture Picture()
    {
        var rgba = new byte[200 * 100 * 4];
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 200; x++)
            {
                var at = (y * 200 + x) * 4;
                (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = ((byte)(255 - x * 255 / 199), (byte)(y * 2), (byte)(x * 255 / 199), 255);
            }
        }
        return new AttachmentPicture(200, 100, rgba);
    }

    internal sealed class Held : IDisposable
    {
        public RgbaImageRenderer Renderer { get; }
        public ChatView<RgbaImage> Chat { get; }
        public List<RectF32> Painted { get; } = [];

        public Held(int width, int height, TranscriptEntry[] entries, ChatPage? page = null)
        {
            Renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chat = new ChatView<RgbaImage>(Renderer) { FontPath = SessionsCapture.FontPath };
            var pic = Picture();
            var blit = AttachmentPictures.PaintOn(Renderer.Surface);
            Chat.Gallery.Lookup = _ => pic;
            Chat.Gallery.Draw = (p, rect) => { Painted.Add(rect); blit(p, rect); };
            var shown = page ?? new ChatPage { Entries = entries, Total = entries.Length };
            Chat.Load = (after, _) => Task.FromResult(after < 0 ? shown : new ChatPage { Entries = [], Total = shown.Total });
            Chat.OnSend = _ => Task.FromResult<string?>(null);
            Chat.Open();
            for (var i = 0; i < 100 && (Chat.Entries.Count < entries.Length || Texts().Contains("Loading the conversation")); i++)
            {
                Task.Delay(50, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            }
            Chat.Close();
        }

        public System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<float>> Arranged(int width, int height, float dpi = 1f) =>
            Layout.Engine.Arrange(Chat.Tree(), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(Renderer, SessionsCapture.FontPath, dpi));

        public string Texts(int width = 1280, int height = 900) => Flat(string.Join('\n', Arranged(width, height)
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)));

        public void Dispose() => Renderer.Dispose();
    }

    /// <summary>Prose is drawn one word to a leaf, so a phrase is asked for with the whitespace between leaves collapsed.</summary>
    internal static string Flat(string texts) => System.Text.RegularExpressions.Regex.Replace(texts, @"\s+", " ");

    internal static TranscriptEntry Tell(int index, string words, string? path = Path) =>
        new() { Index = index, Kind = TranscriptKind.User, Text = path is null ? words : words + TellPictures.Line(path), At = At };

    internal static TranscriptEntry[] Feed() =>
    [
        new() { Index = 0, Kind = TranscriptKind.Assistant, Text = "Ready.", At = At },
        Tell(1, "what is this streak?"),
    ];

    private static List<ClickableRegion> Regions(ChatView<RgbaImage> chat)
    {
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        return regions;
    }

    [Fact]
    public void ThePersonsMessageDrawsItsWordsAndAThumbnailAndThePathAppearsNowhere()
    {
        using var held = new Held(1280, 900, Feed());
        held.Chat.Render(new RectF32(0, 0, 1280, 900));

        var texts = held.Texts();
        texts.ShouldContain("what is this streak?");
        texts.ShouldNotContain("ab12cd34");
        texts.ShouldNotContain("attachments");
        texts.ShouldNotContain("Read tool");
        held.Painted.ShouldNotBeEmpty("the thumbnail was filled");
        held.Painted[0].Width.ShouldBeLessThanOrEqualTo(AttachmentGallery.ThumbWidth + 0.5f);
    }

    [Fact]
    public void AClickOnTheThumbnailOpensItWindowWideAndEscapeClosesIt()
    {
        using var held = new Held(1280, 900, Feed());
        held.Chat.Render(new RectF32(0, 0, 1280, 900));

        Regions(held.Chat).Single(r => r.Result is HitResult.ButtonHit { Action: "attachment:chat:1:0:ab12cd34" }).OnClick!.Invoke(InputModifier.None);
        var open = held.Chat.Gallery.Viewing.ShouldNotBeNull();
        open.Id.ShouldBe("ab12cd34");
        open.Ext.ShouldBe("png");
        held.Chat.Gallery.Viewer().ShouldNotBeNull();

        held.Chat.Gallery.HandleKey(InputKey.Escape).ShouldBeTrue();
        held.Chat.Gallery.Viewing.ShouldBeNull();
    }

    [Fact]
    public void ACopyTheNodeNoLongerHoldsSaysSoEvenOnARowBuiltBeforeTheFetchFailed()
    {
        using var held = new Held(1280, 900, Feed());
        held.Chat.Render(new RectF32(0, 0, 1280, 900));
        held.Texts().ShouldNotContain("No preview");

        held.Chat.Gallery.Failed = _ => true;
        held.Chat.Gallery.Lookup = _ => null;
        held.Chat.Render(new RectF32(0, 0, 1280, 900));

        held.Texts().ShouldContain("No preview: the node no longer has this image");
        held.Texts().ShouldNotContain("ab12cd34");
    }

    [Fact]
    public void ARelayedMessageAndAnotherNodesWatchtowerKeepTheirPictureToo()
    {
        var relayed = "<cross-session-message from=\"x\" from-name=\"controller\" from-mode=\"m\">see this" + TellPictures.Line(Path) + "</cross-session-message>";
        using var held = new Held(1280, 900,
        [
            new() { Index = 0, Kind = TranscriptKind.User, Text = relayed, At = At },
            Tell(1, "[From the watchtower of node SE1] from the other node", Path.Replace("ab12cd34", "ff00ee11")),
        ]);
        held.Chat.Render(new RectF32(0, 0, 1280, 900));

        held.Texts().ShouldContain("see this");
        held.Texts().ShouldNotContain("attachments");
        held.Painted.Count.ShouldBe(2);
    }

    [Fact]
    public void ACardTellWithAPictureKeepsItsQuoteAndShowsTheThumbnail()
    {
        var card = new CardRow { Id = "c7", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/TIA1611", Title = "TIA1611 landed", At = At, State = CardState.Open };
        var words = CardContext.Compose(card, "DE1", null, "is this the right plate?") + TellPictures.Line(Path);
        using var held = new Held(1280, 900, [new() { Index = 0, Kind = TranscriptKind.User, Text = words, At = At }]);
        held.Chat.Render(new RectF32(0, 0, 1280, 900));

        var texts = held.Texts();
        texts.ShouldContain("card c7");
        texts.ShouldContain("is this the right plate?");
        texts.ShouldNotContain("attachments");
        held.Painted.ShouldNotBeEmpty();
    }

    [Fact]
    public void AQueuedLineShowsItsWordsAndAMarkerNeverThePath()
    {
        var queued = "waiting words" + TellPictures.Line(Path);
        using var held = new Held(1280, 900, [], new ChatPage { Entries = [], Total = 0, Queued = [queued], QueuedIds = ["s1"], QueuedBehind = [ChatPage.ReadAtNextStep] });
        held.Chat.Render(new RectF32(0, 0, 1280, 900));

        var texts = held.Texts();
        texts.ShouldContain("waiting words");
        texts.ShouldContain("[picture attached]");
        texts.ShouldNotContain("attachments");
        texts.ShouldNotContain("ab12cd34");
    }

    [Fact]
    public async Task AnOutboxLineWithAPictureShowsTheMarkerNotThePath()
    {
        using var held = new Held(1280, 900, []);
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        held.Chat.GetClipboardImage = () => ([1, 2, 3], "png");
        held.Chat.OnSendWithPicture = (_, _) => gate.Task;
        held.Chat.Tree();
        held.Chat.Input.OnKeyOverride!(TextInputKey.Paste).ShouldBeTrue();
        held.Chat.Input.InsertText("sending words");
        var send = held.Chat.SendAsync();

        var texts = held.Texts();
        texts.ShouldContain("sending words");
        texts.ShouldContain("[picture attached]");

        gate.SetResult("refused");
        await send;
    }

    [Fact]
    public void TheCellsRenderShowsAPictureLineNotThePathAndTheBoxNoPasteHint()
    {
        using var held = new Held(160, 60, Feed());
        held.Chat.Cells = true;
        held.Chat.GetClipboardImage = () => ([1], "png");
        held.Chat.OnSendWithPicture = (_, _) => Task.FromResult<string?>(null);
        var arranged = Layout.Engine.Arrange(held.Chat.Tree(), new Rect<int>(0, 0, 160, 60), CellMeasureContext.PixelAuthored);
        var texts = Flat(string.Join('\n', arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)));

        texts.ShouldContain("what is this streak?");
        texts.ShouldContain("[picture, PNG: shown in the window]");
        texts.ShouldNotContain("attachments");
        texts.ShouldNotContain("ab12cd34");
        texts.ShouldNotContain("Ctrl+V");
    }

    [Theory]
    [InlineData(520, 800)]
    [InlineData(1640, 1000)]
    public void NoTextIsDrawnOverOtherTextAtANarrowAndAWideWidth(int width, int height)
    {
        var longer = Tell(2, "a long question about what the streak across the lower left of this frame could be, whether it is a satellite, a plane or an artefact of the guiding, and what to do about it before the next sub");
        using var held = new Held(width, height, [.. Feed(), longer]);
        var arranged = held.Arranged(width, height);
        TextOverlap.Faults(arranged).Where(f => f.Bounds.Y > 70f).Select(f => f.Line).ShouldBeEmpty($"at {width} wide");
    }
}
