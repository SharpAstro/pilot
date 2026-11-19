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

    /// <summary>What is folded on the Pull requests page: <c>org:name</c> and <c>repo:owner/name</c>, kept for an org or repo no longer listed.</summary>
    public List<string> PullRequestFolds { get; init; } = [];

    /// <summary>Which layers of the Flights page's map are on.</summary>
    public MapLayerSettings? MapLayers { get; init; }

    /// <summary>The target each design is shown at on its page, by design id (PIL1097); nullable, so a file an older build wrote reads as before.</summary>
    public Dictionary<string, SavedDesignTarget>? DesignTargets { get; init; }

    /// <summary>The size each frame of each design is shown at (PIL1171), by design id and then <c>cell.frame</c>; nullable, so a file an older build wrote reads as before.</summary>
    public Dictionary<string, Dictionary<string, SavedFrameSize>>? FrameSizes { get; init; }

    /// <summary>
    /// Whether the For you side bar is collapsed to its edge handle at the right edge (PIL1184, PIL1205). Null means open, which is what a file an older
    /// build wrote reads as; it is written only while the bar is collapsed, so reopening it drops the member again.
    /// </summary>
    public bool? NotificationsCollapsed { get; init; }

    /// <summary>
    /// The For you bar's state (PIL1205): "collapsed", "over" or "docked"; null means docked (the default) and is how that is written. Read first when
    /// present; else the older <see cref="NotificationsCollapsed"/>: true is collapsed, false or missing is docked. Only this member is written.
    /// </summary>
    public string? NotificationsBar { get; init; }

    /// <summary>The open state a collapsed bar returns to ("over" or "docked"); missing reads as docked, as an older file's collapsed bar did.</summary>
    public string? NotificationsBarLastOpen { get; init; }

    /// <summary>
    /// How many hours the Notifications page keeps what happened (PIL1184). Null means 24 hours, which is what a file an older build wrote reads as;
    /// it is written only while the choice is not 24.
    /// </summary>
    public double? NotificationKeepHours { get; init; }
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
    private ImmutableArray<string> _folds;
    private MapLayerSettings? _layers;
    private FlightMapLayerState? _layerState;
    private ImmutableDictionary<string, SavedDesignTarget> _designTargets = ImmutableDictionary<string, SavedDesignTarget>.Empty.WithComparers(StringComparer.Ordinal);

    private Dictionary<string, Dictionary<string, SavedFrameSize>> _frameSizes = [];
    private volatile int _barState = (int)BarStates.Docked;
    private volatile int _barLastOpen = (int)BarStates.Docked;
    // Written on the render thread (the host's event) and read by Flush on the timer thread. A double? cannot be volatile; each write is
    // followed by Touch(), whose Interlocked is a full fence, so the timer's later read sees it.
    private double? _keepHours;

    private GuiSettingsFile(string path, Dictionary<string, Dictionary<string, float>> tables, Dictionary<string, SavedSort> sorts, bool night, MapLayerSettings? layers, List<string> folds, Dictionary<string, SavedDesignTarget> designTargets,
        Dictionary<string, Dictionary<string, SavedFrameSize>> frameSizes, BarStates barState, BarStates barLastOpen, double? keepHours)
    {
        _barLastOpen = (int)barLastOpen;
        _keepHours = keepHours;
        _barState = (int)barState;
        _frameSizes = frameSizes;
        _designTargets = designTargets.ToImmutableDictionary(StringComparer.Ordinal);
        _folds = [.. folds];
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
        var barState = BarStates.Docked;
        var barLastOpen = BarStates.Docked;
        double? keepHours = null;
        var folds = new List<string>();
        MapLayerSettings? layers = null;
        var designTargets = new Dictionary<string, SavedDesignTarget>(StringComparer.Ordinal);
        var frameSizes = new Dictionary<string, Dictionary<string, SavedFrameSize>>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), GuiSettingsJsonContext.Default.GuiSettings) is { } settings)
            {
                night = settings.Night;
                barState = settings.NotificationsBar?.ToLowerInvariant() switch
                {
                    "collapsed" => BarStates.Collapsed,
                    "over" => BarStates.Over,
                    "docked" => BarStates.Docked,
                    _ => settings.NotificationsCollapsed == true ? BarStates.Collapsed : BarStates.Docked,
                };
                barLastOpen = settings.NotificationsBarLastOpen?.ToLowerInvariant() == "over" ? BarStates.Over : BarStates.Docked;
                keepHours = settings.NotificationKeepHours is > 0 and var hours ? hours : null;
                folds.AddRange(settings.PullRequestFolds?.Where(f => !string.IsNullOrEmpty(f)) ?? []);
                layers = settings.MapLayers;
                foreach (var (id, saved) in settings.DesignTargets ?? [])
                {
                    if (saved?.ToChoice() is not null)
                    {
                        designTargets[id] = saved;
                    }
                }
                foreach (var (design, frames) in settings.FrameSizes ?? [])
                {
                    if (frames is { Count: > 0 })
                    {
                        frameSizes[design] = new Dictionary<string, SavedFrameSize>(frames.Where(f => f.Value is not null && f.Value.IsValid), StringComparer.Ordinal);
                    }
                }
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
            barState = BarStates.Docked;
            barLastOpen = BarStates.Docked;
            keepHours = null;
            folds.Clear();
            layers = null;
            designTargets.Clear();
            frameSizes.Clear();
        }
        return new GuiSettingsFile(path, tables, sorts, night, layers, folds, designTargets, frameSizes, barState, barLastOpen, keepHours);
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

    /// <summary>The For you bar's state the file holds; docked when it says nothing (an older file's collapsed: false reads so).</summary>
    public BarStates NotificationsBarState => (BarStates)_barState;

    /// <summary>Whether the file says the For you bar is collapsed.</summary>
    public bool NotificationsCollapsed => NotificationsBarState == BarStates.Collapsed;

    /// <summary>Gives <paramref name="host"/> the choice this file holds, and keeps the file current: a collapse or an open writes it, a moment later.</summary>
    public void Attach(NotificationsHost host)
    {
        // The open state first, so a collapsed bar's handle and key return to the one it was last open in.
        host.State = (BarStates)_barLastOpen;
        host.State = NotificationsBarState;
        host.KeepFor = _keepHours is { } hours ? TimeSpan.FromHours(hours) : NotificationsHost.DefaultKeepFor;
        host.KeepChanged += keep =>
        {
            _keepHours = keep == NotificationsHost.DefaultKeepFor ? null : keep.TotalHours;
            Touch();
        };
        host.StateChanged += state =>
        {
            _barState = (int)state;
            if (state != BarStates.Collapsed)
            {
                _barLastOpen = (int)state;
            }
            Touch();
        };
    }

    /// <summary>The folds the file holds for the Pull requests page.</summary>
    public IReadOnlyList<string> PullRequestFolds => _folds;

    /// <summary>Gives <paramref name="page"/> the folds this file holds, and keeps the file current: a fold or unfold writes it, a moment later.</summary>
    public void Attach<TSurface>(PullRequestsView<TSurface> page)
    {
        page.RestoreFolds(_folds);
        // Read on the thread that builds the page; the timer's write only reads the copy.
        page.FoldsChanged += () =>
        {
            _folds = [.. page.Folds];
            Touch();
        };
    }

    /// <summary>Gives <paramref name="layers"/> the choice this file holds, and keeps the file current: a toggle writes it a moment later.</summary>
    public void Attach(FlightMapLayerState layers)
    {
        layers.Restore(_layers);
        _layerState = layers;
        layers.Changed += Touch;
    }

    /// <summary>The target each design was last shown at, as the file holds it.</summary>
    public IReadOnlyDictionary<string, SavedDesignTarget> DesignTargets => _designTargets;

    /// <summary>Gives <paramref name="page"/> the targets this file holds, and keeps the file current: a pick, a swap or a custom size writes it, a moment later. Nothing goes to the node.</summary>
    public void Attach<TSurface>(DesignsView<TSurface> page)
    {
        page.RestoreTargets(_designTargets);
        page.RestoreFrameSizes(_frameSizes);
        // Read on the render thread, where the choice changes; the timer's write only reads the copy.
        page.TargetChanged += () =>
        {
            _designTargets = page.SnapshotTargets().ToImmutableDictionary(StringComparer.Ordinal);
            _frameSizes = page.SnapshotFrameSizes();
            Touch();
        };
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
                PullRequestFolds = [.. _folds],
                DesignTargets = _designTargets.Count > 0 ? new Dictionary<string, SavedDesignTarget>(_designTargets, StringComparer.Ordinal) : null,
                FrameSizes = _frameSizes.Count > 0 ? _frameSizes : null,
                NotificationsBar = NotificationsBarState == BarStates.Docked ? null : NotificationsBarState.ToString().ToLowerInvariant(),
                NotificationsBarLastOpen = NotificationsBarState == BarStates.Collapsed && (BarStates)_barLastOpen == BarStates.Over ? "over" : null,
                NotificationKeepHours = _keepHours,
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
