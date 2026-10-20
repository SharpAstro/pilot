using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Usage: read from the status line's file, never from a login, and only the newest figures kept.</summary>
public sealed class UsageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddHours(2);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2u-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Now);
    private readonly NodeState _state;

    public UsageTests()
    {
        Directory.CreateDirectory(_folder);
        _state = new NodeState(new InMemoryJournal(), _time);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string File => Path.Combine(_folder, "rate-limits.json");

    private void Write(double five, DateTimeOffset fiveReset, double seven = 64, long? writtenAt = null) =>
        System.IO.File.WriteAllText(File,
            $$$"""{"rate_limits":{"five_hour":{"used_percentage":{{{five}}},"resets_at":{{{fiveReset.ToUnixTimeSeconds()}}}},"seven_day":{"used_percentage":{{{seven}}},"resets_at":"2026-10-24T10:00:00Z"}},"written_at":{{{writtenAt ?? Now.ToUnixTimeSeconds()}}}}""");

    private static UsageRow Row(double five, DateTimeOffset reset, DateTimeOffset readAt) =>
        new() { FiveHour = new UsageWindow { UsedPercent = five, ResetsAt = reset }, ReadAt = readAt };

    [Fact]
    public void TheStatusLinesFileIsReadWithBothWindows()
    {
        Write(42, Reset);

        var usage = UsageReader.Parse(File).ShouldNotBeNull();

        usage.FiveHour.ShouldBe(new UsageWindow { UsedPercent = 42, ResetsAt = Reset });
        usage.SevenDay.ShouldBe(new UsageWindow { UsedPercent = 64, ResetsAt = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero) });
        usage.ReadAt.ShouldBe(Now);
        usage.Line(Now.AddMinutes(2), TimeZoneInfo.Utc).ShouldBe("5h 42% (resets 14:00) · 7d 64% (resets Sat 10:00)");
        usage.Line(Now.AddMinutes(20), TimeZoneInfo.Utc).ShouldEndWith(", as of 20 min ago");
    }

    [Fact]
    public void AMissingOrHalfWrittenFileIsNothing()
    {
        UsageReader.Parse(File).ShouldBeNull();
        System.IO.File.WriteAllText(File, "{\"rate_limits\":");
        UsageReader.Parse(File).ShouldBeNull();
    }

    [Fact]
    public void AStaleWriterNeverWinsOverFresherFigures()
    {
        var held = Row(60, Reset, Now);

        // A session idle since earlier repaints with what its last response carried: same window, lower share.
        UsageRow.Newest(held, Row(41, Reset, Now.AddMinutes(1))).ShouldBe(held);
        // Or an older window altogether.
        UsageRow.Newest(held, Row(95, Reset.AddHours(-5), Now.AddMinutes(1))).ShouldBe(held);
        // The same window, grown: taken, and fresh.
        UsageRow.Newest(held, Row(63, Reset, Now.AddMinutes(1))).ShouldBe(Row(63, Reset, Now.AddMinutes(1)));
        // A new window: taken even though its share is lower.
        UsageRow.Newest(held, Row(3, Reset.AddHours(5), Now.AddMinutes(1))).ShouldBe(Row(3, Reset.AddHours(5), Now.AddMinutes(1)));
    }

    [Fact]
    public void TheNodeKeepsTheNewestAndPublishesOnlyWhatChanged()
    {
        var reader = new UsageReader(_state, File, _time);
        Write(60, Reset);
        reader.Read();
        var seq = _state.Seq;

        Write(41, Reset, writtenAt: Now.AddMinutes(1).ToUnixTimeSeconds());
        reader.Read();
        Write(60, Reset, writtenAt: Now.AddMinutes(2).ToUnixTimeSeconds());
        reader.Read();

        _state.Seq.ShouldBe(seq);
        _state.Usage().ShouldNotBeNull().FiveHour!.UsedPercent.ShouldBe(60);
        _state.Snapshot().Usage.ShouldBe(_state.Usage());
    }

    [Fact]
    public void NearlySpentTheFiveHourWindowRaisesOneCardPerWindow()
    {
        var reader = new UsageReader(_state, File, _time);
        Write(89, Reset);
        reader.Read();
        _state.Cards().ShouldBeEmpty();

        Write(91, Reset);
        reader.Read();
        Write(97, Reset);
        reader.Read();

        var card = _state.Cards().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.ForYou, UsageReader.HighKind));
        card.Title.ShouldStartWith("5-hour usage is at 91%");
    }

    [Fact]
    public void AClientsMirrorFollowsTheUsage()
    {
        var mirror = new NodeMirror();
        mirror.Apply(_state.Snapshot());
        using var _ = _state.Subscribe(null, null, new MirrorSink(mirror));

        _state.PutUsage(Row(42, Reset, Now));

        mirror.Usage.ShouldBe(Row(42, Reset, Now));
    }

    private sealed class MirrorSink(NodeMirror mirror) : INodeEventSink
    {
        public int Capacity => 64;

        public bool TryWrite(byte[] json)
        {
            mirror.Apply(System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.NodeEvent)!);
            return true;
        }

        public void Overflowed()
        {
        }
    }
}
