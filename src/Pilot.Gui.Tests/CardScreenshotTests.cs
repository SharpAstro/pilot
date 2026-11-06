using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A card that carries an image (an overlap's screenshot) shows it as a thumbnail under its text that opens full size, and says so in words when the node no longer has it.</summary>
public sealed class CardScreenshotTests
{
    private const string LongDetail = "TIA1611 (Planets on the GPU) and TIA1620 (Moon phases) both touch src/TianWen.UI/PlanetsView.cs and src/TianWen.UI/MoonView.cs; the sweep found the headline text drawn over the first row of the table at 420 px wide, so the two overlap on screen as well as in the tree.";

    private static CardRow Card(string id, bool image, string? ext = "png", string? detail = LongDetail) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = CardKinds.Overlap, Subject = "flight/TIA1611",
        Title = "Text overlaps text on the Flights page at 420 px wide: the headline runs over the first table row of the planets view",
        Summary = "The live sweep found two texts drawn over each other. Open the screenshot to see where.",
        Detail = detail,
        Attachment = image ? "a1b2c3d4e5f6" : null, AttachmentExt = image ? ext : null,
        At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Dismiss", Verb = Verbs.Dismiss, Target = "card/" + id }],
    };

    // A 200 x 100 picture, red to blue left to right: something to see in a capture, and a shape to fit.
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

    private static CardsView<RgbaImage> View(RgbaImageRenderer renderer, List<RectF32> painted, bool picture = true)
    {
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var one = Picture();
        var blit = AttachmentPictures.PaintOn(renderer.Surface);
        view.Gallery.Lookup = _ => picture ? one : null;
        view.Gallery.Failed = _ => !picture;
        view.Gallery.Draw = (p, rect) =>
        {
            painted.Add(rect);
            blit(p, rect);
        };
        return view;
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardRow card, float width)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, width, 4000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static string Thumb(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: { } k } } && k.StartsWith("thumb:", StringComparison.Ordinal)).Count().ToString();

    [Theory]
    [InlineData(420f, false)]
    [InlineData(420f, true)]
    [InlineData(1300f, false)]
    [InlineData(1300f, true)]
    public void TheCardWrapsItsTextAndNothingIsDrawnOverTextWithOrWithoutTheImage(float width, bool image)
    {
        var arranged = Arrange(Card("c1", image), width);

        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        Thumb(arranged).ShouldBe(image ? "1" : "0");
        // The title still wraps within the panel, whole: no word of it past the width.
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ShouldAllBe(a => a.Bounds.X + a.Bounds.Width <= width + 0.5f);
        if (image)
        {
            // Under the detail, not over it, and the fixed thumbnail size.
            var thumb = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: { } k } } && k.StartsWith("thumb:", StringComparison.Ordinal)).Bounds;
            (thumb.Width, thumb.Height).ShouldBe((AttachmentGallery.ThumbWidth, AttachmentGallery.ThumbHeight));
            var detailBottom = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.FontSize == 11f && a.Bounds.Y < thumb.Y).Max(a => a.Bounds.Y + a.Bounds.Height);
            thumb.Y.ShouldBeGreaterThanOrEqualTo(detailBottom - 0.5f);
        }
    }

    [Fact]
    public void ACardWithoutAnImageHasNoThumbnailAndNoSpaceForOne()
    {
        var without = Arrange(Card("c1", image: false), 420f);
        var with = Arrange(Card("c1", image: true), 420f);

        Thumb(without).ShouldBe("0");
        // The root fills the whole area it is arranged in, so the card's height is where its lowest text ends.
        float Lowest(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
            arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).Max(a => a.Bounds.Y + a.Bounds.Height);
        Lowest(with).ShouldBeGreaterThanOrEqualTo(Lowest(without) + AttachmentGallery.ThumbHeight);
    }

    [Theory]
    [InlineData(420)]
    [InlineData(1300)]
    public void TheThumbnailIsPaintedAndAClickOpensTheFullSizeViewerOverThePanel(int width)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var painted = new List<RectF32>();
        var view = View(renderer, painted);
        var screen = CardsScreen.From([Card("c1", image: true)]);
        view.Render(screen, new RectF32(0, 0, width, 900));
        painted.ShouldHaveSingleItem().Width.ShouldBeLessThanOrEqualTo(AttachmentGallery.ThumbWidth + 0.01f);

        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "attachment:card:c1:a1b2c3d4e5f6" }).OnClick!.Invoke(InputModifier.None);
        view.Gallery.Viewing.ShouldNotBeNull().Id.ShouldBe("a1b2c3d4e5f6");
        view.Gallery.InPanel.ShouldBeTrue();

        painted.Clear();
        view.Render(screen, new RectF32(0, 0, width, 900));
        // Full size now: the picture is painted larger than its thumbnail, and a click on it, or Escape, shuts it.
        painted.Max(r => r.Width).ShouldBeGreaterThan(AttachmentGallery.ThumbWidth);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        view.Gallery.Viewing.ShouldBeNull();
    }

    [Fact]
    public void AViewerOpenedFromACardIsNotDrawnAgainByThePageBesideIt()
    {
        var gallery = new AttachmentGallery();
        gallery.OpenInPanel(new AttachmentRef("a1b2c3d4e5f6", "Screenshot", "png", null));
        var page = Layout.Builder.Spacer();

        gallery.Over(page).ShouldBeSameAs(page);
        gallery.Over(page, panel: true).ShouldNotBeSameAs(page);

        gallery.Open(new AttachmentRef("a1b2c3d4e5f6", "Screenshot", "png", null));
        gallery.Over(page).ShouldNotBeSameAs(page);
        gallery.Over(page, panel: true).ShouldBeSameAs(page);
    }

    [Theory]
    [InlineData(420f)]
    [InlineData(1300f)]
    public void ACopyTheNodeNoLongerHasSaysSoInWordsAndStillOverlapsNothing(float width)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        view.Gallery.Failed = _ => true;
        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From([Card("c1", image: true)])), new Rect<float>(0, 0, width, 4000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

        ArrangedText.Concat(arranged).ShouldContain("no longer has this image");
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void CapturesOfTheCardAtNarrowAndWideWidths()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var width in new[] { 420, 1300 })
        {
            foreach (var (name, image, picture) in new[] { ("without", false, true), ("with", true, true), ("missing", true, false) })
            {
                using var renderer = new RgbaImageRenderer((uint)width, 760);
                var view = View(renderer, [], picture);
                view.Render(CardsScreen.From([Card("c1", image)]), new RectF32(0, 0, width, 760));
                File.WriteAllBytes(Path.Combine(dir, $"card-screenshot-{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 760));
            }
        }
    }
}
