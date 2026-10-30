using Pilot.Contracts;
using Pilot.Node;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Opening a flight's PR brings the branch up to date one way whoever asks (the node after the last leg, a card's Open its PR,
/// pilot flight pr): the base is merged in and the branch pushed plain, even where the queue rebases; a conflict is the
/// watchtower's pr-conflicts card, not a ring that the flight could not open its PR by itself (PIL446, TIA1877 and TIA1894, DE1 2026-10-29).
/// </summary>
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

    [Theory]
    [InlineData("node")]
    [InlineData("alice")]
    public async Task OpeningWhereTheBaseConflictsRaisesThePrConflictsCardAndNoNextStepRing(string who)
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

        var open = _state.Cards(openOnly: true);
        var card = open.Where(c => c.Kind == "pr-conflicts").ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.Ring, Verbs.FlightTarget(flight.Code)));
        open.ShouldNotContain(c => c.Kind == LegCommander.NextStepKind);
        // Nothing was rewritten or half merged: the remote holds what it did, and the plane is clean.
        Git(Remote, "rev-parse", flight.Branch).ShouldBe(pushed);
        Git(flight.Worktree, "status", "--porcelain").ShouldBe("");
        F(flight.Code).PullRequest.ShouldBeNull();
    }

    private FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);
}
