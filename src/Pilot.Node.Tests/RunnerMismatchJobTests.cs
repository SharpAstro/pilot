using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node refuses a <c>run</c> of a <c>dotnet test</c> whose switches belong to the other runner than its folder selects (pilot #1132),
/// before any job is made, as <c>leg plan</c> refuses such a check.
/// </summary>
public sealed class RunnerMismatchJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly JobRunner _jobs;

    public RunnerMismatchJobTests()
    {
        Directory.CreateDirectory(_root);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System) { FreeMemory = () => null };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CommandResult Run(string command) =>
        _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.NodeTarget, Args = [_root, "0", command] });

    [Fact]
    public void APlatformSwitchInAVsTestFolderIsRefusedAndNoJobIsMade()
    {
        var result = Run("dotnet test X --filter-class Y");

        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("--filter-class");
        error.ShouldContain("VSTest");
        error.ShouldContain("--filter 'FullyQualifiedName~");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void AVsTestSwitchInAPlatformFolderIsRefusedAndNoJobIsMade()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

        var result = Run("dotnet test X --filter FullyQualifiedName~Y");

        result.Error.ShouldNotBeNull().ShouldContain("--filter-class");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public void ARunWithTheFoldersOwnSwitchesIsAccepted()
    {
        Run("dotnet test X --filter FullyQualifiedName~Y").Ok.ShouldBeTrue();
    }
}
