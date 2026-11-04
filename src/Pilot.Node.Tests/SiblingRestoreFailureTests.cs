using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A CS1704 or MSB3243 in a sibling clone's path is a stale restore left by another plane's build (pilot #703, SE1's output).</summary>
public sealed class SiblingRestoreFailureTests
{
    private const string Plane = @"C:\Users\SebastianGodelet\source\repos\sharpastro\pilot\.claude\worktrees\pil703";

    private const string Project = @"C:\Users\SebastianGodelet\source\repos\sharpastro\Fonts.Lib\src\SharpAstro.Fonts\SharpAstro.Fonts.csproj";

    private const string Cs1704 = @"C:\Users\SebastianGodelet\source\repos\sharpastro\Fonts.Lib\src\SharpAstro.Fonts\FontLoader.cs(12,1): error CS1704: An assembly with the same simple name 'SharpAstro.Fonts' has already been imported. [" + Project + "]";

    private const string Msb3243 = @"C:\Program Files\dotnet\sdk\10.0.302\Microsoft.Common.CurrentVersion.targets(2412,5): error MSB3243: No way to resolve conflict between ""FreeTypeSharp, Version=3.0.0.0"" and ""FreeTypeSharp, Version=3.0.1.0"". [" + Project + "]";

    [Fact]
    public void ACs1704InASiblingCloneNamesTheSiblingAndTheRestore()
    {
        var found = SiblingRestoreFailure.In($"Build FAILED.\n{Cs1704}\n    1 Error(s)", Plane).ShouldNotBeNull();

        found.Sibling.ShouldBe("Fonts.Lib");
        found.Project.ShouldBe(Project);
        found.Remedy.ShouldBe($"pilot run -- dotnet restore {Project}, then rerun the check");
        found.Line.ShouldBe(Cs1704);
    }

    [Fact]
    public void AnMsb3243NamingASiblingsProjectIsOneToo()
    {
        var found = SiblingRestoreFailure.In(Msb3243, Plane).ShouldNotBeNull();

        found.Sibling.ShouldBe("Fonts.Lib");
        found.Project.ShouldBe(Project);
    }

    [Fact]
    public void AProjectAssetsMismatchInASiblingsObjFolderIsOneToo()
    {
        var line = @"C:\Users\SebastianGodelet\source\repos\sharpastro\Fonts.Lib\src\SharpAstro.Fonts\obj\project.assets.json : error NETSDK1004: Assets file not found.";

        SiblingRestoreFailure.In(line, Plane).ShouldNotBeNull().Sibling.ShouldBe("Fonts.Lib");
    }

    [Fact]
    public void TheSameErrorInsideThePlanesOwnPathIsNotOne()
    {
        var inside = Plane + @"\src\Pilot.Gui\Gui.cs(3,1): error CS1704: An assembly with the same simple name 'X' has already been imported. [" + Plane + @"\src\Pilot.Gui\Pilot.Gui.csproj]";

        SiblingRestoreFailure.In(inside, Plane).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("error CS0103: The name 'x' does not exist in the current context")]
    public void OtherOutputIsNotOne(string? text) => SiblingRestoreFailure.In(text, Plane).ShouldBeNull();

    private sealed class Flown : LegHoldTestsBase
    {
        public string Plane => _state.Flights().Single().Worktree;

        public string SiblingLine => $"{Path.Combine(_root, "Fonts.Lib", "src", "SharpAstro.Fonts", "FontLoader.cs")}(12,1): error CS1704: An assembly with the same simple name 'SharpAstro.Fonts' has already been imported. [{Path.Combine(_root, "Fonts.Lib", "src", "SharpAstro.Fonts", "SharpAstro.Fonts.csproj")}]";

        public Task<CommandResult> Finish(params string[] args) => Leg(Verbs.FinishLeg, "flight/TIA1/parser", args);

        public Task Fly(string? check = null) => FlyingParser(check);

        public LegRow Parser => L("parser");

        public void Fail(string tail) => _checks.Next = new CheckRun(1, tail);

        public bool Rang => _state.Cards().Any(c => c.Kind == "leg-failed");

        public Task Tick() => Leg(Verbs.Check, "flight/TIA1/parser", "1");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedCheckWithOneKeepsTheLegFlyingWithTheRemedyInItsMessage()
    {
        using var t = new Flown();
        await t.Fly(check: "dotnet build");
        await t.Tick();
        t.Fail($"Build FAILED.\n{t.SiblingLine}\n    1 Error(s)");

        var result = await t.Finish("done", "all good");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldStartWith("This is a stale restore in the sibling clone Fonts.Lib");
        result.Error.ShouldContain("dotnet restore");
        t.Parser.State.ShouldBe(LegState.Flying);
        t.Parser.Report.ShouldNotBeNull().ShouldStartWith("This is a stale restore");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFirstFailedReportNamingOneIsRefusedWithTheRemedyAndNoCardAndASecondRings()
    {
        using var t = new Flown();
        await t.Fly();
        var report = $"Build FAILED.\n{t.SiblingLine}";

        var first = await t.Finish("failed", report);

        first.Ok.ShouldBeFalse();
        first.Error.ShouldNotBeNull().ShouldContain("pilot run -- dotnet restore");
        t.Parser.State.ShouldBe(LegState.Flying);
        t.Rang.ShouldBeFalse();

        var second = await t.Finish("failed", report);

        second.Ok.ShouldBeTrue();
        t.Parser.State.ShouldBe(LegState.Failed);
        t.Rang.ShouldBeTrue();
    }
}
