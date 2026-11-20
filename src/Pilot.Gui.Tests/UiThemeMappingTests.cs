using DIR.Lib;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The one Palette-to-UiTheme mapping (PIL1232): one theme per palette, filled from pilot's own colours, with ink that reads on the accent.</summary>
public sealed class UiThemeMappingTests
{
    public static IEnumerable<TheoryDataRow<string>> Palettes() => [new("standard"), new("night")];

    private static Palette Of(string name) => name == "night" ? Palette.Night : Palette.Standard;

    [Theory]
    [MemberData(nameof(Palettes))]
    public void TheInkOnTheAccentReadsAtAaAndBodyTextOnTheGroundReads(string name)
    {
        var ui = UiThemes.For(Of(name)).Palette;
        var ink = ui.InkOn(ui.Accent);
        Contrast.Ratio(ink, ui.Accent).ShouldBeGreaterThanOrEqualTo(UiThemes.InkRatio, $"{name}: the ink on the accent fill");
        // The night palette is red on black and no red under full brightness reaches 4.5:1 on black as text, so it holds to what its own Bright does.
        Contrast.Ratio(ui.BodyText, ui.ContentBg).ShouldBeGreaterThanOrEqualTo(name == "night" ? 3.0 : UiThemes.InkRatio, $"{name}: body text on the ground");
    }

    [Fact]
    public void ThemesAreCachedPerPaletteAndTakePilotsColours()
    {
        UiThemes.For(Palette.Standard).ShouldBeSameAs(UiThemes.For(Palette.Standard));
        UiThemes.For(Palette.Night).ShouldNotBeSameAs(UiThemes.For(Palette.Standard));
        var ui = UiThemes.For(Palette.Standard).Palette;
        ui.ContentBg.ShouldBe(Palette.Standard.Ground);
        ui.Error.ShouldBe(Palette.Standard.Failed);
        UiThemes.For(Palette.Standard).Metrics.ButtonHeight.ShouldBe(AboutCard.HeaderButton);
    }
}
