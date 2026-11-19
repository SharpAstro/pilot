using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The chat with the designer on a design's page (PIL1151), drawn on the CPU from a sample feed of three turns and two versions with no node: the status in
/// plain words, a send, Point at this, a version published in the next read and a click on a version line.
/// </summary>
public sealed class DesignChatTests
{
    private sealed class Rig : IDisposable
    {
        private readonly RectF32 _bounds = new(0, 0, 1400, 900);
        private readonly RgbaImageRenderer _renderer = new(1400, 900);
        private readonly DesignsScreen _screen = PageData.DesignsFor("sample", PageData.Now);

        public Rig(ChatPage first, Func<int, ChatPage>? later = null)
        {
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            Chrome.Show(PilotPage.Designs);
            Router = new InputRouter(Chrome.Designs.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            Chrome.Designs.Chat.Load = (after, _) => Task.FromResult(after < 0 ? first : later?.Invoke(after) ?? new ChatPage { Entries = [], Total = first.Total, Status = first.Status });
            Chrome.Designs.Chat.OnSend = _ => Task.FromResult<string?>(null);
            Chrome.Designs.OnVersion = (id, number) => Versions.Add((id, number));
        }

        public List<(string Id, int Number)> Versions { get; } = [];
        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }
        public DesignsView<RgbaImage> View => Chrome.Designs;

        public void Paint()
        {
            var data = PageData.For("sample");
            // No cards: they open the For you bar over the page's right side (#1184), which is the chat column these tests press in,
            // and the tests are about the chat. Collapsed, the cards would stand as toasts at its top right instead.
            Chrome.Render(data.Sessions, data.Flights, null, data.Jobs, data.Fleet, data.Repos, data.PullRequests, _bounds, data.Machines, null, data.Spend, _screen);
        }

        public async Task OpenAsync(int entries)
        {
            View.Open("DSN-1");
            for (var i = 0; i < 500 && View.Chat.Entries.Count < entries; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            View.Chat.Entries.Count.ShouldBe(entries);
            Paint();
        }

        public List<ClickableRegion> Regions()
        {
            var regions = new List<ClickableRegion>();
            foreach (var widget in Chrome.Widgets)
            {
                widget.CollectPaintedRegions(regions);
            }
            return regions;
        }

        public void Click(ClickableRegion region)
        {
            var x = region.X + region.Width / 2f;
            var y = region.Y + region.Height / 2f;
            Router.Handle(new InputEvent.MouseDown(x, y, MouseButton.Left, InputModifier.None, 1));
            Router.Handle(new InputEvent.MouseUp(x, y, MouseButton.Left));
            Paint();
        }

        public List<string> Texts() =>
            SentenceRuns.Texts(Layout.Engine.Arrange(View.Tree(_screen), new Rect<float>(0, 0, 1400, 900), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).Select(a => a.Node));

        public void Dispose()
        {
            View.Chat.Close();
            _renderer.Dispose();
        }
    }

    [Fact]
    public async Task TheChatShowsThreeTurnsAndTwoVersionsWithTheDesignersStateInWords()
    {
        using var rig = new Rig(PageData.DesignChatPage());
        await rig.OpenAsync(5);

        var texts = string.Join("\n", rig.Texts());
        texts.ShouldContain("Make the tabs bigger.");
        texts.ShouldContain("Drafted both screens; checking them now.");
        texts.ShouldContain("A home screen with two tabs and a settings screen.");
        texts.ShouldContain("published v1: brief");
        texts.ShouldContain("published v2: message");
        texts.ShouldContain("checking: 2 findings");
    }

    [Theory]
    [InlineData("drafting")]
    [InlineData("held until 22:00 UTC")]
    public async Task TheStatusLineIsTheFeedsOwnWords(string status)
    {
        using var rig = new Rig(PageData.DesignChatPage(status));
        await rig.OpenAsync(5);

        string.Join("\n", rig.Texts()).ShouldContain($"designer: {status}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheWorkingLineIsDrawnOnlyWhileTheDesignerWorks(bool working)
    {
        using var rig = new Rig(PageData.DesignChatPage("published v2", working: working));
        await rig.OpenAsync(5);

        string.Join(Environment.NewLine, rig.Texts()).Contains("working…", StringComparison.Ordinal).ShouldBe(working);
    }

    [Fact]
    public async Task EnterSendsTheTypedWordsToTheHost()
    {
        using var rig = new Rig(PageData.DesignChatPage());
        var said = new List<(string Text, (string Name, byte[] Bytes)? Picture)>();
        rig.View.Chat.OnSendWithPicture = (text, picture) =>
        {
            said.Add((text, picture));
            return Task.FromResult<string?>(null);
        };
        await rig.OpenAsync(5);

        rig.View.Chat.Input.InsertText("make the header darker");
        await rig.View.Chat.SendAsync();

        said.ShouldBe([("make the header darker", null)]);
        rig.View.Chat.Input.Text.ShouldBe("");
    }

    [Fact]
    public async Task APastedPictureGoesWithTheNextMessageOnly()
    {
        using var rig = new Rig(PageData.DesignChatPage());
        var said = new List<(string Text, (string Name, byte[] Bytes)? Picture)>();
        rig.View.Chat.OnSendWithPicture = (text, picture) =>
        {
            said.Add((text, picture));
            return Task.FromResult<string?>(null);
        };
        rig.View.Chat.GetClipboardImage = () => ([1, 2, 3], "png");
        await rig.OpenAsync(5);

        rig.Paint();
        rig.View.Chat.Input.Text.ShouldBe("");
        // Ctrl+V with a picture on the clipboard keeps it for the next message.
        rig.View.Chat.Input.OnKeyOverride!(TextInputKey.Paste).ShouldBeTrue();
        rig.View.Chat.PendingPicture.ShouldNotBeNull();
        rig.Paint();
        string.Join("\n", rig.Texts()).ShouldContain("picture: pasted.png");

        rig.View.Chat.Input.InsertText("like this");
        await rig.View.Chat.SendAsync();
        rig.View.Chat.Input.InsertText("and again");
        await rig.View.Chat.SendAsync();

        said[0].Picture.ShouldNotBeNull().Name.ShouldBe("pasted.png");
        said[1].Picture.ShouldBeNull();
    }

    // The failed line's Retry or Edit, pressed as the person presses it.
    private static void PressFailed(Rig rig, string verb)
    {
        rig.Paint();
        rig.Regions().First(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith($"chat:{verb}:", StringComparison.Ordinal)).OnClick!.Invoke(InputModifier.None);
    }

    private static async Task SettledAsync(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        done().ShouldBeTrue();
    }

    [Fact]
    public async Task ARefusedSendWithAPictureRetriesWithItOnceAndEditPutsItBackInTheBox()
    {
        using var rig = new Rig(PageData.DesignChatPage());
        var said = new List<(string Text, (string Name, byte[] Bytes)? Picture)>();
        var refuse = true;
        rig.View.Chat.OnSendWithPicture = (text, picture) =>
        {
            said.Add((text, picture));
            return Task.FromResult(refuse ? "the node is in maintenance" : null);
        };
        rig.View.Chat.GetClipboardImage = () => ([1, 2, 3], "png");
        await rig.OpenAsync(5);
        rig.View.Chat.Input.OnKeyOverride!(TextInputKey.Paste).ShouldBeTrue();
        rig.View.Chat.Input.InsertText("like this");
        await rig.View.Chat.SendAsync();

        // Refused: the failed line holds the picture, and the box does not, so the next typed message is not given it.
        rig.View.Chat.Failures.ShouldBe(["the node is in maintenance"]);
        rig.View.Chat.PendingPicture.ShouldBeNull();

        // Retry sends the same words with the same picture, once.
        refuse = false;
        PressFailed(rig, "retry");
        await SettledAsync(() => said.Count == 2 && rig.View.Chat.Failures.Count == 0);
        said[1].Text.ShouldBe("like this");
        said[1].Picture.ShouldNotBeNull().Name.ShouldBe("pasted.png");
        rig.View.Chat.Input.InsertText("next one");
        await rig.View.Chat.SendAsync();
        said[2].Picture.ShouldBeNull();

        // Refused again, then Edit: the words go back in the box and the picture on it, for the next send.
        refuse = true;
        rig.View.Chat.Input.OnKeyOverride!(TextInputKey.Paste).ShouldBeTrue();
        rig.View.Chat.Input.InsertText("again");
        await rig.View.Chat.SendAsync();
        rig.View.Chat.Failures.Count.ShouldBe(1);
        PressFailed(rig, "edit");
        rig.View.Chat.Failures.ShouldBeEmpty();
        rig.View.Chat.Input.Text.ShouldBe("again");
        rig.View.Chat.PendingPicture.ShouldNotBeNull().Name.ShouldBe("pasted.png");
    }

    [Fact]
    public async Task PointAtThisPutsTheClickedNodesKeyInTheBoxInsteadOfPlayingTheClick()
    {
        using var rig = new Rig(PageData.DesignChatPage());
        await rig.OpenAsync(5);
        var real = rig.View.PlayerOf()!;
        real.CurrentScreen.Id.ShouldBe("home");

        rig.View.Point(true);
        rig.View.Pointing.ShouldBeTrue();
        rig.View.PlayerOf().ShouldNotBeSameAs(real);
        rig.Paint();

        // Every node that can be clicked names itself; a goto button is among them and does not go.
        var pointed = rig.Regions().Where(r => r.Result is HitResult.ButtonHit { Action: var a } && a.Contains("__pointed=", StringComparison.Ordinal)).ToList();
        pointed.ShouldNotBeEmpty();
        var target = pointed[^1];
        var key = ((HitResult.ButtonHit)target.Result!).Action[(((HitResult.ButtonHit)target.Result!).Action.IndexOf('=') + 1)..];
        rig.Click(target);

        rig.View.Chat.Input.Text.ShouldContain(key);
        rig.View.PlayerOf()!.CurrentScreen.Id.ShouldBe("home");
        real.CurrentScreen.Id.ShouldBe("home");

        // Off, the prototype is the one the version made, and a click plays.
        rig.View.Point(false);
        rig.View.PlayerOf().ShouldBeSameAs(real);
    }

    [Fact]
    public async Task AVersionInTheNextFeedReadSwitchesThePlayedVersionWithNoReload()
    {
        var third = new ChatPage
        {
            Entries = [new TranscriptEntry { Index = 5, Id = "version:3", At = PageData.Now, Kind = TranscriptKind.Divider, Text = "published v3: message" }],
            Total = 6,
            Status = "published v3",
        };
        var served = false;
        using var rig = new Rig(PageData.DesignChatPage(), after => after >= 4 && !served ? third : new ChatPage { Entries = [], Total = 6 });
        await rig.OpenAsync(5);
        // The first read only learned what was there: the current version is shown as it was.
        rig.Versions.ShouldBeEmpty();

        rig.View.Chat.Refresh();
        for (var i = 0; i < 500 && rig.Versions.Count == 0; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        served = true;
        rig.Versions.ShouldBe([("DSN-1", 3)]);
    }

    [Fact]
    public async Task AClickOnAVersionLineAsksForThatVersion()
    {
        using var rig = new Rig(PageData.DesignChatPage());
        await rig.OpenAsync(5);

        var line = rig.Regions().Single(r => r.Result is HitResult.ButtonHit { Action: "chat:divider:version:1" });
        rig.Click(line);

        rig.Versions.ShouldBe([("DSN-1", 1)]);
    }

    /// <summary>
    /// The design page with a long chat at a short narrow window (the chat under the two others, its own scroll the only one) and at a wide one (the chat
    /// a column beside the prototype): no wireframe finding, and each written as a PNG under <c>design-chat/</c> beside the test's output, to look at.
    /// </summary>
    [Theory]
    [InlineData(1280, 1.5f)]
    [InlineData(1920, 1f)]
    public async Task TheDesignPageWithALongChatIsCleanAndCapturedAtANarrowShortAndAWideWindow(int width, float dpi)
    {
        var key = WireframeSweepTests.Key("design page", "long", width, dpi, 900);
        (await WireframeSweepTests.FindingsOf("design page", "long", width, dpi, 900)).ShouldBeEmpty();
        File.Exists(await PageTextOverlapTests.CaptureOf("design page", "long", width, dpi, 900, key, Path.Combine(AppContext.BaseDirectory, "design-chat"))).ShouldBeTrue();
    }
}
