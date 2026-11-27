using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The shared watcher of buddy-settings.json: a fake cat host built on it hears a change at once, event driven, with a deadline on every wait.</summary>
public sealed class BuddySettingsWatchTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
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

    /// <summary>The part of the cat's host that matters here: it holds settings, takes what the watcher finds, and tells the watcher what it saved itself.</summary>
    private sealed class FakeCatHost : IDisposable
    {
        private readonly BuddySettingsWatcher _watcher;
        private TaskCompletionSource<BuddySettings> _next = Fresh();
        private int _heard;

        public FakeCatHost(string path)
        {
            _watcher = new BuddySettingsWatcher(path, changed =>
            {
                Interlocked.Increment(ref _heard);
                Volatile.Read(ref _next).TrySetResult(changed);
            }, Debounce);
        }

        public int Heard => Volatile.Read(ref _heard);

        private static TaskCompletionSource<BuddySettings> Fresh() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Arms the wait for the next change, so a change made after this call is the one it sees.</summary>
        public Task<BuddySettings> NextChange()
        {
            var fresh = Fresh();
            Volatile.Write(ref _next, fresh);
            return fresh.Task;
        }

        /// <summary>The cat's own save: written, and remembered so the watcher stays silent about it.</summary>
        public void SaveOwn(BuddySettings settings, string path)
        {
            _watcher.Remember(settings);
            settings.Save(path).ShouldBeTrue();
        }

        public void Dispose() => _watcher.Dispose();
    }

    [Fact]
    public async Task AFileChangeIsSeenWithoutARestart()
    {
        Directory.CreateDirectory(_folder);
        new BuddySettings().Save(FilePath).ShouldBeTrue();
        using var host = new FakeCatHost(FilePath);

        var seen = host.NextChange();
        // The GUI's save: a temporary file and a move.
        new BuddySettings { Corner = 2, WakeAsk = false }.Save(FilePath).ShouldBeTrue();
        var changed = await seen.WaitAsync(Deadline, Ct);

        changed.CornerIndex.ShouldBe(2);
        changed.WakesForAsk.ShouldBeFalse();
    }

    [Fact]
    public async Task AFileThatDidNotExistYetIsSeenOnceWritten()
    {
        using var host = new FakeCatHost(FilePath);
        var seen = host.NextChange();
        new BuddySettings { ClickThrough = true }.Save(FilePath).ShouldBeTrue();
        (await seen.WaitAsync(Deadline, Ct)).ClicksPassThrough.ShouldBeTrue();
    }

    [Fact]
    public async Task ASaveOfTheHostsOwnIsNotHeardAsAChange()
    {
        Directory.CreateDirectory(_folder);
        using var host = new FakeCatHost(FilePath);

        host.SaveOwn(new BuddySettings { Corner = 1 }, FilePath);
        // Then somebody else saves: that is heard, and it proves the own save's events have been through the debounce first.
        var seen = host.NextChange();
        await Task.Delay(Quiet, Ct);
        host.Heard.ShouldBe(0);
        new BuddySettings { Corner = 2 }.Save(FilePath).ShouldBeTrue();
        (await seen.WaitAsync(Deadline, Ct)).CornerIndex.ShouldBe(2);
        host.Heard.ShouldBe(1);
    }

    [Fact]
    public async Task ASaveThatWritesTwiceInOneBurstIsOneChange()
    {
        Directory.CreateDirectory(_folder);
        using var host = new FakeCatHost(FilePath);
        var seen = host.NextChange();
        var settings = new BuddySettings { Corner = 1 };
        settings.Save(FilePath).ShouldBeTrue();
        settings.Save(FilePath).ShouldBeTrue();
        await seen.WaitAsync(Deadline, Ct);
        await Task.Delay(Quiet, Ct);
        host.Heard.ShouldBe(1);
    }

    [Fact]
    public async Task ADisposedWatcherHearsNothingMore()
    {
        Directory.CreateDirectory(_folder);
        var host = new FakeCatHost(FilePath);
        host.Dispose();
        new BuddySettings { Corner = 2 }.Save(FilePath).ShouldBeTrue();
        await Task.Delay(Quiet, Ct);
        host.Heard.ShouldBe(0);
    }
}
