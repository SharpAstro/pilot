using DIR.Lib;
using Pilot.Buddy;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The desktop cat's click-through shape: for each mood, with a card open and in each corner, the mask covers the cat, the open bubble or card and
/// nothing else (a pixel the buddy drew is where a click lands on it; everywhere else it goes to the window below). Also where the window sits in a
/// corner of a monitor, and which monitor a drop lands on. CPU renders, no window.
/// </summary>
public sealed class BuddySilhouetteTests
{
    private const int Width = 480, Height = 720;

    private static string Font => SessionsCapture.FontPath;

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var what in new[] { "asleep", "grumpy-asleep", "one-eye", "attention", "good-news", "trouble", "peek", "settings" })
        {
            foreach (var corner in new[] { 0, 1, 2 })
            {
                data.Add(what, corner);
            }
        }
        return data;
    }

    private static (BuddyView View, Func<Pen<RgbaImage>, BuddyCard>? Card) Of(string what)
    {
        var model = new BuddyModel(TimeProvider.System);
        switch (what)
        {
            case "grumpy-asleep":
                model.Tell(new BuddyNotice("ci", NoticeKind.Trouble, "CI failed", "Tests failed."));
                model.Dismiss();
                break;
            case "one-eye":
                model.Hover(true);
                break;
            case "attention":
                model.Tell(new BuddyNotice("a", NoticeKind.Ask, "DE1 · DSN-2 designer asks you", "Publish v3 of Desktop cat buddy, or change the bubble first?"));
                break;
            case "good-news":
                model.Tell(new BuddyNotice("g", NoticeKind.GoodNews, "PR #1346 merged in pilot", "Flight 42 landed."));
                break;
            case "trouble":
                model.Tell(new BuddyNotice("t", NoticeKind.Trouble, "CI failed on PR #1351", "3 tests failed in Pilot.Tests on SE1."));
                break;
        }
        var view = BuddyScene.ViewOf(model, "Three flights flying, one waits on you.");
        return what switch
        {
            "peek" => (view, pen => PeekCard.Build(pen, new PeekView(1, 2, [new PeekRow(PeekState.Asking, "SE1", "DSN-2 designer", "asks you", null), new PeekRow(PeekState.Idle, "SE1", "flight 40", "idle", null)]))),
            "settings" => (view, pen => SettingsCard.Build(pen, new BuddySettings())),
            _ => (view, null),
        };
    }

    private static bool InRect(int x, int y, (float X, float Y, float W, float H)? r, float slack = 2) =>
        r is { } b && x >= b.X - slack && x < b.X + b.W + slack && y >= b.Y - slack && y < b.Y + b.H + slack;

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheMaskCoversTheCatAndWhatItSaysAndNothingElse(string what, int corner)
    {
        var (view, card) = Of(what);
        using var renderer = new RgbaImageRenderer(Width, Height);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), view, 1f, false, corner, card);
        var geometry = new BuddyGeometry(layout);
        var mask = BuddySilhouette.Of(renderer.Surface);

        // The cat's body and its cushion are in; so is the middle of the bubble or card; the window's far corner is out.
        BuddySilhouette.Catches(mask, (int)(layout.CatX + 100), (int)(layout.CatY + 80)).ShouldBeTrue("the cat's body");
        BuddySilhouette.Catches(mask, (int)(layout.CatX + 100), (int)(layout.CatY + GrumpCat.CushionY + 5)).ShouldBeTrue("the cushion");
        if (geometry.Bubble is { } bubble)
        {
            BuddySilhouette.Catches(mask, (int)(bubble.X + bubble.W / 2), (int)(bubble.Y + bubble.H / 2)).ShouldBeTrue("the bubble");
        }
        if (geometry.Card is { } open)
        {
            BuddySilhouette.Catches(mask, (int)(open.X + open.W / 2), (int)(open.Y + open.H / 2)).ShouldBeTrue("the card");
        }
        (what is "good-news" or "attention" or "trouble" or "one-eye").ShouldBe(geometry.Bubble is not null);
        (what is "peek" or "settings").ShouldBe(geometry.Card is not null);

        // Nothing is in the mask that is not the cat's box (with its z Z and cushion), the bubble or the card.
        var cat = (layout.CatX, layout.CatY, GrumpCat.Width, GrumpCat.CushionY + GrumpCat.CushionHeight);
        // The furious cat's anger marks are drawn beside its head, outside the box the body sits in; they are the cat, so they are in the shape.
        var catSlack = what == "trouble" ? 40f : 2f;
        var outside = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (BuddySilhouette.Catches(mask, x, y) && !InRect(x, y, cat, catSlack) && !InRect(x, y, geometry.Bubble) && !InRect(x, y, geometry.Card))
                {
                    outside++;
                }
            }
        }
        outside.ShouldBe(0, "the mask has pixels away from the cat, its bubble and its card");

        // The far corner opposite the cat is empty: a click there goes through.
        var farX = corner == 1 ? Width - 3 : 2;
        var farY = corner == 2 ? Height - 3 : 2;
        BuddySilhouette.Catches(mask, farX, farY).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheCatAndItsCardSitInTheChosenCornerAndOpenTowardsTheMiddle(int corner)
    {
        var (view, card) = Of("peek");
        using var renderer = new RgbaImageRenderer(Width, Height);
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), view, 1f, false, corner, card);

        (layout.CatX < Width / 2f).ShouldBe(corner == 1);
        (layout.CatY < Height / 2f).ShouldBe(corner == 2);
        var open = new BuddyGeometry(layout).Card!.Value;
        if (corner == 2)
        {
            open.Y.ShouldBeGreaterThan(layout.CatY + GrumpCat.CushionY, "below the cat in the top corner");
        }
        else
        {
            (open.Y + open.H).ShouldBeLessThanOrEqualTo(layout.CatY, "above the cat in a bottom corner");
        }
        open.X.ShouldBeGreaterThanOrEqualTo(0);
        (open.X + open.W).ShouldBeLessThanOrEqualTo(Width);
        open.Y.ShouldBeGreaterThanOrEqualTo(0);
        (open.Y + open.H).ShouldBeLessThanOrEqualTo(Height);
    }

    [Fact]
    public void TheWindowIsBigEnoughForTheTallestCardAndWideEnoughForTheWidest()
    {
        var (width, height) = BuddyWindowSize.Measure(Font);
        width.ShouldBeGreaterThanOrEqualTo(PeekCard.CardWidth + 2 * BuddyScene.Margin);
        height.ShouldBeGreaterThan(GrumpCat.CushionY + GrumpCat.CushionHeight + 2 * BuddyScene.Margin + 100);
        // The card the window is sized for fits above the cat at that size.
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var (view, card) = Of("peek");
        var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(false), view, 1f, false, 0, card);
        layout.CardY.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void TheWindowSitsAgainstTheChosenCornerOfTheChosenMonitor()
    {
        var left = new DisplayArea("Left", 0, 0, 1920, 1040);
        var right = new DisplayArea("Right", 1920, -200, 2560, 1400);
        BuddyPlacement.Corner(left, 420, 600, 0).ShouldBe((1500, 440));
        BuddyPlacement.Corner(left, 420, 600, 1).ShouldBe((0, 440));
        BuddyPlacement.Corner(right, 420, 600, 2).ShouldBe((4060, -200));

        BuddyPlacement.Chosen([left, right], "Right").ShouldBe(right);
        BuddyPlacement.Chosen([left, right], "Gone").ShouldBe(left, "a monitor that is gone falls back to the first");
        BuddyPlacement.Chosen([left, right], null).ShouldBe(left);
        BuddyPlacement.Chosen([], null).ShouldBeNull();
    }

    [Fact]
    public void ADropLandsOnTheMonitorUnderTheWindowCentreOrTheNearestOne()
    {
        var left = new DisplayArea("Left", 0, 0, 1920, 1040);
        var right = new DisplayArea("Right", 1920, 0, 2560, 1400);
        BuddyPlacement.Under([left, right], 1700, 400, 420, 600).ShouldBe(left);
        BuddyPlacement.Under([left, right], 2000, 400, 420, 600).ShouldBe(right);
        BuddyPlacement.Under([left, right], 6000, 400, 420, 600).ShouldBe(right, "off every monitor, the nearest");
        BuddyPlacement.Under([], 0, 0, 1, 1).ShouldBeNull();
    }
}
