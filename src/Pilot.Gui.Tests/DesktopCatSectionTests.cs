using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Settings pane's Desktop cat section (#1373): each control writes buddy-settings.json through BuddySettings, the file's answer is what shows, and the search finds its rows.</summary>
public sealed class DesktopCatSectionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-cat-section-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    private string FilePath => Path.Combine(_folder, "buddy-settings.json");

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    private static readonly SettingsPage Sample = new()
    {
        File = @"C:\Users\ada\AppData\Local\pilot2\settings.json",
        Settings =
        [
            new SettingRow { Key = "toasts", Label = "Desktop toasts", Detail = "Show a desktop toast for every Ring card", Value = "on", Source = SettingSources.Default, Choices = ["on", "off"] },
            new SettingRow { Key = "lan", Label = "On the LAN", Detail = "Listen on the LAN and announce it", Value = "on", Saved = "on", Source = SettingSources.File, Choices = ["on", "off"] },
        ],
    };

    private BuddySettingsSource Source(BuddySettings? start = null)
    {
        Directory.CreateDirectory(_folder);
        start?.Save(FilePath);
        var source = new BuddySettingsSource(FilePath, new FixedClock(Now), TimeZoneInfo.Utc, TimeSpan.FromMilliseconds(20));
        _owned.Add(source);
        return source;
    }

    private static async Task<PilotChrome<RgbaImage>> LoadedAsync(RgbaImageRenderer renderer, BuddySettingsSource source, int width, int height)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.LoadGrants = _ => Task.FromResult<GrantInfo[]>([]);
        chrome.Settings.Buddy = source;
        chrome.Show(PilotPage.Settings);
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, height));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(Deadline);
        while (chrome.Settings.Page is null)
        {
            await Task.Delay(10, deadline.Token);
        }
        return chrome;
    }

    private static void Render(PilotChrome<RgbaImage> chrome, int width, int height) =>
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, height));

    private static void Click(PilotChrome<RgbaImage> chrome, string action, int height = 1400)
    {
        Render(chrome, 1280, height);
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        var at = regions.FindIndex(r => r.Result is HitResult.ButtonHit b && b.Action == action);
        (at >= 0).ShouldBeTrue($"no painted '{action}' among: {string.Join(", ", regions.Select(r => (r.Result as HitResult.ButtonHit)?.Action).Where(a => a is not null))}");
        regions[at].OnClick!.Invoke(InputModifier.None);
    }

    private static List<string> Texts(PilotChrome<RgbaImage> chrome, RgbaImageRenderer renderer, float width = 1280, float height = 1400)
    {
        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(Now, width), new Rect<float>(0, 0, width, height),
            new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];
    }

    [Theory]
    [InlineData("settings:buddy-wake-ask")]
    [InlineData("settings:buddy-wake-merged")]
    [InlineData("settings:buddy-wake-failed")]
    public async Task AWakeSwitchWritesTheFileAndShowsTheFilesAnswer(string action)
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var source = Source();
        var chrome = await LoadedAsync(renderer, source, 1280, 1400);
        Click(chrome, action);

        var written = BuddySettings.Load(FilePath);
        (action switch
        {
            "settings:buddy-wake-ask" => written.WakeAsk,
            "settings:buddy-wake-merged" => written.WakeMerged,
            _ => written.WakeFailed,
        }).ShouldBe(false);
        source.Current.ShouldBe(written);
        Texts(chrome, renderer).ShouldContain("Off");
    }

    [Fact]
    public async Task ClickThroughAndCornerAndSnoozeWriteTheFile()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var source = Source();
        var chrome = await LoadedAsync(renderer, source, 1280, 1400);

        Click(chrome, "settings:buddy-click-through");
        BuddySettings.Load(FilePath).ClicksPassThrough.ShouldBeTrue();

        Click(chrome, "settings:buddy-corner:2");
        BuddySettings.Load(FilePath).CornerIndex.ShouldBe(2);

        Click(chrome, "settings:buddy-snooze:1");
        var snoozed = BuddySettings.Load(FilePath);
        snoozed.SnoozeIndex.ShouldBe(1);
        snoozed.SnoozedUntil.ShouldBe(Now.AddHours(1));
        // The other settings stay as they were written.
        snoozed.ClicksPassThrough.ShouldBeTrue();
        snoozed.CornerIndex.ShouldBe(2);
    }

    [Fact]
    public async Task PlacementAndSizeAreSegmentsThatWriteTheFileAndFreeHidesTheCornerRow()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var source = Source();
        var chrome = await LoadedAsync(renderer, source, 1280, 1400);
        Texts(chrome, renderer).ShouldContain("Bottom right");
        Texts(chrome, renderer).ShouldContain("Size");

        Click(chrome, "settings:buddy-size:2");
        BuddySettings.Load(FilePath).SizeIndex.ShouldBe(2);
        Click(chrome, "settings:buddy-placement:1");
        var free = BuddySettings.Load(FilePath);
        free.IsFree.ShouldBeTrue();
        free.SizeIndex.ShouldBe(2, "the other settings stay as they were written");
        Texts(chrome, renderer).ShouldNotContain("Bottom right");
        Texts(chrome, renderer).ShouldContain("Placement");
    }

    [Fact]
    public async Task WakeNowClearsTheSnooze()
    {
        using var renderer = new RgbaImageRenderer(1280, 2400);
        var source = Source(new BuddySettings().WithSnooze(1, Now, TimeZoneInfo.Utc));
        var chrome = await LoadedAsync(renderer, source, 1280, 2400);
        // The section's rows alone (every key holds "buddy"): the page is taller than the first screen, so the capture is taller too.
        chrome.Settings.Search.Text = "buddy";
        chrome.Settings.Search.CursorPos = 5;
        var texts = Texts(chrome, renderer);
        texts.ShouldContain("Snoozed until");
        texts.ShouldContain("Wake now");

        Click(chrome, "settings:buddy-wake-now", 2400);
        BuddySettings.Load(FilePath).SnoozedUntil.ShouldBeNull();
        var after = Texts(chrome, renderer);
        after.ShouldNotContain("Snoozed until");
        after.ShouldNotContain("Wake now");
    }

    [Fact]
    public async Task AChangeWrittenByTheCatsCardShowsWithoutARestart()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var source = Source();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed = () => changed.TrySetResult();
        var chrome = await LoadedAsync(renderer, source, 1280, 1400);
        Texts(chrome, renderer).ShouldNotContain("Snoozed until");

        // As the cat's card saves it: a temporary file moved over the settings.
        new BuddySettings { WakeAsk = false }.WithSnooze(2, Now, TimeZoneInfo.Utc).Save(FilePath).ShouldBeTrue();
        await changed.Task.WaitAsync(Deadline, Ct);

        source.Current.WakesForAsk.ShouldBeFalse();
        var texts = Texts(chrome, renderer);
        texts.ShouldContain("Snoozed until");
    }

    [Fact]
    public async Task TheSearchFindsTheRowsByTitleDescriptionAndKey()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var source = Source();
        var chrome = await LoadedAsync(renderer, source, 1280, 1400);
        foreach (var (query, title) in new[]
        {
            ("Click-through", "Click-through"),
            ("the window below", "Click-through"),
            ("buddy-corner", "Corner"),
            ("CI failed", "Wake for failures"),
            ("buddy-snooze", "Snooze for"),
            ("a flight landed", "Wake for merges"),
        })
        {
            chrome.Settings.Search.Text = query;
            chrome.Settings.Search.CursorPos = query.Length;
            var found = Texts(chrome, renderer);
            found.ShouldContain(title, $"query '{query}'");
            found.ShouldContain("Desktop cat");
            found.ShouldNotContain("Desktop toasts");
        }
    }

    [Fact]
    public async Task WithNoBuddyPathTheSectionIsLeftOut()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var source = Source();
        var chrome = await LoadedAsync(renderer, source, 1280, 1400);
        chrome.Settings.Buddy = null;
        Texts(chrome, renderer).ShouldNotContain("Desktop cat");
    }

    [Theory]
    [InlineData(755, false)]
    [InlineData(755, true)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    public async Task CapturesTheSectionBrightAndDarkNarrowAndWide(int width, bool night)
    {
        if ((Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is { Length: > 0 } given ? given : Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS")) is not { Length: > 0 } dir)
        {
            return;
        }
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var source = Source(new BuddySettings { WakeMerged = false, ClickThrough = true, Corner = 1 }.WithSnooze(2, Now, TimeZoneInfo.Utc));
        var chrome = await LoadedAsync(renderer, source, width, 900);
        chrome.Night.On = night;
        // The query keeps the section's rows alone (every key holds "buddy"), so the capture is the section.
        chrome.Settings.Search.Text = "buddy";
        chrome.Settings.Search.CursorPos = 5;
        Render(chrome, width, 900);
        File.WriteAllBytes(Path.Combine(dir, $"desktop-cat-{width}-{(night ? "dark" : "bright")}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 900));
    }
}
