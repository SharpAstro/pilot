using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A session's age on the sessions page, and the time its spend was used over (#454).</summary>
public sealed class SessionAgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static SessionRow RowStarted(DateTimeOffset startedAt, long? budget = null) =>
        PageData.Row("aaaa0000", "worker", SessionState.Working) with { StartedAt = startedAt, SpendWeighted = 1_200_000, Budget = budget };

    [Fact]
    public void ARowStartedNineHoursAgoReadsItsAgeAndItsSpendWithoutTheTime()
    {
        var line = SessionLine.From(RowStarted(Now.AddHours(-9)), Now);

        line.Age.ShouldBe("9 h");
        line.Spend.ShouldBe("1.2M");
        line.ToDumpLine().ShouldContain("spend 1.2M | age 9 h | spend/min 2.22k/min");
    }

    [Fact]
    public void ABudgetAndItsPercentAreTheSpendAndTheTimeIsTheAgesToSay()
    {
        var line = SessionLine.From(RowStarted(Now.AddHours(-9) - TimeSpan.FromMinutes(5), budget: 4_000_000), Now);

        line.Age.ShouldBe("9 h 5 m");
        line.Spend.ShouldBe("1.2M / 4M · 30%");
        line.SpendFraction.ShouldNotBeNull().ShouldBe(0.3f, 0.001f);
    }

    [Theory]
    [InlineData(30, "30 s")]
    [InlineData(180, "3 m")]
    [InlineData(3 * 3600 + 41 * 60, "3 h 41 m")]
    [InlineData(2 * 86400 + 5 * 3600, "2 d 5 h")]
    public void TheAgeIsInTheLargestTwoUnits(int seconds, string expected) =>
        SessionLine.From(RowStarted(Now.AddSeconds(-seconds)), Now).Age.ShouldBe(expected);

    [Fact]
    public void AnUnknownStartReadsAsUnknownNotAsDecades()
    {
        var line = SessionLine.From(RowStarted(DateTimeOffset.UnixEpoch), Now);

        line.Age.ShouldBe("unknown");
        line.Spend.ShouldBe("1.2M");
        line.SpendPerMinute.ShouldBe("unknown");
        line.SpendRate.ShouldBeNull();
    }

    [Theory]
    [InlineData(9 * 3600, "2.22k/min")]
    [InlineData(20 * 60, "60k/min")]
    [InlineData(30, "1.2M/min")]
    [InlineData(0, "1.2M/min")]
    public void TheRateIsTheSpendOverTheAgeInMinutesAndASessionUnderAMinuteCountsAsOne(int seconds, string expected) =>
        SessionLine.From(RowStarted(Now.AddSeconds(-seconds)), Now).SpendPerMinute.ShouldBe(expected);

    [Fact]
    public void ARowStartedInTheFutureCountsAsOneMinuteToo() =>
        SessionLine.From(RowStarted(Now.AddMinutes(5)), Now).SpendPerMinute.ShouldBe("1.2M/min");

    [Fact]
    public void TheSamplePageShowsTheAgeAndTheRateAndDropsThemWithTheOtherOptionalColumnsWhenNarrow()
    {
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        screen.Lines.ShouldAllBe(l => l.Age != "unknown" && l.SpendPerMinute.EndsWith("/min", StringComparison.Ordinal) && !l.Spend.Contains(" over "));

        using var vessel = new RgbaImageRenderer(1, 1);
        var sessions = new SessionsView<RgbaImage>(vessel);
        var wide = CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored));
        wide.ShouldContain("Age");
        wide.ShouldContain(screen.Lines[0].Age);
        wide.ShouldContain("Spend/min");
        wide.ShouldContain(screen.Lines[0].SpendPerMinute);

        var narrow = CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 100, 40), CellMeasureContext.PixelAuthored));
        narrow.ShouldNotContain("Age");
        narrow.ShouldNotContain("Spend/min");
        narrow.ShouldNotContain("Spend");
        narrow.ShouldContain("Session");
    }

    [Theory]
    [InlineData(700)]
    [InlineData(900)]
    [InlineData(1300)]
    [InlineData(1900)]
    public async Task TheSessionsPageFromTheSampleHasNoTextOverText(int width)
    {
        var faults = await PageTextOverlapTests.FaultsOf("sessions", "sample", width, 1f);

        faults.Select(f => f.Line).ShouldBeEmpty();
    }
}
