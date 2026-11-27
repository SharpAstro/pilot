using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The rest of the Desktop cat section (#1373): the Running row (running or not, Start when not, the probe's answer after a start and not the click),
/// the Start at logon switch (the node's setup verb, shown from the check's answer) and the toasts row. Fakes only: no process, no gate, no registry.
/// </summary>
public sealed class DesktopCatRunningTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-cat-running-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

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

    /// <summary>The cat as a test sees it: what the probe says, how often it was started, and whether a start makes the probe say running.</summary>
    private sealed class FakeCat(bool running, bool startMakesItRun = true) : ICatDesk
    {
        public volatile bool Running = running;
        public int Starts;
        public int Probes;

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Probes);
            return Task.FromResult(Running);
        }

        public Task<string?> StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            if (startMakesItRun)
            {
                Running = true;
            }
            return Task.FromResult<string?>(null);
        }
    }

    private static readonly SettingsPage Sample = new()
    {
        File = @"C:\Users\ada\AppData\Local\pilot2\settings.json",
        Settings =
        [
            new SettingRow { Key = "toasts", Label = "Desktop toasts", Detail = "Which Ring cards show a Windows toast", Value = "all", Source = SettingSources.Default, Choices = ["all", "urgent", "off"] },
            new SettingRow { Key = "lan", Label = "On the LAN", Detail = "Listen on the LAN and announce it", Value = "on", Saved = "on", Source = SettingSources.File, Choices = ["on", "off"] },
        ],
    };

    private static SetupChecks Checks(bool logon, string? line = null, string? fix = "Start at logon") => new()
    {
        CheckedAt = Now,
        Checks =
        [
            new SetupCheck { Key = "autostart", Title = "Starts at logon", Ok = true, Line = @"C:\WINDOWS\system32\conhost.exe --headless C:\Users\ada\pilot-node.exe" },
            new SetupCheck
            {
                Key = "buddy-autostart", Title = "Desktop cat starts at logon", Ok = logon, Optional = true,
                Line = line ?? (logon ? @"C:\Users\ada\pilot-buddy.exe" : "the desktop cat is not started at logon; the node still is"),
                Fix = logon ? null : fix,
            },
        ],
    };

    private BuddySettingsSource Source()
    {
        Directory.CreateDirectory(_folder);
        var source = new BuddySettingsSource(Path.Combine(_folder, "buddy-settings.json"), new FixedClock(Now), TimeZoneInfo.Utc, TimeSpan.FromMilliseconds(20));
        _owned.Add(source);
        return source;
    }

    private sealed class Logon(bool on)
    {
        public volatile bool On = on;
        public readonly List<string[]> Sent = [];
    }

    private async Task<PilotChrome<RgbaImage>> LoadedAsync(RgbaImageRenderer renderer, ICatDesk? cat, Logon? logon, int width = 1280, int height = 1400)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.LoadGrants = _ => Task.FromResult<GrantInfo[]>([]);
        chrome.Settings.Buddy = Source();
        chrome.Settings.CatDesk = cat;
        if (logon is not null)
        {
            chrome.Settings.LoadChecks = (_, _) => Task.FromResult(Checks(logon.On));
            chrome.Settings.Send = (verb, args, _) =>
            {
                lock (logon.Sent)
                {
                    logon.Sent.Add([verb, .. args]);
                }
                logon.On = args[0] == "buddy-autostart";
                return Task.FromResult(new SettingsReply("done"));
            };
        }
        chrome.Show(PilotPage.Settings);
        Render(chrome, width, height);
        await WaitAsync(() => chrome.Settings.Page is not null && (logon is null || chrome.Settings.Checks is not null), chrome, width, height);
        return chrome;
    }

    private static void Render(PilotChrome<RgbaImage> chrome, int width, int height) =>
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, height));

    // A timer with a deadline, never a spin: the page reads on the pool, and a draw shows what it has read.
    private static async Task WaitAsync(Func<bool> done, PilotChrome<RgbaImage> chrome, int width, int height)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(Deadline);
        while (!done())
        {
            await Task.Delay(10, deadline.Token);
            Render(chrome, width, height);
        }
    }

    private static List<string> Texts(PilotChrome<RgbaImage> chrome, RgbaImageRenderer renderer, float width = 1280, float height = 1400)
    {
        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(Now, width), new Rect<float>(0, 0, width, height),
            new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];
    }

    private static void Click(PilotChrome<RgbaImage> chrome, string action)
    {
        Render(chrome, 1280, 1400);
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
    }

    private static bool HasButton(PilotChrome<RgbaImage> chrome, string action)
    {
        Render(chrome, 1280, 1400);
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        return regions.Any(r => r.Result is HitResult.ButtonHit b && b.Action == action);
    }

    [Fact]
    public async Task ARunningCatReadsRunningWithNoStart()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = await LoadedAsync(renderer, new FakeCat(running: true), null);
        // The row's title and its status are both "Running" once the probe has answered.
        await WaitAsync(() => Texts(chrome, renderer).Count(t => t == "Running") >= 2, chrome, 1280, 1400);

        HasButton(chrome, "settings:buddy-start").ShouldBeFalse();
    }

    [Fact]
    public async Task ACatThatIsNotRunningReadsSoAndOffersStart()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = await LoadedAsync(renderer, new FakeCat(running: false), null);
        await WaitAsync(() => Texts(chrome, renderer).Contains("Not running"), chrome, 1280, 1400);

        HasButton(chrome, "settings:buddy-start").ShouldBeTrue();
    }

    [Fact]
    public async Task StartCallsTheLauncherOnceAndTheRowShowsTheProbesAnswer()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var cat = new FakeCat(running: false);
        var chrome = await LoadedAsync(renderer, cat, null);
        await WaitAsync(() => Texts(chrome, renderer).Contains("Not running"), chrome, 1280, 1400);

        Click(chrome, "settings:buddy-start");
        await WaitAsync(() => Texts(chrome, renderer).Count(t => t == "Running") >= 2 && !Texts(chrome, renderer).Contains("Not running"), chrome, 1280, 1400);

        cat.Starts.ShouldBe(1);
        HasButton(chrome, "settings:buddy-start").ShouldBeFalse();
    }

    [Fact]
    public async Task AStartTheProbeDoesNotSeeLeavesTheRowNotRunning()
    {
        // The click is not the answer: a cat that did not take its gate is still "Not running", with Start offered again.
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var cat = new FakeCat(running: false, startMakesItRun: false);
        var chrome = await LoadedAsync(renderer, cat, null);
        await WaitAsync(() => Texts(chrome, renderer).Contains("Not running"), chrome, 1280, 1400);

        Click(chrome, "settings:buddy-start");
        await WaitAsync(() => cat.Starts == 1 && HasButton(chrome, "settings:buddy-start"), chrome, 1280, 1400);

        Texts(chrome, renderer).ShouldContain("Not running");
        Texts(chrome, renderer).Count(t => t == "Running").ShouldBe(1, "the title alone: no row says the cat runs");
    }

    [Fact]
    public async Task BeforeTheFirstAnswerTheRowSaysItIsChecking()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.Buddy = Source();
        // A probe that never answers.
        chrome.Settings.CatDesk = new NeverAnswers();
        chrome.Show(PilotPage.Settings);
        Render(chrome, 1280, 1400);
        await WaitAsync(() => chrome.Settings.Page is not null, chrome, 1280, 1400);

        Texts(chrome, renderer).ShouldContain("checking…");
        HasButton(chrome, "settings:buddy-start").ShouldBeFalse("no Start before the probe has said the cat is not there");
    }

    private sealed class NeverAnswers : ICatDesk
    {
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken) => new TaskCompletionSource<bool>().Task.WaitAsync(cancellationToken);

        public Task<string?> StartAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task TheLogonSwitchSendsTheNodesVerbAndShowsTheChecksAnswer()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var logon = new Logon(on: false);
        var chrome = await LoadedAsync(renderer, new FakeCat(true), logon);

        Click(chrome, "settings:fix:buddy-autostart");
        await WaitAsync(() => logon.On && chrome.Settings.Checks!.Checks.Single(c => c.Key == "buddy-autostart").Ok, chrome, 1280, 1400);
        lock (logon.Sent)
        {
            logon.Sent.ShouldBe([["setup", "buddy-autostart"]], ignoreOrder: false);
        }

        Click(chrome, "settings:fix:buddy-autostart");
        await WaitAsync(() => !logon.On && !chrome.Settings.Checks!.Checks.Single(c => c.Key == "buddy-autostart").Ok, chrome, 1280, 1400);
        lock (logon.Sent)
        {
            logon.Sent.Last().ShouldBe(["setup", "no-buddy-autostart"]);
        }
    }

    [Fact]
    public async Task TheCheckIsShownOnceNotAlsoInTheSetupList()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = await LoadedAsync(renderer, new FakeCat(true), new Logon(on: false));

        var texts = Texts(chrome, renderer);
        texts.ShouldContain("Start at logon");
        texts.ShouldNotContain("Desktop cat starts at logon");
        // The node's own start at logon is the Setup list's, as before.
        texts.ShouldContain("Starts at logon");
    }

    [Fact]
    public async Task WithNoCatSectionTheCheckStaysInTheSetupList()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = await LoadedAsync(renderer, null, new Logon(on: false));
        chrome.Settings.Buddy = null;

        Texts(chrome, renderer).ShouldContain("Desktop cat starts at logon");
    }

    [Fact]
    public async Task WithNothingToSwitchTheRowSaysWhyAndHasNoSwitch()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.Buddy = Source();
        chrome.Settings.LoadChecks = (_, _) => Task.FromResult(Checks(false, "no pilot-buddy beside the node, so there is no desktop cat to start at logon", fix: null));
        chrome.Settings.Send = (_, _, _) => Task.FromResult(new SettingsReply("done"));
        chrome.Show(PilotPage.Settings);
        Render(chrome, 1280, 1400);
        await WaitAsync(() => chrome.Settings.Checks is not null, chrome, 1280, 1400);

        Texts(chrome, renderer).ShouldContain("unavailable");
        HasButton(chrome, "settings:fix:buddy-autostart").ShouldBeFalse();
    }

    [Fact]
    public async Task TheToastsRowSaysWhatToastsDoWhileTheCatShowsTheCards()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = await LoadedAsync(renderer, new FakeCat(true), null);

        var texts = Texts(chrome, renderer);
        texts.ShouldContain("Toasts");
        texts.ShouldContain("Desktop toasts: all");
        texts.Any(t => t.Contains("while the cat shows the cards", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Running", "Running")]
    [InlineData("settings:buddy-running", "Running")]
    [InlineData("one per desktop", "Running")]
    [InlineData("buddy-autostart", "Start at logon")]
    [InlineData("apart from the node", "Start at logon")]
    [InlineData("urgent ones still toast", "Toasts")]
    [InlineData("buddy-toasts", "Toasts")]
    public async Task TheSearchFindsTheNewRows(string query, string title)
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var chrome = await LoadedAsync(renderer, new FakeCat(false), new Logon(on: true));
        // The key of the Running row is its action's name: both the key and the words are searched.
        chrome.Settings.Search.Text = query.Replace("settings:", "");
        chrome.Settings.Search.CursorPos = chrome.Settings.Search.Text.Length;

        var found = Texts(chrome, renderer);
        found.ShouldContain(title, $"query '{query}'");
        found.ShouldContain("Desktop cat");
    }

    [Theory]
    [InlineData(755, false, true)]
    [InlineData(755, true, true)]
    [InlineData(1280, false, true)]
    [InlineData(1280, true, true)]
    [InlineData(755, false, false)]
    [InlineData(755, true, false)]
    [InlineData(1280, false, false)]
    [InlineData(1280, true, false)]
    public async Task CapturesTheSectionBrightAndDarkNarrowAndWideCatRunningAndNot(int width, bool night, bool running)
    {
        if ((Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is { Length: > 0 } given ? given : Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS")) is not { Length: > 0 } dir)
        {
            return;
        }
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var chrome = await LoadedAsync(renderer, new FakeCat(running), new Logon(on: running), width, 900);
        chrome.Night.On = night;
        await WaitAsync(() => Texts(chrome, renderer, width, 900).Contains(running ? "Running" : "Not running"), chrome, width, 900);
        // The query keeps the section's rows alone (every key holds "buddy"), so the capture is the section.
        chrome.Settings.Search.Text = "buddy";
        chrome.Settings.Search.CursorPos = 5;
        Render(chrome, width, 900);
        File.WriteAllBytes(Path.Combine(dir, $"desktop-cat-running-{width}-{(night ? "dark" : "bright")}-{(running ? "running" : "not-running")}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 900));
    }
}
