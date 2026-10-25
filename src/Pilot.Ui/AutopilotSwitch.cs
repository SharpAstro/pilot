using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The header's <b>Autopilot</b> switch (#208): whether an assistant's your-only requests run at once (on) or wait on a card the
/// person allows (off), for the node this window shows. One switch, shared by every page, so it stands in the same place in each
/// header; lit while the node says autopilot is on, plain while it is off. A click asks the host to flip it as the person
/// (<see cref="OnToggle"/>); the host sets <see cref="On"/> from the node's own answer, never from the click.
/// </summary>
internal sealed class AutopilotSwitch
{
    /// <summary>The button's action id, as a click on it reaches the host and as a test finds it.</summary>
    public const string Action = "autopilot:toggle";

    /// <summary>The word on the switch, lit or not.</summary>
    public const string Label = "Autopilot";

    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);

    /// <summary>The fill while autopilot is on: the accent the rail's chosen page and the New flight wizard share.</summary>
    internal static readonly RGBAColor32 LitBg = new(0x3d, 0x6f, 0xb8, 0xff);

    internal static readonly RGBAColor32 LitHover = new(0x4c, 0x82, 0xd0, 0xff);

    /// <summary>The fill while it is off: a plain header button's.</summary>
    internal static readonly RGBAColor32 PlainBg = new(0x2c, 0x35, 0x4a, 0xff);

    internal static readonly RGBAColor32 PlainHover = new(0x3a, 0x46, 0x62, 0xff);

    /// <summary>Whether the node in view says its autopilot is on. Set by the host from the node's <see cref="Pilot.Contracts.NodeInfo.Autopilot"/>.</summary>
    public bool On { get; set; }

    /// <summary>Flips the autopilot to the given state, as the person. Null (no host to send it) leaves the switch a mark that says, and does not click.</summary>
    public Action<bool>? OnToggle { get; set; }

    /// <summary>What the switch's tooltip says: the state, and what a click does.</summary>
    public string Tooltip => On
        ? "Autopilot is on: an assistant's merge, land, settings and grant requests run at once. Click to turn it off"
        : "Autopilot is off: an assistant's merge, land, settings and grant requests wait on a card for you. Click to turn it on";

    /// <summary>The header's button: lit while on. <paramref name="night"/> is the Flights page's own palette.</summary>
    public Layout.Node Button(FlightsPalette? night = null)
    {
        var button = Layout.Builder.Text(Label, 12f, On ? Bright : night?.Dim ?? Dim, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(On ? LitBg : night?.Button ?? PlainBg)
            .BgHover(On ? LitHover : night?.ButtonHover ?? PlainHover)
            .WithTooltip(Tooltip);
        return OnToggle is { } toggle ? button.Follows(new HitResult.ButtonHit(Action), _ => toggle(!On)) : button;
    }

    /// <summary>The switch for a header's row, or nothing in its place where a page has no switch.</summary>
    public static Layout.Node In(AutopilotSwitch? autopilot, FlightsPalette? night = null) =>
        autopilot?.Button(night) ?? Layout.Builder.Spacer().WFixed(0f);
}

/// <summary>The switch's verb on the node, for either host.</summary>
internal static class AutopilotCommands
{
    /// <summary>
    /// <c>set node autopilot on|off</c> on <paramref name="client"/>'s node as <paramref name="who"/>; what became of it in a line, a
    /// refusal included (the node takes the setting from the person only), for the status line.
    /// </summary>
    public static async Task<string> SetAsync(NodeClient client, bool on, string who, CancellationToken cancellationToken)
    {
        var said = await SettingsCommands.SendAsync(client, Verbs.Set, ["autopilot", on ? "on" : "off"], who, cancellationToken);
        return $"autopilot {(on ? "on" : "off")}: {said.ReplaceLineEndings(" ").Trim()}";
    }
}
