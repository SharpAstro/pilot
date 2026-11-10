using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Where the captions of a Settings range row (#770) go: <see cref="RangeCaptions"/> is pure, so these drive it with a fixed-width
/// measure (6 per character) and no renderer.
/// </summary>
public sealed class RangeCaptionsTests
{
    private const float Gap = 4f;
    private const float Margin = 4f;

    private static float Measure(string text) => text.Length * 6f;

    private static float Line(int columns, float cell) => columns * cell + (columns - 1) * Gap;

    private static void ShouldHoldItsHandle(RangeCaptions.Caption caption, int handle) =>
        (caption.From <= handle && handle <= caption.To).ShouldBeTrue($"'{caption.Text}' [{caption.From}..{caption.To}] does not hold column {handle}");

    private static void NoTwoOnOneLineShouldMeet(IReadOnlyList<RangeCaptions.Caption> captions)
    {
        for (var i = 0; i < captions.Count; i++)
        {
            for (var j = i + 1; j < captions.Count; j++)
            {
                var (a, b) = (captions[i], captions[j]);
                (a.Line == b.Line && a.From <= b.To && b.From <= a.To).ShouldBeFalse($"'{a.Text}' [{a.From}..{a.To}] meets '{b.Text}' [{b.From}..{b.To}] on line {a.Line}");
            }
        }
    }

    [Fact]
    public void CloseHandlesThatCannotSitSideBySideStackTheMaxOnASecondLine()
    {
        // 30 wide columns: 'default' (42 + 4) spans two, 'max' (18 + 4) one, and handles on columns 0 and 1 leave no room to shift apart.
        var captions = RangeCaptions.Place(8, 0, 1, Line(8, 30f), Gap, Margin, Measure);

        captions.Count.ShouldBe(2);
        var def = captions.Single(c => c.Text == "default");
        var max = captions.Single(c => c.Text == "max");
        def.Line.ShouldBe(0);
        max.Line.ShouldBe(1);
        ShouldHoldItsHandle(def, 0);
        ShouldHoldItsHandle(max, 1);
        NoTwoOnOneLineShouldMeet(captions);
    }

    [Fact]
    public void CloseHandlesAtTheLinesFarEndShiftTheDefaultLeftAndNeedNoSecondLine()
    {
        // Unlike the start of the line, the far end has room to the left of the default: it moves to columns 5..6 and the max keeps column 7.
        var captions = RangeCaptions.Place(8, 6, 7, Line(8, 30f), Gap, Margin, Measure);

        captions.ShouldAllBe(c => c.Line == 0);
        ShouldHoldItsHandle(captions.Single(c => c.Text == "default"), 6);
        ShouldHoldItsHandle(captions.Single(c => c.Text == "max"), 7);
        NoTwoOnOneLineShouldMeet(captions);
    }

    [Fact]
    public void CloseHandlesWithRoomStaySideBySideOnTheFirstLine()
    {
        var captions = RangeCaptions.Place(8, 0, 1, Line(8, 60f), Gap, Margin, Measure);

        captions.ShouldAllBe(c => c.Line == 0);
        captions.Single(c => c.Text == "default").To.ShouldBeLessThan(captions.Single(c => c.Text == "max").From);
    }

    [Fact]
    public void HandlesThatMeetShiftApartWhereTheLineHasRoomAndStayOnOneLine()
    {
        var captions = RangeCaptions.Place(8, 3, 4, Line(8, 30f), Gap, Margin, Measure);

        captions.ShouldAllBe(c => c.Line == 0);
        ShouldHoldItsHandle(captions.Single(c => c.Text == "default"), 3);
        ShouldHoldItsHandle(captions.Single(c => c.Text == "max"), 4);
        NoTwoOnOneLineShouldMeet(captions);
    }

    [Fact]
    public void ADefaultAboveTheMaxIsPlacedWithoutAnyTwoCaptionsMeeting()
    {
        foreach (var cell in new[] { 20f, 30f, 60f })
        {
            var captions = RangeCaptions.Place(8, 5, 2, Line(8, cell), Gap, Margin, Measure);

            captions.Count.ShouldBe(2);
            ShouldHoldItsHandle(captions.Single(c => c.Text == "default"), 5);
            ShouldHoldItsHandle(captions.Single(c => c.Text == "max"), 2);
            NoTwoOnOneLineShouldMeet(captions);
        }
    }

    [Fact]
    public void OneStopGivesOneDefaultEqualsMaxCaption()
    {
        var captions = RangeCaptions.Place(8, 4, 4, Line(8, 30f), Gap, Margin, Measure);

        var one = captions.ShouldHaveSingleItem();
        one.Text.ShouldBe("default = max");
        ShouldHoldItsHandle(one, 4);
    }
}
