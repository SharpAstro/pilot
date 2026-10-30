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

    /// <summary>Whether the window is painted red on black (the header's Night switch).</summary>
    public bool Night { get; init; }

    /// <summary>What is folded on the Pull requests page: <c>org:name</c> and <c>repo:owner/name</c>, kept for an org or repo no longer listed.</summary>
    public List<string> PullRequestFolds { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuiSettings))]
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
    private ImmutableList<ListTable> _attached = [];
    private readonly Timer _pending;
    private int _dirty;
    private volatile bool _night;
    private ImmutableArray<string> _folds;

    private GuiSettingsFile(string path, Dictionary<string, Dictionary<string, float>> tables, bool night, List<string> folds)
    {
        _folds = [.. folds];
        Path = path;
        _night = night;
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
        var night = false;
        var folds = new List<string>();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), GuiSettingsJsonContext.Default.GuiSettings) is { } settings)
            {
                night = settings.Night;
                folds.AddRange(settings.PullRequestFolds?.Where(f => !string.IsNullOrEmpty(f)) ?? []);
                foreach (var (page, widths) in settings.Tables)
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
            night = false;
            folds.Clear();
        }
        return new GuiSettingsFile(path, tables, night, folds);
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
        table.Changed += Touch;
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
        var json = JsonSerializer.Serialize(
            new GuiSettings { Tables = Volatile.Read(ref _tables).ToDictionary(t => t.Key, t => new Dictionary<string, float>(t.Value, StringComparer.Ordinal), StringComparer.Ordinal), Night = _night, PullRequestFolds = [.. _folds] },
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
