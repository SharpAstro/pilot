using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The leg-failed card names what failed and what each button leads to, not the first words of the worker's report (pilot #844).
/// c78: PIL834/read-stamps stopped with "All 7 steps done and committed; filtered tests pass (115, run on ...", while the node's own
/// check failed building Pilot.Ui in files the leg never touched.
/// </summary>
public sealed class LegFailureTests : IDisposable
{
    private const string Branch = "flight/tia9";
    private const string Target = "flight/TIA9/parser";
    private const string Worker = "du1-tia9-parser";
    private const string Report = "All 7 steps done and committed; filtered tests pass (115, run on the plane)";
    private const string SendRound = "Send it round: a fresh worker fixes it and finishes";

    /// <summary>The c78 card's summary (PIL834/read-stamps) as the node raises it with the outside and the main notes; LegFailedCardTests draws the same text.</summary>
    internal const string C78Summary = "The worker of PIL834/read-stamps stopped and its check fails building Pilot.Ui. The flight changed none of the failing files or their projects. "
        + "main has changed those files since the flight took it (2 commits), so taking main may fix it.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = TestTemp.NewPath();
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    // The plane is clean and at origin/<branch>, so a passing check would let the node finish a failed leg itself and raise no card:
    // the check fails here, as it does in the flights this pins.
    private readonly FakeChecks _checks = new() { Next = new CheckRun(1, "the check fails") };
    private readonly LegCommander _legs;

    public LegFailureTests()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "-q", "--bare", "-b", "main", "remote.git");
        Git(_root, "clone", "-q", Remote, "plane");
        Git(Plane, "config", "user.email", "t@example.com");
        Git(Plane, "config", "user.name", "t");
        Write("src/Pilot.Ui/PilotWidgetBase.cs", "class PilotWidgetBase {}\n");
        Write("src/Pilot.Ui/Other.cs", "class Other {}\n");
        Commit(Plane, "first");
        Git(Plane, "push", "-q", "origin", "main");
        Git(Plane, "checkout", "-q", "-b", Branch);
        Write("src/Pilot.Node/Mine.cs", "class Mine {}\n");
        Commit(Plane, "mine");
        Git(Plane, "push", "-q", "-u", "origin", Branch);

        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), _checks, new FakeGitHub(), git, new Planes(git), flights, options, TimeProvider.System);
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = Plane, Issue = 9, Branch = Branch, Worktree = Plane, Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        _state.PutLeg(new LegRow
        {
            Flight = "TIA9", Name = "parser", Order = 1, Goal = "Parse", DoneWhen = "Tests pass", Check = "dotnet test", State = LegState.Flying, Worker = Worker,
        });
    }

    private string Remote => Path.Combine(_root, "remote.git");

    private string Plane => Path.Combine(_root, "plane");

    private string BuildTail => $"Build FAILED.\n{Plane}{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Pilot.Ui{Path.DirectorySeparatorChar}PilotWidgetBase.cs(48,26): error CS0108: 'PilotWidgetBase.Selected' hides inherited member 'PixelWidgetBase.Selected'. [{Plane}/src/Pilot.Ui/Pilot.Ui.csproj]\n    1 Error(s)";

    private Task<CommandResult> Run(string who, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = Target, Args = args, Who = who }, Ct);

    // What the refused finish leaves on the leg's report before the worker's own failed overwrites it.
    private void RecordCheckFailure(string tail, int code = 1) =>
        _state.PutLeg(_state.Legs("TIA9").Single() with { Report = $"check `dotnet test` failed ({code}):\n{tail}" });

    private async Task<CardRow> FailedCard(string report = Report)
    {
        (await Run(Worker, "failed", report)).Ok.ShouldBeTrue();
        return _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheC78CardNamesTheBuildThatFailedAndSaysItIsOutsideTheLegsCode()
    {
        // The real path: the done finish is refused and records the check failure, then the worker says it failed.
        _checks.Next = new CheckRun(1, BuildTail);
        (await Run(Worker, "done", Report)).Ok.ShouldBeFalse();
        _state.Legs("TIA9").Single().Report.ShouldNotBeNull().ShouldContain("error CS0108");

        var card = await FailedCard();

        card.Title.ShouldBe("TIA9/parser: its check fails building Pilot.Ui, in files this leg did not change");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([(SendRound, Verbs.GoAround), ("Accept as it is: the flight goes on with the check failing", Verbs.FinishLeg)]);
        card.Choices.ShouldNotContain(c => c.Label.Contains("All 7 steps") || c.Label.Contains("filtered tests"));
        card.Choices.Single(c => c.Verb == Verbs.GoAround).Args.ShouldBe([$"the last worker stopped: {Report}"]);
        card.Summary.ShouldNotBeNull().ShouldContain("its check fails building Pilot.Ui");
        card.Summary.ShouldBe("The worker of TIA9/parser stopped and its check fails building Pilot.Ui. The flight changed none of the failing files or their projects.");
        card.Title.ShouldNotContain("stopped");
        card.Detail.ShouldNotBeNull().ShouldContain(Report);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheRoundAndModelSuffixOfTheSendRoundButtonIsKept()
    {
        _legs.Models = new RoleModels(null);
        RecordCheckFailure(BuildTail);
        _state.PutLeg(_state.Legs("TIA9").Single() with { GoArounds = 1 });

        var card = await FailedCard();

        card.Choices.Single(c => c.Verb == Verbs.GoAround).Label.ShouldStartWith(SendRound + " (round 2");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightThatChangedTheFailingProjectIsNotCalledOutsideItsCode()
    {
        Write("src/Pilot.Ui/Other.cs", "class Other { int X; }\n");
        Commit(Plane, "touches Pilot.Ui");
        RecordCheckFailure(BuildTail);

        var card = await FailedCard();

        card.Title.ShouldBe("TIA9/parser: its check fails building Pilot.Ui");
        card.Summary.ShouldNotBeNull().ShouldNotContain("changed none of the failing files");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailingTestCheckNamesTheTestsAndTheClass()
    {
        RecordCheckFailure("  Failed Pilot.Node.Tests.ParserTests.Reads [11 ms]\n  Failed Pilot.Node.Tests.ParserTests.Writes [2 ms]\nFailed!  - Failed: 2, Passed: 5");

        var card = await FailedCard();

        card.Title.ShouldBe("TIA9/parser: its check fails 2 tests in ParserTests");
        card.Choices.Last().Label.ShouldBe("Accept as it is: the flight goes on with the check failing");
        card.Summary.ShouldNotBeNull().ShouldNotContain("changed none of the failing files");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReportWithNoCheckFailureFallsBackToItsClippedFirstLine()
    {
        var long_ = "the grammar cannot be parsed because the third production loops forever and the tokenizer never ends it";

        var card = await FailedCard(long_ + "\nmore");

        card.Title.ShouldBe($"TIA9/parser: {long_[..67].TrimEnd()}...");
        card.Choices.Select(c => c.Label).ShouldBe([SendRound, "Accept as it is: the flight goes on, the failure kept in its report"]);
        card.Choices.ShouldNotContain(c => c.Label.Contains("grammar"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WhenMainAlreadyChangedTheFailingFilesTheCardSaysTakingMainMayFixIt()
    {
        // Main fixes the file after the flight branched; the plane has fetched it but not merged it.
        Git(_root, "clone", "-q", Remote, "other");
        Git(Path.Combine(_root, "other"), "config", "user.email", "t@example.com");
        Git(Path.Combine(_root, "other"), "config", "user.name", "t");
        File.WriteAllText(Path.Combine(_root, "other", "src", "Pilot.Ui", "PilotWidgetBase.cs"), "class PilotWidgetBase { int Fixed; }\n");
        Commit(Path.Combine(_root, "other"), "Fix the widget base clash");
        Git(Path.Combine(_root, "other"), "push", "-q", "origin", "main");
        Git(Plane, "fetch", "-q", "origin");
        RecordCheckFailure(BuildTail);

        var card = await FailedCard();

        card.Summary.ShouldBe("The worker of TIA9/parser stopped and its check fails building Pilot.Ui. The flight changed none of the failing files or their projects. "
            + "main has changed those files since the flight took it (1 commit), so taking main may fix it.");
        // The panel draws a summary up to CardsView.SummaryChars (240) only: the c78 card, with a longer name and both notes, fits too.
        card.Summary.ShouldNotBeNull().Replace("TIA9/parser", "PIL834/read-stamps").Replace("(1 commit)", "(2 commits)").ShouldBe(C78Summary);
        C78Summary.Length.ShouldBeLessThanOrEqualTo(240);
        card.Detail.ShouldNotBeNull().ShouldContain("Fix the widget base clash");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithoutAChangeOnMainTheCardSaysNothingOfMain()
    {
        RecordCheckFailure(BuildTail);

        var card = await FailedCard();

        card.Summary.ShouldNotBeNull().ShouldNotContain("main has changed");
        card.Detail.ShouldNotBeNull().ShouldNotContain("main has changed");
    }

    private const string BothNotes = " The flight changed none of the failing files or their projects. main has changed those files since the flight took it (12 commits), so taking main may fix it.";

    [Fact]
    public void TheSummaryOfAnyFailureStaysWithinThePanelsCharacters()
    {
        // Not only the c78 case: a long leg name with many failing projects, and a long clipped report line, each with both notes.
        var manyProjects = string.Join(" and ", Enumerable.Range(1, 8).Select(i => $"Pilot.LongProjectName{i}"));
        var longReport = LegFailure.Clip(new string('x', 200));
        foreach (var (cause, checkFailed) in new[] { ($"its check fails building {manyProjects}", true), ($"its check fails 9 tests in {new string('C', 90)} and 4 more classes", true), (longReport, false), ("its check fails (exit 1)", true) })
        {
            foreach (var name in new[] { "PIL834/read-stamps", "PIL834/" + new string('l', 80) })
            {
                var summary = LegCommander.FailedSummary(name, cause, checkFailed, BothNotes);

                summary.Length.ShouldBeLessThanOrEqualTo(240, $"{name} / {cause}");
                // The notes about the files and main are the facts kept; the cause gives way first, then the end of a long name
                // (the card's title keeps the whole name), however long either is.
                summary.ShouldContain("main has changed those files", customMessage: $"{name} / {cause}");
                summary.ShouldStartWith(name.Length > 40 ? "The worker of PIL834/" : $"The worker of {name} stopped");
            }
        }
    }

    [Theory]
    [InlineData(38)]
    [InlineData(40)]
    [InlineData(45)]
    [InlineData(60)]
    public void ALegNameOfFortyCharactersOrMoreStillKeepsTheMainNote(int nameLength)
    {
        var name = "PIL834/" + new string('n', nameLength - 7);

        var summary = LegCommander.FailedSummary(name, "its check fails building Pilot.Ui and Pilot.Gui", true, BothNotes);

        summary.Length.ShouldBeLessThanOrEqualTo(240);
        summary.ShouldContain("so taking main may fix it.");
        // Both notes leave 53 characters for name and cause together (240 less the 159 of the notes and the 28 of "The worker of ... stopped and ... ."), so the cause is cut (to its start), not dropped.
        summary.ShouldContain("its check fails");
        summary.ShouldStartWith("The worker of PIL834/");
    }

    [Fact]
    public void ASummaryThatFitsIsLeftAlone()
    {
        LegCommander.FailedSummary("TIA9/parser", "its check fails (exit 1)", true, "").ShouldBe("The worker of TIA9/parser stopped and its check fails (exit 1).");
    }

    [Fact]
    public void ACauseTooLongForTheSummaryEndsInAnEllipsis()
    {
        var summary = LegCommander.FailedSummary("PIL834/read-stamps", "its check fails building " + new string('P', 300), true, BothNotes);

        summary.Length.ShouldBeLessThanOrEqualTo(240);
        summary.ShouldContain("...");
    }

    [Fact]
    public void AnyOtherFailedCheckGivesItsExitCode()
    {
        LegFailure.Read("check `dotnet test` failed (137):\nKilled", Report).Cause.ShouldBe("its check fails (exit 137)");
    }

    [Fact]
    public void AReportWithNothingInItSaysNoReasonWasGiven()
    {
        LegFailure.Read(null, "").Cause.ShouldBe("no reason given");
    }

    [Fact]
    public void AReportOfTheLegItselfIsNotTakenForAFailedCheck()
    {
        // A fresh round's report starts "go-around: ..."; a check failure of an earlier round is not read.
        LegFailure.Read("go-around: the check failed", "stuck").Cause.ShouldBe("stuck");
    }

    [Fact]
    public void BuildErrorFilesAreReadRepoRelativeAndOnceEach()
    {
        var read = LegFailure.Read($"check `x` failed (1):\nC:\\w\\pil\\src\\Pilot.Ui\\A.cs(1,2): error CS1: a\nC:\\w\\pil\\src\\Pilot.Ui\\A.cs(1,2): error CS1: a [C:\\w\\pil\\src\\Pilot.Ui\\Pilot.Ui.csproj]\nC:\\w\\pil\\src\\Pilot.Gui\\B.cs(3,4): error CS2: b", "r", "C:\\w\\pil");

        read.Files.ShouldBe(["src/Pilot.Ui/A.cs", "src/Pilot.Gui/B.cs"]);
        read.Projects.ShouldBe(["src/Pilot.Ui", "src/Pilot.Gui"]);
        read.Cause.ShouldBe("its check fails building Pilot.Ui and Pilot.Gui");
    }

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Plane, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Commit(string cwd, string message)
    {
        Git(cwd, "add", ".");
        Git(cwd, "commit", "-q", "-m", message);
    }

    private static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
    }
}
