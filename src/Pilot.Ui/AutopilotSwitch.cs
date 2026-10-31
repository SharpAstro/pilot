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

    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Dim => Palette.Current.Dim;

    /// <summary>The fill while autopilot is on: the accent the rail's chosen page and the New flight wizard share.</summary>
    internal static RGBAColor32 LitBg => Palette.Current.LitBg;

    internal static RGBAColor32 LitHover => Palette.Current.LitHover;

    /// <summary>The fill while it is off: a plain header button's.</summary>
    internal static RGBAColor32 PlainBg => Palette.Current.Action;

    internal static RGBAColor32 PlainHover => Palette.Current.ActionHover;

    /// <summary>Whether the node in view says its autopilot is on. Set by the host from the node's <see cref="Pilot.Contracts.NodeInfo.Autopilot"/>.</summary>
    public bool On { get; set; }

    /// <summary>
    /// Flips the autopilot to the given state, as the person, and answers with the node's refusal in its words, or null when it took
    /// it. Null (no host to send it) leaves the switch a mark that says, and does not click.
    /// </summary>
    public Func<bool, Task<string?>>? OnToggle { get; set; }

    /// <summary>What a press is doing: working with the node (the switch shows the asked state, dimmed, with no hit), taken, or refused.</summary>
    internal Presses Presses { get; } = new();

    /// <summary>Called when a press changed what the switch shows, from whichever thread: the host redraws.</summary>
    public Action? Changed { get => Presses.Changed; set => Presses.Changed = value; }

    private bool _asked;

    /// <summary>
    /// The state the switch shows: the asked one while the node has the press and after it took it, until the node's own look says
    /// so (<see cref="On"/> then agrees) or the taken mark lapses; else the node's. A refusal flips it back.
    /// </summary>
    public bool Shown
    {
        get
        {
            if (Presses.PendingOf(Action) is not null)
            {
                return _asked;
            }
            if (Presses.IsTaken(Action))
            {
                if (On == _asked)
                {
                    Presses.ClearTaken(Action);
                    return On;
                }
                return _asked;
            }
            return On;
        }
    }

    /// <summary>Why the node refused the last press, in its words; null when it did not.</summary>
    public string? Refused => Presses.RefusedOf(Action);

    private void Press(Func<bool, Task<string?>> toggle)
    {
        var want = !Shown;
        if (Presses.PendingOf(Action) is not null)
        {
            return;
        }
        _asked = want;
        _ = Presses.PressAsync(Action, Action, _ => toggle(want), say: why => $"autopilot {(want ? "on" : "off")} refused: {why}");
    }

    /// <summary>What the switch's tooltip says: the state, and what a click does.</summary>
    public string Tooltip => Presses.PendingOf(Action) is not null
        ? $"Asking the node to turn autopilot {(_asked ? "on" : "off")}…"
        : Refused is { } why
            ? $"{why}. Autopilot stays {(On ? "on" : "off")}"
            : Shown
        ? "Autopilot is on: an assistant's merge, land, settings and grant requests run at once. Click to turn it off"
        : "Autopilot is off: an assistant's merge, land, settings and grant requests wait on a card for you. Click to turn it on";

    /// <summary>The header's button: lit while on. <paramref name="night"/> is the Flights page's own palette.</summary>
    public Layout.Node Button(Palette? night = null)
    {
        // As tall as the header's other buttons, its word in their middle.
        var on = Shown;
        var working = Presses.PendingOf(Action) is not null;
        var button = Layout.Builder.Text(Label, 12f, working ? night?.Dim ?? Dim : on ? Bright : night?.Dim ?? Dim, TextAlign.Center, TextAlign.Center)
            .Pad(10f, 0f)
            .WAuto()
            .HFixed(AboutCard.HeaderButton)
            .Bg(on ? LitBg : night?.Button ?? PlainBg)
            .WithTooltip(Tooltip);
        if (OnToggle is not { } toggle)
        {
            return button.BgHover(on ? LitHover : night?.ButtonHover ?? PlainHover);
        }
        // The asked state, dimmed, with no hit while the node has it: a press that missed would read the same, and no second one is taken.
        return Presses.Button(Action, Action,
            () => button.BgHover(on ? LitHover : night?.ButtonHover ?? PlainHover).Follows(new HitResult.ButtonHit(Action), _ => Press(toggle)),
            _ => button);
    }
}

/// <summary>The switch's verb on the node, for either host.</summary>
internal static class AutopilotCommands
{
    /// <summary>
    /// <c>set node autopilot on|off</c> on <paramref name="client"/>'s node as <paramref name="who"/>; what became of it in a line, a
    /// refusal included (the node takes the setting from the person only), for the status line.
    /// </summary>
    public static async Task<string> SetAsync(NodeClient client, bool on, string who, CancellationToken cancellationToken) =>
        (await TrySetAsync(client, on, who, cancellationToken)).Said;

    /// <summary>As <see cref="SetAsync"/>, and the node's refusal in its words (null when it took it), for the switch to say where it was pressed.</summary>
    public static async Task<(string? Refused, string Said)> TrySetAsync(NodeClient client, bool on, string who, CancellationToken cancellationToken)
    {
        var reply = await SettingsCommands.SendAsync(client, Verbs.Set, ["autopilot", on ? "on" : "off"], who, cancellationToken);
        return (reply.Kind == ReplyKind.Done ? null : reply.Line, $"autopilot {(on ? "on" : "off")}: {reply.Line.ReplaceLineEndings(" ").Trim()}");
    }
}
