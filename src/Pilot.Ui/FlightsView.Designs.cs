using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The flight's designs (PIL1185): "DSN-3 is now v4; this flight builds v3" with Re-plan on v4, from the one declared notice.
internal sealed partial class FlightsView<TSurface>
{
    /// <summary>The node's designs: what a flight's recorded versions are compared with. The host sets them from the Designs page's read; empty where it has none.</summary>
    public IReadOnlyList<DesignRow> Designs { get; set; } = [];

    /// <summary>Sets <see cref="Designs"/> (null leaves it empty) and returns the view, for a host that builds the page's tree in one expression.</summary>
    public FlightsView<TSurface> WithDesigns(IReadOnlyList<DesignRow>? designs)
    {
        Designs = designs ?? [];
        return this;
    }

    // One notice a design with a newer version, each with its button (dead while the flight's node is unreachable, "…" while the node has the press).
    private IEnumerable<Layout.Node> DesignNotices(FlightLine line)
    {
        foreach (var newer in DesignVersionNotice.For(line.Row, Designs))
        {
            var n = newer;
            var label = DesignVersionNotice.ReplanLabel(n);
            var hit = $"flight:{line.Code}:replan:{n.Design}";
            Layout.Node? button = null;
            if (CanCommand && OnAction is not null)
            {
                button = line.Stale is null
                    ? Presses.Button(line.Code, hit,
                        () => Pill(label, hit, () => _ = Run(DesignVersionNotice.Action(line.Code, n) with { Node = line.Node }, line.Code, hit)).WithTooltip(DesignVersionNotice.ReplanTip(line.Code, n)),
                        pressed => CardsView<TSurface>.Dead(pressed ? $"{label} …" : label))
                    : CardsView<TSurface>.Dead(label).WithTooltip(StaleWhy(line));
            }
            yield return DesignVersionNotice.Row(n, P, button);
        }
    }
}
