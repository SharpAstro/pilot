using Pilot.Cli;
using Shouldly;
using SharpAstro.Codecs;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Node.Tests;

public sealed class ImageMagnifierTests
{
    [Fact]
    public void A_small_image_is_capped_at_three_times_its_size()
    {
        ImageMagnifier.Factor(100, 100, 800, 4000).ShouldBe(3);
    }

    [Fact]
    public void A_mid_image_fits_the_width_at_under_three_times()
    {
        ImageMagnifier.Factor(400, 100, 800, 4000).ShouldBe(2);
        ImageMagnifier.Factor(300, 100, 800, 4000).ShouldBeInRange(2.6, 2.7);
    }

    [Fact]
    public void An_image_wider_than_the_budget_is_left_to_the_renderer()
    {
        ImageMagnifier.Factor(1000, 100, 800, 4000).ShouldBe(1);
    }

    [Fact]
    public void A_tall_image_is_bound_by_the_height_budget()
    {
        // 100 wide would take 3x by width, but 200 tall into 300 rows of pixels takes 1.5x.
        ImageMagnifier.Factor(100, 200, 800, 300).ShouldBe(1.5);
        ImageMagnifier.Factor(100, 400, 800, 300).ShouldBe(1);
    }

    [Fact]
    public void A_degenerate_size_is_not_magnified()
    {
        ImageMagnifier.Factor(0, 10, 800, 400).ShouldBe(1);
        ImageMagnifier.Factor(10, 10, 0, 400).ShouldBe(1);
    }

    [Fact]
    public void A_small_png_comes_back_enlarged_and_keeps_its_pixels()
    {
        var src = Png(4, 2, (x, y) => x < 2 ? (byte)255 : (byte)0);

        var magnified = ImageMagnifier.Magnify(src, 800, 400);

        magnified.ShouldNotBeNull();
        ImageCodecs.TryReadInfo(magnified, out var info).ShouldBeTrue();
        info.Width.ShouldBe(12);
        info.Height.ShouldBe(6);
        var rgba = new byte[12 * 6 * 4];
        ImageCodecs.TryDecodeIntoRgba8(magnified, rgba).ShouldBeTrue();
        rgba[(0 * 12 + 5) * 4].ShouldBe((byte)255);
        rgba[(5 * 12 + 6) * 4].ShouldBe((byte)0);
    }

    [Fact]
    public void A_wide_image_is_returned_as_the_same_bytes()
    {
        var src = Png(1000, 4, (x, y) => 128);

        ImageMagnifier.Magnify(src, 800, 400).ShouldBeSameAs(src);
    }

    [Fact]
    public void Bytes_that_do_not_decode_are_returned_as_they_are()
    {
        var junk = new byte[] { 1, 2, 3, 4 };

        ImageMagnifier.Magnify(junk, 800, 400).ShouldBeSameAs(junk);
        ImageMagnifier.Magnify(null, 800, 400).ShouldBeNull();
    }

    private static byte[] Png(int width, int height, Func<int, int, byte> red)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                rgba[i] = red(x, y);
                rgba[i + 3] = 255;
            }
        }
        return PngWriter.Encode(rgba, width, height);
    }
}
