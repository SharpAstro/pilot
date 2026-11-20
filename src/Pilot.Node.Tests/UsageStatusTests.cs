using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What <c>pilot status</c> says of the account's limits (PIL1193): each window, when it was read and from where, and the hold.</summary>
public sealed class UsageStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private static SnapshotEvent Snapshot(UsageRow usage) => new()
    {
        Seq = 1,
        At = Now,
        Stream = "s",
        Sessions = [],
        Notes = [],
        Usage = usage,
    };

    private static UsageRow Row(HoldRow? hold = null) => new()
    {
        FiveHour = new UsageWindow { UsedPercent = 42, ResetsAt = Now.AddHours(3) },
        SevenDay = new UsageWindow { UsedPercent = 64.4, ResetsAt = Now.AddDays(2) },
        ReadAt = Now.AddMinutes(-3),
        Source = "server",
        Hold = hold,
    };

    private static readonly HoldRow Limit = new() { Kind = TurnOutage.LimitHoldKind, Until = Now.AddHours(3), Why = "session limit reached" };

    [Fact]
    public void TheTextPrintsEachWindowWithPercentResetAndWhenItWasReadFromWhere()
    {
        var text = PilotCommand.Status(Snapshot(Row()), now: Now, zone: TimeZoneInfo.Utc);

        text.ShouldContain("5-hour  42%, resets 13:00 (read 09:57, 3 min ago, from the server)");
        text.ShouldContain("7-day  64%, resets Sun 10:00 (read 09:57, 3 min ago, from the server)");
        text.ShouldContain("hold  no hold");
    }

    [Fact]
    public void TheTextSaysWhenAndWhyTheAccountIsHeld()
    {
        var text = PilotCommand.Status(Snapshot(Row(Limit)), now: Now, zone: TimeZoneInfo.Utc);

        text.ShouldContain("hold  held until 13:00: session limit reached");
    }

    [Fact]
    public void AHoldPastItsEndIsNoHold()
    {
        var text = PilotCommand.Status(Snapshot(Row(Limit)), now: Now.AddHours(4), zone: TimeZoneInfo.Utc);

        text.ShouldContain("hold  no hold");
    }

    [Fact]
    public void TheMarkdownPrintsTheSameLines()
    {
        var markdown = StatusMarkdown.Build(Snapshot(Row(Limit)), now: Now, zone: TimeZoneInfo.Utc);

        markdown.ShouldContain("**5-hour** 42%, resets 13:00 (read 09:57, 3 min ago, from the server)");
        markdown.ShouldContain("**7-day** 64%, resets Sun 10:00 (read 09:57, 3 min ago, from the server)");
        markdown.ShouldContain("**hold** held until 13:00: session limit reached");
    }

    [Fact]
    public void AFigureWithNoSourceSaysSo()
    {
        var text = PilotCommand.Status(Snapshot(Row() with { Source = null }), now: Now, zone: TimeZoneInfo.Utc);

        text.ShouldContain("from an unknown source");
    }

    [Fact]
    public void TheHoldIsKeptAcrossANewFigureReadAndEndsWithItsPut()
    {
        var held = Row(Limit);
        var read = Row() with { ReadAt = Now, FiveHour = new UsageWindow { UsedPercent = 50, ResetsAt = Now.AddHours(3) } };

        UsageRow.Newest(held, read).Hold.ShouldBe(Limit);
        UsageRow.Replace(held, read).Hold.ShouldBe(Limit);
    }
}
