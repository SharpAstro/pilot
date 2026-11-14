using DIR.Lib;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Standard palette built from a desktop accent: accent roles follow it or fall back to the blue, state colours stay, Night is untouched.</summary>
public sealed class AccentPaletteTests
{
    private static readonly RGBAColor32 Navy = new(0x1f, 0x2a, 0x6b, 0xff);
    private static readonly RGBAColor32 PaleYellow = new(0xff, 0xf2, 0x9a, 0xff);
    private static readonly RGBAColor32 Red = new(0xe8, 0x1e, 0x2a, 0xff);
    private static readonly RGBAColor32 Amber = new(0xf2, 0xa6, 0x1c, 0xff);
    private static readonly RGBAColor32 Grey = new(0x80, 0x80, 0x80, 0xff);
    private static readonly RGBAColor32 Teal = new(0x4f, 0xd1, 0xc5, 0xff);

    public static TheoryData<string> Names => new() { "navy", "yellow", "red", "amber", "grey" };

    private static RGBAColor32 Accent(string name) => name switch
    {
        "navy" => Navy,
        "yellow" => PaleYellow,
        "red" => Red,
        "amber" => Amber,
        _ => Grey,
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryAccentRoleReadsOnItsSurfaceOrIsTheBuiltInBlue(string name)
    {
        var b = Palette.BuiltIn;
        var p = Palette.StandardFor(Accent(name));
        foreach (var s in new[] { p.Ground, p.PanelBg, p.MenuBg, p.HeaderBg })
        {
            (p.RailAccent == b.RailAccent || Contrast.Ratio(p.RailAccent, s) >= 3.0).ShouldBeTrue($"RailAccent on {s}");
            (p.Link == b.Link || Contrast.Ratio(p.Link, s) >= 4.5).ShouldBeTrue($"Link on {s}");
            (p.LinkSoft == b.LinkSoft || Contrast.Ratio(p.LinkSoft, s) >= 4.5).ShouldBeTrue($"LinkSoft on {s}");
        }
        (p.RowFocusBg == b.RowFocusBg || Contrast.Ratio(p.RowFocusBg, p.Bright) >= 4.5).ShouldBeTrue("Bright on RowFocusBg");
        (p.SelectionBg == b.SelectionBg || Contrast.Ratio(p.SelectionBg, p.SelectionText) >= 4.5).ShouldBeTrue("SelectionText on SelectionBg");
        foreach (var (fill, words) in new[] { (p.LitBg, p.Strong), (p.PillLit, p.Strong), (p.Chosen, p.Strong), (p.Button, p.Bright) })
        {
            Contrast.Ratio(fill, words).ShouldBeGreaterThanOrEqualTo(4.0);
        }
    }

    [Fact]
    public void NavyDoesNotReachItsRolesSoTheBlueStays()
    {
        Palette.StandardFor(Navy).Link.ShouldBe(Palette.BuiltIn.Link);
        Palette.StandardFor(Navy).RailAccent.ShouldBe(Palette.BuiltIn.RailAccent);
    }

    [Fact]
    public void AReadableAccentIsTaken()
    {
        var p = Palette.StandardFor(PaleYellow);
        p.Link.ShouldBe(PaleYellow);
        p.RailAccent.ShouldBe(PaleYellow);
        Palette.StandardFor(Teal).Link.ShouldBe(Teal);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("amber")]
    public void ARedOrAmberAccentDoesNotColourASelectedRowLikeFailedOrHeld(string name)
    {
        var p = Palette.StandardFor(Accent(name));
        Contrast.HueNear(p.SelectionBg, p.Failed, 30).ShouldBeFalse();
        Contrast.HueNear(p.SelectionBg, p.Held, 30).ShouldBeFalse();
        Contrast.HueNear(p.RowFocusBg, p.Failed, 30).ShouldBeFalse();
        Contrast.HueNear(p.RowFocusBg, p.Held, 30).ShouldBeFalse();
        p.SelectionBg.ShouldBe(Palette.BuiltIn.SelectionBg);
        p.RowFocusBg.ShouldBe(Palette.BuiltIn.RowFocusBg);
    }

    [Fact]
    public void ATealAccentTintsTheFocusedRowAndSelection()
    {
        var p = Palette.StandardFor(Teal);
        p.RowFocusBg.ShouldNotBe(Palette.BuiltIn.RowFocusBg);
        p.SelectionBg.ShouldNotBe(Palette.BuiltIn.SelectionBg);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StateColoursStayLiteral(string name)
    {
        var b = Palette.BuiltIn;
        var p = Palette.StandardFor(Accent(name));
        (p.Landed, p.Flying, p.Held, p.Failed, p.Ok, p.Running, p.FailedBg, p.FailedText)
            .ShouldBe((b.Landed, b.Flying, b.Held, b.Failed, b.Ok, b.Running, b.FailedBg, b.FailedText));
    }

    [Fact]
    public void NoAccentIsExactlyTodaysStandard()
    {
        Palette.StandardFor(null).ShouldBe(Palette.BuiltIn);
        Palette.Standard.ShouldBe(Palette.BuiltIn);
    }

    [Fact]
    public void NightIsTheSameRecordWhateverTheAccent()
    {
        var night = Palette.Night;
        Palette.StandardFor(PaleYellow);
        Palette.Night.ShouldBeSameAs(night);
        night.Link.ShouldBe(new RGBAColor32(0xc8, 0x00, 0x00, 0xff));
        night.SelectionBg.ShouldBe(new RGBAColor32(0xf0, 0x00, 0x00, 0xff));
    }

    [Fact]
    public void TheSharedContrastHelperMatchesKnownRatios()
    {
        Contrast.Ratio(new RGBAColor32(0, 0, 0, 0xff), new RGBAColor32(0xff, 0xff, 0xff, 0xff)).ShouldBe(21.0, 0.01);
    }
}
