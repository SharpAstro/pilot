using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// "DSN-3 is now v4; this flight builds v3" (PIL1185), declared once: a flight stays on the design version it was planned from when a newer one is published,
/// and this is what says so, on the flight's page and under each idea whose flight it is, with the Re-plan on v4 button that sends <see cref="Verbs.DesignReplan"/>.
/// The Designs list's "built in v3 (PIL1185)" and "v4 not built yet" come from here too. The pixel host and the terminal's draw the same declarations; no view words this
/// itself, and none knows which host it is in. A line too long for its room ends in an ellipsis and carries the whole text on hover.
/// </summary>
internal static class DesignVersionNotice
{
    /// <summary>One design of a flight that has a newer published version than the one the flight builds.</summary>
    internal readonly record struct Newer(string Design, int Now, int Built);

    /// <summary>The designs of <paramref name="flight"/> with a newer published version among <paramref name="designs"/>, in the flight's order. A landed flight has none: it is not re-planned.</summary>
    internal static IReadOnlyList<Newer> For(FlightRow flight, IEnumerable<DesignRow>? designs)
    {
        if (flight.State == FlightState.Landed || designs is null)
        {
            return [];
        }
        var list = designs as IReadOnlyCollection<DesignRow> ?? [.. designs];
        return [.. flight.Designs
            .Select(d => (Built: d, Row: list.FirstOrDefault(r => string.Equals(r.Id, d.Id, StringComparison.OrdinalIgnoreCase))))
            .Where(p => p.Row?.Current is { } now && now > p.Built.Version)
            .Select(p => new Newer(p.Built.Id, p.Row!.Current!.Value, p.Built.Version))];
    }

    /// <summary>The notice's words.</summary>
    internal static string Text(Newer n) => $"{n.Design} is now v{n.Now.ToString(CultureInfo.InvariantCulture)}; this flight builds v{n.Built.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The button's label.</summary>
    internal static string ReplanLabel(Newer n) => $"Re-plan on v{n.Now.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>What the button says on hover.</summary>
    internal static string ReplanTip(string flight, Newer n) =>
        $"Move {flight} to {n.Design} v{n.Now.ToString(CultureInfo.InvariantCulture)} (its prototype hash with it), tell its issue and have a fresh planner re-plan it";

    /// <summary>The flight action the button sends: <c>design-replan flight/&lt;code&gt; &lt;design&gt;</c>.</summary>
    internal static FlightAction Action(string code, Newer n, NodeMark? node = null) =>
        new($"re-plan {code} on {n.Design} v{n.Now.ToString(CultureInfo.InvariantCulture)}", Verbs.DesignReplan, Verbs.FlightTarget(code), [n.Design]) { Node = node };

    /// <summary>
    /// The notice as one row of one height: its words (ending in an ellipsis, whole on hover) and, when the host can command and has built one, the button, vertically centred
    /// beside it. <paramref name="button"/> is the host's press helper's node, so the pressed state and the node's refusal are the page's own.
    /// </summary>
    internal static Layout.Node Row(Newer n, Palette p, Layout.Node? button)
    {
        var text = Text(n);
        var words = WrappedProse.OneLine(text, 12f, p.Held, selectable: true, vAlign: TextAlign.Center).WStar().HFixed(RowH).Whole(text);
        return button is null
            ? words.RowH(RowH)
            : Layout.Builder.HStack(words, button.WAuto().HFixed(RowH)).WithGap(8f).RowH(RowH);
    }

    /// <summary>The one height of a notice's row and its button.</summary>
    internal const float RowH = 26f;

    /// <summary>
    /// What the Designs list says of design <paramref name="row"/>: "built in v3 (PIL1185)" where a flight builds or built a version, and "v4 not built yet" where the current one
    /// is newer than that (or no version is built at all). Empty for a design with no published version. The flights come from the snapshot, the design's own build record fills in
    /// a flight the snapshot no longer holds.
    /// </summary>
    internal static string BuiltText(DesignRow row, IEnumerable<FlightRow>? flights)
    {
        if (row.Current is not { } current)
        {
            return "";
        }
        var carried = (flights ?? []).Where(f => f.Designs.Any(d => string.Equals(d.Id, row.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(f => (Version: f.Designs.First(d => string.Equals(d.Id, row.Id, StringComparison.OrdinalIgnoreCase)).Version, Flight: f.Code, At: f.CreatedAt));
        var recorded = row.Builds.Where(b => b.Flight is { Length: > 0 }).Select(b => (b.Version, Flight: b.Flight!, b.At));
        var best = carried.Concat(recorded).OrderByDescending(b => b.Version).ThenByDescending(b => b.At).Cast<(int Version, string Flight, DateTimeOffset At)?>().FirstOrDefault();
        var now = current.ToString(CultureInfo.InvariantCulture);
        if (best is not { } built)
        {
            return $"v{now} not built yet";
        }
        var built1 = $"built in v{built.Version.ToString(CultureInfo.InvariantCulture)} ({built.Flight})";
        return built.Version >= current ? built1 : $"{built1}, v{now} not built yet";
    }
}
