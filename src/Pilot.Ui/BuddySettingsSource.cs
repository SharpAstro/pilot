namespace Pilot.Ui;

/// <summary>
/// The Settings pane's one view of <c>buddy-settings.json</c>: what the file says now, as one immutable record swapped whole (the render thread reads it,
/// a change lands from the watcher's timer thread, neither waits). A change is written through <see cref="BuddySettings.Save"/> and the answer is read
/// back from the file, so the pane shows the file's state and not the click. The cat's right-click card writes the same file; the shared
/// <see cref="BuddySettingsWatcher"/> brings that in with no polling. Disposed with the page's owner.
/// </summary>
public sealed class BuddySettingsSource : IDisposable
{
    private readonly string _path;
    private readonly BuddySettingsWatcher _watcher;
    private BuddySettings _current;

    /// <summary>Reads <paramref name="path"/> (default: <see cref="BuddySettings.DefaultPath"/>) and watches it.</summary>
    public BuddySettingsSource(string? path = null, TimeProvider? time = null, TimeZoneInfo? zone = null, TimeSpan? debounce = null)
    {
        _path = Path.GetFullPath(path ?? BuddySettings.DefaultPath);
        Time = time ?? TimeProvider.System;
        Zone = zone ?? TimeZoneInfo.Local;
        _current = BuddySettings.Load(_path);
        _watcher = new BuddySettingsWatcher(_path, changed =>
        {
            Volatile.Write(ref _current, changed);
            Changed?.Invoke();
        }, debounce, time);
    }

    /// <summary>The clock a snooze is counted from.</summary>
    public TimeProvider Time { get; }

    /// <summary>The zone "Until tomorrow" ends midnight in.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>What the file says now.</summary>
    public BuddySettings Current => Volatile.Read(ref _current);

    /// <summary>Called when the file changed under the page (from a timer thread): the host redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>Applies <paramref name="change"/> to what the file says, saves it and shows what the file then holds. False when it could not be written.</summary>
    public bool Update(Func<BuddySettings, BuddySettings> change)
    {
        var next = change(BuddySettings.Load(_path));
        if (!next.Save(_path))
        {
            return false;
        }
        _watcher.Remember(next);
        Volatile.Write(ref _current, BuddySettings.Load(_path));
        Changed?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _watcher.Dispose();
}
