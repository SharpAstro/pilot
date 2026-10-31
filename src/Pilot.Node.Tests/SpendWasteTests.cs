using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Spend that bought nothing kept: later rounds, base-merge re-reviews, sessions with no result, replanned plans, failed starts again.</summary>
public sealed class SpendWasteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // Every message is output tokens only, so a session's weighted tokens are 5 per token and the order is plain.
    private static void Spend(SpendLedger ledger, string session, string? name, string? flight, string? leg, string role, int round, DateTimeOffset at, long output, string model = "claude-sonnet-5-5") =>
        ledger.Record(session, name, new SessionRoleInfo(SessionRole.Worker, flight, leg, null), role, round,
            [new SpendMessage(session + "-m", new MessageUsage(0, 0, 0, output), at, model, "high")], at);

    private static SpendLedger Seeded()
    {
        var ledger = new SpendLedger();
        // round: PIL1/a went round a second time on the review's findings
        Spend(ledger, "s-round", "du1-pil1-a", "PIL1", "a", "worker", 2, Noon.AddHours(-6), 500);
        ledger.Mark(new WasteMark(WasteKinds.Round, "PIL1", "a", null, null, 2, WasteReasons.Findings, Noon.AddHours(-6)));
        // base-merge: PIL2's reviewer started just after the node noted a head changed only by a base merge
        Spend(ledger, "s-base", "du1-pil2-review", "PIL2", null, "reviewer", 0, Noon.AddHours(-3), 400);
        ledger.Mark(new WasteMark(WasteKinds.BaseMergeReview, "PIL2", null, null, null, 0, "head changed only by a base merge", Noon.AddHours(-3).AddSeconds(-5)));
        // no-result: PIL3's first worker ended without finishing
        Spend(ledger, "s-none", "du1-pil3-a", "PIL3", "a", "worker", 0, Noon.AddHours(-4), 300);
        ledger.Mark(new WasteMark(WasteKinds.NoResult, "PIL3", null, null, "s-none", 0, "worker ended without finishing its leg", Noon.AddHours(-4)));
        // replanned: PIL4's first planner, before the replan; the second planner is kept
        Spend(ledger, "s-plan1", "du1-pil4-plan", "PIL4", null, "planner", 0, Noon.AddHours(-5), 200);
        ledger.Mark(new WasteMark(WasteKinds.Replanned, "PIL4", null, null, null, 0, "replanned", Noon.AddHours(-4).AddMinutes(-30)));
        Spend(ledger, "s-plan2", "du1-pil4-plan", "PIL4", null, "planner", 0, Noon.AddHours(-4), 1_000);
        // failed start: the second failure's session is waste, the first is not
        Spend(ledger, "s-fail1", "du1-pil5-a", "PIL5", "a", "worker", 0, Noon.AddHours(-2).AddMinutes(-1), 1_000);
        Spend(ledger, "s-fail2", "du1-pil5-a", "PIL5", "a", "worker", 0, Noon.AddHours(-1).AddMinutes(-1), 100);
        ledger.Mark(new WasteMark(WasteKinds.FailedStart, "PIL5", null, "du1-pil5-a", null, 0, "boom", Noon.AddHours(-2)));
        ledger.Mark(new WasteMark(WasteKinds.FailedStart, "PIL5", null, "du1-pil5-a", null, 0, "boom", Noon.AddHours(-1)));
        // not waste: a first round that finished, a review of a real change
        Spend(ledger, "s-ok", "du1-pil6-a", "PIL6", "a", "worker", 0, Noon.AddHours(-2), 2_000);
        Spend(ledger, "s-ok-review", "du1-pil6-review", "PIL6", null, "reviewer", 0, Noon.AddHours(-1), 700);
        return ledger;
    }

    private static SpendWasteReport Waste(SpendLedger ledger) =>
        ledger.Waste("today", Noon.AddHours(1), TimeZoneInfo.Utc, null).ShouldNotBeNull();

    [Fact]
    public void EachKindIsClassifiedWithItsReasonTokensAndShareWorstFirst()
    {
        var report = Waste(Seeded());

        report.Rows.Select(r => (r.Kind, r.Reason, r.Flight, r.Role, r.Tokens)).ShouldBe(
        [
            (WasteKinds.Round, "findings", "PIL1", "worker", 500),
            (WasteKinds.BaseMergeReview, "full re-review of a head changed only by a base merge", "PIL2", "reviewer", 400),
            (WasteKinds.NoResult, "worker ended without finishing its leg", "PIL3", "worker", 300),
            (WasteKinds.Replanned, "its plan was replanned", "PIL4", "planner", 200),
            (WasteKinds.FailedStart, "start failed again (2)", "PIL5", "worker", 100),
        ]);
        report.Rows.Select(r => r.Weighted).ShouldBe([2_500L, 2_000, 1_500, 1_000, 500]);
        report.Rows[0].Model.ShouldBe("claude-sonnet-5-5");
        report.Rows[0].Leg.ShouldBe("a");

        // The window held 500+400+300+200+1000+1000+100+2000+700 = 6200 tokens, 31000 weighted.
        report.WindowWeighted.ShouldBe(31_000);
        report.WasteWeighted.ShouldBe(7_500);
        report.Rows.Select(r => r.Share).ShouldBe([8.1, 6.5, 4.8, 3.2, 1.6]);
        report.WasteShare.ShouldBe(24.2);
        report.WasteTokens.ShouldBe(1_500);
    }

    [Fact]
    public void SpendThatIsNotWasteIsNotListed()
    {
        var ledger = new SpendLedger();
        Spend(ledger, "w", "du1-pil6-a", "PIL6", "a", "worker", 0, Noon.AddHours(-2), 2_000);
        Spend(ledger, "r", "du1-pil6-review", "PIL6", null, "reviewer", 0, Noon.AddHours(-1), 700);
        Spend(ledger, "p", "du1-pil6-plan", "PIL6", null, "planner", 0, Noon.AddHours(-3), 100);
        // A single failed start is a failure, not a repeat; a base-merge mark of another flight does not touch this reviewer.
        ledger.Mark(new WasteMark(WasteKinds.FailedStart, "PIL6", null, "du1-pil6-a", null, 0, "boom", Noon.AddHours(-2)));
        ledger.Mark(new WasteMark(WasteKinds.BaseMergeReview, "PIL9", null, null, null, 0, "x", Noon.AddHours(-1)));

        var report = Waste(ledger);

        report.Rows.ShouldBeEmpty();
        (report.WasteWeighted, report.WasteShare).ShouldBe((0, 0));
        report.WindowWeighted.ShouldBe(14_000);
    }

    [Fact]
    public void AFailureSoonAfterAnotherCountsOnlyTheSessionBegunSinceTheOneBefore()
    {
        var ledger = new SpendLedger();
        // Two failures ten minutes apart: the first attempt's session began inside the second's half hour but before the first failure.
        Spend(ledger, "try1", "du1-pil5-a", "PIL5", "a", "worker", 0, Noon.AddMinutes(-12), 1_000);
        Spend(ledger, "try2", "du1-pil5-a", "PIL5", "a", "worker", 0, Noon.AddMinutes(-1), 100);
        ledger.Mark(new WasteMark(WasteKinds.FailedStart, "PIL5", null, "du1-pil5-a", null, 0, "boom", Noon.AddMinutes(-10)));
        ledger.Mark(new WasteMark(WasteKinds.FailedStart, "PIL5", null, "du1-pil5-a", null, 0, "boom", Noon));

        Waste(ledger).Rows.Select(r => (r.Kind, r.Tokens)).ShouldBe([(WasteKinds.FailedStart, 100L)]);
    }

    [Theory]
    [InlineData("the review asked for changes: fix the null check", WasteReasons.Findings)]
    [InlineData("the review asked for changes: nits only, two renames", WasteReasons.Nits)]
    [InlineData("the PR conflicts with its base (main moved): merge it", WasteReasons.Conflicts)]
    [InlineData("the PR's checks failed on abc12345: read them", WasteReasons.Checks)]
    [InlineData("the review asked for changes: this conflicts with the existing name", WasteReasons.Findings)]
    [InlineData("the review asked for changes: a merge conflict marker is left in X", WasteReasons.Findings)]
    [InlineData("the review asked for changes: the log says checks failed, quoted", WasteReasons.Findings)]
    [InlineData("the last worker gave up: no reason given", WasteReasons.Other)]
    public void AGoAroundsReasonIsReadFromItsWhy(string why, string reason) => WasteReasons.OfGoAround(why).ShouldBe(reason);

    [Fact]
    public void RoundsAreSplitByReasonAndTheFirstRoundIsNotWaste()
    {
        var ledger = new SpendLedger();
        Spend(ledger, "first", "du1-pil1-a", "PIL1", "a", "worker", 0, Noon.AddHours(-5), 900);
        Spend(ledger, "second", "du1-pil1-a", "PIL1", "a", "worker", 1, Noon.AddHours(-4), 100);
        Spend(ledger, "third", "du1-pil1-a", "PIL1", "a", "worker", 2, Noon.AddHours(-3), 200);
        Spend(ledger, "fourth", "du1-pil1-a", "PIL1", "a", "worker", 3, Noon.AddHours(-2), 50);
        ledger.Mark(new WasteMark(WasteKinds.Round, "PIL1", "a", null, null, 1, WasteReasons.Conflicts, Noon.AddHours(-4)));
        ledger.Mark(new WasteMark(WasteKinds.Round, "PIL1", "a", null, null, 2, WasteReasons.Checks, Noon.AddHours(-3)));

        Waste(ledger).Rows.Select(r => (r.Reason, r.Tokens)).ShouldBe([("checks", 200L), ("conflicts", 100), ("other", 50)]);
    }

    [Fact]
    public void OnlyWhatWasSpentInTheWindowCountsAndMarksSurviveARestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-waste-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = SpendLedger.FileIn(folder);
            var ledger = new SpendLedger(file);
            Spend(ledger, "old", "du1-pil1-a", "PIL1", "a", "worker", 1, Noon.AddDays(-2), 500);
            Spend(ledger, "now", "du1-pil1-b", "PIL1", "b", "worker", 1, Noon.AddHours(-1), 100);
            ledger.Mark(new WasteMark(WasteKinds.Round, "PIL1", "a", null, null, 1, WasteReasons.Findings, Noon.AddDays(-2)));
            ledger.Mark(new WasteMark(WasteKinds.Round, "PIL1", "b", null, null, 1, WasteReasons.Nits, Noon.AddHours(-1)));

            var restarted = new SpendLedger(file);

            restarted.Marks().Count.ShouldBe(2);
            restarted.Entries().Count.ShouldBe(2);
            Waste(restarted).Rows.Select(r => (r.Leg, r.Reason, r.Tokens)).ShouldBe([("b", "nits", 100L)]);
            restarted.Waste("fortnight", Noon, TimeZoneInfo.Utc, null).ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PilotSpendWasteListsItWorstFirstWithTheWindowsTimeframe()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ledger = node.App.Services.GetRequiredService<SpendLedger>();
        var at = DateTimeOffset.UtcNow.AddSeconds(-30);
        Spend(ledger, "kept", "du1-pil1-a", "PIL1", "a", "worker", 0, at, 600);
        Spend(ledger, "again", "du1-pil1-a", "PIL1", "a", "worker", 1, at, 300);
        Spend(ledger, "lost", "du1-pil2-a", "PIL2", "a", "worker", 0, at, 100);
        ledger.Mark(new WasteMark(WasteKinds.Round, "PIL1", "a", null, null, 1, WasteReasons.Conflicts, at));
        ledger.Mark(new WasteMark(WasteKinds.NoResult, "PIL2", null, null, "lost", 0, "worker ended without finishing its leg", at));

        var report = (await node.Client.GetSpendWasteAsync("week", Ct)).Response.ShouldNotBeNull();
        var range = $"{report.From.ToLocalTime():yyyy-MM-dd HH:mm} to {report.To.ToLocalTime():yyyy-MM-dd HH:mm}";
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "spend", "--waste", "--window", "week"], node.Folder, output, error, Ct);

        exit.ShouldBe(0, error.ToString() + output);
        var text = output.ToString();
        text.ShouldStartWith("spend waste, 7-day window: " + range);
        text.ShouldContain("2,000 of 5,000 weighted tokens spent for nothing kept (40.0 %), " + range);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var rows = lines.Where(l => l.StartsWith("  ")).ToArray();
        rows.Length.ShouldBe(2);
        rows[0].ShouldContain("round: conflicts  PIL1/a");
        rows[0].ShouldContain("300 tokens");
        rows[1].ShouldContain("no-result: worker ended without finishing its leg  PIL2");
        (await node.Client.GetSpendWasteAsync("fortnight", Ct)).Success.ShouldBeFalse();
    }
}
