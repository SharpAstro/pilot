using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The header's <b>Night</b> switch (#406): red on black for the whole window, not a view of the Flights page. One state, owned by
/// the chrome and read by every page; lit while on. It is the person's own setting, so unlike Autopilot a click flips it at once,
/// and <see cref="Changed"/> lets <see cref="GuiSettingsFile"/> keep it across a restart.
/// </summary>
internal sealed class NightSwitch
{
    /// <summary>The button's action id, as a click on it reaches the host and as a test finds it.</summary>
    public const string Action = "night:toggle";

    /// <summary>The word on the switch, lit or not.</summary>
    public const string Label = "Night";

    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Dim => Palette.Current.Dim;

    private bool _on;

    /// <summary>Whether the window is painted in the night palette.</summary>
    public bool On
    {
        get => _on;
        set
        {
            if (_on != value)
            {
                _on = value;
                Changed?.Invoke(value);
            }
        }
    }

    /// <summary>Raised when <see cref="On"/> changes, with its new value.</summary>
    public event Action<bool>? Changed;

    /// <summary>The palette the pages paint with: night while on, else null (the standard one).</summary>
    public Palette? Palette => _on ? Palette.Night : null;

    /// <summary>What the switch's tooltip says: the state, and what a click does.</summary>
    public string Tooltip => On ? "Night is on: red on black. Click to turn it off" : "Night is off. Click to paint the window red on black";

    /// <summary>The header's button: lit while on, painted in the night palette itself then.</summary>
    public Layout.Node Button()
    {
        var night = Palette;
        var button = Layout.Builder.Text(Label, 12f, On ? night!.Bright : Dim, TextAlign.Center, TextAlign.Center)
            .Pad(10f, 0f)
            .WAuto()
            .HFixed(AboutCard.HeaderButton)
            .Bg(night?.Button ?? AutopilotSwitch.PlainBg)
            .BgHover(night?.ButtonHover ?? AutopilotSwitch.PlainHover)
            .WithTooltip(Tooltip);
        return button.Follows(new HitResult.ButtonHit(Action), _ => On = !On);
    }
}
