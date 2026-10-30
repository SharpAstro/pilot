using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What the GUI keeps for its user between runs; today the widths of the list pages' columns.</summary>
internal sealed record GuiSettings
{
    /// <summary>The dragged column widths, design units: a page's name (<c>sessions</c>), then a column's title.</summary>
    public Dictionary<string, Dictionary<string, float>> Tables { get; init; } = [];

    /// <summary>The sort of each page that has one, by the page's name; an optional property, so an older build reads the file as before.</summary>
    public Dictionary<string, SavedSort>? Sorts { get; init; }

    /// <summary>Whether the window is painted red on black (the header's Night switch).</summary>
    public bool Night { get; init; }

    /// <summary>Which layers of the Flights page's map are on.</summary>
    public MapLayerSettings? MapLayers { get; init; }
}

/// <summary>A page's saved sort: the column's title and whether it runs descending.</summary>
internal sealed record SavedSort(string? Column, bool Descending);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GuiSettings))]
[JsonSerializable(typeof(MapLayerSettings))]
internal sealed partial class GuiSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// The GUI's own settings file, <c>gui-settings.json</c> beside the node's other per-user files (<see cref="NodeSocket.DefaultDataFolder"/>):
/// the widths the user dragged the list pages' columns to, so they survive a restart. It is the window's, not the node's:
/// a missing or unreadable file means the defaults, and a file that cannot be written is a width forgotten, never a crash.
/// </summary>
internal sealed class GuiSettingsFile : IDisposable
{
    /// <summary>How long after the last drag move the file is written: a drag is a stream of changes, and one write is enough.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    // A drag touches it on the render thread and the write runs on the timer's: the tables and the attached list are
    // swapped whole, the dirty mark is one Interlocked int, and the one timer is moved, never replaced, so neither thread
    // waits on the other.
    private ImmutableDictionary<string, IReadOnlyDictionary<string, float>> _tables;
    private ImmutableDictionary<string, SavedSort> _sorts;
    private ImmutableList<ListTable> _attached = [];
    private readonly Timer _pending;
    private int _dirty;
    private volatile bool _night;
    private MapLayerSettings? _layers;
    private FlightMapLayerState? _layerState;

    private GuiSettingsFile(string path, Dictionary<string, Dictionary<string, float>> tables, Dictionary<string, SavedSort> sorts, bool night, MapLayerSettings? layers)
    {
        Path = path;
        _night = night;
        _layers = layers;
        _sorts = sorts.ToImmutableDictionary(StringComparer.Ordinal);
        _tables = tables.ToImmutableDictionary(t => t.Key, t => (IReadOnlyDictionary<string, float>)t.Value, StringComparer.Ordinal);
        _pending = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The file this reads and writes.</summary>
    public string Path { get; }

    /// <summary>The file in the per-user data folder.</summary>
    public static string DefaultPath => System.IO.Path.Combine(NodeSocket.DefaultDataFolder, "gui-settings.json");

    /// <summary>Reads <paramref name="path"/>, or the default file; whatever is wrong with it (absent, unreadable, not JSON, the wrong shape) leaves the defaults.</summary>
    public static GuiSettingsFile Open(string? path = null)
    {
        path ??= DefaultPath;
        var tables = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
        var sorts = new Dictionary<string, SavedSort>(StringComparer.Ordinal);
        var night = false;
        MapLayerSettings? layers = null;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), GuiSettingsJsonContext.Default.GuiSettings) is { } settings)
            {
                night = settings.Night;
                layers = settings.MapLayers;
                foreach (var (page, sort) in settings.Sorts ?? [])
                {
                    if (sort is { Column.Length: > 0 })
                    {
                        sorts[page] = sort;
                    }
                }
                foreach (var (page, widths) in settings.Tables ?? [])
                {
                    if (widths is not null)
                    {
                        tables[page] = new Dictionary<string, float>(widths, StringComparer.Ordinal);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            tables.Clear();
            sorts.Clear();
            night = false;
            layers = null;
        }
        return new GuiSettingsFile(path, tables, sorts, night, layers);
    }

    /// <summary>Whether the file says Night is on.</summary>
    public bool Night => _night;

    /// <summary>Gives <paramref name="night"/> the state this file holds, and keeps the file current: a toggle writes it, a moment later.</summary>
    public void Attach(NightSwitch night)
    {
        night.On = _night;
        night.Changed += on =>
        {
            _night = on;
            Touch();
        };
    }

    /// <summary>
    /// Gives <paramref name="table"/> the widths this file holds for its page, and keeps the file current: a drag that
    /// changes them writes it, a moment after the pointer is still.
    /// </summary>
    public void Attach(ListTable table)
    {
        ImmutableInterlocked.Update(ref _attached, attached => attached.Add(table));
        table.Restore(Volatile.Read(ref _tables).GetValueOrDefault(table.Page));
        table.RestoreSort(Volatile.Read(ref _sorts).TryGetValue(table.Page, out var saved) && saved.Column is { } column ? new ListTable.SortState(column, saved.Descending) : null);
        table.Changed += Touch;
    }

    /// <summary>Gives <paramref name="layers"/> the choice this file holds, and keeps the file current: a toggle writes it a moment later.</summary>
    public void Attach(FlightMapLayerState layers)
    {
        layers.Restore(_layers);
        _layerState = layers;
        layers.Changed += Touch;
    }

    private void Touch()
    {
        Interlocked.Exchange(ref _dirty, 1);
        _pending.Change(Quiet, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes the file now if a width changed since it was last written.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }
        ImmutableInterlocked.Update(ref _tables, held => held.SetItems(Volatile.Read(ref _attached).Select(t => KeyValuePair.Create(t.Page, t.Saved))));
        ImmutableInterlocked.Update(ref _sorts, held =>
        {
            foreach (var t in Volatile.Read(ref _attached))
            {
                held = t.Sort is { } s ? held.SetItem(t.Page, new SavedSort(s.Column, s.Descending)) : held.Remove(t.Page);
            }
            return held;
        });
        _layers = _layerState?.Snapshot() ?? _layers;
        var json = JsonSerializer.Serialize(
            new GuiSettings
            {
                MapLayers = _layers,
                Tables = Volatile.Read(ref _tables).ToDictionary(t => t.Key, t => new Dictionary<string, float>(t.Value, StringComparer.Ordinal), StringComparer.Ordinal),
                Sorts = Volatile.Read(ref _sorts).Count > 0 ? new Dictionary<string, SavedSort>(Volatile.Read(ref _sorts), StringComparer.Ordinal) : null,
                Night = _night,
            },
            GuiSettingsJsonContext.Default.GuiSettings);

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // Beside it and moved over it, so a window closed mid-write leaves the old file whole.
            var temp = Path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The widths are forgotten at the next start; nothing is worth stopping the window for.
        }
    }

    public void Dispose()
    {
        _pending.Dispose();
        Flush();
    }
}
