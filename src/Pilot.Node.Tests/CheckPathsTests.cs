using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A check runs in the plane's root, after any leading <c>cd</c>: <c>leg plan</c> refuses one whose <c>dotnet build|test|run|pack</c>
/// names a project, solution or folder that is not there, and says where it is (PIL319: <c>dotnet build Pilot.slnx</c> for a
/// solution in <c>src/</c> could never pass).
/// </summary>
public sealed class CheckPathsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2k-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), new ManualTime(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero)));
    private readonly LegCommander _legs;

    public CheckPathsTests()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, time);
        Directory.CreateDirectory(Plane);
        _state.PutFlight(new FlightRow
        {
            Code = "PIL1", Repo = "pilot", RepoPath = Path.Combine(_root, "pilot"), Issue = 1, Branch = "flight/pil1", Worktree = Plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private string Plane => Path.Combine(_root, "plane");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative)
    {
        var path = Path.Combine(Plane, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />\n");
    }

    /// <summary>This repo's own layout: the solution and the test project sit in src/.</summary>
    private void WriteSrcLayout()
    {
        Write("src/Pilot.slnx");
        Write("src/Pilot.Node/Pilot.Node.csproj");
        Write("src/Pilot.Node.Tests/Pilot.Node.Tests.csproj");
    }

    private Task<CommandResult> PlanLeg(string check, string? ciDiffers = null) =>
        _legs.RunAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = "flight/PIL1", Who = "tests",
            Args = ["name=check", "goal=Do the work", "done-when=Its check passes", $"check={check}", .. ciDiffers is null ? [] : new[] { $"ci-differs={ciDiffers}" }],
        }, Ct);

    [Fact]
    public void ASolutionThatSitsInSrcIsNotFoundFromTheRootAndTheRefusalSaysWhereItIsAndHowToWriteTheCheck()
    {
        WriteSrcLayout();

        var why = CheckPaths.Refusal("dotnet build Pilot.slnx -c Release", Plane).ShouldNotBeNull();

        why.ShouldContain("Pilot.slnx is src/Pilot.slnx");
        why.ShouldContain("not in the plane's root");
        why.ShouldContain("cd src && dotnet build Pilot.slnx -c Release");
    }

    [Fact]
    public void ACheckPlannedWithTheWrongPathsIsCorrectedToFindThemAndOneWithNothingWrongIsLeft()
    {
        WriteSrcLayout();

        CheckPaths.Corrected("dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build", Plane)
            .ShouldBe("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build");
        CheckPaths.Corrected("dotnet build -c Release", Plane).ShouldBe("cd src && dotnet build Pilot.slnx -c Release");
        CheckPaths.Corrected("cd src && dotnet build Pilot.slnx -c Release", Plane).ShouldBeNull();
        // Found nowhere: no rewrite to make by itself.
        CheckPaths.Corrected("dotnet build Missing.slnx", Plane).ShouldBeNull();
    }

    [Fact]
    public void TheSameCheckWithItsWorkingDirectoryIsTaken()
    {
        WriteSrcLayout();

        CheckPaths.Refusal("cd src && dotnet build Pilot.slnx -c Release", Plane).ShouldBeNull();
        CheckPaths.Refusal("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet build src/Pilot.slnx -c Release", Plane).ShouldBeNull();
    }

    [Fact]
    public void AProjectFolderIsHeldToTheFolderTheCheckHasCdTo()
    {
        WriteSrcLayout();

        var why = CheckPaths.Refusal("dotnet test Pilot.Node.Tests -c Release", Plane).ShouldNotBeNull();

        why.ShouldContain("Pilot.Node.Tests is src/Pilot.Node.Tests");
        why.ShouldContain("cd src && dotnet test Pilot.Node.Tests -c Release");
    }

    [Fact]
    public void ACdIntoAFolderThatIsNotThereIsRefusedNamingWhereTheFolderIs()
    {
        WriteSrcLayout();

        var why = CheckPaths.Refusal("cd Pilot.Node.Tests && dotnet test -c Release", Plane).ShouldNotBeNull();

        why.ShouldContain("src/Pilot.Node.Tests");
        why.ShouldContain("cd src/Pilot.Node.Tests");
    }

    [Fact]
    public void ABareDotnetTestWhereThereIsNoProjectOrSolutionIsRefusedNamingTheOnesNearestTheRoot()
    {
        WriteSrcLayout();

        var why = CheckPaths.Refusal("dotnet test -c Release", Plane).ShouldNotBeNull();

        why.ShouldContain("no project or solution to test");
        why.ShouldContain("src/Pilot.slnx");
        why.ShouldContain("cd src && dotnet test Pilot.slnx -c Release");
    }

    [Fact]
    public void ABareBuildInAnEmptyPlaneIsRefusedAndOneWhereAProjectSitsIsTaken()
    {
        CheckPaths.Refusal("dotnet build -c Release", Plane).ShouldNotBeNull().ShouldContain("holds no solution or project at all");

        Write("Pilot.csproj");

        CheckPaths.Refusal("dotnet build -c Release", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet test --filter 'A|B' -c Release", Plane).ShouldBeNull();
    }

    [Theory]
    [InlineData("dotnet run --project src/Missing/Missing.csproj")]
    [InlineData("dotnet run -p src/Missing/Missing.csproj")]
    [InlineData("dotnet test --project src/Missing/Missing.csproj")]
    [InlineData("dotnet build --project=src/Missing/Missing.csproj")]
    public void AProjectNamedByAnOptionIsCheckedToo(string check)
    {
        WriteSrcLayout();

        CheckPaths.Refusal(check, Plane).ShouldNotBeNull().ShouldContain("src/Missing/Missing.csproj");
    }

    [Fact]
    public void AProjectNamedByAnOptionThatIsThereIsTakenAndSoIsOneWithAMovedWorkingDirectory()
    {
        WriteSrcLayout();

        CheckPaths.Refusal("dotnet run --project src/Pilot.Node/Pilot.Node.csproj -c Release", Plane).ShouldBeNull();
        CheckPaths.Refusal("cd src && dotnet run -p Pilot.Node/Pilot.Node.csproj", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet run --project src/Pilot.Node/Pilot.Node.csproj -- --verbose x.csproj", Plane).ShouldBeNull();
    }

    [Fact]
    public void AFolderNamedByTheCheckThatHoldsNoProjectIsRefused()
    {
        WriteSrcLayout();
        Directory.CreateDirectory(Path.Combine(Plane, "docs"));

        CheckPaths.Refusal("dotnet build docs", Plane).ShouldNotBeNull().ShouldContain("docs");
    }

    [Fact]
    public void OptionValuesPropertiesAndAnythingDynamicAreNotTakenForPaths()
    {
        WriteSrcLayout();
        Write("Pilot.csproj");

        CheckPaths.Refusal("dotnet build -c Release --no-restore -p:Version=1.0 /p:Foo=Bar -v q", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet test --filter 'FullyQualifiedName~A|FullyQualifiedName~B' --logger trx -c Release", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet build -p Configuration=Release", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet build $env:SOLUTION", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet test src/*.Tests", Plane).ShouldBeNull();
        CheckPaths.Refusal("cd $dir && dotnet build Missing.slnx", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet publish Missing.slnx && git status && dotnet --version", Plane).ShouldBeNull();
        CheckPaths.Refusal("dotnet build --help", Plane).ShouldBeNull();
    }

    [Fact]
    public void ASeparatorInsideQuotesDoesNotSplitTheCheck()
    {
        Write("Pilot.csproj");

        CheckPaths.Refusal("dotnet test --filter 'A&&B;C' -c Release", Plane).ShouldBeNull();
    }

    [Fact]
    public void ThePlaneNeverLooksInGitBinObjOrNodeModules()
    {
        Write(".git/Hidden.slnx");
        Write("bin/Debug/Bin.slnx");
        Write("obj/Obj.slnx");
        Write("node_modules/pkg/Node.slnx");

        CheckPaths.Refusal("dotnet build Hidden.slnx", Plane).ShouldNotBeNull().ShouldContain("nowhere in the plane");
        CheckPaths.Refusal("dotnet build Bin.slnx", Plane).ShouldNotBeNull().ShouldContain("nowhere in the plane");
        CheckPaths.Refusal("dotnet build Node.slnx", Plane).ShouldNotBeNull().ShouldContain("nowhere in the plane");
    }

    [Fact]
    public void ACheckAboveTheFolderItHasCdToIsWrittenWithARelativePath()
    {
        WriteSrcLayout();
        Write("Tools.slnx");

        var why = CheckPaths.Refusal("cd src && dotnet build Tools.slnx", Plane).ShouldNotBeNull();

        why.ShouldContain("Tools.slnx is Tools.slnx");
        why.ShouldContain("dotnet build ../Tools.slnx");
    }

    [Fact]
    public void APlaneNotOnDiskIsNotLookedIn() =>
        CheckPaths.Refusal("dotnet build Missing.slnx", Path.Combine(_root, "nowhere")).ShouldBeNull();

    [Fact]
    public async Task LegPlanRefusesAMissingSolutionNamingItsRealPlaceAndPlansNothing()
    {
        WriteSrcLayout();

        var result = await PlanLeg("dotnet build Pilot.slnx -c Release");

        result.Error.ShouldNotBeNull().ShouldContain("src/Pilot.slnx");
        result.Error.ShouldContain("cd src && dotnet build Pilot.slnx -c Release");
        _state.Legs("PIL1").ShouldBeEmpty();
    }

    [Fact]
    public async Task CiDiffersDoesNotWaiveAMissingSolution()
    {
        WriteSrcLayout();

        var result = await PlanLeg("dotnet build Pilot.slnx -c Debug", "the GUI only builds Debug");

        result.Error.ShouldNotBeNull().ShouldContain("src/Pilot.slnx");
        _state.Legs("PIL1").ShouldBeEmpty();
    }

    [Fact]
    public async Task LegPlanTakesTheCheckWrittenWithItsWorkingDirectory()
    {
        WriteSrcLayout();

        (await PlanLeg("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build")).Ok.ShouldBeTrue();
        _state.Legs("PIL1").Single().Check.ShouldNotBeNull().ShouldStartWith("cd src && ");
    }

    [Fact]
    public async Task LegPlanRefusesABareDotnetTestInARootWithNoProjectOrSolution()
    {
        var result = await PlanLeg("dotnet test -c Release");

        result.Error.ShouldNotBeNull().ShouldContain("no project or solution");
        _state.Legs("PIL1").ShouldBeEmpty();
    }

    [Fact]
    public async Task LegPlanRefusesAProjectNamedByProjectThatIsNotThere()
    {
        WriteSrcLayout();

        var result = await PlanLeg("dotnet run --project src/Gui/Gui.csproj -c Release");

        result.Error.ShouldNotBeNull().ShouldContain("src/Gui/Gui.csproj");
    }

    [Fact]
    public void ThePlannersBriefTellsItToWriteChecksAsCiRunsThemWithTheirWorkingDirectory()
    {
        var flight = _state.Flights().Single();

        var brief = Briefs.Plan(flight, [], "pilot");

        brief.ShouldContain(Briefs.CheckWhereRule.Split('\n')[0]);
        brief.ShouldContain("including its working-directory");
        brief.ShouldContain("cd <dir> && dotnet build <solution> -c Release");
        brief.ShouldContain("refuses a check naming a project, solution or folder that is not where it runs");
    }
}
