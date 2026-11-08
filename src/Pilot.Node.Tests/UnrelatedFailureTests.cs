using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A failing test the flight's change does not touch does not fail the leg (#752): the node reruns the check without it, matches it
/// to an open issue or files one, and passes the leg with it named as not this flight's. Anything else fails as before.
/// </summary>
public sealed class UnrelatedFailureTests : LegCommanderTestsBase
{
    private const string Check = "dotnet test Tianwen.slnx --filter FullyQualifiedName~FooTests";

    private static string Failing(string fullName) =>
        $"""
        [xUnit.net 00:00:01.00]     {fullName} [FAIL]
          Failed {fullName} [12 ms]
          Error Message:
           boom
          Stack Trace:
             at {fullName}() in X.cs:line 3
        Failed!  - Failed: 1, Passed: 4, Skipped: 0, Total: 5
        """;

    /// <summary>A flight with one leg flying, whose plane changed <c>src/Foo.cs</c> (so <c>FooTests</c> is the flight's).</summary>
    private async Task<FlightRow> Flying()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse", "done-when=It parses", $"check={Check}")).Ok.ShouldBeTrue();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        var flight = _state.Flights().Single();
        Directory.CreateDirectory(Path.Combine(flight.Worktree, "src"));
        File.WriteAllText(Path.Combine(flight.Worktree, "src", "Foo.cs"), "class Foo { }\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "Foo");
        return flight;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUntouchedClassWithAnOpenIssuePassesTheLegNamingIt()
    {
        await Flying();
        _github.Issues[5] = "BarTests.Breaks fails on a loaded machine";
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();

        L("parser").State.ShouldBe(LegState.Done);
        L("parser").Report.ShouldBe("parsed\nnot this flight's: BarTests.Breaks (#5)");
        _checks.Runs.Count.ShouldBe(2);
        _checks.Runs[1].Command.ShouldContain("FullyQualifiedName!~BarTests");
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUntouchedClassWithNoIssueFilesOneAndPasses()
    {
        await Flying();
        _github.Issues[3] = "Something else entirely";
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();

        _checks.Runs.Count.ShouldBe(3);
        _checks.Runs[2].Command.ShouldContain("--filter 'FullyQualifiedName~BarTests.Breaks'");
        _checks.Runs[2].Cwd.ShouldNotBe(_state.Flights().Single().Worktree, "the base run is in its own worktree at the base");
        Directory.Exists(_checks.Runs[2].Cwd).ShouldBeFalse("the base's worktree is removed after");
        var filed = _github.IssuesCreated.ShouldHaveSingleItem();
        filed.Title.ShouldContain("BarTests.Breaks");
        filed.Title.ShouldContain("outside the flight's change");
        filed.Body.ShouldNotBeNull();
        filed.Body.ShouldContain("TIA1/parser");
        filed.Body.ShouldContain("boom");
        L("parser").State.ShouldBe(LegState.Done);
        L("parser").Report.ShouldBe("parsed\nnot this flight's: BarTests.Breaks (#4)");
    }

    /// <summary>The leg's failure investigations (PIL716), as the node wires them: flights made for them are counted, not flown.</summary>
    private (FailureInvestigations Investigations, List<CommandRequest> Made) Investigating()
    {
        var made = new List<CommandRequest>();
        var investigations = new FailureInvestigations(_state, _github, new FailureLedger(), TimeProvider.System)
        {
            CreateFlight = (command, _) =>
            {
                made.Add(command);
                return Task.FromResult(new CommandResult { Seq = 0, Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = true, Made = $"TIA{900 + made.Count}" });
            },
        };
        _legs.Investigations = investigations;
        return (investigations, made);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUntouchedFailureOnTheFinishIsFiledAsTheInvestigationThatALaterCiFailureJoins()
    {
        var flight = await Flying();
        var (investigations, made) = Investigating();
        _github.Issues[3] = "Something else entirely";
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();

        // The finish files the investigation, not #752's plain issue: priority high, its flight made and a For-you card.
        var filed = _github.IssuesCreated.ShouldHaveSingleItem();
        filed.Title.ShouldContain("BarTests.Breaks");
        filed.Title.ShouldContain("cause unknown");
        filed.Title.ShouldNotContain(UnrelatedFailures.TitleMark);
        _github.IssuePriorities[4].ShouldBe(IssuePriority.High);
        made.ShouldHaveSingleItem().Args[0].ShouldBe("4");
        _state.Cards().ShouldContain(c => c.Kind == FailureInvestigations.CardKind && c.Bin == CardBin.ForYou);
        investigations.Ledger.All().ShouldHaveSingleItem().Where.ShouldBe(FailureWhere.PassedLegCheck);
        L("parser").State.ShouldBe(LegState.Done);
        L("parser").Report.ShouldBe("parsed\nnot this flight's: BarTests.Breaks (#4)");

        // The same failure later in another PR's CI joins that investigation: a comment, no second issue or flight.
        _github.FailedRuns[(77, false)] = new FailedRun(555, "https://github.com/x/tianwen/actions/runs/555", ["build"], Failing("Pilot.Tests.BarTests.Breaks"));
        var look = await investigations.LookAtRunAsync(new FailureSite(flight.Repo, flight.RepoPath, 77, "TIA77", new string('c', 40), FailureWhere.Ci), mergeGroup: false, Ct);

        look.Untouched.ShouldHaveSingleItem().Issue.ShouldBe(4);
        _github.IssuesCreated.Count.ShouldBe(1);
        made.Count.ShouldBe(1);
        _github.IssueComments.ShouldContain(c => c.Number == 4 && c.Body.Contains("run 555"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnIssueTheFinishFiledEarlierBecomesTheInvestigationOfALaterCiFailure()
    {
        var flight = await Flying();
        var (investigations, made) = Investigating();
        _github.Issues[6] = UnrelatedFailures.IssueTitle("Pilot.Tests.BarTests.Breaks", "TIA0");
        _github.FailedRuns[(77, false)] = new FailedRun(555, "https://github.com/x/tianwen/actions/runs/555", ["build"], Failing("Pilot.Tests.BarTests.Breaks"));

        var look = await investigations.LookAtRunAsync(new FailureSite(flight.Repo, flight.RepoPath, 77, "TIA77", new string('c', 40), FailureWhere.Ci), mergeGroup: false, Ct);

        look.Untouched.ShouldHaveSingleItem().Issue.ShouldBe(6);
        _github.IssuesCreated.ShouldBeEmpty();
        _github.IssuePriorities[6].ShouldBe(IssuePriority.High);
        made.ShouldHaveSingleItem().Args[0].ShouldBe("6");
        _github.IssueComments.ShouldContain(c => c.Number == 6 && c.Body.Contains("now the investigation"));
    }

    private void OtherFlight(string code, FlightRow like) =>
        _state.PutFlight(like with { Code = code, Issue = 2, Branch = $"flight/{code.ToLowerInvariant()}", PullRequest = 2, PullRequestUrl = "https://github.com/x/tianwen/pull/2" });

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARepeatOfAnotherFlightsFailureOnTheFinishPutsTheTowerOnAlertHoldingTheOtherFlightAndNotThisOne()
    {
        var flight = await Flying();
        var (investigations, made) = Investigating();
        var alerts = new FailureAlerts(_state, _github, investigations, TimeProvider.System);
        _legs.Alerts = alerts;
        OtherFlight("TIA2", flight);
        // Another flight's PR hit the same failure first: it is the investigation's first occurrence.
        var output = Failing("Pilot.Tests.BarTests.Breaks");
        (await investigations.InvestigateTestAsync(new FailureSite(flight.Repo, flight.RepoPath, 2, "TIA2", new string('b', 40), FailureWhere.Ci), "Pilot.Tests.BarTests.Breaks", output, null, Ct))
            .ShouldNotBeNull().Others.ShouldBeFalse();
        _checks.Script.Enqueue(new CheckRun(1, output));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));

        // The leg passes through #752, and the repeat still reaches the tower: one Ring card, TIA2 held, this flight not.
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();

        L("parser").State.ShouldBe(LegState.Done);
        var alert = alerts.Open().ShouldHaveSingleItem();
        alert.Holds.ShouldHaveSingleItem().Flight.ShouldBe("TIA2");
        alerts.HoldOf("TIA1").ShouldBeNull();
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == FailureAlerts.AlertKind && c.Bin == CardBin.Ring);
        made.ShouldHaveSingleItem();
        _github.IssuesCreated.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALaterAlertDoesNotHoldAFlightWhoseLegCheckFailureTheFinishPassed()
    {
        var flight = await Flying();
        var (investigations, _) = Investigating();
        var alerts = new FailureAlerts(_state, _github, investigations, TimeProvider.System);
        _legs.Alerts = alerts;
        _github.Issues[3] = "Something else entirely";
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();
        alerts.Open().ShouldBeEmpty();
        OtherFlight("TIA2", flight);
        OtherFlight("TIA3", flight);

        // Two other flights' PRs then hit the same failure in CI: the alert opens, and holds them, not TIA1 whose leg passed.
        foreach (var (pr, code) in new[] { (2, "TIA2"), (3, "TIA3") })
        {
            _github.FailedRuns[(pr, false)] = new FailedRun(600 + pr, $"https://github.com/x/tianwen/actions/runs/{600 + pr}", ["build"], Failing("Pilot.Tests.BarTests.Breaks"));
            await alerts.LookAtRunAsync(new FailureSite(flight.Repo, flight.RepoPath, pr, code, new string((char)('a' + pr), 40), FailureWhere.Ci), mergeGroup: false, Ct);
        }

        alerts.HoldOf("TIA1").ShouldBeNull();
        alerts.Open().ShouldHaveSingleItem().Holds.Select(h => h.Flight).ShouldNotContain("TIA1");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFirstUntouchedFailureOnTheFinishRaisesNoAlert()
    {
        await Flying();
        var (investigations, _) = Investigating();
        var alerts = new FailureAlerts(_state, _github, investigations, TimeProvider.System);
        _legs.Alerts = alerts;
        _github.Issues[3] = "Something else entirely";
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();

        alerts.Open().ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == FailureAlerts.AlertKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnIssueAdoptedAsTheInvestigationThatAFlightFliesAlreadyGetsNoSecondFlight()
    {
        var flight = await Flying();
        var (investigations, made) = Investigating();
        _github.Issues[6] = UnrelatedFailures.IssueTitle("Pilot.Tests.BarTests.Breaks", "TIA0");
        // A flight flies #6 already: it is the investigation, and create-flight (which would make a second beside it) is not asked.
        _state.PutFlight(flight with { Code = "TIA6", Issue = 6, Branch = "flight/tia6", PullRequest = null, PullRequestUrl = null });
        _github.FailedRuns[(77, false)] = new FailedRun(555, "https://github.com/x/tianwen/actions/runs/555", ["build"], Failing("Pilot.Tests.BarTests.Breaks"));

        var look = await investigations.LookAtRunAsync(new FailureSite(flight.Repo, flight.RepoPath, 77, "TIA77", new string('c', 40), FailureWhere.Ci), mergeGroup: false, Ct);

        var finding = look.Untouched.ShouldHaveSingleItem();
        (finding.Issue, finding.Investigation).ShouldBe((6, "TIA6"));
        made.ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Kind == FailureInvestigations.CardKind && c.Title.Contains("TIA6 investigates"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnIssueThatNamesOnlyTheClassIsNoInvestigationOnTheFinishSoANewOneIsFiled()
    {
        await Flying();
        var (_, made) = Investigating();
        // Without investigations #752 listed this issue for the class; with them the first occurrence is filed as its own investigation.
        _github.Issues[5] = "BarTests is slow on a loaded machine";
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();

        var filed = _github.IssuesCreated.ShouldHaveSingleItem();
        filed.Title.ShouldContain("cause unknown");
        // Its own investigation (#6), not the class-only issue #5 that #752 alone would have listed.
        L("parser").Report.ShouldBe("parsed\nnot this flight's: BarTests.Breaks (#6)");
        made.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUntouchedClassWithNoIssueThatPassesOnTheBaseFailsTheLeg()
    {
        await Flying();
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(0, "all green"));
        _checks.Script.Enqueue(new CheckRun(0, "all green on the base"));

        var finish = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed");

        finish.Ok.ShouldBeFalse();
        finish.Error.ShouldNotBeNull();
        finish.Error.ShouldContain("not shown failing on the base");
        L("parser").State.ShouldBe(LegState.Flying);
        _checks.Runs.Count.ShouldBe(3);
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailingInvariantGuardTheDiffDoesNotTouchFailsTheLeg()
    {
        var flight = await Flying();
        // The plane's CardKindTests is an invariant guard: a card kind the change forgot to list breaks it without touching it.
        var tests = Path.Combine(flight.Worktree, "src", "Pilot.Node.Tests");
        Directory.CreateDirectory(tests);
        File.WriteAllText(Path.Combine(tests, "CardKindTests.cs"), """
            public sealed class CardKindTests
            {
                [Fact]
                [Trait("Category", "Invariant")]
                public void EveryKindIsListed() { }
            }
            """);
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "the guard");
        _github.Issues[5] = "CardKindTests.EveryKindIsListed fails on a loaded machine";
        _checks.Next = new CheckRun(1, Failing("Pilot.Node.Tests.CardKindTests.EveryKindIsListed"));

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeFalse();

        L("parser").State.ShouldBe(LegState.Flying);
        _checks.Runs.Count.ShouldBe(1, "an invariant guard's failure is the flight's at once: no rerun without it");
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailureInAClassTheDiffTouchesFailsTheLeg()
    {
        await Flying();
        _checks.Next = new CheckRun(1, Failing("Pilot.Tests.FooTests.Works"));

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeFalse();

        L("parser").State.ShouldBe(LegState.Flying);
        _checks.Runs.Count.ShouldBe(1);
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightsFailureFoundOnTheRerunFailsTheLeg()
    {
        await Flying();
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.BarTests.Breaks")));
        _checks.Script.Enqueue(new CheckRun(1, Failing("Pilot.Tests.FooTests.Works")));

        var finish = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed");

        finish.Ok.ShouldBeFalse();
        finish.Error.ShouldNotBeNull();
        finish.Error.ShouldContain("FooTests.Works");
        L("parser").State.ShouldBe(LegState.Flying);
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("build")]
    [InlineData("timeout")]
    [InlineData("nothing")]
    public async Task ABuildErrorATimeoutOrNoParsedTestFailsTheLeg(string kind)
    {
        await Flying();
        _checks.Next = kind switch
        {
            "build" => new CheckRun(1, "src/Bar.cs(3,1): error CS1002: ; expected [X.csproj]\n" + Failing("Pilot.Tests.BarTests.Breaks")),
            "timeout" => new CheckRun(-1, "the check was stopped at 10 min of running: its limit\n" + Failing("Pilot.Tests.BarTests.Breaks")) { StoppedByBudget = true },
            _ => new CheckRun(1, "Test Run Aborted.\nThe active test run was aborted."),
        };

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeFalse();

        L("parser").State.ShouldBe(LegState.Flying);
        _checks.Runs.Count.ShouldBe(1);
        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    public void TheFilterNamesTheFlightsClassesAndTheExcludingRerunLeavesTheOthersOut()
    {
        var check = "cd src && dotnet build X.slnx -c Release && dotnet test T -c Release --no-build --filter '(FullyQualifiedName~UnrelatedFailureTests|FullyQualifiedName~LegFinish)|Category=Invariant'";
        var names = UnrelatedFailures.FilterNames(check);
        names.ShouldBe(["UnrelatedFailureTests", "LegFinish"]);
        var classes = UnrelatedFailures.ClassesOf(["src/Pilot.Node/Flights/LegCommander.Finish.cs", "src/Pilot.Node.Tests/BarTests.cs", "README.md"]);

        UnrelatedFailures.IsFlights("UnrelatedFailureTests.Works", classes, names).ShouldBeTrue();
        UnrelatedFailures.IsFlights("LegFinishingTests.Works", classes, names).ShouldBeTrue();
        UnrelatedFailures.IsFlights("LegCommanderTests.Works", classes, names).ShouldBeTrue();
        UnrelatedFailures.IsFlights("BarTests.Works", classes, names).ShouldBeTrue();
        UnrelatedFailures.IsFlights("EphemerisTests.Loads", classes, names).ShouldBeFalse();

        UnrelatedFailures.Excluding(check, ["EphemerisTests"]).ShouldBe(
            "cd src && dotnet build X.slnx -c Release && dotnet test T -c Release --no-build --filter '((FullyQualifiedName~UnrelatedFailureTests|FullyQualifiedName~LegFinish)|Category=Invariant)&FullyQualifiedName!~EphemerisTests'");
        UnrelatedFailures.Excluding("dotnet test T -c Release", ["A", "B"]).ShouldBe("dotnet test --filter 'FullyQualifiedName!~A&FullyQualifiedName!~B' T -c Release");
        UnrelatedFailures.Excluding("dotnet build X", ["A"]).ShouldBeNull();

        UnrelatedFailures.Only(check, "EphemerisTests.Loads").ShouldBe(
            "cd src && dotnet build X.slnx -c Release && dotnet test T -c Release --no-build --filter 'FullyQualifiedName~EphemerisTests.Loads'");
        UnrelatedFailures.Only("dotnet build X", "A.B").ShouldBeNull();
    }

    [Fact]
    public void ThisReposInvariantGuardsAreFoundAndTheirFailuresAreAlwaysTheFlights()
    {
        var src = AppContext.BaseDirectory;
        while (src is not null && !File.Exists(Path.Combine(src, "src", "Pilot.slnx")))
        {
            src = Path.GetDirectoryName(src);
        }
        src.ShouldNotBeNull();
        var invariant = UnrelatedFailures.InvariantClasses(Path.Combine(src, "src"));

        invariant.ShouldContain("CardKindTests");
        invariant.ShouldContain("SplitFileSizeTests");
        invariant.ShouldNotContain("InvariantTraitTests", "it names the guards but is not one");
        invariant.ShouldNotContain("InvariantChecks", "a doc comment quoting the trait is not one");
        invariant.ShouldNotContain("UnrelatedFailures");
        UnrelatedFailures.IsInvariant("CardKindTests.EveryKindIsListed", invariant).ShouldBeTrue();
        UnrelatedFailures.IsInvariant("EphemerisTests.Loads", invariant).ShouldBeFalse();
    }

    [Fact]
    public void AnIssueIsMatchedByTheTestThenByItsClass()
    {
        IssueInfo[] open =
        [
            new() { Number = 1, Title = "BarTestsExtra is slow" },
            new() { Number = 2, Title = "BarTests fails on ubuntu" },
            new() { Number = 3, Title = "BarTests.Breaks times out" },
        ];
        UnrelatedFailures.Matching(open, "BarTests.Breaks")!.Number.ShouldBe(3);
        UnrelatedFailures.Matching(open, "BarTests.Other")!.Number.ShouldBe(2);
        UnrelatedFailures.Matching(open, "BazTests.Other").ShouldBeNull();
        UnrelatedFailures.FailedTests("error CS1002: ; expected").ShouldBeNull();
        UnrelatedFailures.FailedTests(Failing("Ns.BarTests.Breaks(x: 1)")).ShouldBe(["BarTests.Breaks"]);
    }
}
