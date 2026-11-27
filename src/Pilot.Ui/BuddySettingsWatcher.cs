namespace Pilot.Ui;

/// <summary>
/// Tells its owner when <c>buddy-settings.json</c> changes on disk, whoever wrote it (the cat's right-click card or the GUI's Settings pane). A
/// <see cref="FileSystemWatcher"/> on the file's folder, filtered to the file name, with a debounce timer (a save is a temporary file and a move, so
/// one save is several events); no polling loop. The file is read once the events have been quiet for the debounce, and the callback runs only when
/// what was read differs from what this side already knows (<see cref="Remember"/>), so a writer never hears the echo of its own save.
/// The callback runs on a timer thread. The known settings are an immutable record swapped whole, so nothing here takes a lock. Disposed with its owner.
/// </summary>
public sealed class BuddySettingsWatcher : IDisposable
{
    /// <summary>The quiet time a burst of events must have before the file is read.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly string _path;
    private readonly Action<BuddySettings> _changed;
    private readonly TimeSpan _debounce;
    private readonly FileSystemWatcher _watcher;
    private readonly ITimer _timer;
    private BuddySettings _known;
    private volatile bool _disposed;

    /// <summary>Starts watching <paramref name="path"/>; <paramref name="changed"/> hears each different content.</summary>
    public BuddySettingsWatcher(string path, Action<BuddySettings> changed, TimeSpan? debounce = null, TimeProvider? time = null)
    {
        _path = Path.GetFullPath(path);
        _changed = changed;
        _debounce = debounce ?? DefaultDebounce;
        _known = BuddySettings.Load(_path);
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        _watcher = new FileSystemWatcher(folder, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += Touched;
        _watcher.Created += Touched;
        _watcher.Renamed += Touched;
        _watcher.Deleted += Touched;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>The settings this side holds now (after its own save); a change to exactly these is not reported.</summary>
    public void Remember(BuddySettings settings) => Volatile.Write(ref _known, settings);

    private void Touched(object sender, FileSystemEventArgs e)
    {
        if (!_disposed)
        {
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        if (_disposed)
        {
            return;
        }
        var read = BuddySettings.Load(_path);
        if (read == Volatile.Read(ref _known))
        {
            return;
        }
        Volatile.Write(ref _known, read);
        _changed(read);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
