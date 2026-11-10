using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Standing orders (#782): kept by the node as notes with an id, listed, retired, and still there after a restart.</summary>
public sealed class StandingOrderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2o-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            TestDirs.DeleteTree(_folder);
        }
    }

    private static CommandRequest Command(string verb, string who, params string[] args) =>
        new() { Verb = verb, Target = Verbs.NodeTarget, Args = args, Who = who };

    [Fact]
    public void AnOrderIsKeptWithAnIdListedAndRetired()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.Execute(Command(Verbs.Order, "gui", "Hold", "TIA2000")).Made.ShouldBe("o1");
        state.Execute(Command(Verbs.Order, "gui", "No work in DIR.Lib")).Made.ShouldBe("o2");

        state.StandingOrders().Select(o => (o.Id, o.Text)).ShouldBe([("o1", "Hold TIA2000"), ("o2", "No work in DIR.Lib")]);
        state.Execute(Command(Verbs.Orders, "gui")).Made.ShouldBe("o1  Hold TIA2000\no2  No work in DIR.Lib");

        state.Execute(Command(Verbs.OrderDone, "gui", "o1")).Ok.ShouldBeTrue();
        state.StandingOrders().Select(o => o.Id).ShouldBe(["o2"]);
        state.Execute(Command(Verbs.OrderDone, "gui", "o1")).Error.ShouldNotBeNull().ShouldContain("o1");
        state.Execute(Command(Verbs.OrderDone, "gui", "o9")).Ok.ShouldBeFalse();
        state.Execute(Command(Verbs.Order, "gui")).Ok.ShouldBeFalse();
        // A retired id is never handed out again.
        state.Execute(Command(Verbs.Order, "gui", "next")).Made.ShouldBe("o3");
    }

    [Fact]
    public void OrdersSurviveARestartOverTheSameJournalAndARetiredOneStaysGone()
    {
        using (var journal = new FileJournal(_folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            state.Execute(Command(Verbs.Order, "gui", "Hold TIA2000"));
            state.Execute(Command(Verbs.Order, "gui", "Scope: pilot only"));
            state.Execute(Command(Verbs.OrderDone, "gui", "o1"));
        }

        using var again = new FileJournal(_folder);
        var restarted = new NodeState(again, TimeProvider.System);
        restarted.StandingOrders().ShouldHaveSingleItem().ShouldSatisfyAllConditions(o => o.Id.ShouldBe("o2"), o => o.Text.ShouldBe("Scope: pilot only"));
        Pilot.Node.Flights.Briefs.Watchtower("wt", [], [], [], "pilot", orders: restarted.StandingOrders()).ShouldSatisfyAllConditions(
            b => b.ShouldContain("Scope: pilot only"), b => b.ShouldNotContain("Hold TIA2000"));
    }

    [Fact]
    public void AFlightSessionMayNotGiveOrRetireTheControllersOrders()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var order = Command(Verbs.Order, "du1-tia9-parser", "Hold TIA2000");
        var done = Command(Verbs.OrderDone, "du1-tia9-parser", "o1");
        var session = new Standing(StandingKind.FlightSession, "TIA9");

        session.Refusal(order, state).ShouldNotBeNull();
        session.Refusal(done, state).ShouldNotBeNull();
        Standing.Person.Refusal(order, state).ShouldBeNull();
        Standing.Person.Refusal(done, state).ShouldBeNull();
        new Standing(StandingKind.Lookout).Refusal(order, state).ShouldBeNull();
    }

    [Fact]
    public void TheBriefCapsOneOrderAndTheTotalListedAndNamesWhoGaveIt()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.Execute(Command(Verbs.Order, "gui", new string('x', 5000)));
        for (var i = 0; i < 30; i++)
        {
            state.Execute(Command(Verbs.Order, "gui", "Hold " + i));
        }

        var brief = Pilot.Node.Flights.Briefs.Watchtower("wt", [], [], [], "pilot", orders: state.StandingOrders());

        // The short brief (PIL855) keeps an order to MaxOrderText characters, so ten of them still fit its 6,000.
        var cap = Pilot.Node.Flights.Briefs.MaxOrderText;
        brief.ShouldContain("o1 (from gui): " + new string('x', cap) + "...");
        brief.ShouldNotContain(new string('x', cap + 1));
        brief.ShouldContain("more orders stand than fit here");
    }

    [Fact]
    public async Task TheLookoutAndThePersonMayBothGiveAndRetireOrders()
    {
        await using var node = await NodeHarness.StartAsync(TestContext.Current.CancellationToken);
        var lookout = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        var added = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Order, Target = Verbs.NodeTarget, Args = ["Hold TIA2000"], Who = lookout, Agent = true }, TestContext.Current.CancellationToken)).Response.ShouldNotBeNull();
        added.Ok.ShouldBeTrue(added.Error);
        added.Made.ShouldBe("o1");
        var done = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.OrderDone, Target = Verbs.NodeTarget, Args = ["o1"], Who = "gui" }, TestContext.Current.CancellationToken)).Response.ShouldNotBeNull();
        done.Ok.ShouldBeTrue(done.Error);
        node.State.StandingOrders().ShouldBeEmpty();
    }
}
