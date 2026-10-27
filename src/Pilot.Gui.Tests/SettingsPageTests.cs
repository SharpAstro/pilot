using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Settings page: the checklist with its fixes and the settings with their choices, each a verb on the node.</summary>
public sealed class SettingsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SettingsPage Sample = new()
    {
        File = @"C:\Users\ada\AppData\Local\pilot2\settings.json",
        Settings =
        [
            new SettingRow { Key = "lan", Label = "On the LAN", Detail = "Listen on the LAN and announce it", Value = "on", Saved = "on", Source = SettingSources.File, Choices = ["on", "off"] },
            new SettingRow { Key = "machine", Label = "Machine prefix", Detail = "Two letters and a number", Value = "DE1", Source = SettingSources.Default },
            new SettingRow { Key = "repo-roots", Label = "Repo roots", Detail = "Folders of git clones the new-flight wizard offers", Value = @"C:\src\sharpastro" + "\n" + @"C:\src\my org", Saved = @"C:\src\sharpastro" + "\n" + @"C:\src\my org", Source = SettingSources.File, Many = true },
            new SettingRow { Key = "update-channel", Label = "Update channel", Detail = "stable or beta", Value = "stable", Saved = "beta", Source = SettingSources.File, Choices = ["stable", "beta"], Pending = true },
        ],
    };

    /// <summary>The checks as the node measured them, <paramref name="age"/> before <paramref name="now"/>.</summary>
    private static SetupChecks Measured(DateTimeOffset now, TimeSpan age) => new()
    {
        CheckedAt = now - age,
        Checks =
        [
            new SetupCheck { Key = "autostart", Title = "Starts at logon", Ok = true, Line = @"C:\WINDOWS\system32\conhost.exe --headless C:\Users\ada\AppData\Local\Programs\pilot2\pilot-node.exe" },
            new SetupCheck { Key = "hooks", Title = "Claude Code hooks", Ok = true, Line = @"in C:\Users\ada\.claude\settings.json" },
            new SetupCheck { Key = "lan", Title = "On the LAN", Ok = true, Line = "listening on port 47731 and announcing itself as DE1" },
            new SetupCheck { Key = "firewall", Title = "Firewall", Ok = false, Runs = "runs netsh", Line = "no rule named pilot-node: other machines' connections are dropped before they reach the node", Fix = "Allow (asks for elevation)" },
            new SetupCheck { Key = "trust", Title = "Watchtower folder trusted", Ok = true, Line = @"C:\Users\ada\AppData\Local\pilot2\watchtower, through C:\" },
            new SetupCheck { Key = "gh", Title = "GitHub CLI signed in", Ok = true, Runs = "runs gh, which asks GitHub", Line = "Logged in to github.com account ada (keyring)" },
            new SetupCheck { Key = "usage", Title = "Usage figures", Ok = false, Optional = true, Line = "no rate-limits.json: a status line that writes it gives the fleet this machine's usage" },
        ],
    };

    [Fact]
    public void TheLineWaitingForTheSettingsSitsAtTheTopOfThePageNotDownItsMiddle()
    {
        using var renderer = new RgbaImageRenderer(755, 1180);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        // A read that never answers: the page waits on it.
        chrome.Settings.Load = _ => new TaskCompletionSource<SettingsPage>().Task;
        chrome.Show(PilotPage.Settings);

        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 755, 1180));
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is { Length: > 0 } dir)
        {
            File.WriteAllBytes(Path.Combine(dir, "settings-reading-755.png"), PngWriter.Encode(renderer.Surface.Pixels, 755, 1180));
        }

        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(DateTimeOffset.UtcNow), new Rect<float>(0, 0, 677, 1180),
            new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var line = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var text } } && text.StartsWith("reading the node's settings", StringComparison.Ordinal)).Bounds;
        line.Y.ShouldBeLessThan(200f);
        line.X.ShouldBeGreaterThanOrEqualTo(8f);
    }

    [Fact]
    public async Task ThePageShowsTheChecklistAndTheSettingsAndEachButtonIsAVerb()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sent = new List<(string Verb, string[] Args)>();
        var loaded = new TaskCompletionSource();
        var measured = Measured(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3));
        chrome.Settings.Load = _ =>
        {
            loaded.TrySetResult();
            return Task.FromResult(Sample);
        };
        chrome.Settings.LoadChecks = (_, _) => Task.FromResult(measured);
        chrome.Settings.Send = (verb, args, _) =>
        {
            lock (sent)
            {
                sent.Add((verb, args));
            }
            return Task.FromResult(new SettingsReply("done"));
        };
        chrome.Show(PilotPage.Settings);

        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 900));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await WaitAsync(() => chrome.Settings.Page is not null && chrome.Settings.Checks is not null);
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 900));
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            File.WriteAllBytes(capture, PngWriter.Encode(renderer.Surface.Pixels, 1280, 900));
        }

        SettingsView<RgbaImage>.Summary(Sample, measured).ShouldBe("1 of 6 setup steps left · 1 saved for the next start");
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain("settings:fix:firewall");
        buttons.ShouldNotContain("settings:fix:autostart");
        buttons.ShouldContain("settings:update-channel:beta");
        buttons.ShouldContain(@"settings:repo-roots:remove:C:\src\my org");

        Click(regions, "settings:fix:firewall");
        Click(regions, "settings:update-channel:stable");
        Click(regions, @"settings:repo-roots:remove:C:\src\my org");
        await WaitAsync(() => { lock (sent) { return sent.Count == 3; } });
        lock (sent)
        {
            sent.ShouldContain(s => s.Verb == Verbs.Setup && s.Args.SequenceEqual(new[] { "firewall" }));
            sent.ShouldContain(s => s.Verb == Verbs.Set && s.Args.SequenceEqual(new[] { "update-channel", "stable" }));
            sent.ShouldContain(s => s.Verb == Verbs.Set && s.Args.SequenceEqual(new[] { "repo-roots", @"C:\src\sharpastro" }));
        }
    }

    private const string DefenderLong = @"not excluded, as recorded at the last fix on 2026-10-20: C:\src\sharpastro\pilot, C:\src\sharpastro\tianwen, C:\src\sharpastro\DIR.Lib, C:\src\sharpastro\Console.Lib, C:\src\sharpastro\SdlVulkan.Renderer, C:\Users\ada\.nuget\packages, C:\Program Files\dotnet: every file a build and its tests write is scanned";

    /// <summary>A failing defender check shows its Fix button, which sends the node's setup verb; its long list of missing folders wraps and keeps the whole text on hover.</summary>
    [Theory]
    [InlineData(700, false)]
    [InlineData(1280, false)]
    [InlineData(700, true)]
    [InlineData(1280, true)]
    public async Task AFailingDefenderCheckShowsItsFixAndSendsSetupDefender(int width, bool ok)
    {
        var defender = ok
            ? new SetupCheck { Key = "defender", Title = "Windows Defender exclusions", Ok = true, Optional = true, Line = "7 folders excluded, as recorded at the last fix on 2026-10-27" }
            : new SetupCheck { Key = "defender", Title = "Windows Defender exclusions", Ok = false, Optional = true, Line = DefenderLong, Fix = "Exclude (asks for elevation)" };
        var checks = Measured(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3));
        checks = checks with { Checks = [.. checks.Checks.Where(c => c.Key != "defender"), defender] };
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sent = new List<(string Verb, string[] Args)>();
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.LoadChecks = (_, _) => Task.FromResult(checks);
        chrome.Settings.Send = (verb, args, _) =>
        {
            lock (sent)
            {
                sent.Add((verb, args));
            }
            return Task.FromResult(new SettingsReply("done"));
        };
        chrome.Show(PilotPage.Settings);

        void Draw() => chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 900));
        Draw();
        await WaitAsync(() => chrome.Settings.Page is not null && chrome.Settings.Checks is not null);
        Draw();
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is { Length: > 0 } dir)
        {
            File.WriteAllBytes(Path.Combine(dir, $"settings-defender-{(ok ? "ok" : "failing")}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 900));
        }

        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        if (ok)
        {
            buttons.ShouldNotContain("settings:fix:defender");
            return;
        }
        buttons.ShouldContain("settings:fix:defender");
        Click(regions, "settings:fix:defender");
        await WaitAsync(() => { lock (sent) { return sent.Count == 1; } });
        lock (sent)
        {
            sent.ShouldContain(s => s.Verb == Verbs.Setup && s.Args.SequenceEqual(new[] { "defender" }));
        }
    }

    /// <summary>Maintenance and accept-fleet-flights are on / off rows: a pill each, the saved one chosen, and a pill is the node's set verb.</summary>
    [Fact]
    public async Task MaintenanceAndAcceptFleetFlightsAreOnOffRowsThatSendSet()
    {
        var page = Sample with
        {
            Settings =
            [
                new SettingRow { Key = "maintenance", Label = "Maintenance", Detail = "While on, this node takes no new flight", Value = "off", Source = SettingSources.Default, Choices = ["on", "off"] },
                new SettingRow { Key = "accept-fleet-flights", Label = "Accept fleet flights", Detail = "Willing to take a flight another node routes to it", Value = "off", Source = SettingSources.Default, Choices = ["on", "off"] },
            ],
        };
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sent = new List<(string Verb, string[] Args)>();
        chrome.Settings.Load = _ => Task.FromResult(page);
        chrome.Settings.Send = (verb, args, _) =>
        {
            lock (sent)
            {
                sent.Add((verb, args));
            }
            return Task.FromResult(new SettingsReply("done"));
        };
        chrome.Show(PilotPage.Settings);

        void Draw() => chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 900));
        Draw();
        await WaitAsync(() => chrome.Settings.Page is not null);
        Draw();
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();

        buttons.ShouldContain("settings:maintenance:on");
        buttons.ShouldContain("settings:maintenance:off");
        buttons.ShouldContain("settings:accept-fleet-flights:on");
        buttons.ShouldContain("settings:accept-fleet-flights:off");
        Click(regions, "settings:maintenance:on");
        Click(regions, "settings:accept-fleet-flights:on");
        await WaitAsync(() => { lock (sent) { return sent.Count == 2; } });
        lock (sent)
        {
            sent.ShouldContain(s => s.Verb == Verbs.Set && s.Args.SequenceEqual(new[] { "maintenance", "on" }));
            sent.ShouldContain(s => s.Verb == Verbs.Set && s.Args.SequenceEqual(new[] { "accept-fleet-flights", "on" }));
        }
    }

    [Fact]
    public async Task TheGrantsSectionListsWhatThisNodeGaveWithRevokeAndWhatYouHoldWithForget()
    {
        var tokens = Path.Combine(Path.GetTempPath(), $"pilot-held-{Guid.NewGuid():N}.json");
        try
        {
            Pilot.Commands.NodeTargets.Save("le1.local:47731", "tok-a", tokens);
            Pilot.Commands.NodeTargets.Save("se2.local:47731", "tok-b", tokens);
            var given = new List<GrantInfo> { new() { Id = "g1", Label = "se2-gui", GrantedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero) } };
            var forgotten = new List<string>();
            using var renderer = new RgbaImageRenderer(1280, 2000);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            var sent = new List<(string Verb, string[] Args)>();
            chrome.Settings.Load = _ => Task.FromResult(Sample);
            chrome.Settings.LoadGrants = _ =>
            {
                lock (given)
                {
                    return Task.FromResult(given.ToArray());
                }
            };
            chrome.Settings.LoadHeld = () => Pilot.Commands.NodeTargets.Granted(tokens);
            chrome.Settings.Forget = endpoint =>
            {
                forgotten.Add(endpoint);
                Pilot.Commands.NodeTargets.Forget(endpoint, tokens);
            };
            chrome.Settings.Send = (verb, args, _) =>
            {
                lock (sent)
                {
                    sent.Add((verb, args));
                }
                if (verb == Verbs.RevokeGrant)
                {
                    lock (given)
                    {
                        given.RemoveAll(g => g.Id == args[0]);
                    }
                }
                return Task.FromResult(new SettingsReply("done"));
            };
            chrome.Show(PilotPage.Settings);

            void Draw() => chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 2000));
            Draw();
            await WaitAsync(() => chrome.Settings.Page is not null && chrome.Settings.Given.Length == 1);
            Draw();
            var regions = new List<ClickableRegion>();
            chrome.Settings.CollectPaintedRegions(regions);
            var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
            buttons.ShouldContain("settings:revoke:g1");
            buttons.ShouldContain("settings:forget:le1.local:47731");
            buttons.ShouldContain("settings:forget:se2.local:47731");

            // Revoke is the node's revoke-grant for that id, and the list reads again without it.
            Click(regions, "settings:revoke:g1");
            await WaitAsync(() => { lock (sent) { return sent.Any(s => s.Verb == Verbs.RevokeGrant && s.Args.SequenceEqual(new[] { "g1" })); } });
            await WaitAsync(() => chrome.Settings.Given.Length == 0);

            // Forget drops the token from the file; the page lists one node fewer. The file is written off the render thread,
            // so it is waited for.
            Click(regions, "settings:forget:le1.local:47731");
            await WaitAsync(() => Pilot.Commands.NodeTargets.TokenFor("le1.local:47731", tokens) is null);
            forgotten.ShouldBe(["le1.local:47731"]);
            Pilot.Commands.NodeTargets.Granted(tokens).ShouldBe(["se2.local:47731"]);
            Pilot.Commands.NodeTargets.TokenFor("le1.local:47731", tokens).ShouldBeNull();
            Pilot.Commands.NodeTargets.Forget("le1.local:47731", tokens).ShouldBeFalse();

            // The steerable set, read again from the file as the fleet tick does, turns that node's rows watch-only.
            var held = new HashSet<string>(Pilot.Commands.NodeTargets.Granted(tokens), StringComparer.OrdinalIgnoreCase);
            using var here = NodeClient.Remote("127.0.0.1:1");
            using var clients = new FleetClients(here, held.Contains);
            clients.CanSteer(new NodeMark("le1.local:47731", "le1.local:47731", "LE1", Here: false)).ShouldBeFalse();
            clients.CanSteer(new NodeMark("se2.local:47731", "se2.local:47731", "SE2", Here: false)).ShouldBeTrue();
        }
        finally
        {
            // A background read of the token file can still hold it for a moment when the whole suite loads the machine (Windows
            // refuses the delete then); the file is a temp one, so a stubborn lock is left to the temp folder rather than failing the test.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Delete(tokens);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }
            }
        }
    }

    /// <summary>The page drawn with the clock the chrome gives it, as the rows are drawn: the words each shows and the buttons it offers.</summary>
    private static (string[] Texts, string[] Buttons) Look(PilotChrome<RgbaImage> chrome, RgbaImageRenderer renderer, DateTimeOffset now)
    {
        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(now), new Rect<float>(0, 0, 1280, 3000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return (
            [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())],
            [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(h => h.Action)]);
    }

    private static PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    private static void Draw(PilotChrome<RgbaImage> chrome) => chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 2000));

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome)
    {
        Draw(chrome);
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        return regions;
    }

    /// <summary>The settings are drawn as soon as they are read; each check row says "checking…" until the checks answer, and then how old they are.</summary>
    [Fact]
    public async Task TheSettingsShowWhileTheChecksAreStillBeingRead()
    {
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = Chrome(renderer);
        var now = DateTimeOffset.UtcNow;
        var checksGate = new TaskCompletionSource<SetupChecks>(TaskCreationOptions.RunContinuationsAsynchronously);
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.LoadChecks = (_, _) => checksGate.Task;
        chrome.Show(PilotPage.Settings);

        Draw(chrome);
        await WaitAsync(() => chrome.Settings.Page is not null);
        var (texts, _) = Look(chrome, renderer, now);
        texts.ShouldContain("Machine prefix");
        texts.ShouldContain("Update channel");
        texts.ShouldContain(t => t.StartsWith("checking…", StringComparison.Ordinal));
        texts.ShouldNotContain(t => t.Contains("checked ", StringComparison.Ordinal));
        chrome.Settings.Checks.ShouldBeNull();
        // The header counts setup steps once they are known, and says what it can meanwhile.
        SettingsView<RgbaImage>.Summary(Sample, null).ShouldBe("1 saved for the next start");

        checksGate.SetResult(Measured(now, TimeSpan.FromSeconds(12)));
        await WaitAsync(() => chrome.Settings.Checks is not null);
        (texts, _) = Look(chrome, renderer, now);
        texts.ShouldNotContain(t => t.StartsWith("checking…", StringComparison.Ordinal));
        texts.ShouldContain("done · checked 12s ago");
        texts.ShouldContain("to do · checked 12s ago");
        texts.ShouldContain("optional · checked 12s ago");
        // A measurement that has aged is marked, not shown as current.
        Look(chrome, renderer, now + TimeSpan.FromMinutes(3)).Texts.ShouldContain("done · checked 3m ago (stale)");
    }

    /// <summary>Refresh reads the checks again with refresh on, the rows say what that runs while it is out, and the age starts over.</summary>
    [Fact]
    public async Task EachCheckRowShowsItsAgeAndRefreshReloadsWithRefresh()
    {
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = Chrome(renderer);
        var now = DateTimeOffset.UtcNow;
        var asked = new List<bool>();
        TaskCompletionSource<SetupChecks>? gate = null;
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.LoadChecks = (refresh, _) =>
        {
            lock (asked)
            {
                asked.Add(refresh);
            }
            return refresh ? (gate = new TaskCompletionSource<SetupChecks>(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.FromResult(Measured(now, TimeSpan.FromSeconds(12)));
        };
        // A fix is offered only where the page can send it.
        chrome.Settings.Send = (_, _, _) => Task.FromResult(new SettingsReply("done"));
        chrome.Show(PilotPage.Settings);

        Draw(chrome);
        await WaitAsync(() => chrome.Settings.Checks is not null);
        Look(chrome, renderer, now).Texts.ShouldContain("done · checked 12s ago");
        Look(chrome, renderer, now).Buttons.ShouldContain("settings:checks:refresh");

        Click(Regions(chrome), "settings:checks:refresh");
        await WaitAsync(() => gate is not null);
        lock (asked)
        {
            asked.ShouldBe([false, true]);
        }
        // While the node measures: each row says what reading it runs, and the old lines are not shown as current.
        var (texts, buttons) = Look(chrome, renderer, now);
        texts.ShouldContain("checking… (runs netsh)");
        texts.ShouldContain("checking… (runs gh, which asks GitHub)");
        texts.ShouldContain("checking… (reads files)");
        texts.ShouldNotContain(t => t.StartsWith("no rule named", StringComparison.Ordinal));
        buttons.ShouldNotContain("settings:checks:refresh");
        buttons.ShouldNotContain("settings:fix:firewall");

        gate!.SetResult(Measured(now, TimeSpan.Zero));
        await WaitAsync(() => chrome.Settings.Checks!.CheckedAt == now);
        (texts, buttons) = Look(chrome, renderer, now);
        texts.ShouldContain("done · checked 0s ago");
        texts.ShouldNotContain(t => t.StartsWith("checking…", StringComparison.Ordinal));
        buttons.ShouldContain("settings:checks:refresh");
        buttons.ShouldContain("settings:fix:firewall");
    }

    /// <summary>A set's reply carries the changed row: it replaces the shown one at once, and neither the settings nor the checks are read again.</summary>
    [Fact]
    public async Task ASetsReplyRowReplacesTheShownRowWithNoSecondRead()
    {
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = Chrome(renderer);
        var now = DateTimeOffset.UtcNow;
        var loads = 0;
        var checkLoads = 0;
        var changed = new SettingRow { Key = "update-channel", Label = "Update channel", Detail = "stable or beta", Value = "beta", Saved = "beta", Source = SettingSources.File, Choices = ["stable", "beta"] };
        chrome.Settings.Load = _ =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult(Sample);
        };
        chrome.Settings.LoadChecks = (_, _) =>
        {
            Interlocked.Increment(ref checkLoads);
            return Task.FromResult(Measured(now, TimeSpan.FromSeconds(1)));
        };
        chrome.Settings.Send = (_, _, _) => Task.FromResult(new SettingsReply("update-channel saved as beta; the node runs with it already", ReplyKind.Done, changed));
        chrome.Show(PilotPage.Settings);

        Draw(chrome);
        await WaitAsync(() => chrome.Settings.Page is not null && chrome.Settings.Checks is not null);
        chrome.Settings.Page!.Settings.Single(s => s.Key == "update-channel").Pending.ShouldBeTrue();
        Look(chrome, renderer, now).Texts.ShouldContain("saved as beta: applies at the next start");

        Click(Regions(chrome), "settings:update-channel:beta");
        await WaitAsync(() => !chrome.Settings.Page!.Settings.Single(s => s.Key == "update-channel").Pending);

        var row = chrome.Settings.Page!.Settings.Single(s => s.Key == "update-channel");
        row.Value.ShouldBe("beta");
        row.Saved.ShouldBe("beta");
        Look(chrome, renderer, now).Texts.ShouldNotContain("saved as beta: applies at the next start");
        chrome.Settings.Said.ShouldBe("update-channel saved as beta; the node runs with it already");
        // The other rows are as they were, and no read came for the answer.
        chrome.Settings.Page.Settings.Select(s => s.Key).ShouldBe(Sample.Settings.Select(s => s.Key));
        Volatile.Read(ref loads).ShouldBe(1);
        Volatile.Read(ref checkLoads).ShouldBe(1);
    }

    /// <summary>A pressed choice shows it is working until the node answers and is not clicked again; a refusal leaves the node's value and says why.</summary>
    [Fact]
    public async Task APressedChoiceShowsWorkingUntilTheReplyAndKeepsTheOldValueIfRefused()
    {
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = Chrome(renderer);
        var now = DateTimeOffset.UtcNow;
        var reply = new TaskCompletionSource<SettingsReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        chrome.Settings.Load = _ => Task.FromResult(Sample);
        chrome.Settings.LoadChecks = (_, _) => Task.FromResult(Measured(now, TimeSpan.FromSeconds(1)));
        chrome.Settings.Send = (_, _, _) =>
        {
            Interlocked.Increment(ref sends);
            return reply.Task;
        };
        chrome.Show(PilotPage.Settings);

        Draw(chrome);
        await WaitAsync(() => chrome.Settings.Page is not null && chrome.Settings.Checks is not null);
        var (texts, buttons) = Look(chrome, renderer, now);
        texts.ShouldNotContain("off…");
        buttons.ShouldContain("settings:lan:off");

        var regions = Regions(chrome);
        Click(regions, "settings:lan:off");
        // Pressed twice, it is sent once; the value is still the node's.
        Click(regions, "settings:lan:off");
        (texts, buttons) = Look(chrome, renderer, now);
        texts.ShouldContain("off…");
        buttons.ShouldNotContain("settings:lan:off");
        buttons.ShouldContain("settings:lan:on");
        chrome.Settings.Page!.Settings.Single(s => s.Key == "lan").Value.ShouldBe("on");
        // The send runs on the thread pool, so it is waited for; the second press was refused at once, before it.
        await WaitAsync(() => Volatile.Read(ref sends) > 0);

        reply.SetResult(new SettingsReply("refused: the node's command line gives lan, and wins", ReplyKind.Refused));
        await WaitAsync(() => chrome.Settings.Said!.StartsWith("refused", StringComparison.Ordinal));
        (texts, buttons) = Look(chrome, renderer, now);
        texts.ShouldNotContain("off…");
        texts.ShouldContain("off");
        buttons.ShouldContain("settings:lan:off");
        chrome.Settings.Page.Settings.Single(s => s.Key == "lan").Value.ShouldBe("on");
        chrome.Settings.Said.ShouldBe("refused: the node's command line gives lan, and wins");
        Volatile.Read(ref sends).ShouldBe(1);
    }

    /// <summary>Captures of the page narrow and wide, checks loading and loaded, with long lines, into the folder named by PILOT_TEST_CAPTURE_DIR (nothing without it).</summary>
    [Fact]
    public async Task ThePageCapturedNarrowAndWideWhileTheChecksLoadAndOnceLoaded()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is not { Length: > 0 } dir)
        {
            return;
        }
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        var page = Sample with
        {
            Restart = "waiting for a job to finish before restarting the node for update-channel",
            Settings = [.. Sample.Settings, new SettingRow { Key = "machine-long", Label = "A setting with a long label that has to wrap or end", Detail = "A long detail line that says a good deal about what this setting does, and what the node does when it changes", Value = @"C:\Users\a-very-long-user-name\source\repos\an-org-with-a-long-name\a-repo-with-a-long-name", Source = SettingSources.Default }],
        };
        var measured = Measured(now, TimeSpan.FromSeconds(12));
        measured = measured with { Checks = [.. measured.Checks.Select(c => c.Key == "firewall" ? c with { Line = c.Line + ": a long explanation that goes on, and says what to do about it, and where to look when that does not help" } : c)] };
        foreach (var width in new[] { 700, 1400 })
        {
            using var renderer = new RgbaImageRenderer((uint)width, 1500);
            var chrome = Chrome(renderer);
            var gate = new TaskCompletionSource<SetupChecks>(TaskCreationOptions.RunContinuationsAsynchronously);
            chrome.Settings.Load = _ => Task.FromResult(page);
            chrome.Settings.LoadChecks = (_, _) => gate.Task;
            chrome.Settings.Send = (_, _, _) => Task.FromResult(new SettingsReply("done"));
            chrome.Show(PilotPage.Settings);
            void Shot(string name)
            {
                chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 1500));
                File.WriteAllBytes(Path.Combine(dir, $"settings-{width}-{name}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 1500));
            }
            Shot("start");
            await WaitAsync(() => chrome.Settings.Page is not null);
            Shot("checks-loading");
            gate.SetResult(measured);
            await WaitAsync(() => chrome.Settings.Checks is not null);
            Shot("checks-loaded");
        }
    }

    private static readonly RepoInfo[] Repos =
    [
        new() { Name = "tianwen", Path = @"C:\src\sharpastro\tianwen", GitHub = "SharpAstro/tianwen", Flights = 9 },
        new() { Name = "pilot", Path = @"C:\src\sharpastro\pilot", GitHub = "SharpAstro/pilot", Flights = 7 },
        new() { Name = "widget", Path = @"C:\src\my org\widget", Flights = 1 },
        new() { Name = "a-repo-with-a-very-long-name-that-goes-past-any-chip-and-any-row", Path = @"C:\Users\a-very-long-user-name\source\repos\an-org-with-a-long-name\a-repo-with-a-very-long-name-that-goes-past-any-chip-and-any-row" },
    ];

    private static SettingsPage ListsPage() => Sample with
    {
        Settings =
        [
            new SettingRow { Key = "no-auto-merge-repos", Label = "No auto-merge repos", Detail = "Repos whose PRs the node never merges by itself", Value = "tianwen\nwidget", Saved = "tianwen\nwidget", Source = SettingSources.File, Many = true },
            new SettingRow { Key = "repo-trackers", Label = "Repo trackers", Detail = "The tracker a bare number means in the repo", Value = "tianwen=shortcut", Saved = "tianwen=shortcut", Source = SettingSources.File, Many = true },
        ],
    };

    /// <summary>What a frame holds: the tooltips by text and the text fields, to look at and to type into.</summary>
    private static (string[] Tooltips, TextInputState[] Fields) Hovers(PilotChrome<RgbaImage> chrome, RgbaImageRenderer renderer, DateTimeOffset now)
    {
        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(now), new Rect<float>(0, 0, 1280, 3000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return (
            [.. arranged.Select(a => a.Node.Tooltip).OfType<string>()],
            [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.TextInput>().Select(t => t.State)]);
    }

    private static async Task<(PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer, List<(string Verb, string[] Args)> Sent)> ListsAsync(Func<string, string[], SettingsReply>? reply = null)
    {
        var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = Chrome(renderer);
        var sent = new List<(string Verb, string[] Args)>();
        chrome.Settings.Load = _ => Task.FromResult(ListsPage());
        chrome.Settings.LoadRepos = _ => Task.FromResult(Repos);
        chrome.Settings.Send = (verb, args, _) =>
        {
            lock (sent)
            {
                sent.Add((verb, args));
            }
            return Task.FromResult(reply?.Invoke(verb, args) ?? new SettingsReply("done"));
        };
        chrome.Show(PilotPage.Settings);
        Draw(chrome);
        await WaitAsync(() => chrome.Settings.Page is not null);
        await WaitAsync(() => Look(chrome, renderer, DateTimeOffset.UtcNow).Texts.Contains("Choose a repo ▾"));
        return (chrome, renderer, sent);
    }

    /// <summary>A list row offers the node's repos by name (the folder on hover, the ones already listed left out); Add sends the list with the pick.</summary>
    [Fact]
    public async Task AListRowOffersTheNodesReposByNameAndAddSendsTheListWithThePick()
    {
        var (chrome, renderer, sent) = await ListsAsync();
        using var shot = renderer;
        var now = DateTimeOffset.UtcNow;

        Click(Regions(chrome), "settings:no-auto-merge-repos:choose");
        var (texts, buttons) = Look(chrome, renderer, now);
        buttons.ShouldContain("settings:no-auto-merge-repos:pick:pilot");
        buttons.ShouldContain("settings:no-auto-merge-repos:pick:a-repo-with-a-very-long-name-that-goes-past-any-chip-and-any-row");
        // tianwen and widget are listed already.
        buttons.ShouldNotContain("settings:no-auto-merge-repos:pick:tianwen");
        buttons.ShouldNotContain("settings:no-auto-merge-repos:pick:widget");
        Hovers(chrome, renderer, now).Tooltips.ShouldContain(@"C:\src\sharpastro\pilot");
        // Nothing is sent by choosing; Add is idle until a repo is chosen.
        lock (sent)
        {
            sent.ShouldBeEmpty();
        }

        Click(Regions(chrome), "settings:no-auto-merge-repos:pick:pilot");
        (texts, buttons) = Look(chrome, renderer, now);
        texts.ShouldContain("pilot ▾");
        buttons.ShouldNotContain("settings:no-auto-merge-repos:pick:pilot");
        Click(Regions(chrome), "settings:no-auto-merge-repos:add");
        await WaitAsync(() => { lock (sent) { return sent.Count == 1; } });
        lock (sent)
        {
            sent.Single().Verb.ShouldBe(Verbs.Set);
            sent.Single().Args.ShouldBe(["no-auto-merge-repos", "tianwen", "widget", "pilot"]);
        }
        // The node took it: the choice is spent.
        await WaitAsync(() => Look(chrome, renderer, now).Texts.Count(t => t == "Choose a repo ▾") == 2);
    }

    /// <summary>Each entry is a chip whose × sends the list without it; the last one's × takes the setting out.</summary>
    [Fact]
    public async Task EachEntryIsAChipWhoseXSendsTheListWithoutIt()
    {
        var (chrome, renderer, sent) = await ListsAsync();
        using var shot = renderer;
        var (texts, buttons) = Look(chrome, renderer, DateTimeOffset.UtcNow);
        texts.ShouldContain("tianwen");
        texts.ShouldContain("widget");
        texts.ShouldContain("tianwen=shortcut");
        buttons.ShouldContain("settings:no-auto-merge-repos:remove:tianwen");

        Click(Regions(chrome), "settings:no-auto-merge-repos:remove:tianwen");
        Click(Regions(chrome), "settings:repo-trackers:remove:tianwen=shortcut");
        await WaitAsync(() => { lock (sent) { return sent.Count == 2; } });
        lock (sent)
        {
            sent.ShouldContain(s => s.Args.SequenceEqual(new[] { "no-auto-merge-repos", "widget" }));
            sent.ShouldContain(s => s.Args.SequenceEqual(new[] { "repo-trackers", "default" }));
        }
    }

    /// <summary>Repo trackers: the pick also chooses the tracker, and Add sends repo=tracker.</summary>
    [Fact]
    public async Task RepoTrackersAddsTheRepoWithTheChosenTracker()
    {
        var (chrome, renderer, sent) = await ListsAsync();
        using var shot = renderer;

        Click(Regions(chrome), "settings:repo-trackers:choose");
        Click(Regions(chrome), "settings:repo-trackers:pick:pilot");
        Click(Regions(chrome), "settings:repo-trackers:tracker:shortcut");
        Click(Regions(chrome), "settings:repo-trackers:add");
        await WaitAsync(() => { lock (sent) { return sent.Count == 1; } });
        lock (sent)
        {
            sent.Single().Args.ShouldBe(["repo-trackers", "tianwen=shortcut", "pilot=shortcut"]);
        }
    }

    /// <summary>A folder outside the node's repos is typed and sent as it is; a refusal's reason is shown, and the list stays as the node has it.</summary>
    [Fact]
    public async Task AFreePathOutsideTheReposIsSentAndARefusalsReasonIsShown()
    {
        var (chrome, renderer, sent) = await ListsAsync((_, args) => args.Contains(@"D:\clones\own")
            ? new SettingsReply(@"refused: D:\clones\own is not a folder on this machine", ReplyKind.Refused)
            : new SettingsReply("done"));
        using var shot = renderer;
        var now = DateTimeOffset.UtcNow;

        var field = Hovers(chrome, renderer, now).Fields.First(f => f.Placeholder == "or a folder's path, then Enter");
        field.Text = @"D:\clones\own";
        await field.OnCommit!(field.Text);
        await WaitAsync(() => chrome.Settings.Said?.StartsWith("refused", StringComparison.Ordinal) == true);

        lock (sent)
        {
            sent.Single().Args.ShouldBe(["no-auto-merge-repos", "tianwen", "widget", @"D:\clones\own"]);
        }
        chrome.Settings.Said.ShouldBe(@"refused: D:\clones\own is not a folder on this machine");
        var texts = Look(chrome, renderer, now).Texts;
        texts.ShouldNotContain(@"D:\clones\own");
        texts.ShouldContain("tianwen");
    }

    /// <summary>No role row's button reads "none": that choice is "default", the back-to-the-default pill is "reset", and the wire value stays none.</summary>
    [Fact]
    public async Task NoRoleButtonReadsNoneAndNoTwoButtonsOnARowReadDefault()
    {
        var page = Sample with
        {
            Settings =
            [
                new SettingRow { Key = "worker-model", Label = "Worker model", Detail = "A leg's worker; default leaves the choice to Claude Code", Value = "none", Saved = "none", Source = SettingSources.File, Choices = ["sonnet", "opus", "haiku", "none"] },
                new SettingRow { Key = "worker-effort", Label = "Worker effort", Detail = "The effort each worker starts at; default leaves the choice to Claude Code", Value = "none", Source = SettingSources.Default, Choices = ["low", "medium", "high", "none"] },
            ],
        };
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = Chrome(renderer);
        var sent = new List<string[]>();
        chrome.Settings.Load = _ => Task.FromResult(page);
        chrome.Settings.Send = (_, args, _) =>
        {
            lock (sent)
            {
                sent.Add(args);
            }
            return Task.FromResult(new SettingsReply("done"));
        };
        chrome.Show(PilotPage.Settings);
        Draw(chrome);
        await WaitAsync(() => chrome.Settings.Page is not null);

        var texts = Look(chrome, renderer, DateTimeOffset.UtcNow).Texts;
        texts.ShouldNotContain("none");
        // One "default" per row is the choice (the state line of an unset row says it too); "reset" is the one pill that takes a saved value out.
        texts.Count(t => t == "default").ShouldBeGreaterThanOrEqualTo(2);
        texts.Count(t => t == "reset").ShouldBe(1);
        texts.ShouldContain("reset");
        texts.ShouldContain("Worker model");
        Click(Regions(chrome), "settings:worker-effort:none");
        await WaitAsync(() => { lock (sent) { return sent.Count == 1; } });
        lock (sent)
        {
            sent.Single().ShouldBe(["worker-effort", "none"]);
        }
    }

    /// <summary>Captures of the list pickers narrow and wide, with long repo names and several chips, into PILOT_TEST_CAPTURE_DIR (nothing without it).</summary>
    [Fact]
    public async Task TheListPickersCapturedNarrowAndWideWithLongNamesAndSeveralChips()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is not { Length: > 0 } dir)
        {
            return;
        }
        Directory.CreateDirectory(dir);
        var longName = Repos[3].Name;
        var page = ListsPage() with
        {
            Settings =
            [
                .. ListsPage().Settings.Select(s => s.Key == "no-auto-merge-repos"
                    ? s with { Value = $"tianwen\nwidget\nfc-sdk\ndir-lib\nsdl-vulkan-renderer\n{longName}", Saved = $"tianwen\nwidget\nfc-sdk\ndir-lib\nsdl-vulkan-renderer\n{longName}" }
                    : s),
                new SettingRow { Key = "worker-model", Label = "Worker model", Detail = "A leg's worker, unless its plan names a model; default leaves the choice to Claude Code", Value = "none", Saved = "none", Source = SettingSources.File, Choices = ["sonnet", "opus", "haiku", "none"] },
            ],
        };
        foreach (var width in new[] { 700, 1400 })
        {
            using var renderer = new RgbaImageRenderer((uint)width, 900);
            var chrome = Chrome(renderer);
            chrome.Settings.Load = _ => Task.FromResult(page);
            chrome.Settings.LoadRepos = _ => Task.FromResult<RepoInfo[]>([.. Repos, new RepoInfo { Name = "fc-sdk", Path = @"C:\src\sharpastro\FC.SDK" }, .. Enumerable.Range(1, 12).Select(i => new RepoInfo { Name = $"clone-{i}", Path = $@"C:\src\sharpastro\clone-{i}" })]);
            chrome.Settings.Send = (_, _, _) => Task.FromResult(new SettingsReply("done"));
            chrome.Show(PilotPage.Settings);
            void Shot(string name)
            {
                chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 900));
                File.WriteAllBytes(Path.Combine(dir, $"settings-lists-{width}-{name}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 900));
            }
            Shot("start");
            await WaitAsync(() => chrome.Settings.Page is not null);
            await Task.Delay(300, Ct);
            Shot("closed");
            // The chrome's own button hit goes to the picker as a press would.
            var regions = new List<ClickableRegion>();
            chrome.Settings.CollectPaintedRegions(regions);
            Click(regions, "settings:no-auto-merge-repos:choose");
            Shot("open");
            regions.Clear();
            chrome.Settings.CollectPaintedRegions(regions);
            Click(regions, "settings:no-auto-merge-repos:pick:pilot");
            Shot("picked");
        }
    }

    private static void Click(List<ClickableRegion> regions, string action) =>
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25, Ct);
        }
        done().ShouldBeTrue();
    }
}
