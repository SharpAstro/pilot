using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The words a row is drawn in, and the command line.</summary>
public sealed class FormattingTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(999L, "999")]
    [InlineData(1_000L, "1k")]
    [InlineData(38_412L, "38.4k")]
    [InlineData(212_877L, "213k")]
    [InlineData(999_499L, "999k")]
    [InlineData(999_500L, "1M")]
    [InlineData(1_000_000L, "1M")]
    [InlineData(1_825_402L, "1.83M")]
    [InlineData(6_402_115L, "6.4M")]
    [InlineData(10_000_000L, "10M")]
    [InlineData(123_456_789L, "123M")]
    public void TokensReadInThreeDigits(long tokens, string expected) => SessionLine.Tokens(tokens).ShouldBe(expected);

    [Theory]
    [InlineData(0, "0s ago")]
    [InlineData(59, "59s ago")]
    [InlineData(60, "1m ago")]
    [InlineData(3599, "59m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(6120, "1h 42m ago")]
    [InlineData(86400, "1d ago")]
    [InlineData(190800, "2d 5h ago")]
    [InlineData(-5, "0s ago")]
    public void AgoSaysTheLargestTwoUnits(int seconds, string expected) => SessionLine.Ago(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

    [Theory]
    [InlineData("claude-opus-5-5", "xhigh", "Opus 5.5 · xhigh")]
    [InlineData("claude-sonnet-5-5", null, "Sonnet 5.5")]
    [InlineData("claude-haiku-5-5[1m]", "low", "Haiku 5.5[1m] · low")]
    [InlineData("some-other-model", "high", "some-other-model · high")]
    [InlineData(null, null, "no response yet")]
    public void ModelsReadAsTheirNames(string? model, string? effort, string expected) => SessionLine.ModelText(model, effort).ShouldBe(expected);

    [Fact]
    public void AWorkerNamesItsFlightAndLegAndAnAdHocSessionItsRepo()
    {
        var row = new SessionRow
        {
            Id = "00000000",
            SessionId = "00000000-0000-0000-0000-000000000000",
            Name = "n",
            Kind = SessionKind.Background,
            Cwd = "/x",
            Role = SessionRole.Worker,
            Flight = "TIA1611",
            Leg = "node",
            ContextTokens = 0,
            ContextWindow = 0,
            State = SessionState.Idle,
            SpendWeighted = 5_000_000,
            Budget = 4_000_000,
            StartedAt = DateTimeOffset.UnixEpoch,
        };

        SessionLine.RoleText(row).ShouldBe("worker · TIA1611 / node");
        SessionLine.RoleText(row with { Role = SessionRole.Reviewer, Leg = null }).ShouldBe("reviewer · TIA1611");
        SessionLine.RoleText(row with { Role = SessionRole.AdHoc, Repo = "tianwen" }).ShouldBe("ad hoc · tianwen");
        SessionLine.RoleText(row with { Role = SessionRole.AdHoc, Flight = null, Leg = null }).ShouldBe("ad hoc");

        // Over budget: the figure says so, the fraction goes past 1, and a zero window leaves the context bar empty.
        var line = SessionLine.From(row, DateTimeOffset.UnixEpoch);
        line.Spend.ShouldBe("5M / 4M · 125%");
        line.SpendFraction.ShouldBe(1.25f);
        line.Context.ShouldBe("0 / 0 · 0%");
        line.ContextFraction.ShouldBe(0f);
    }

    [Fact]
    public void TheCommandLineReadsEveryMode()
    {
        GuiOptions.Parse([], out var error).ShouldBe(new GuiOptions());
        error.ShouldBeNull();

        var capture = GuiOptions.Parse(["--snapshot", "s.json", "--capture", "o.png", "--size", "1920x1080"], out _).ShouldNotBeNull();
        capture.ShouldBe(new GuiOptions { Snapshot = "s.json", Capture = "o.png", Width = 1920, Height = 1080 });

        GuiOptions.Parse(["--exit-after", "5s"], out _).ShouldNotBeNull().ExitAfter.ShouldBe(TimeSpan.FromSeconds(5));
        GuiOptions.Parse(["--exit-after", "500ms"], out _).ShouldNotBeNull().ExitAfter.ShouldBe(TimeSpan.FromMilliseconds(500));
        GuiOptions.Parse(["--exit-after", "2m"], out _).ShouldNotBeNull().ExitAfter.ShouldBe(TimeSpan.FromMinutes(2));

        GuiOptions.Parse(["--size", "big"], out error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("--size");
        GuiOptions.Parse(["--capture"], out error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("needs a value");
        GuiOptions.Parse(["--frobnicate", "x"], out error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("unknown argument");
        GuiOptions.Parse(["--exit-after", "0s"], out _).ShouldBeNull();
    }
}
