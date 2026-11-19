using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The idea's flight and its designs (PIL1185): the same declared notice as the flight's page, "DSN-3 is now v4; this flight builds v3" with Re-plan on v4.
internal sealed partial class IdeasView<TSurface>
{
    // One notice a design of the idea's flight that has a newer version; none for an idea with no flight, or one that has landed.
    private IEnumerable<Layout.Node> DesignNotices(IdeaRow idea)
    {
        if (IdeaIds.FlightOf(_screen.Flights, _screen.Moves, idea.Repo, idea.Number) is not { } code
            || _screen.Flights.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } flight)
        {
            yield break;
        }
        foreach (var newer in DesignVersionNotice.For(flight, Designs))
        {
            var n = newer;
            Layout.Node? button = Send is null
                ? null
                : NodeButton(idea, DesignVersionNotice.ReplanLabel(n), $"replan:{n.Design}", "re-planning",
                    () => Run(() => ReplanOnDesignAsync(idea, flight.Code, n), $"re-plan {flight.Code} on {n.Design}"), Tint)
                    .WithTooltip(DesignVersionNotice.ReplanTip(flight.Code, n));
            yield return DesignVersionNotice.Row(n, P, button);
        }
    }

    /// <summary>Moves the idea's flight to the newer version of a design and has it re-planned (design-replan, PIL1185).</summary>
    internal async Task ReplanOnDesignAsync(IdeaRow idea, string flight, DesignVersionNotice.Newer newer)
    {
        var reply = await RunAsync(KeyOf(idea), $"ideas:replan:{newer.Design}:{KeyOf(idea)}", "re-planning", Verbs.DesignReplan, Verbs.FlightTarget(flight), [newer.Design]);
        if (reply is not null)
        {
            Say(reply.Ok ? $"{flight} is re-planned on {newer.Design} v{newer.Now}" : $"{flight} not re-planned: {reply.Error}", !reply.Ok);
        }
    }
}
