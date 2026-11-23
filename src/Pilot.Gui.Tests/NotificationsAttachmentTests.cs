using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A card's screenshot on the Notifications page (PIL1287): Needs you and History paint the thumbnail through the window's one gallery, and a
/// click on it opens the picture over the whole window, as the cards panel does. Captures go to PILOT_JOB_ARTIFACTS.
/// </summary>
public sealed class NotificationsAttachmentTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;
    private const string ImageId = "a1b2c3d4e5f6";

    private static CardRow Card() => new()
    {
        Id = "c1", Bin = CardBin.Ring, Kind = CardKinds.Overlap, Subject = "flight/TIA1611",
        Title = "Text overlaps text on the Flights page at 420 px wide: the headline runs over the first table row of the planets view",
        Summary = "The live sweep found two texts drawn over each other. Open the screenshot to see where.",
        Detail = "TIA1611 and TIA1620 both touch src/TianWen.UI/PlanetsView.cs; the sweep found the headline drawn over the first row at 420 px wide.",
        Attachment = ImageId, AttachmentExt = "png", At = T0.AddMilliseconds(500), State = CardState.Open,
        Choices = [new CardChoice { Label = "Dismiss", Verb = Verbs.Dismiss, Target = "card/c1" }],
    };

    private static AttachmentPicture Picture()
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

    private sealed class Window : IDisposable
    {
        private readonly int _width, _height;
        private readonly PageData _data = PageData.For("sample");
        private readonly AttachmentPicture _picture = Picture();
        public RgbaImageRenderer Renderer { get; }
        public PilotChrome<RgbaImage> Chrome { get; }
        public List<RectF32> Painted { get; } = [];

        public Window(int width, int height)
        {
            _width = width;
            _height = height;
            Renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(Renderer, new SessionsView<RgbaImage>(Renderer)) { FontPath = SessionsCapture.FontPath };
            var blit = AttachmentPictures.PaintOn(Renderer.Surface);
            Chrome.Gallery.Lookup = _ => _picture;
            Chrome.Gallery.Failed = _ => false;
            Chrome.Gallery.Draw = (p, rect) =>
            {
                Painted.Add(rect);
                blit(p, rect);
            };
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Jobs);
        }

        public void Frame(DateTimeOffset at, CardsScreen cards) =>
            Chrome.Render(_data.Sessions with { AsOf = at }, _data.Flights, cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, _width, _height), _data.Machines);

        public void Save(string name)
        {
            var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(Renderer.Surface.Pixels, _width, _height));
        }

        public ClickableRegion Thumb()
        {
            var regions = new List<ClickableRegion>();
            Chrome.PageWidget.CollectPaintedRegions(regions);
            return regions.Single(r => r.Result is HitResult.ButtonHit { Action: "attachment:card:c1:" + ImageId });
        }

        public void Dispose() => Renderer.Dispose();
    }

    [Theory]
    [InlineData(false, 1920, 1080)]
    [InlineData(false, 700, 900)]
    [InlineData(true, 1920, 1080)]
    [InlineData(true, 700, 900)]
    public void TheThumbnailIsPaintedOnThePageAndAClickOpensItOverTheWholeWindow(bool history, int width, int height)
    {
        using var w = new Window(width, height);
        var screen = history ? CardsScreen.Empty : CardsScreen.From([Card()]);
        w.Frame(T0, CardsScreen.From([Card()]));
        if (history)
        {
            w.Frame(T0.AddSeconds(1), CardsScreen.Empty);
        }
        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(T0.AddSeconds(2), screen);
        w.Chrome.Page.ShouldBe(PilotPage.Notifications);
        (history ? w.Chrome.Notifications.History.Count : w.Chrome.Notifications.Feed.NeedsYou.Count).ShouldBe(1);
        var name = $"notifications-attachment-{(history ? "history" : "needs-you")}-{width}x{height}";
        w.Save(name);

        var thumb = w.Painted.ShouldHaveSingleItem();
        thumb.Width.ShouldBeLessThanOrEqualTo(AttachmentGallery.ThumbWidth + 0.01f);
        var well = Palette.Current.Well;
        var pixels = w.Renderer.Surface.Pixels;
        var other = 0;
        for (var y = (int)thumb.Y; y < (int)(thumb.Y + thumb.Height); y++)
        {
            for (var x = (int)thumb.X; x < (int)(thumb.X + thumb.Width); x++)
            {
                var at = (y * width + x) * 4;
                if (pixels[at] != well.Red || pixels[at + 1] != well.Green || pixels[at + 2] != well.Blue)
                {
                    other++;
                }
            }
        }
        other.ShouldBeGreaterThan(0, "the picture is painted, not the plain well");

        w.Chrome.Faults().Faults.ShouldBeEmpty("the page's text does not overlap");

        w.Thumb().OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        w.Chrome.Gallery.Viewing.ShouldNotBeNull().Id.ShouldBe(ImageId);
        w.Painted.Clear();
        w.Frame(T0.AddSeconds(3), screen);
        var full = w.Painted.Last();
        full.Width.ShouldBeGreaterThan(200f, "scaled up to fill the window");
        full.Width.ShouldBeGreaterThan(CardsView<RgbaImage>.Width, "wider than the panel");
        (full.X + full.Width).ShouldBeLessThanOrEqualTo(width + 0.01f);
        w.Save(name + "-open");

        w.Chrome.HandleInput(new InputEvent.KeyDown(InputKey.Escape, InputModifier.None)).ShouldBeTrue();
        w.Chrome.Gallery.Viewing.ShouldBeNull();
    }
}
