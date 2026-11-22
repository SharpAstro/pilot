using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Nothing lands open (pilot #1107): the points a flight's reviews left with a reason are filed as one follow-up issue before its PR
/// merges, the PR links it, the row carries its number and the landing names it. In-memory node, fake reviewer and fake GitHub.
/// </summary>
public sealed class ReviewFollowUpTests : ReviewTestsBase
{
    private const string FirstReport =
        "## Blocking\n- A is wrong in Parser.cs:10\n- B is missing a guard in Parser.cs:15\n\n## Not blocking\n- C is a nit in Lexer.cs:3\n- D renames Parser.cs:20, a wider change\n";

    private async Task<string> SecondRound(string firstReport)
    {
        var flight = await NewFlight("40", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", firstReport)).Error.ShouldBeNull();
        File.WriteAllText(Path.Combine(F(flight.Code).Worktree, "Parser.cs"), "more\n");
        Git(F(flight.Code).Worktree, "add", ".");
        Git(F(flight.Code).Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix A, B and C");
        // The node sent the fix round itself; the worker's finish pushes and starts the second review.
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), _state.Legs(flight.Code).Single().Worker!, "done", "fixed")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        return flight.Code;
    }

    private Task<CommandResult> Merge(string code) =>
        Run(Verbs.Merge, Verbs.FlightTarget(code), "controller", _github.Pr!.Head);

    private const string Answers =
        "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n"
        + "| R1.1 | addressed | fix.txt:1 |\n| R1.2 | addressed | fix.txt:1 |\n| R1.3 | addressed | fix.txt:1 |\n"
        + "| R1.4 | deferred: larger than the flight | a rename of Parser.cs touches every caller, out of this flight's scope |\n";

    [Fact]
    public async Task APointLeftWithAReasonIsFiledAsOneIssueTheMergeLinksAndTheMergeGoesAhead()
    {
        var code = await SecondRound(FirstReport);

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Answers)).Error.ShouldBeNull();
        (await Merge(code)).Error.ShouldBeNull();

        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        issue.Body.ShouldNotBeNull().ShouldContain("R1.4");
        issue.Body.ShouldContain("Parser.cs:20");
        issue.Body.ShouldContain("out of this flight's scope");
        issue.Body.ShouldContain("What would settle it");
        issue.Body.ShouldNotContain("R1.1");
        var flight = F(code);
        flight.FollowUpIssue.ShouldNotBeNull();
        flight.FollowUpPoints.ShouldBe(["R1.4"]);
        _github.IssueComments.ShouldContain(c => c.Number == flight.PullRequest && c.Body.Contains($"#{flight.FollowUpIssue}"));
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ThePassingRoundsNotesNobodyMadeAreFiledAtMergeBesideTheLeftPointAndOnlyOnce()
    {
        var code = await SecondRound(FirstReport);

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass",
            Answers + "\n## Not blocking\n- E could be tidier in Lexer.cs:8\n- F deserves a comment in Parser.cs:40\n")).Error.ShouldBeNull();
        // A pass with notes holds the flight; a person clears the hold untaken, so the notes were never made.
        // Only the person's word clears it with the notes unmade (pilot #1226): a reason, then the as-is merge.
        (await Run(Verbs.Clear, Verbs.FlightTarget(code), "me", "to merge the notes as follow-ups")).Error.ShouldBeNull();
        Task<CommandResult> AsIs() => Run(Verbs.Merge, Verbs.FlightTarget(code), "me", _github.Pr!.Head, "squash", LegCommander.MergeAsIs);
        (await AsIs()).Error.ShouldBeNull();

        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        // The PR-ready card filed the left point first; the merge adds the unmade notes to that same issue's body, where the tick reads them.
        issue.Body.ShouldNotBeNull().ShouldContain("R1.4");
        var body = _github.IssueBodies[F(code).FollowUpIssue!.Value];
        body.ShouldContain("R1.4");
        body.ShouldContain("E could be tidier");
        body.ShouldContain("F deserves a comment");
        body.ShouldContain("merged without being made");
        _github.IssueComments.ShouldNotContain(c => c.Number == F(code).FollowUpIssue);
        F(code).FollowUpPoints.ShouldNotBeNull().Length.ShouldBe(3);

        // A second ask (the flight has landed) files nothing more.
        var commentsBefore = _github.IssueComments.Count;
        (await AsIs()).Error.ShouldNotBeNull().ShouldContain("landed");
        _github.IssuesCreated.ShouldHaveSingleItem();
        _github.IssueComments.Count.ShouldBe(commentsBefore);
    }

    [Fact]
    public void TheReportExampleTheWorkerIsToldTicksTheLineItNames()
    {
        // What the worker is told (the issue's line and the brief are the same text), taken as the worker writes it: the heading, then each example row
        // with its placeholder filled in. Both rows must parse and the addressed one tick its line; a format the parser skips would tick nothing.
        var rows = System.Text.RegularExpressions.Regex.Matches(ReviewFollowUp.ReportLine, @"`(\|[^`]+\|)`").Select(m => m.Groups[1].Value).ToList();
        rows.Count.ShouldBe(2);
        var report = "## Earlier points\n" + string.Join("\n", rows).Replace("file:line", "A.cs:3").Replace("| why |", "| a title is not code |") + "\n";

        var replies = ReviewPoints.ParseAnswers(report);

        replies.Count.ShouldBe(1, "both rows name the same key, so the later one stands");
        var addressed = ReviewPoints.ParseAnswers("## Earlier points\n" + rows[0].Replace("file:line", "A.cs:3") + "\n").ShouldHaveSingleItem();
        addressed.Answer.ShouldBe(ReviewPoints.Answer.Addressed);
        ReviewPoints.HasWhere(addressed.Detail).ShouldBeTrue();
        ReviewPoints.ParseAnswers("## Earlier points\n" + rows[1].Replace("| why |", "| a title is not code |") + "\n").ShouldHaveSingleItem().Answer.ShouldBe(ReviewPoints.Answer.NotWork);

        var issue = ReviewFollowUp.Points([new ReviewFollowUp.Item("R1.2", "found in A.cs:3", "larger than the flight", "settle")], "PIL12");
        var (ticked, count, left, _) = ReviewFollowUp.Tick(issue, [addressed.Id]);
        count.ShouldBe(1);
        left.ShouldBe(0);
        ticked.ShouldContain("- [x] `PIL12:R1.2`");
    }

    [Fact]
    public async Task NoPointsLeftFilesNoIssue()
    {
        var code = await SecondRound("## Blocking\n- A is wrong in Parser.cs:10\n");

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n")).Error.ShouldBeNull();
        (await Merge(code)).Error.ShouldBeNull();

        _github.IssuesCreated.ShouldBeEmpty();
        F(code).FollowUpIssue.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnIssueThatCannotBeFiledHoldsTheMergeAndDropsNoPoint()
    {
        var code = await SecondRound(FirstReport);
        _github.CreateIssueFails = "gh could not reach GitHub";

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Answers)).Error.ShouldBeNull();
        var held = await Merge(code);

        held.Error.ShouldNotBeNull().ShouldContain("follow-up issue could not be filed");
        _github.Merged.ShouldBeEmpty();
        F(code).FollowUpIssue.ShouldBeNull();

        // Once gh answers, the same ask files it and merges: the point was kept, not lost.
        _github.CreateIssueFails = null;
        (await Merge(code)).Error.ShouldBeNull();
        _github.IssuesCreated.ShouldHaveSingleItem().Body.ShouldNotBeNull().ShouldContain("R1.4");
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public void AControllersPassAnywayPutsTheOpenPointsInTheIssueWithThatWordAsTheReason()
    {
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong in Parser.cs:10\n" },
            new() { Round = 2, Verdict = ReviewState.Passed, Findings = "ready (passed by controller over: A is wrong)", PassedBy = "controller" },
        ];

        var standing = ReviewFollowUp.Read(rounds);

        standing.NotAddressed.ShouldBeEmpty();
        var item = standing.Items.ShouldHaveSingleItem();
        item.Key.ShouldBe("R1.1");
        item.Why.ShouldContain("pass anyway by controller");
        item.Where.ShouldBe("Parser.cs:10");
    }

    [Fact]
    public void AReviewersPassKeepsNoPointOpenAndAStandingNotAddressedPointIsNamed()
    {
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong in Parser.cs:10\n" },
            new() { Round = 2, Verdict = ReviewState.Changes, Findings = "## Earlier points\n| R1.1 | not addressed | |\n", Answers = [new("R1.1", "not addressed", "")] },
        ];

        ReviewFollowUp.Read(rounds).NotAddressed.ShouldBe(["R1.1"]);
        ReviewFollowUp.Read([rounds[0]]).Items.ShouldBeEmpty();
    }

    [Fact]
    public void APassAnywayReportIsNumberedOnceNotAgainFromItsTail()
    {
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong\n" },
            new() { Round = 2, Verdict = ReviewState.Passed, Findings = "ready anyway (passed by controller over: Round 3 read x\n\n## Blocking\n- A is wrong\n)", PassedBy = "controller" },
        ];

        ReviewPoints.Number(rounds).Select(p => p.Id).ShouldBe(["R1.1"]);
    }

    [Fact]
    public void AProseWordPassedByHandBeforeTheControllerTailDoesNotNameThePasser()
    {
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong in Parser.cs:10\n" },
            new() { Round = 2, Verdict = ReviewState.Passed, Findings = "A note (passed by hand) in prose. ready anyway (passed by controller over: A is wrong)", PassedBy = "controller" },
        ];

        var item = ReviewFollowUp.Read(rounds).Items.ShouldHaveSingleItem();
        item.Why.ShouldContain("pass anyway by controller");
        item.Why.ShouldNotContain("by hand)");
    }

    [Fact]
    public void ProseSayingPassedByAfterTheControllerTailStillNamesTheController()
    {
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong in Parser.cs:10\n" },
            new() { Round = 2, Verdict = ReviewState.Passed, Findings = "ready anyway (passed by controller over: the note said (passed by hand) here)", PassedBy = "controller" },
        ];

        var item = ReviewFollowUp.Read(rounds).Items.ShouldHaveSingleItem();
        item.Why.ShouldContain("pass anyway by controller");
        item.Why.ShouldNotContain("by hand");
    }

    [Fact]
    public async Task TwoMergeAsksTogetherFileOneIssueAndMergeOnce()
    {
        var code = await SecondRound(FirstReport);
        // The pass-time filing fails, so the issue is filed by the merge path, where the two asks can race.
        _github.CreateIssueFails = "gh could not reach GitHub";
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Answers)).Error.ShouldBeNull();
        _github.CreateIssueFails = null;
        _github.IssuesCreated.ShouldBeEmpty();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.CreateIssueHold = release.Task;

        var first = Merge(code);
        await _github.CreateIssueHoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // The first ask is parked filing the issue; the second, asked now, is answered while it is held, so the two really overlap.
        var second = await Merge(code).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        first.IsCompleted.ShouldBeFalse();
        release.SetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        firstResult.Error.ShouldBeNull();
        second.Error.ShouldBeNull();
        _github.IssuesCreated.ShouldHaveSingleItem();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TwoOverlappingMergeAsksMergeOnce()
    {
        var code = await SecondRound(FirstReport);
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Answers)).Error.ShouldBeNull();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.MergeHold = release.Task;

        var first = Merge(code);
        await _github.MergeHoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = Merge(code);
        // One of the two is answered while the other is held at the forge's merge: the overlap, shown from the second ask's side.
        (await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(second);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        results.ShouldContain(r => r.Error == null);
        results.Where(r => r.Error != null).ShouldAllBe(r => r.Error!.Contains("landed"));
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public void ProseSayingPassedByIsNotThePassAnywayTail()
    {
        ReviewPoints.PassedTailStart(new ReviewRound { Round = 1, Verdict = ReviewState.Passed, Findings = "## Blocking\n- a note (passed by hand) here\n- B\n", PassedBy = "controller" }).ShouldBe(-1);
        ReviewPoints.PassedTailStart(new ReviewRound { Round = 1, Verdict = ReviewState.Passed, Findings = "x (passed by controller over: y)", PassedBy = "controller" }).ShouldBe(1);
        // A passer whose name holds a space is still found: the node matches the name it stored, not one word of prose.
        ReviewPoints.PassedTailStart(new ReviewRound { Round = 1, Verdict = ReviewState.Passed, Findings = "x (passed by the owner over: y)", PassedBy = "the owner" }).ShouldBe(1);
        // A round with no stored passer has no tail, whatever its prose quotes.
        ReviewPoints.PassedTailStart(new ReviewRound { Round = 1, Verdict = ReviewState.Passed, Findings = "x (passed by controller over: y)" }).ShouldBe(-1);
    }

    [Fact]
    public void AReviewersReportThatQuotesATailMidReportKeepsEveryPointAfterIt()
    {
        // A re-review quoting an earlier pass anyway, in a changes round and in a reviewer's own pass: neither is the node's tail.
        const string Quoting = "## Blocking\n- `ready anyway (passed by controller over: A)` is quoted here\n- B is wrong\n\n## Not blocking\n- C could be tidier\n";
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = Quoting },
            new() { Round = 2, Verdict = ReviewState.Passed, Findings = Quoting, Answers = [new("R1.2", "left", "out of scope")] },
        ];

        ReviewPoints.Number(rounds).Select(p => p.Id).ShouldBe(["R1.1", "R1.2", "R1.3", "R2.1", "R2.2", "R2.3"]);
        ReviewPoints.Number(rounds).Where(p => p.Round == 2).Select(p => p.Text).ShouldContain("C could be tidier");
        // The reviewer passed, so nothing is filed as a controller's pass anyway.
        // Not on an empty list: the point left is filed, and nothing as a pass anyway (the review of #1142).
        var items = ReviewFollowUp.Read(rounds).Items;
        items.ShouldNotBeEmpty();
        items.ShouldAllBe(i => !i.Why.Contains("pass anyway"));
    }

    private static ReviewFollowUp.Standing TwoLeft(string found1, string why1, string found2, string why2) => ReviewFollowUp.Read(
    [
        new ReviewRound { Round = 1, Verdict = ReviewState.Changes, Findings = $"## Blocking\n- {found1}\n- {found2}\n" },
        new ReviewRound { Round = 2, Verdict = ReviewState.Passed, Findings = "", Answers = [new("R1.1", "left", why1), new("R1.2", "left", why2)] },
    ]);

    [Fact]
    public void APointWhoseFindingNamesNoFileIsHeadedWithTheFileItsReasonNames()
    {
        var standing = TwoLeft("The loop never ends", "src/X.cs:12 needs a wider change", "Other thing", "out of scope");

        var points = ReviewFollowUp.Points(standing.Items);

        points.ShouldContain("### R1.1: src/X.cs:12");
        points.ShouldContain("### R1.2: no file named");
    }

    [Fact]
    public void TwoLeftPointsGetTheirOwnWhatWouldSettleItLines()
    {
        var standing = TwoLeft("Parser.cs:20 renames a type", "wider", "The guard in Lexer.cs:3 is missing", "later");

        var settle = standing.Items.Select(i => i.Settle).ToList();

        settle[0].ShouldNotBe(settle[1]);
        settle[0].ShouldContain("Parser.cs:20");
        settle[1].ShouldContain("Lexer.cs:3");
        settle.ShouldAllBe(l => !l.Contains('\n') && !l.Contains('\u2014'));
    }

    [Fact]
    public void AFindingThatOpensWithAFileKeepsItsWordsInTheSettleLine()
    {
        var standing = TwoLeft("Parser.cs: the guard is missing", "wider", "Other thing", "later");

        standing.Items[0].Settle.ShouldContain("the guard is missing");
    }

    private static ReviewFollowUp.Standing WithAnswers(params ReviewPointAnswer[] answers) => ReviewFollowUp.Read(
    [
        new ReviewRound { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A in Parser.cs:1\n- B in Parser.cs:2\n- C in Parser.cs:3\n- D in Parser.cs:4\n- E in Parser.cs:5\n" },
        new ReviewRound { Round = 2, Verdict = ReviewState.Passed, Findings = "", Answers = answers },
    ]);

    [Fact]
    public void NotWorkAndNodesOwnPointsFileNothingAndAMadeOneNeitherButADeferredOneIsAnItemWithItsReason()
    {
        var standing = WithAnswers(
            new("R1.1", "addressed", "Parser.cs:9"),
            new("R1.2", "not work", "praise"),
            new("R1.3", "node's own", ""),
            new("R1.4", "deferred", "needs the other package") { Reason = "another repo" },
            new("R1.5", "deferred", "") { Reason = "larger than the flight" });

        standing.NotAddressed.ShouldBeEmpty();
        standing.Items.Select(i => i.Key).ShouldBe(["R1.4", "R1.5"]);
        standing.Items.Select(i => i.Kind).ShouldBe([ReviewPoints.DeferReason.OtherRepo, ReviewPoints.DeferReason.LargerThanFlight]);
        standing.Items[0].Why.ShouldContain("another repo");
        standing.Items[0].Why.ShouldContain("needs the other package");
    }

    [Fact]
    public void AnOldStoredLeftAnswerReadsAsDeferredWithNoReason()
    {
        var standing = WithAnswers(new ReviewPointAnswer("R1.1", "left","out of scope, Parser.cs is wider"));

        var item = standing.Items.ShouldHaveSingleItem();
        item.Key.ShouldBe("R1.1");
        item.Kind.ShouldBeNull();
        item.Why.ShouldBe("out of scope, Parser.cs is wider");
    }

    [Theory]
    [InlineData("deferred, another repo: read/write widths in px/em", "src/Pilot.Node of DIR.Lib", "DIR.Lib")]
    [InlineData("deferred, another repo: the fix belongs in src/Pilot.Node", "", null)]
    [InlineData("deferred, another repo: and/or the sibling", "", null)]
    [InlineData("deferred, another repo: widths in px/em", "", null)]
    public void TargetRepoNeverTakesAPathOrAPhraseWithASlashForARepo(string why, string found, string? expected)
    {
        var item = new ReviewFollowUp.Item("R1.4", found.Length > 0 ? found : "read/write widths in px/em and/or src/Pilot.Node", why, "") { Kind = ReviewPoints.DeferReason.OtherRepo };

        ReviewFollowUp.TargetRepo(item, n => n == "DIR.Lib").ShouldBe(expected);
    }

    [Fact]
    public void TargetRepoPrefersTheSiblingTheAnswerNamesToOneTheFindingMentions()
    {
        var item = new ReviewFollowUp.Item("R1.4", "Parser.cs in SharpAstro/Other is wrong", "deferred, another repo: DIR.Lib", "") { Kind = ReviewPoints.DeferReason.OtherRepo };

        ReviewFollowUp.TargetRepo(item, n => n is "DIR.Lib" or "Other").ShouldBe("DIR.Lib");
    }

    [Fact]
    public void AnAnswerStoredBeforeTheReasonExistedStillLoads()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<ReviewPointAnswer>("""{"Id":"R1.1","Answer":"left","Detail":"why"}""");

        old.ShouldNotBeNull().Reason.ShouldBeNull();
    }

    [Theory]
    [InlineData("All notes are made in the review-notes round", true)]
    [InlineData("This is already answered by the issue", true)]
    [InlineData("The text no longer exists after the rewrite", false)]
    [InlineData("The commit title is removed by the squash", true)]
    [InlineData("The guard in Parser.cs:3 is missing", false)]
    [InlineData("validation is already done in the caller but the lexer still accepts a bare quote", false)]
    [InlineData("the rename is already made in Parser.cs but not in Lexer.cs", false)]
    [InlineData("the old call is no longer in the hot path, yet the cache still holds it", false)]
    [InlineData("", false)]
    public void TextThatSaysItIsSettledIsRecognised(string text, bool settled)
    {
        ReviewFollowUp.IsSettledText(text).ShouldBe(settled);
    }

    [Fact]
    public void ASettledLeftPointOrNoteIsNeverFiled()
    {
        var rounds = new[]
        {
            new ReviewRound
            {
                Round = 1, Verdict = ReviewState.Passed, Findings = "## Not blocking\n- all notes are made in Parser.cs\n- the guard in Lexer.cs:3 is missing\n",
                LeftPoints = ["already answered by the issue", "a gap in Parser.cs:9"],
            },
        };

        ReviewFollowUp.Read(rounds).Items.Select(i => i.Found).ShouldBe(["a gap in Parser.cs:9"]);
        ReviewFollowUp.LastRoundNotes(rounds).Select(i => i.Found).ShouldBe(["the guard in Lexer.cs:3 is missing"]);
        ReviewFollowUp.NotesItems(1, ["the title is removed by the squash", "Parser.cs:1 is wrong"]).Select(i => i.Found).ShouldBe(["Parser.cs:1 is wrong"]);
    }
}
