using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1226: the follow-up backlog is visible and limited. The sweep counts each repo's open follow-up issues and those with no flight, status and the
/// digest show the counts, an issue with no flight past a day rings once, the points' lines are tickable, and landing a flight on a follow-up issue ticks the
/// lines its points name and closes the issue when none is left. In-memory node, fake GitHub and a fake TimeProvider.
/// </summary>
public sealed class FollowUpCountTests : ReviewTestsBase
{
    private const string Repo_ = "SharpAstro/tianwen";

    private static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);

    private FollowUpSweep Sweep() => new(_state, _github, _time);

    /// <summary>A flight of the repo named as GitHub names it, on <paramref name="issue"/>, so the sweep knows the repo.</summary>
    private void FlightOn(int issue, string code)
    {
        _state.PutFlight(new FlightRow
        {
            Code = code, Repo = Repo_, RepoPath = Repo, Issue = issue, Branch = "flight/" + code.ToLowerInvariant(), Worktree = Path.Combine(_root, code), Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
    }

    /// <summary>A follow-up issue made <paramref name="age"/> ago, as the label alone, or as its title alone, marks it.</summary>
    private int FollowUp(TimeSpan age, bool labelled = true, string body = "")
    {
        _github.IdeaClock = Start - age;
        var issue = _github.AddIssue(Repo, labelled ? "Follow-up of PIL1: points left" : "Follow-up of PIL2: points left", null, body: body);
        if (labelled)
        {
            _github.IssueLabels.GetOrAdd(issue.Number, _ => [])[DeferredRoutes.FollowUpLabel] = 0;
        }
        return issue.Number;
    }

    [Fact]
    public async Task EachRepoCountsItsOpenFollowUpsByLabelOrTitleAndThoseWithNoFlight()
    {
        var withFlight = FollowUp(TimeSpan.FromHours(2));
        var labelled = FollowUp(TimeSpan.FromHours(2));
        var titled = FollowUp(TimeSpan.FromHours(2), labelled: false);
        _github.AddIssue(Repo, "A plain issue", null);
        FlightOn(withFlight, "SWP1");

        var row = (await Sweep().SweepAsync(Ct)).ShouldNotBeNull();

        var repo = row.Repos.ShouldHaveSingleItem();
        repo.Repo.ShouldBe(Repo_);
        repo.Open.ShouldBe(3);
        repo.NoFlight.ShouldBe([labelled, titled]);
        _state.FollowUps().ShouldNotBeNull().Repos.ShouldBe(row.Repos);
    }

    [Fact]
    public async Task AClosedFollowUpIsNotCountedAndARepoWithNoneIsNotListed()
    {
        var number = FollowUp(TimeSpan.FromHours(2));
        FlightOn(900, "SWP1");
        await _github.CloseIssueAsync(Repo, number, "done", Ct);
        _github.Issues.TryRemove(number, out _);
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Repo));
        _github.MilestoneIssuesOf[key][number] = _github.MilestoneIssuesOf[key][number] with { Open = false };

        var row = (await Sweep().SweepAsync(Ct)).ShouldNotBeNull();

        row.Repos.ShouldBeEmpty();
        row.Lines().ShouldBeEmpty();
    }

    [Fact]
    public async Task StatusAndTheDigestShowTheCountsInTheParkedSection()
    {
        var withFlight = FollowUp(TimeSpan.FromHours(2));
        var a = FollowUp(TimeSpan.FromHours(2));
        var b = FollowUp(TimeSpan.FromHours(2), labelled: false);
        FlightOn(withFlight, "SWP1");
        await Sweep().SweepAsync(Ct);
        var line = $"follow-ups  {Repo_}: 3 open, 2 without a flight (#{a}, #{b})";

        PilotCommand.Status(_state.Snapshot()).ShouldContain(line);
        var (_, detail) = new Digest(_state, _time).Compose(Start - TimeSpan.FromHours(12), Start);
        detail.ShouldContain("Parked:");
        detail.ShouldContain($"- {line}");
    }

    [Fact]
    public async Task StatusAndTheDigestSayNothingOfFollowUpsWhenNoRepoHasAnOpenOne()
    {
        FlightOn(900, "SWP1");
        await Sweep().SweepAsync(Ct);

        PilotCommand.Status(_state.Snapshot()).ShouldNotContain("follow-ups");
        new Digest(_state, _time).Compose(Start - TimeSpan.FromHours(12), Start).Detail.ShouldNotContain("follow-ups");
    }

    [Fact]
    public async Task AnIssueWithNoFlightRingsOnceAfterADayNamingTheIssueAndTheCommand()
    {
        var number = FollowUp(TimeSpan.FromHours(23));
        FlightOn(900, "SWP1");
        var sweep = Sweep();

        await sweep.SweepAsync(Ct);
        _state.Cards().Where(c => c.Kind == FollowUpSweep.RingKind).ShouldBeEmpty();

        _time.Advance(TimeSpan.FromHours(2));
        await sweep.SweepAsync(Ct);
        _time.Advance(TimeSpan.FromHours(6));
        await sweep.SweepAsync(Ct);
        await new FollowUpSweep(_state, _github, _time).SweepAsync(Ct);

        var card = _state.Cards().Where(c => c.Kind == FollowUpSweep.RingKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe($"follow-up:{Repo_}#{number}");
        card.Title.ShouldContain($"#{number}");
        card.Detail.ShouldNotBeNull().ShouldContain($"pilot flight create {number} --repo {Repo_}");
    }

    [Fact]
    public async Task NoRingForAnIssueUnderADayOldOrOneThatHasAFlight()
    {
        var young = FollowUp(TimeSpan.FromHours(3));
        var old = FollowUp(TimeSpan.FromDays(3));
        FlightOn(old, "SWP1");

        await Sweep().SweepAsync(Ct);
        _time.Advance(TimeSpan.FromHours(10));
        await Sweep().SweepAsync(Ct);

        _state.Cards().Where(c => c.Kind == FollowUpSweep.RingKind).ShouldBeEmpty();
        young.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ARepoGitHubCannotBeReadKeepsItsLastCountAndTheSweepGoesOn()
    {
        FollowUp(TimeSpan.FromHours(2));
        FlightOn(900, "SWP1");
        await Sweep().SweepAsync(Ct);
        _github.OpenIssuesFail = "offline";
        _github.MilestoneIssuesOf.Clear();

        await Sweep().SweepAsync(Ct);

        _state.FollowUps().ShouldNotBeNull().Repos.ShouldHaveSingleItem().Open.ShouldBe(1);
    }

    [Fact]
    public void EachPointOfTheBodyHasOneTickableLineAfterItsHeadingAndKeepsItsBullets()
    {
        var items = new[]
        {
            new ReviewFollowUp.Item("R1.3", "the lexer in Lexer.cs:3 wants a rewrite", "larger than the flight", "a flight on it"),
            new ReviewFollowUp.Item("R1.4", "the rename touches every caller", "larger than the flight", "a flight on it"),
        };

        var lines = ReviewFollowUp.Points(items).ReplaceLineEndings("\n").Split('\n');

        lines.Count(l => l.StartsWith("- [ ] ", StringComparison.Ordinal)).ShouldBe(2);
        var first = Array.FindIndex(lines, l => l.StartsWith("### R1.3: Lexer.cs:3", StringComparison.Ordinal));
        lines[first + 1].ShouldBe("- [ ] `R1.3`");
        lines[first + 2].ShouldStartWith("- Found: ");
        lines[first + 3].ShouldStartWith("- Why it was left: ");
        lines[first + 4].ShouldStartWith("- What would settle it: ");
        lines[Array.FindIndex(lines, l => l.StartsWith("### R1.4", StringComparison.Ordinal)) + 1].ShouldBe("- [ ] `R1.4`");
    }

    [Fact]
    public void TickTicksOnlyTheLinesItsKeysNameAndCountsWhatIsLeft()
    {
        var body = "### R1.3: a\n- [ ] `R1.3`\n- Found: x\n\n### R1.4: b\n- [ ] `R1.4`\n- Found: y\n";

        var (ticked, count, left, boxes) = ReviewFollowUp.Tick(body, ["r1.3"]);

        count.ShouldBe(1);
        left.ShouldBe(1);
        boxes.ShouldBeTrue();
        ticked.ShouldContain("- [x] `R1.3`");
        ticked.ShouldContain("- [ ] `R1.4`");
        ReviewFollowUp.Tick(ticked, ["R1.4"]).Left.ShouldBe(0);
        ReviewFollowUp.Tick("An older body: R1.3 is the point\n", ["R1.3"]).HasBoxes.ShouldBeFalse();
    }

    private const string TwoPoints = "### R1.3: a\n- [ ] `R1.3`\n- Found: x\n\n### R1.4: b\n- [ ] `R1.4`\n- Found: y\n";

    private const string Answers = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n";

    /// <summary>A flight on the follow-up issue, flown with <paramref name="report"/>, its PR merged on GitHub and the landing seen by the checks.</summary>
    private async Task<string> Landed(int issue, string report)
    {
        var flight = await NewFlight(issue.ToString(), "parser");
        await Fly(flight, "parser", report: report);
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Ok.ShouldBeTrue();
        _github.Pr = _github.Pr! with { State = "MERGED" };
        await new PrChecks(_state, _github, TimeProvider.System, _flights).LookAsync(Ct);
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        return flight.Code;
    }

    [Fact]
    public async Task LandingTicksThePointsItsReportMadeAndLeavesTheIssueOpenWhileALineIsLeft()
    {
        var issue = FollowUp(TimeSpan.FromHours(2), body: TwoPoints);

        await Landed(issue, "did R1.3\n\n" + Answers + "| R1.3 | addressed | Lexer.cs:3 |\n");

        var body = _github.IssueBodies[issue];
        body.ShouldContain("- [x] `R1.3`");
        body.ShouldContain("- [ ] `R1.4`");
        _github.ClosedIssues.ShouldBeEmpty();
    }

    [Fact]
    public async Task LandingClosesTheIssueWhenItsLastLineIsTicked()
    {
        var issue = FollowUp(TimeSpan.FromHours(2), body: TwoPoints);

        var code = await Landed(issue, "did both\n\n" + Answers + "| R1.3 | addressed | Lexer.cs:3 |\n| R1.4 | not work | the rename is a taste |\n");

        var body = _github.IssueBodies[issue];
        body.ShouldContain("- [x] `R1.3`");
        body.ShouldContain("- [x] `R1.4`");
        _github.ClosedIssues.ShouldBe([(issue, $"Landed in #{F(code).PullRequest} ({code}).")]);
    }

    [Fact]
    public async Task AnOlderFollowUpIssueWithNoCheckboxLineIsLeftAsItIsAndClosesAsAnyIssueDoes()
    {
        var issue = FollowUp(TimeSpan.FromHours(2), body: "### R1.3: a\n- Found: x\n");

        await Landed(issue, "did R1.3\n\n" + Answers + "| R1.3 | addressed | Lexer.cs:3 |\n");

        _github.IssueBodies.ContainsKey(issue).ShouldBeFalse();
        _github.ClosedIssues.Select(c => c.Number).ShouldBe([issue]);
    }

    [Fact]
    public async Task AnIssueThatIsNoFollowUpClosesAsBefore()
    {
        _github.IdeaClock = Start;
        var plain = _github.AddIssue(Repo, "A plain issue", null, body: TwoPoints).Number;

        await Landed(plain, "did R1.3\n\n" + Answers + "| R1.3 | addressed | Lexer.cs:3 |\n");

        _github.IssueBodies.ContainsKey(plain).ShouldBeFalse();
        _github.ClosedIssues.Select(c => c.Number).ShouldBe([plain]);
    }

    private const string Namespaced = "### PIL12:R1.3: a\n- [ ] `PIL12:R1.3`\n- Found: x\n\n### PIL12:R1.4: b\n- [ ] `PIL12:R1.4`\n- Found: y\n";

    [Fact]
    public void ThePointsOfAFlightAreKeyedWithItsCodeAndTheBodyTellsHowToReport()
    {
        var flight = new FlightRow { Code = "PIL12", Repo = Repo_, RepoPath = Repo, Issue = 1, Branch = "b", Worktree = "w", Base = "main", State = FlightState.Flying, CreatedAt = Start };
        var body = ReviewFollowUp.Body(flight, [new ReviewFollowUp.Item("R1.2", "in A.cs:1", "later", "a flight")]);

        body.ShouldContain("- [ ] `PIL12:R1.2`");
        body.ShouldContain("### PIL12:R1.2: A.cs:1");
        body.IndexOf("key | addressed | file:line", StringComparison.Ordinal).ShouldBeInRange(0, body.IndexOf("### ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReviewAnswerOfTheFollowUpFlightsOwnTicksNothingOfTheIssue()
    {
        var issue = FollowUp(TimeSpan.FromHours(2), body: TwoPoints);
        var flight = await NewFlight(issue.ToString(), "parser");
        await Fly(flight, "parser", report: "did nothing of the issue");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Blocking\n- the guard is missing in Parser.cs:20\n\n## Not blocking\n- a nit in Lexer.cs:3\n")).Error.ShouldBeNull();
        File.WriteAllText(Path.Combine(F(flight.Code).Worktree, "fix.txt"), "more\n");
        Git(F(flight.Code).Worktree, "add", ".");
        Git(F(flight.Code).Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix the guard");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        var rows = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | Parser.cs:20 |\n| R1.2 | addressed | Lexer.cs:3 |\n| R1.3 | addressed | Lexer.cs:3 |\n";
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", rows)).Error.ShouldBeNull();
        _github.Pr = _github.Pr! with { State = "MERGED" };
        await new PrChecks(_state, _github, TimeProvider.System, _flights).LookAsync(Ct);

        _github.IssueBodies.ContainsKey(issue).ShouldBeFalse();
        _github.ClosedIssues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ABareKeyOfAnotherFlightsLegTicksNoNamespacedLineAndTheNamespacedKeyDoes()
    {
        var issue = FollowUp(TimeSpan.FromHours(2), body: Namespaced);

        await Landed(issue, "did\n\n" + Answers + "| R1.3 | addressed | Lexer.cs:3 |\n");
        _github.IssueBodies.ContainsKey(issue).ShouldBeFalse();
        _github.ClosedIssues.ShouldBeEmpty();

        var second = FollowUp(TimeSpan.FromHours(2), body: Namespaced);
        await Landed(second, "did\n\n" + Answers + "| PIL12:R1.3 | addressed | Lexer.cs:3 |\n");
        var body = _github.IssueBodies[second];
        body.ShouldContain("- [x] `PIL12:R1.3`");
        body.ShouldContain("- [ ] `PIL12:R1.4`");
        _github.ClosedIssues.ShouldBeEmpty();
    }

    [Fact]
    public async Task APointALaterRoundAddsLandsInTheIssueBodyWhereTheTickReadsItAndKeepsTheIssueOpen()
    {
        var existing = FollowUp(TimeSpan.FromHours(2), body: "### PIL1:R1.3: a\n- [x] `PIL1:R1.3`\n- Found: x\n");
        var flight = await NewFlight("40", "parser");
        await Fly(flight, "parser");
        _state.PutFlight(F(flight.Code) with { FollowUpIssue = existing, FollowUpPoints = ["R1.3"], FollowUpFlight = "PIL99" });
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Blocking\n- the guard is missing in Parser.cs:20\n- the lexer wants a rewrite\n")).Error.ShouldBeNull();
        File.WriteAllText(Path.Combine(F(flight.Code).Worktree, "fix.txt"), "more\n");
        Git(F(flight.Code).Worktree, "add", ".");
        Git(F(flight.Code).Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass",
            "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n| R1.2 | deferred: larger than the flight | the lexer wants a rewrite |\n")).Error.ShouldBeNull();

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head)).Error.ShouldBeNull();

        var body = _github.IssueBodies[existing];
        body.ShouldContain("- [x] `PIL1:R1.3`");
        body.ShouldContain($"- [ ] `{flight.Code}:R1.2`");
        _github.IssueComments.ShouldNotContain(c => c.Number == existing);
        var (_, _, left, _) = ReviewFollowUp.Tick(body, []);
        left.ShouldBe(1);
    }

    [Fact]
    public async Task TheSweepReadsATitleOnlyIssuesCreationTimeOncePerIssueNotOncePerSweep()
    {
        FlightOn(1, "PIL1");
        FollowUp(TimeSpan.FromHours(2), labelled: false);
        var sweep = new FollowUpSweep(_state, _github, _time);
        _github.IssueNowCalls = 0;

        await sweep.SweepAsync(Ct);
        await sweep.SweepAsync(Ct);

        _github.IssueNowCalls.ShouldBe(1);
    }
}
