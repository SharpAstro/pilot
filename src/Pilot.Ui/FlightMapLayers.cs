using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>The map layers as the settings file keeps them; <see cref="Node"/> is the one node shown, or null for all.</summary>
internal sealed record MapLayerSettings
{
    public bool Landed { get; init; }

    public bool Held { get; init; } = true;

    public bool ArcsOnly { get; init; }

    public string? Node { get; init; }

    public bool PrStates { get; init; } = true;
}

/// <summary>
/// Which layers of the flight map are on, and where the layer palette sits. Landed flights are off by default: a long-lived
/// window collects many, and the map is for what is in play.
/// </summary>
internal sealed class FlightMapLayerState
{
    /// <summary>Landed flights, drawn as faded routes.</summary>
    public bool ShowLanded { get; set; }

    /// <summary>Held flights, parked at their gate.</summary>
    public bool ShowHeld { get; set; } = true;

    /// <summary>Only the flights an intercontinental arc joins, and no domestic connection.</summary>
    public bool ArcsOnly { get; set; }

    /// <summary>The key of the one node shown, or null for every node.</summary>
    public string? OnlyNode { get; set; }

    /// <summary>The PR's state on each destination: its colour and its word.</summary>
    public bool ShowPrStates { get; set; } = true;

    /// <summary>The nodes on the page, for the pick of one: (key, name), set by the view each frame.</summary>
    public IReadOnlyList<(string Key, string Name)> Nodes { get; set; } = [];

    /// <summary>Where the palette sits and whether it is rolled up; pinned to the right edge by default.</summary>
    public FloatingPaletteState Palette { get; } = new() { Side = Layout.DockSide.Right };

    /// <summary>A layer was changed: the settings file writes itself and the window asks for a frame.</summary>
    public event Action? Changed;

    public void NoteChanged() => Changed?.Invoke();

    public MapLayerSettings Snapshot() => new() { Landed = ShowLanded, Held = ShowHeld, ArcsOnly = ArcsOnly, Node = OnlyNode, PrStates = ShowPrStates };

    public void Restore(MapLayerSettings? saved)
    {
        saved ??= new MapLayerSettings();
        (ShowLanded, ShowHeld, ArcsOnly, OnlyNode, ShowPrStates) = (saved.Landed, saved.Held, saved.ArcsOnly, saved.Node, saved.PrStates);
    }
}

/// <summary>One toggleable layer of the flight map: its label and how to read and write its flag.</summary>
internal readonly record struct FlightMapLayer(
    string Label,
    string Id,
    Func<FlightMapLayerState, bool> IsOn,
    Action<FlightMapLayerState, bool> Set,
    Func<FlightMapLayerState, bool>? IsAvailable = null)
{
    public bool Available(FlightMapLayerState state) => IsAvailable is null || IsAvailable(state);

    /// <summary>Flips the layer and says so; false for an unavailable one.</summary>
    public bool Toggle(FlightMapLayerState state)
    {
        if (!Available(state))
        {
            return false;
        }
        Set(state, !IsOn(state));
        state.NoteChanged();
        return true;
    }
}

/// <summary>The map's layers, in the order the palette lists them: one table the palette reads.</summary>
internal static class FlightMapLayers
{
    public static readonly ImmutableArray<FlightMapLayer> All =
    [
        new("Landed", "landed", static s => s.ShowLanded, static (s, v) => s.ShowLanded = v),
        new("Held", "held", static s => s.ShowHeld, static (s, v) => s.ShowHeld = v),
        new("Arcs only", "arcs", static s => s.ArcsOnly, static (s, v) => s.ArcsOnly = v),
        // On picks the first node on the page (the one the window runs on); the rows under it pick another.
        new("One node", "node", static s => s.OnlyNode is not null, static (s, v) => s.OnlyNode = v ? s.Nodes.FirstOrDefault().Key : null,
            static s => s.Nodes.Count > 1 || s.OnlyNode is not null),
        new("PR states", "pr", static s => s.ShowPrStates, static (s, v) => s.ShowPrStates = v),
    ];

    /// <summary>Picks <paramref name="key"/> as the one node shown, when one is shown and it is a node on the page.</summary>
    public static bool PickNode(FlightMapLayerState state, string key)
    {
        if (state.OnlyNode is null || !state.Nodes.Any(n => n.Key == key))
        {
            return false;
        }
        state.OnlyNode = key;
        state.NoteChanged();
        return true;
    }
}

/// <summary>The layer table as DIR.Lib's <see cref="FloatingPalette"/>, coloured from the page's palette so night stays red.</summary>
internal static class FlightMapLayerPalette
{
    public const float PanelWidth = 124f;
    public const string GripAction = "FlightMapLayerPaletteGrip";
    private const string RowPrefix = "FlightMapLayer:";
    private const string NodePrefix = "FlightMapNode:";

    public static PaletteColors ColorsOf(Palette p) => new(
        PanelBg: p.MenuBg,
        GripBg: p.HeaderBg,
        TitleInk: p.Bright,
        CountInk: p.Held,
        RowOnBg: p.RowFocusBg,
        RowOffBg: p.RowAltBg,
        RowHoverBg: p.RowHoverBg,
        KeyChipBg: p.HeaderBg,
        OnInk: p.Bright,
        OffInk: p.Dim,
        DisabledInk: p.Faint);

    public static string RowAction(FlightMapLayer layer) => RowPrefix + layer.Id;

    public static ImmutableArray<PaletteItem> ItemsFor(FlightMapLayerState state)
    {
        var items = ImmutableArray.CreateBuilder<PaletteItem>();
        foreach (var layer in FlightMapLayers.All)
        {
            var available = layer.Available(state);
            items.Add(new PaletteItem(layer.Label, RowAction(layer), available && layer.IsOn(state), null, available));
            if (layer.Id == "node")
            {
                foreach (var (key, name) in state.Nodes)
                {
                    items.Add(new PaletteItem("  " + name, NodePrefix + key, state.OnlyNode == key, null, state.OnlyNode is not null));
                }
            }
        }
        return items.ToImmutable();
    }

    /// <summary>Does what a row's click does: toggles its layer, or picks its node. False when the action is no row's.</summary>
    public static bool Press(FlightMapLayerState state, string action)
    {
        if (action.StartsWith(NodePrefix, StringComparison.Ordinal))
        {
            return FlightMapLayers.PickNode(state, action[NodePrefix.Length..]);
        }
        foreach (var layer in FlightMapLayers.All)
        {
            if (RowAction(layer) == action)
            {
                return layer.Toggle(state);
            }
        }
        return false;
    }

    public static Layout.Node Build(FlightMapLayerState state, Palette palette, float fontSize, DesignScale scale, Action? onChanged = null)
    {
        var colors = ColorsOf(palette);
        return FloatingPalette.Build(state.Palette, "LAYERS", ItemsFor(state).AsSpan(), in colors, fontSize, GripAction,
            action => Press(state, action), scale, onChanged, panelWidth: PanelWidth);
    }
}
