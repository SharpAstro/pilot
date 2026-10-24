using System.Text.Json;
using System.Text.Json.Serialization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What the GUI keeps for its user between runs; today the widths of the list pages' columns.</summary>
internal sealed record GuiSettings
{
    /// <summary>The dragged column widths, design units: a page's name (<c>sessions</c>), then a column's title.</summary>
    public Dictionary<string, Dictionary<string, float>> Tables { get; init; } = [];
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

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, float>> _tables;
    private readonly List<ListTable> _attached = [];
    private Timer? _pending;
    private bool _dirty;

    private GuiSettingsFile(string path, Dictionary<string, Dictionary<string, float>> tables)
    {
        Path = path;
        _tables = tables;
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
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), GuiSettingsJsonContext.Default.GuiSettings) is { } settings)
            {
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
        }
        return new GuiSettingsFile(path, tables);
    }

    /// <summary>
    /// Gives <paramref name="table"/> the widths this file holds for its page, and keeps the file current: a drag that
    /// changes them writes it, a moment after the pointer is still.
    /// </summary>
    public void Attach(ListTable table)
    {
        lock (_lock)
        {
            _attached.Add(table);
            table.Restore(_tables.GetValueOrDefault(table.Page));
        }
        table.Changed += Touch;
    }

    private void Touch()
    {
        lock (_lock)
        {
            _dirty = true;
            _pending?.Dispose();
            _pending = new Timer(_ => Flush(), null, Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the file now if a width changed since it was last written.</summary>
    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }
            _dirty = false;
            _pending?.Dispose();
            _pending = null;
            foreach (var table in _attached)
            {
                _tables[table.Page] = new Dictionary<string, float>(table.Saved, StringComparer.Ordinal);
            }
            json = JsonSerializer.Serialize(new GuiSettings { Tables = new Dictionary<string, Dictionary<string, float>>(_tables) }, GuiSettingsJsonContext.Default.GuiSettings);
        }

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

    public void Dispose() => Flush();
}
