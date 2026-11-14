using DIR.Lib;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The desktop accent: the ramp parsed from the AccentPalette bytes and the pick for dark and light chrome. No test reads the real registry.</summary>
public sealed class DesktopAccentTests
{
    private sealed class FakeSource(byte[]? bytes, string? reason = null, bool throws = false) : IAccentSource
    {
        public (byte[]? Bytes, string? Reason) Read() => throws ? throw new InvalidOperationException("locked hive") : (bytes, reason);
    }

    // Entry i is (10i+1, 10i+2, 10i+3, 0): Light3, Light2, Light1, Base, Dark1, Dark2, Dark3, extra.
    private static byte[] Ramp()
    {
        var b = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            b[i * 4] = (byte)(10 * i + 1);
            b[i * 4 + 1] = (byte)(10 * i + 2);
            b[i * 4 + 2] = (byte)(10 * i + 3);
        }
        return b;
    }

    [Fact]
    public void ARealRampParsesIntoEightColoursInOrder()
    {
        var accent = SystemAccent.Resolve(new FakeSource(Ramp())).Accent.ShouldNotBeNull();
        accent.Light3.ShouldBe(new RGBAColor32(1, 2, 3, 0xff));
        accent.Light2.ShouldBe(new RGBAColor32(11, 12, 13, 0xff));
        accent.Base.ShouldBe(new RGBAColor32(31, 32, 33, 0xff));
        accent.Dark1.ShouldBe(new RGBAColor32(41, 42, 43, 0xff));
        accent.Dark3.ShouldBe(new RGBAColor32(61, 62, 63, 0xff));
        accent.Extra.ShouldBe(new RGBAColor32(71, 72, 73, 0xff));
    }

    [Fact]
    public void DarkChromePicksLight2AndLightChromePicksDark1()
    {
        var accent = DesktopAccent.Parse(Ramp()).ShouldNotBeNull();
        accent.Pick(dark: true).ShouldBe(accent.Light2);
        accent.Pick(dark: false).ShouldBe(accent.Dark1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void AShortOrEmptyValueGivesNullWithAReason(int length)
    {
        DesktopAccent.Parse(new byte[length]).ShouldBeNull();
        var (accent, reason) = SystemAccent.Resolve(new FakeSource(new byte[length]));
        accent.ShouldBeNull();
        reason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ASourceWithNoValueGivesItsReason()
    {
        var (accent, reason) = SystemAccent.Resolve(new FakeSource(null, "not Windows"));
        accent.ShouldBeNull();
        reason.ShouldBe("not Windows");
    }

    [Fact]
    public void AFailingSourceGivesNullWithAReasonAndDoesNotThrow()
    {
        var (accent, reason) = SystemAccent.Resolve(new FakeSource(null, throws: true));
        accent.ShouldBeNull();
        reason.ShouldContain("locked hive");
    }
}
