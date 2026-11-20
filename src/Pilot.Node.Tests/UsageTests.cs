using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Usage: read from the status line's file, never from a login's tokens, only the newest figures kept, and whose they are.</summary>
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

    public void Dispose() => TestDirs.DeleteTree(_folder);

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
        usage.Source.ShouldBe("status line");
        usage.Line(Now.AddMinutes(2), TimeZoneInfo.Utc).ShouldBe("5h 42% (resets 14:00) · 7d 64% (resets Sat 10:00), from the status line");
        usage.Line(Now.AddMinutes(20), TimeZoneInfo.Utc).ShouldEndWith(", as of 20 min ago, from the status line");
        (usage with { Source = null }).Line(Now.AddMinutes(2), TimeZoneInfo.Utc).ShouldBe("5h 42% (resets 14:00) · 7d 64% (resets Sat 10:00)");
    }

    [Fact]
    public void AServerReadWithALowerShareOnTheSameResetReplacesTheHeldFigureAsAUsedResetDoes()
    {
        var reader = new UsageReader(_state, File, _time);
        Write(91, Reset);
        reader.Read();
        _state.Usage()!.FiveHour!.UsedPercent.ShouldBe(91);

        _time.Advance(TimeSpan.FromMinutes(1));
        reader.Read(new UsageRow { FiveHour = new UsageWindow { UsedPercent = 15, ResetsAt = Reset }, ReadAt = _time.GetUtcNow(), Source = "server" });

        var usage = _state.Usage().ShouldNotBeNull();
        usage.FiveHour!.UsedPercent.ShouldBe(15);
        usage.Source.ShouldBe("server");
        usage.Line(_time.GetUtcNow(), TimeZoneInfo.Utc).ShouldEndWith(", from the server");
    }

    [Fact]
    public void AFileWrittenBeforeTheLastServerReadNeverOverridesItAndALaterOneIsMerged()
    {
        var reader = new UsageReader(_state, File, _time);
        _time.Advance(TimeSpan.FromMinutes(5));
        var serverAt = _time.GetUtcNow();
        reader.Read(new UsageRow { FiveHour = new UsageWindow { UsedPercent = 15, ResetsAt = Reset }, ReadAt = serverAt, Source = "server" });

        // A status line that last redrew before the reset still says 91%: older news.
        Write(91, Reset, writtenAt: Now.ToUnixTimeSeconds());
        reader.Read();
        _state.Usage()!.FiveHour!.UsedPercent.ShouldBe(15);

        // Written after the server read, and grown: merged as ever.
        Write(20, Reset, writtenAt: serverAt.AddMinutes(1).ToUnixTimeSeconds());
        reader.Read();
        _state.Usage()!.FiveHour!.UsedPercent.ShouldBe(20);
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

    private string Account(string json)
    {
        var path = Path.Combine(_folder, ".claude.json");
        System.IO.File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void TheAccountIsClaudeCodesLoginOnRecordAndNothingElse()
    {
        var path = Account("""{"oauthAccount":{"emailAddress":"ada@example.org","displayName":"Ada","organizationName":"Example","organizationType":"claude_team","accountUuid":"x"},"userID":"y"}""");

        var account = ClaudeAccount.Read(path).ShouldNotBeNull();

        account.ShouldBe(new AccountRow { Email = "ada@example.org", Name = "Ada", Organization = "Example", Plan = "team" });
        account.Line().ShouldBe("ada@example.org (Example, team)");
        new AccountRow { Email = "bo@example.org" }.Line().ShouldBe("bo@example.org");
        ClaudeAccount.Read(Account("""{"userID":"y"}""")).ShouldBeNull();
        ClaudeAccount.Read(Path.Combine(_folder, "none.json")).ShouldBeNull();
    }

    [Fact]
    public void TheAccountFileIsInsideAConfiguredProfileElseBesideTheDefaultOne()
    {
        var profile = Path.Combine(_folder, ".claude");
        Directory.CreateDirectory(profile);

        ClaudeAccount.FileFor(profile).ShouldBe(Path.Combine(_folder, ".claude.json"));
        System.IO.File.WriteAllText(Path.Combine(profile, ".claude.json"), "{}");
        ClaudeAccount.FileFor(profile).ShouldBe(Path.Combine(profile, ".claude.json"));
    }

    [Fact]
    public void TheAccountIsPutWithItsFiguresOrAloneWhenNoStatusLineWritesThem()
    {
        var reader = new UsageReader(_state, File, _time) { AccountFile = Account("""{"oauthAccount":{"emailAddress":"ada@example.org"}}""") };

        reader.Read();
        _state.Usage().ShouldNotBeNull().Account!.Email.ShouldBe("ada@example.org");
        _state.Usage()!.HasFigures.ShouldBeFalse();

        Write(42, Reset);
        reader.Read();
        _state.Usage()!.HasFigures.ShouldBeTrue();
        _state.Usage()!.Account!.Email.ShouldBe("ada@example.org");

        // Logged in as someone else: their figures start over rather than merge with the last account's.
        Account("""{"oauthAccount":{"emailAddress":"bo@example.org"}}""");
        Write(5, Reset);
        reader.Read();
        _state.Usage()!.Account!.Email.ShouldBe("bo@example.org");
        _state.Usage()!.FiveHour!.UsedPercent.ShouldBe(5);
    }

    [Fact]
    public void AfterALoginChangeTheLastAccountsFiguresAreNotPutAsTheNewOnesUntilTheStatusLineWritesAgain()
    {
        // The owner switched accounts and the node said 87% of the week for one that had used 6% (DE1, 2026-10-30): the file the
        // status line writes names no account, so it still held the old account's figures.
        var reader = new UsageReader(_state, File, _time) { AccountFile = Account("""{"oauthAccount":{"emailAddress":"ada@example.org"}}""") };
        Write(80, Reset, seven: 87);
        reader.Read();
        _state.Usage()!.SevenDay!.UsedPercent.ShouldBe(87);

        _time.Advance(TimeSpan.FromMinutes(30));
        Account("""{"oauthAccount":{"emailAddress":"bo@example.org"}}""");
        reader.Read();
        // The new login, with no figures yet: not ada's 87% under bo's name.
        _state.Usage()!.Account!.Email.ShouldBe("bo@example.org");
        _state.Usage()!.HasFigures.ShouldBeFalse();

        // The status line writes bo's: those are put.
        Write(34, Reset, seven: 6, writtenAt: Now.AddMinutes(31).ToUnixTimeSeconds());
        reader.Read();
        _state.Usage()!.SevenDay!.UsedPercent.ShouldBe(6);
        _state.Usage()!.Account!.Email.ShouldBe("bo@example.org");
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
        (card.Bin, card.Kind).ShouldBe((CardBin.Handled, UsageReader.HighKind));
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

    [Fact]
    public void ALoginChangeOnANodeThatNeverReadAnAccountKeepsTheTimeAndShowsNoUsage()
    {
        _state.PutLoginChanged(Now);

        var usage = _state.Usage().ShouldNotBeNull();
        usage.LoginChangedAt.ShouldBe(Now);
        usage.IsEmpty.ShouldBeTrue();
        usage.Line(Now, TimeZoneInfo.Utc).ShouldBe("");
        (usage with { Account = new AccountRow { Email = "a@example.org" } }).IsEmpty.ShouldBeFalse();
        Row(42, Reset, Now).IsEmpty.ShouldBeFalse();
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

    [Fact]
    public void TheFiveHourCardIsClosedWithTheNewFigureWhenAFreshReadIsUnderTheLimitAndRestatedWhenItMoves()
    {
        var reader = new UsageReader(_state, File, _time);
        reader.Read(Row(92, Reset, Now) with { Source = "server" });
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == UsageReader.HighKind);
        card.Detail.ShouldContain($"figures read {Now.ToLocalTime():HH:mm} from the server");

        _time.Advance(TimeSpan.FromMinutes(1));
        reader.Read(Row(95, Reset, _time.GetUtcNow()) with { Source = "server" });
        var restated = _state.Cards(openOnly: true).Single(c => c.Kind == UsageReader.HighKind);
        restated.Id.ShouldNotBe(card.Id);
        restated.Title.ShouldStartWith("5-hour usage is at 95%");

        _time.Advance(TimeSpan.FromMinutes(1));
        var at = _time.GetUtcNow();
        reader.Read(Row(40, Reset, at) with { Source = "server" });
        _state.Cards(openOnly: true).Where(c => c.Kind == UsageReader.HighKind).ShouldBeEmpty();
        _state.Cards().Single(c => c.Id == restated.Id).Outcome.ShouldBe($"the 5-hour window now reads 40 %, figures read {at.ToLocalTime():HH:mm} from the server");
    }

    [Fact]
    public void AFiveHourCardRungFromAnOldFileReadSaysAsOfItsAge()
    {
        var reader = new UsageReader(_state, File, _time);
        reader.Read(Row(95, Reset, Now.AddMinutes(-50)) with { Source = "status line" });
        _state.Cards(openOnly: true).Single(c => c.Kind == UsageReader.HighKind).Detail.ShouldContain("as of 50 min ago");
    }
}
