using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A filtered dotnet test in a leg's check also runs the Invariant guards (PIL700, PIL715, PIL697: red in CI, green in the plane).</summary>
public sealed class InvariantChecksTests
{
    private const string Widened = "'(FullyQualifiedName~Foo)|Category=Invariant'";

    [Theory]
    [InlineData("dotnet test X.slnx --filter FullyQualifiedName~Foo", $"dotnet test X.slnx --filter {Widened}")]
    [InlineData("dotnet test X.slnx --filter 'FullyQualifiedName~Foo'", $"dotnet test X.slnx --filter {Widened}")]
    [InlineData("dotnet test X.slnx --filter \"FullyQualifiedName~Foo\"", $"dotnet test X.slnx --filter {Widened}")]
    [InlineData("dotnet test X.slnx --filter=FullyQualifiedName~Foo", $"dotnet test X.slnx --filter={Widened}")]
    [InlineData("dotnet test X.slnx --filter=\"FullyQualifiedName~Foo\" -c Release", $"dotnet test X.slnx --filter={Widened} -c Release")]
    public void EachFilterFormIsWidenedAndSingleQuoted(string check, string expected) => InvariantChecks.With(check).ShouldBe(expected);

    [Fact]
    public void EveryDotnetTestSegmentIsWidened()
    {
        InvariantChecks.With("cd src && dotnet test A -c Release --filter FullyQualifiedName~Foo && dotnet test B --filter 'Name~Bar'")
            .ShouldBe("cd src && dotnet test A -c Release --filter '(FullyQualifiedName~Foo)|Category=Invariant' && dotnet test B --filter '(Name~Bar)|Category=Invariant'");
    }

    [Theory]
    [InlineData("dotnet test X.slnx -c Release --no-build")]
    [InlineData("cd src && dotnet build X.slnx -c Release")]
    [InlineData("dotnet build X.slnx --filter Foo")]
    [InlineData("dotnet test X.slnx --filter 'Category=Invariant'")]
    [InlineData("dotnet test X.slnx --filter 'Name~Foo|Category=Invariant'")]
    [InlineData("dotnet test X.slnx --filter \"Name~it's\"")]
    [InlineData("echo --filter Foo")]
    public void ACheckWithNothingToWidenIsLeftAlone(string check) => InvariantChecks.With(check).ShouldBe(check);

    [Fact]
    public void WideningTwiceChangesNothing()
    {
        var once = InvariantChecks.With("dotnet test X.slnx --filter FullyQualifiedName~Foo");
        InvariantChecks.With(once).ShouldBe(once);
    }
}
