using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>flight-priority</c> (#570): writes the Priority field of the flight's issue and puts the level on its row; <c>none</c> clears it;
/// a repo without the field refuses, naming the <c>pilot: priority</c> label.
/// </summary>
/// <remarks>Slow: every test creates a real flight through git (5.8 s in a loaded fast step).</remarks>
[Trait("Category", "Slow")]
public sealed class PriorityCommandTests : LegCommanderTestsBase
{
    // The base builds its commanders without a GitHub client, so this one has the tracker the verb writes through.
    private readonly FlightCommander _tracked;

    public PriorityCommandTests() => _tracked = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System, _workers, _github);

    private async Task<CommandResult> Send(string verb, string target, params string[] args) =>
        await _tracked.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private async Task Flight(int issue) =>
        (await Send(Verbs.CreateFlight, Verbs.RepoTarget(Repo), issue.ToString(), "Work")).Ok.ShouldBeTrue();

    private FlightRow Row(int issue) => _state.Flights().Single(f => f.Code == $"TIA{issue}");

    [Theory]
    [InlineData("urgent", IssuePriority.Urgent)]
    [InlineData("High", IssuePriority.High)]
    [InlineData("medium", IssuePriority.Medium)]
    [InlineData("low", IssuePriority.Low)]
    public async Task TheVerbWritesTheFieldAndPutsTheLevelOnTheRow(string word, IssuePriority level)
    {
        _github.HasPriorityField = true;
        await Flight(1);

        var result = await Send(Verbs.FlightPriority, Verbs.FlightTarget("TIA1"), word);

        result.Ok.ShouldBeTrue(result.Error);
        _github.PrioritySets.ShouldBe([(1, (IssuePriority?)level)]);
        Row(1).IssuePriority.ShouldBe(level);
        Row(1).HasPriorityField.ShouldBeTrue();
    }

    [Fact]
    public async Task NoneClearsTheField()
    {
        _github.HasPriorityField = true;
        await Flight(1);
        (await Send(Verbs.FlightPriority, Verbs.FlightTarget("TIA1"), "urgent")).Ok.ShouldBeTrue();

        var result = await Send(Verbs.FlightPriority, Verbs.FlightTarget("TIA1"), "none");

        result.Ok.ShouldBeTrue(result.Error);
        _github.PrioritySets.Last().ShouldBe((1, (IssuePriority?)null));
        _github.IssuePriorities.ShouldNotContainKey(1);
        Row(1).IssuePriority.ShouldBeNull();
    }

    [Fact]
    public async Task ARepoWithoutTheFieldRefusesAndNamesTheLabel()
    {
        await Flight(1);

        var result = await Send(Verbs.FlightPriority, Verbs.FlightTarget("TIA1"), "high");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("pilot: priority");
        _github.PrioritySets.ShouldBeEmpty();
        Row(1).IssuePriority.ShouldBeNull();
    }

    [Fact]
    public async Task AWordThatIsNoLevelIsRefused()
    {
        _github.HasPriorityField = true;
        await Flight(1);

        var result = await Send(Verbs.FlightPriority, Verbs.FlightTarget("TIA1"), "soon");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("urgent, high, medium, low or none");
        _github.PrioritySets.ShouldBeEmpty();
    }

    [Fact]
    public async Task GitHubRefusingTheWriteLeavesTheRowAsItWas()
    {
        _github.HasPriorityField = true;
        _github.PrioritySetFails = "field is read-only";
        await Flight(1);

        var result = await Send(Verbs.FlightPriority, Verbs.FlightTarget("TIA1"), "high");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("field is read-only");
        Row(1).IssuePriority.ShouldBeNull();
    }
}
