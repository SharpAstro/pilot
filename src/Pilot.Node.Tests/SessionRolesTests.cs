using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What a session's name says it is for (PIL1008): a fixer, a PR reviewer, a flight's reviewer, planner and worker, or a person's own.</summary>
public sealed class SessionRolesTests
{
    [Fact]
    public void APrFixerIsAFixerWithNoFlightOrLeg()
    {
        var info = SessionRoles.Resolve("de1-fix-pilot-1000");

        (info.Role, info.Flight, info.Leg).ShouldBe((SessionRole.Fixer, null, null));
        SessionRoles.Resolve("de1-fix-some.repo-name-12").Role.ShouldBe(SessionRole.Fixer);
        SpendRoles.Of("de1-fix-pilot-1000", SessionRole.Fixer).ShouldBe("fixer");
        SpendRoles.Of("de1-fix-pilot-1000", SessionRole.AdHoc).ShouldBe("fixer");
    }

    [Fact]
    public void APrReviewerStaysAReviewerWithNoFlight()
    {
        var info = SessionRoles.Resolve("de1-review-pilot-7");

        (info.Role, info.Flight, info.Leg).ShouldBe((SessionRole.Reviewer, null, null));
    }

    [Fact]
    public void AFlightsReviewerPlannerAndWorkerKeepTheirFlight()
    {
        var reviewer = SessionRoles.Resolve("de1-pil1008-review");
        var planner = SessionRoles.Resolve("de1-pil1008-plan");
        var worker = SessionRoles.Resolve("de1-pil1008-fixer-role");

        (reviewer.Role, reviewer.Flight, reviewer.Leg).ShouldBe((SessionRole.Reviewer, "PIL1008", null));
        (planner.Role, planner.Flight, planner.Leg).ShouldBe((SessionRole.Planner, "PIL1008", null));
        (worker.Role, worker.Flight, worker.Leg).ShouldBe((SessionRole.Worker, "PIL1008", "fixer-role"));
    }

    [Theory]
    [InlineData("fix the build")]
    [InlineData("de1-fix-pilot")]
    [InlineData("seb-fixing-pilot-3")]
    [InlineData(null)]
    public void APersonsOwnNameStaysAdHoc(string? name) => SessionRoles.Resolve(name).Role.ShouldBe(SessionRole.AdHoc);
}
