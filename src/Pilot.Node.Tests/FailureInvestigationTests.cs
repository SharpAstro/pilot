using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A failure in a test the PR does not touch is filed at once, from its first occurrence, as an investigation (PIL716): an issue of
/// priority high carrying the run, the test, its assertion message and the PRs it hit, a flight for it, and a For-you card. One
/// investigation per signature; a later occurrence comments on that issue. Nothing calls it chance. Run against the in-memory node
/// state and the fake GitHub; the live node's own run is the controller's.
/// </summary>
public sealed class FailureInvestigationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Test = "Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing";
    private const string Message = "(503, No answer within 10s) should be (409,";

    private static string Log(string test = Test) => $"""
        [xUnit.net 00:00:41.52]     {test} [FAIL]
        [xUnit.net 00:00:41.52]       Shouldly.ShouldAssertException : {Message} "a refused start says why")
          Failed {test} [10 s]
          Error Message:
           Shouldly.ShouldAssertException : {Message} "a refused start says why")
          Stack Trace:
             at {test}() in /_/src/Pilot.Node.Tests/NodePlanetaryTests.cs:line 88
        """;

    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly List<CommandRequest> _flights = [];
    private readonly FailureInvestigations _failures;

    public FailureInvestigationTests()
    {
        _failures = new FailureInvestigations(_state, _github, new FailureLedger(), TimeProvider.System)
        {
            CreateFlight = (command, _) =>
            {
                _flights.Add(command);
                return Task.FromResult(new CommandResult { Seq = 0, Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = true, Made = $"TIA{900 + _flights.Count}" });
            },
        };
    }

    private PrChecks Checks() => new(_state, _github, TimeProvider.System) { Investigations = _failures };

    private void Flight(string code, int pr, string[]? armed = null)
    {
        _state.PutFlight(new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = int.Parse(code[3..], System.Globalization.CultureInfo.InvariantCulture), Branch = $"flight/{code.ToLowerInvariant()}",
            Worktree = "/r/plane", Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
            PullRequest = pr, PullRequestUrl = $"https://github.com/x/tianwen/pull/{pr}", MergeWhenGreen = armed,
        });
        _state.PutLeg(new LegRow { Flight = code, Name = "a", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
    }

    private static string Head(char c) => new(c, 40);

    private void FailedRun(int pr, bool queue, long id, string? log = null) =>
        _github.FailedRuns[(pr, queue)] = new FailedRun(id, $"https://github.com/x/tianwen/actions/runs/{id}", ["build"], log ?? Log());

    private void QueueRemoval(string head, string reason = "failed checks") =>
        _github.Queue = new MergeQueueState("OPEN", head, Queued: false, RemovedAt: DateTimeOffset.UtcNow.AddSeconds(-5), RemovedReason: reason);

    private (string Repo, string Title, string? Body) OneIssue() => _github.IssuesCreated.ShouldHaveSingleItem();

    private void OneInvestigation(string pr, long run)
    {
        var (repo, title, body) = OneIssue();
        repo.ShouldBe("/r/tianwen");
        title.ShouldContain("NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing");
        title.ShouldContain("cause unknown");
        title.ShouldNotContain("intermittent");
        body.ShouldNotBeNull().ShouldContain($"run {run}");
        body.ShouldContain(Test);
        body.ShouldContain(Message);
        body.ShouldContain(pr);
        _github.PrioritySets.ShouldHaveSingleItem().ShouldBe((1, IssuePriority.High));
        var flight = _flights.ShouldHaveSingleItem();
        (flight.Verb, flight.Target, flight.Args![0]).ShouldBe((Verbs.CreateFlight, "repo//r/tianwen", "1"));
        var card = _state.Cards().Where(c => c.Kind == FailureInvestigations.CardKind).ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "flight/TIA901"));
        card.Title.ShouldContain("NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing");
        card.Title.ShouldContain("TIA901");
        card.Detail.ShouldNotBeNull().ShouldContain($"run {run}");
        card.Detail.ShouldContain(Message);
    }

    [Fact]
    public async Task AFirstFailureInATestThePrDoesNotTouchIsFiledAsOneInvestigationWithItsFlightAndCard()
    {
        // The merge queue took the PR out on its queue run's failed test: the whole investigation, from the first occurrence.
        Flight("TIA71", 71);
        _github.PrFiles[71] = ["src/Pilot.Node/Flights/PrChecks.cs"];
        FailedRun(71, queue: true, id: 5001);
        QueueRemoval(Head('a'));

        await Checks().LookAsync(Ct);

        OneInvestigation("tianwen#71 (TIA71)", 5001);
        _state.Cards().ShouldContain(c => c.Kind == FailureInvestigations.CardKind && c.Bin == CardBin.ForYou);
        var record = _failures.Ledger.All().ShouldHaveSingleItem();
        (record.Where, record.Pr, record.Flight, record.RunId, record.Test, record.Investigation, record.Issue).ShouldBe((FailureWhere.Queue, 71, "TIA71", 5001L, Test, "TIA901", 1));

        // The next looks add nothing: the removal is read once.
        await Checks().LookAsync(Ct);
        _github.IssuesCreated.Count.ShouldBe(1);
        _flights.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFailureInAPrsOwnChecksIsFiledTheSameWay()
    {
        Flight("TIA72", 72, armed: [Head('b'), "squash"]);
        _github.PrFiles[72] = ["README.md"];
        _github.Pr = new PullRequest(72, "OPEN", Head('b'), "https://github.com/x/tianwen/pull/72");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 1, 2, "build", "https://github.com/x/tianwen/actions/runs/5002");
        FailedRun(72, queue: false, id: 5002);

        await Checks().LookAsync(Ct);

        OneInvestigation("tianwen#72 (TIA72)", 5002);
        _failures.Ledger.All().ShouldHaveSingleItem().Where.ShouldBe(FailureWhere.Ci);
    }

    [Fact]
    public async Task AFailureInALegsCheckIsFiledTheSameWay()
    {
        // Before the flight has a PR: the changed files are the plane's, given by the caller.
        var site = new FailureSite("tianwen", "/r/tianwen", null, "TIA73", Head('c'), FailureWhere.LegCheck);

        await _failures.LookAtCheckAsync(site, Log(), () => Task.FromResult<string[]>(["src/Pilot.Node/Flights/PrChecks.cs"]), Ct);

        var (_, _, body) = OneIssue();
        body.ShouldNotBeNull().ShouldContain(Test);
        body.ShouldContain(Message);
        body.ShouldContain("TIA73");
        _github.PrioritySets.ShouldHaveSingleItem().Level.ShouldBe(IssuePriority.High);
        _flights.ShouldHaveSingleItem();
        _state.Cards().Where(c => c.Kind == FailureInvestigations.CardKind).ShouldHaveSingleItem().Bin.ShouldBe(CardBin.ForYou);
        _failures.Ledger.All().ShouldHaveSingleItem().Where.ShouldBe(FailureWhere.LegCheck);
    }

    [Fact]
    public async Task ASecondOccurrenceOfTheSameSignatureInAnotherPrCommentsItsRunAndPrOnTheSameIssue()
    {
        Flight("TIA71", 71);
        _github.PrFiles[71] = ["a.cs"];
        _github.PrFiles[74] = ["b.cs"];
        FailedRun(71, queue: true, id: 5001);
        QueueRemoval(Head('a'));
        await Checks().LookAsync(Ct);
        _github.IssueComments.ShouldBeEmpty();

        // Another PR, another run, the same test with another timing in its message.
        Flight("TIA74", 74, armed: [Head('d'), "squash"]);
        _github.Queue = null;
        _github.Pr = new PullRequest(74, "OPEN", Head('d'), "https://github.com/x/tianwen/pull/74");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 1, 2, "build", null);
        FailedRun(74, queue: false, id: 5003, Log().Replace("10s", "12s"));
        await Checks().LookAsync(Ct);

        _github.IssuesCreated.Count.ShouldBe(1);
        _flights.Count.ShouldBe(1);
        _state.Cards().Count(c => c.Kind == FailureInvestigations.CardKind).ShouldBe(1);
        var (number, comment) = _github.IssueComments.ShouldHaveSingleItem();
        number.ShouldBe(1);
        comment.ShouldContain("run 5003");
        comment.ShouldContain("tianwen#74 (TIA74)");
        _failures.Ledger.All().Select(r => (r.Pr, r.Issue, r.Investigation)).ShouldBe([(71, 1, "TIA901"), (74, 1, "TIA901")]);
    }

    [Fact]
    public async Task AFailureInATestFileThePrChangesFilesNothing()
    {
        Flight("TIA75", 75);
        _github.PrFiles[75] = ["src/Pilot.Node.Tests/NodePlanetaryTests.cs"];
        FailedRun(75, queue: true, id: 5004);
        QueueRemoval(Head('e'));

        await Checks().LookAsync(Ct);

        _github.IssuesCreated.ShouldBeEmpty();
        _flights.ShouldBeEmpty();
        _failures.Ledger.All().ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == FailureInvestigations.CardKind);
    }

    [Fact]
    public async Task AFailureReadThatFailsFilesNothingAndStopsNoLook()
    {
        Flight("TIA76", 76);
        _github.PrFiles[76] = ["a.cs"];
        FailedRun(76, queue: true, id: 5005);
        _github.FailedRunFails = true;
        QueueRemoval(Head('f'));

        // The run cannot be read: nothing is filed, and the look still cards the removal as it did.
        await Checks().LookAsync(Ct);
        _github.IssuesCreated.ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Kind == PrChecks.QueueDropped);

        // The PR's files cannot be read: not known to be another's test, so nothing is filed.
        _github.FailedRunFails = false;
        _github.PrFilesFail.Add(76);
        await _failures.LookAtRunAsync(new FailureSite("tianwen", "/r/tianwen", 76, "TIA76", Head('f'), FailureWhere.Ci), mergeGroup: true, Ct);
        _github.IssuesCreated.ShouldBeEmpty();
        _flights.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedIssueFilesNothingAndIsTriedAgainAtTheNextLook()
    {
        _github.CreateIssueFails = "offline";
        var site = new FailureSite("tianwen", "/r/tianwen", 77, "TIA77", Head('g'), FailureWhere.Queue);
        FailedRun(77, queue: true, id: 5006);
        _github.PrFiles[77] = ["a.cs"];

        await _failures.LookAtRunAsync(site, mergeGroup: true, Ct);
        _failures.Ledger.All().ShouldBeEmpty();
        _flights.ShouldBeEmpty();

        _github.CreateIssueFails = null;
        await _failures.LookAtRunAsync(site, mergeGroup: true, Ct);
        _github.IssuesCreated.Count.ShouldBe(1);
        _flights.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ABuildTheOnesOwnPrBrokeIsNoInvestigation()
    {
        var site = new FailureSite("tianwen", "/r/tianwen", 78, "TIA78", Head('h'), FailureWhere.Ci);

        await _failures.LookAtCheckAsync(site, "src/A.cs(10,5): error CS0234: The namespace 'Diagnostics' does not exist", () => Task.FromResult<string[]>([]), Ct);

        _github.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailureWithNoTestInItIsFiledByItsNormalisedErrorLine()
    {
        var site = new FailureSite("tianwen", "/r/tianwen", 79, "TIA79", Head('i'), FailureWhere.Ci);
        var log = "##[error]fatal: unable to access 'https://github.com/x/y/': Could not resolve host after 3 tries";

        await _failures.LookAtCheckAsync(site, log, () => Task.FromResult<string[]>(["a.cs"]), Ct);
        await _failures.LookAtCheckAsync(site with { Pr = 80, Flight = "TIA80", Head = Head('j') }, log.Replace("3 tries", "9 tries"), () => Task.FromResult<string[]>(["b.cs"]), Ct);

        _github.IssuesCreated.Count.ShouldBe(1);
        _github.IssueComments.ShouldHaveSingleItem();
    }

    [Fact]
    public void TheLedgerIsKeptAcrossARestart()
    {
        var file = Path.Combine(Path.GetTempPath(), "failures-" + Guid.NewGuid().ToString("N")[..8], "failures.json");
        try
        {
            var record = new FailureRecord("tianwen", 71, "TIA71", Head('a'), FailureWhere.Queue, 5001, Test, "No answer within 10s", "test:" + Test, "TIA901", 1, DateTimeOffset.Parse("2026-11-05T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            new FailureLedger(file).Add(record);

            var again = new FailureLedger(file);
            again.All().ShouldBe([record]);
            again.InvestigationOf("TIANWEN", "test:" + Test).ShouldBe(record);
            again.Has("tianwen", 71, "TIA71", Head('a'), FailureWhere.Queue, "test:" + Test).ShouldBeTrue();
        }
        finally
        {
            TestDirs.DeleteTree(Path.GetDirectoryName(file)!);
        }
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void NoCardOrLineTheNodeWritesCallsAFailureAFlakeOrByChance()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Pilot.slnx")) && !File.Exists(Path.Combine(root, "src", "Pilot.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }
        root.ShouldNotBeNull();
        var node = Path.Combine(File.Exists(Path.Combine(root, "Pilot.slnx")) ? root : Path.Combine(root, "src"), "Pilot.Node");
        var wording = new Regex(@"\bflak(?:e|es|y|iness)\b|by chance", RegexOptions.IgnoreCase);
        var found = Directory.EnumerateFiles(node, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line.Trim())))
            // The words a person reads are in string literals; a comment may name the history of a test.
            .Where(l => !l.Text.StartsWith("//", StringComparison.Ordinal) && l.Text.Contains('"') && wording.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text}")
            .ToList();

        found.ShouldBeEmpty(string.Join('\n', found));
    }
}
