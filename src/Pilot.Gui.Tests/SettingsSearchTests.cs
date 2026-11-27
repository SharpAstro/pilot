using System.Diagnostics;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Settings page's search box (#1374): it filters every row by title, description and key, and says so when nothing matches.</summary>
public sealed class SettingsSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SettingsPage Sample = new()
    {
        File = @"C:\Users\ada\AppData\Local\pilot2\settings.json",
        Settings =
        [
            new SettingRow { Key = "toasts", Label = "Desktop toasts", Detail = "Show a desktop toast for every Ring card", Value = "on", Source = SettingSources.Default, Choices = ["on", "off"] },
            new SettingRow { Key = "stale-recheck-hours", Label = "Stale recheck", Detail = "How long a green PR waits before it is looked at again", Value = "6", Source = SettingSources.Default },
            new SettingRow { Key = "lan", Label = "On the LAN", Detail = "Listen on the LAN and announce it", Value = "on", Saved = "on", Source = SettingSources.File, Choices = ["on", "off"] },
            new SettingRow { Key = "update-channel", Label = "Update channel", Detail = "stable or beta", Value = "stable", Source = SettingSources.Default, Choices = ["stable", "beta"] },
        ],
    };

    private static SetupChecks Measured(DateTimeOffset now) => new()
    {
        CheckedAt = now,
        Checks =
        [
            new SetupCheck { Key = "autostart", Title = "Starts at logon", Ok = true, Line = @"C:\WINDOWS\system32\conhost.exe --headless pilot-node.exe" },
            new SetupCheck { Key = "firewall", Title = "Firewall", Ok = false, Runs = "runs netsh", Line = "no rule pilot2 node (TCP 47731): other machines' connections are dropped", Fix = "Allow (asks for elevation)" },
        ],
    };

    private static PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer, SettingsPage? page = null)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var shown = page ?? Sample;
        chrome.Settings.Load = _ => Task.FromResult(shown);
        chrome.Settings.LoadChecks = (_, _) => Task.FromResult(Measured(DateTimeOffset.UtcNow));
        chrome.Settings.LoadGrants = _ => Task.FromResult<GrantInfo[]>([]);
        chrome.Show(PilotPage.Settings);
        return chrome;
    }

    private static async Task<PilotChrome<RgbaImage>> LoadedAsync(RgbaImageRenderer renderer, int width, int height, SettingsPage? page = null)
    {
        var chrome = Chrome(renderer, page);
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, height));
        for (var i = 0; i < 200 && (chrome.Settings.Page is null || chrome.Settings.Checks is null); i++)
        {
            await Task.Delay(25, Ct);
        }
        chrome.Settings.Page.ShouldNotBeNull();
        chrome.Settings.Checks.ShouldNotBeNull();
        return chrome;
    }

    private static void Type(PilotChrome<RgbaImage> chrome, string text)
    {
        chrome.Settings.Search.Text = text;
        chrome.Settings.Search.CursorPos = text.Length;
    }

    private static List<string> Texts(PilotChrome<RgbaImage> chrome, RgbaImageRenderer renderer, float width = 1280, float height = 900)
    {
        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(DateTimeOffset.UtcNow, width), new Rect<float>(0, 0, width, height),
            new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];
    }

    [Fact]
    public async Task ATitleFindsItsRowAndHidesTheRestAndTheEmptyHeadings()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = await LoadedAsync(renderer, 1280, 900);
        var all = Texts(chrome, renderer);
        all.ShouldContain("Setup");
        all.ShouldContain("Desktop toasts");
        all.ShouldContain("On the LAN");

        Type(chrome, "DESKTOP toasts");
        var found = Texts(chrome, renderer);
        found.ShouldContain("Desktop toasts");
        found.ShouldNotContain("On the LAN");
        found.ShouldNotContain("Update channel");
        // The Setup section has no row left, so its heading goes; Settings keeps its own.
        found.ShouldNotContain("Setup");
        found.ShouldContain("Settings");
    }

    [Fact]
    public async Task ADescriptionFindsItsRow()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = await LoadedAsync(renderer, 1280, 900);
        Type(chrome, "announce it");
        var found = Texts(chrome, renderer);
        found.ShouldContain("On the LAN");
        found.ShouldNotContain("Desktop toasts");
        found.ShouldNotContain("Stale recheck");
    }

    [Theory]
    [InlineData("toasts", "Desktop toasts")]
    [InlineData("stale-recheck-hours", "Stale recheck")]
    [InlineData("update-channel", "Update channel")]
    [InlineData("autostart", "Starts at logon")]
    public async Task AKeyFindsItsRowAndHidesTheRest(string key, string title)
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = await LoadedAsync(renderer, 1280, 900);
        Type(chrome, key);
        var found = Texts(chrome, renderer);
        found.ShouldContain(title);
        foreach (var other in new[] { "Desktop toasts", "Stale recheck", "On the LAN", "Update channel", "Starts at logon", "Firewall" }.Where(t => t != title))
        {
            found.ShouldNotContain(other);
        }
    }

    [Fact]
    public void AnEmptyQueryKeepsEverythingAndAGrantsSubheadGoesWithItsRows()
    {
        SettingsView<RgbaImage>.Piece<string>[] pieces =
        [
            new(SettingsView<RgbaImage>.Part.Section, [], "Grants"),
            new(SettingsView<RgbaImage>.Part.Subhead, [], "Given"),
            new(SettingsView<RgbaImage>.Part.Row, ["ada"], "row-ada"),
            new(SettingsView<RgbaImage>.Part.Subhead, [], "Held"),
            new(SettingsView<RgbaImage>.Part.Row, ["grace"], "row-grace"),
            new(SettingsView<RgbaImage>.Part.Section, [], "Other"),
            new(SettingsView<RgbaImage>.Part.Row, ["zed"], "row-zed"),
        ];
        SettingsView<RgbaImage>.Filter<string>(pieces, "  ").Count.ShouldBe(7);
        SettingsView<RgbaImage>.Filter<string>(pieces, "GRACE").ShouldBe(["Grants", "Held", "row-grace"]);
        SettingsView<RgbaImage>.Filter<string>(pieces, "nothing").ShouldBeEmpty();
        SettingsView<RgbaImage>.Matches("Ab", "xxABxx").ShouldBeTrue();
        SettingsView<RgbaImage>.Matches("ab", null, "xx").ShouldBeFalse();
    }

    [Fact]
    public async Task NoMatchShowsTheLineAndClearEmptiesTheBox()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = await LoadedAsync(renderer, 1280, 900);
        Type(chrome, "zzz");
        var texts = Texts(chrome, renderer);
        texts.ShouldContain("No setting matches 'zzz'");
        texts.ShouldNotContain("Setup");
        texts.ShouldNotContain("Settings");

        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 900));
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == "settings:search:clear").OnClick!.Invoke(InputModifier.None);
        chrome.Settings.Search.Text.ShouldBe("");
        Texts(chrome, renderer).ShouldContain("Desktop toasts");
    }

    [Fact]
    public async Task EscapeClearsTheBoxAndAnEmptyBoxLeavesEscapeAlone()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = await LoadedAsync(renderer, 1280, 900);
        Type(chrome, "lan");
        chrome.HandleInput(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        chrome.Settings.Search.Text.ShouldBe("");
        chrome.Settings.HandleSearchKey(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeFalse();
    }

    [Fact]
    public async Task CtrlFFocusesTheBox()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = await LoadedAsync(renderer, 1280, 900);
        chrome.Settings.Search.IsActive.ShouldBeFalse();
        chrome.HandleInput(new InputEvent.KeyDown(InputKey.F, InputModifier.Ctrl)).ShouldBeTrue();
        chrome.Settings.Search.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task TheListStaysAtTheTopWhileTheQueryChanges()
    {
        // Many rows, so the list scrolls.
        var many = Sample with
        {
            Settings = [.. Enumerable.Range(0, 40).Select(i => new SettingRow { Key = $"key-{i}", Label = $"Setting {i}", Detail = $"detail {i}", Value = "on", Source = SettingSources.Default, Choices = ["on", "off"] })],
        };
        using var renderer = new RgbaImageRenderer(755, 500);
        var chrome = await LoadedAsync(renderer, 755, 500, many);
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 755, 500));
        chrome.HandleInput(new InputEvent.Scroll(-5f, 300f, 300f, InputModifier.None));
        chrome.Settings.ScrollOffset.ShouldBeGreaterThan(0f);
        Type(chrome, "setting 3");
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 755, 500));
        chrome.Settings.ScrollOffset.ShouldBe(0f);
    }

    [Theory]
    [InlineData(755, false)]
    [InlineData(755, true)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    public async Task CapturesUnfilteredOneRowAndNoMatch(int width, bool night)
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is not { Length: > 0 } dir)
        {
            return;
        }
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var chrome = await LoadedAsync(renderer, width, 900);
        chrome.Night.On = night;
        foreach (var (name, query) in new[] { ("all", ""), ("one", "stale-recheck-hours"), ("none", "no such setting, a long query to see how it is kept whole") })
        {
            Type(chrome, query);
            chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 900));
            File.WriteAllBytes(Path.Combine(dir, $"settings-search-{name}-{width}-{(night ? "dark" : "bright")}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 900));
        }
    }

    /// <summary>The cost of a keystroke with the full list: the tree and its arrangement, reported (never asserted: a loaded machine inflates wall clock).</summary>
    [Fact]
    public async Task ReportsTheFrameTimeOfAKeystrokeWithTheFullList()
    {
        var many = Sample with
        {
            Settings = [.. Enumerable.Range(0, 60).Select(i => new SettingRow { Key = $"key-{i}", Label = $"Setting {i}", Detail = $"detail of setting {i}", Value = "on", Source = SettingSources.Default, Choices = ["on", "off"] })],
        };
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = await LoadedAsync(renderer, 1280, 900, many);
        var measure = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f);
        const string query = "setting 5";
        var times = new List<double>();
        for (var length = 0; length <= query.Length; length++)
        {
            Type(chrome, query[..length]);
            var watch = Stopwatch.StartNew();
            var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(DateTimeOffset.UtcNow, 1280), new Rect<float>(0, 0, 1280, 900), measure);
            watch.Stop();
            arranged.IsDefault.ShouldBeFalse();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        var line = $"settings search, 60 settings, Tree + Arrange per keystroke: first {times[0]:F1} ms, max {times.Max():F1} ms, mean {times.Average():F1} ms over {times.Count} keystrokes";
        TestContext.Current.SendDiagnosticMessage(line);
        System.Console.Error.WriteLine(line);
    }
}
