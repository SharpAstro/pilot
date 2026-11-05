using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class CommandKeyTests
{
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "commandkey-repo");

    [Fact]
    public void TheFilterTextCounts()
    {
        CommandKey.Of("dotnet test X -c Release --filter A", Repo).ShouldNotBe(CommandKey.Of("dotnet test X -c Release --filter B", Repo));
        CommandKey.Of("dotnet test X --filter A", Repo).ShouldBe(CommandKey.Of("dotnet test X --filter=A", Repo));
    }

    [Fact]
    public void APlaneAndItsCloneMatch()
    {
        var plane = Path.Combine(Repo, ".claude", "worktrees", "pil1");

        CommandKey.Of("dotnet test X", plane).Text.ShouldBe(CommandKey.Of("dotnet test X", Repo).Text);
    }

    [Fact]
    public void PlumbingIsIgnored()
    {
        CommandKey.Of("dotnet test X 2>&1 | tail -20; echo done", Repo).ShouldBe(CommandKey.Of("dotnet test X", Repo));
    }

    [Fact]
    public void EveryWorkStepCountsInOrder()
    {
        var chain = CommandKey.Of("dotnet build X && dotnet test X", Repo);

        chain.Steps.Count.ShouldBe(2);
        chain.ShouldNotBe(CommandKey.Of("dotnet test X", Repo));
        chain.Step(1).ShouldBe(CommandKey.Of("dotnet test X", Repo));
    }

    [Fact]
    public void ACdMovesTheFolderOfLaterStepsButIsNotAStep()
    {
        CommandKey.Of("cd src && dotnet build", Repo).Steps.Count.ShouldBe(1);
        CommandKey.Of("cd a && dotnet build", Repo).ShouldNotBe(CommandKey.Of("cd b && dotnet build", Repo));
    }
}
