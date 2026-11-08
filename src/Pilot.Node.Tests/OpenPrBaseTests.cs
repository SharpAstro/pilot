using Pilot.Contracts;
using Pilot.Node;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Opening a flight's PR brings the branch up to date one way whoever asks (the node after the last leg, a card's Open its PR,
/// pilot flight pr): the base is merged in and the branch pushed plain, even where the queue rebases; a conflict sends the last
/// leg round by the node, once for each head of the base, not a ring that the flight could not open its PR by itself (PIL446, PIL438).
/// </summary>
[Trait("Category", "Slow")]
public sealed class OpenPrBaseTests : ReviewTestsBase
{
    /// <summary>A flight whose branch is pushed, then main moves on by <paramref name="mainFile"/> holding <paramref name="mainText"/>.</summary>
    private async Task<(FlightRow Flight, string Pushed)> PushedFlightWhoseBaseMoved(string mainFile, string mainText)
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "clash.txt"), "mine\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "clash, mine");
        Git(flight.Worktree, "push", "-q", "-u", "origin", flight.Branch);
        var pushed = Git(flight.Worktree, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Repo, mainFile), mainText);
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "main moves");
        Git(Repo, "push", "-q", "origin", "main");
        return (flight, pushed);
    }

    private string Remote => Path.Combine(_root, "origin.git");

    [Theory]
    [InlineData("node")]
    [InlineData("alice")]
    public async Task OpeningWhereTheBaseMovedMergesItInAndPushesPlainWhoeverAsks(string who)
    {
        var (flight, pushed) = await PushedFlightWhoseBaseMoved("moved.txt", "moved\n");

        var result = await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), who);

        result.Error.ShouldBeNull();
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        Git(Remote, "rev-parse", flight.Branch).ShouldBe(head);
        // A merge of the base, never a rewrite: what was pushed before is still in the pushed branch's history.
        Git(Remote, "merge-base", "--is-ancestor", pushed, flight.Branch).ShouldBe("");
        Git(Remote, "merge-base", "--is-ancestor", "main", flight.Branch).ShouldBe("");
        Git(flight.Worktree, "rev-parse", "HEAD^2").ShouldNotBeNullOrEmpty();
    }

    /// <summary>The last leg is flying again, told the conflict, nothing rings, and nothing was rewritten or half merged.</summary>
    private void ShouldBeSentRound(FlightRow flight, string pushed)
    {
        var open = _state.Cards(openOnly: true);
        open.ShouldNotContain(c => c.Bin == CardBin.Ring || c.Kind == LegCommander.NextStepKind);
        var leg = _state.Legs(flight.Code).Single();
        leg.State.ShouldBe(LegState.Flying);
        leg.ReviewGoArounds.ShouldBe(0);
        leg.Report.ShouldNotBeNull().ShouldContain(" conflicts (");
        Git(Remote, "rev-parse", flight.Branch).ShouldBe(pushed);
        Git(flight.Worktree, "status", "--porcelain").ShouldBe("");
        F(flight.Code).PullRequest.ShouldBeNull();
    }

    [Theory]
    [InlineData("node")]
    [InlineData("alice")]
    public async Task OpeningWhereTheBaseConflictsSendsTheLastLegRoundAndRaisesNoCard(string who)
    {
        var (flight, pushed) = await PushedFlightWhoseBaseMoved("clash.txt", "theirs\n");
        // The node's own sweep goes on by itself, as it does after the last leg.
        _legs.Models = new RoleModels(new NodeSettings());

        if (who == "node")
        {
            await _legs.AdvanceAllAsync(Ct);
        }
        else
        {
            var result = await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), who);
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull().ShouldContain(" conflicts (");
        }

        ShouldBeSentRound(flight, pushed);
    }

    [Fact]
    public async Task AConflictOnAPackageVersionPinLineIsSentRoundOnceWithTheHigherPinRule()
    {
        const string props = "Directory.Packages.props";
        static string Pins(string version) => $"<Project>\n  <PackageVersion Include=\"Foo\" Version=\"{version}\" />\n</Project>\n";
        File.WriteAllText(Path.Combine(Repo, props), Pins("1.0.0"));
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "pins");
        Git(Repo, "push", "-q", "origin", "main");
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, props), Pins("1.2.0"));
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "pin, mine");
        Git(flight.Worktree, "push", "-q", "-u", "origin", flight.Branch);
        var pushed = Git(flight.Worktree, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Repo, props), Pins("1.1.0"));
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "main pins lower");
        Git(Repo, "push", "-q", "origin", "main");
        _legs.Models = new RoleModels(new NodeSettings());

        await _legs.AdvanceAllAsync(Ct);

        ShouldBeSentRound(flight, pushed);
        var report = _state.Legs(flight.Code).Single().Report!;
        report.ShouldContain("PackageVersion");
        report.ShouldContain("higher of the two versions");
        var started = _workers.Started.Count;

        // The same base head is not sent again.
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.Count.ShouldBe(started);
        _state.Legs(flight.Code).Single().GoArounds.ShouldBe(1);
    }

    [Fact]
    public async Task TheNodesOwnOpenThatConflictedOpensThePrOnceTheWorkerResolvedItAndFinishedTheLeg()
    {
        var (flight, _) = await PushedFlightWhoseBaseMoved("clash.txt", "theirs\n");
        _legs.Models = new RoleModels(new NodeSettings());
        await _legs.AdvanceAllAsync(Ct);
        var worker = _state.Legs(flight.Code).Single().Worker!;

        // Resolved in the plane as the worker does: the base merged in, the conflict settled, committed.
        Git(flight.Worktree, "fetch", "-q", "origin");
        Should.Throw<InvalidOperationException>(() => Git(flight.Worktree, "merge", "--no-edit", "origin/main"));
        File.WriteAllText(Path.Combine(flight.Worktree, "clash.txt"), "both\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--no-edit");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "resolved")).Error.ShouldBeNull();

        // The finish's own sweep may open it already; the sweep that follows does if not.
        await _legs.AdvanceAllAsync(Ct);

        F(flight.Code).PullRequest.ShouldNotBeNull();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.NextStepKind);
    }
}
