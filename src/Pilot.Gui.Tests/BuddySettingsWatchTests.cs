using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The shared watcher of buddy-settings.json. The debounce and the echo rule are unit tests on the watcher's seam: the file's events are sent by hand,
/// the file is an in-memory value and the quiet period is a <see cref="FakeClock"/> moved on, so nothing waits on the wall clock or touches the disk.
/// </summary>
public sealed class BuddySettingsWatchTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    /// <summary>A watcher on a file held in memory, with every change it reports kept in order.</summary>
    private sealed class Rig : IDisposable
    {
        public FakeClock Clock { get; } = new();
        public BuddySettings OnDisk { get; set; }
        public List<BuddySettings> Heard { get; } = [];
        public BuddySettingsWatcher Watcher { get; }

        public Rig(BuddySettings? onDisk = null)
        {
            OnDisk = onDisk ?? new BuddySettings();
            Watcher = new BuddySettingsWatcher(() => OnDisk, Heard.Add, Debounce, Clock);
        }

        /// <summary>Another writer's save: the new content, then the burst of events a temporary file and a move make.</summary>
        public void SavedElsewhere(BuddySettings settings)
        {
            OnDisk = settings;
            Watcher.Touch();
            Watcher.Touch();
        }

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void AFileChangeIsSeenOnceTheQuietPeriodPasses()
    {
        using var rig = new Rig();
        rig.SavedElsewhere(new BuddySettings { Corner = 2, WakeAsk = false });

        rig.Clock.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        rig.Heard.ShouldBeEmpty();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));

        rig.Heard.Count.ShouldBe(1);
        rig.Heard[0].CornerIndex.ShouldBe(2);
        rig.Heard[0].WakesForAsk.ShouldBeFalse();
    }

    [Fact]
    public void EachEventRestartsTheQuietPeriod()
    {
        using var rig = new Rig();
        rig.SavedElsewhere(new BuddySettings { Corner = 1 });
        rig.Clock.Advance(Debounce / 2);
        rig.Watcher.Touch();
        rig.Clock.Advance(Debounce / 2 + TimeSpan.FromMilliseconds(10));
        rig.Heard.ShouldBeEmpty();
        rig.Clock.Advance(Debounce);
        rig.Heard.Count.ShouldBe(1);
    }

    [Fact]
    public void ASaveOfTheHostsOwnIsNotHeardAsAChange()
    {
        using var rig = new Rig();
        var own = new BuddySettings { Corner = 1 };
        rig.Watcher.Remember(own);
        rig.SavedElsewhere(own);
        rig.Clock.Advance(Debounce * 4);
        rig.Heard.ShouldBeEmpty();

        rig.SavedElsewhere(new BuddySettings { Corner = 2 });
        rig.Clock.Advance(Debounce);
        rig.Heard.Single().CornerIndex.ShouldBe(2);
    }

    [Fact]
    public void ASaveThatWritesTwiceInOneBurstIsOneChange()
    {
        using var rig = new Rig();
        var settings = new BuddySettings { Corner = 1 };
        rig.SavedElsewhere(settings);
        rig.SavedElsewhere(settings);
        rig.Clock.Advance(Debounce * 4);
        rig.Heard.Count.ShouldBe(1);
    }

    [Fact]
    public void TheSameContentReadAgainIsNotHeardTwice()
    {
        using var rig = new Rig();
        rig.SavedElsewhere(new BuddySettings { Corner = 3 });
        rig.Clock.Advance(Debounce);
        rig.Watcher.Touch();
        rig.Clock.Advance(Debounce);
        rig.Heard.Count.ShouldBe(1);
    }

    [Fact]
    public void ADisposedWatcherHearsNothingMore()
    {
        var rig = new Rig();
        rig.SavedElsewhere(new BuddySettings { Corner = 2 });
        rig.Dispose();
        rig.Watcher.Touch();
        rig.Clock.Advance(Debounce * 4);
        rig.Heard.ShouldBeEmpty();
    }
}

/// <summary>
/// The watcher on a real file: a <see cref="FileSystemWatcher"/> and a temporary folder, so this is an integration test, out of the default unit
/// run. It proves only that real saves reach the watcher; the debounce and the echo rule are proved on the fake clock in <see cref="BuddySettingsWatchTests"/>.
/// Every wait is on the change itself, with a deadline; nothing waits out a quiet period.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BuddySettingsFileWatchTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(20);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-cat-watch-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "buddy-settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static TaskCompletionSource<BuddySettings> Fresh() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AFileChangeIsSeenWithoutARestart()
    {
        Directory.CreateDirectory(_folder);
        new BuddySettings().Save(FilePath).ShouldBeTrue();
        var seen = Fresh();
        using var watcher = new BuddySettingsWatcher(FilePath, changed => seen.TrySetResult(changed), Debounce);

        // The GUI's save: a temporary file and a move.
        new BuddySettings { Corner = 2, WakeAsk = false }.Save(FilePath).ShouldBeTrue();
        var changed = await seen.Task.WaitAsync(Deadline, Ct);

        changed.CornerIndex.ShouldBe(2);
        changed.WakesForAsk.ShouldBeFalse();
    }

    [Fact]
    public async Task AFileThatDidNotExistYetIsSeenOnceWritten()
    {
        var seen = Fresh();
        using var watcher = new BuddySettingsWatcher(FilePath, changed => seen.TrySetResult(changed), Debounce);
        new BuddySettings { ClickThrough = true }.Save(FilePath).ShouldBeTrue();
        (await seen.Task.WaitAsync(Deadline, Ct)).ClicksPassThrough.ShouldBeTrue();
    }
}
