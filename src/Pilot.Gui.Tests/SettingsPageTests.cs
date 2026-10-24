using DIR.Lib;
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
        Checks =
        [
            new SetupCheck { Key = "autostart", Title = "Starts at logon", Ok = true, Line = @"C:\WINDOWS\system32\conhost.exe --headless C:\Users\ada\AppData\Local\Programs\pilot2\pilot-node.exe" },
            new SetupCheck { Key = "hooks", Title = "Claude Code hooks", Ok = true, Line = @"in C:\Users\ada\.claude\settings.json" },
            new SetupCheck { Key = "lan", Title = "On the LAN", Ok = true, Line = "listening on port 47731 and announcing itself as DE1" },
            new SetupCheck { Key = "firewall", Title = "Firewall", Ok = false, Line = "no rule named pilot-node: other machines' connections are dropped before they reach the node", Fix = "Allow (asks for elevation)" },
            new SetupCheck { Key = "trust", Title = "Watchtower folder trusted", Ok = true, Line = @"C:\Users\ada\AppData\Local\pilot2\watchtower, through C:\" },
            new SetupCheck { Key = "gh", Title = "GitHub CLI signed in", Ok = true, Line = "Logged in to github.com account ada (keyring)" },
            new SetupCheck { Key = "usage", Title = "Usage figures", Ok = false, Optional = true, Line = "no rate-limits.json: a status line that writes it gives the fleet this machine's usage" },
        ],
        Settings =
        [
            new SettingRow { Key = "lan", Label = "On the LAN", Detail = "Listen on the LAN and announce it", Value = "on", Saved = "on", Source = SettingSources.File, Choices = ["on", "off"] },
            new SettingRow { Key = "machine", Label = "Machine prefix", Detail = "Two letters and a number", Value = "DE1", Source = SettingSources.Default },
            new SettingRow { Key = "repo-roots", Label = "Repo roots", Detail = "Folders of git clones the new-flight wizard offers", Value = @"C:\src\sharpastro" + "\n" + @"C:\src\my org", Saved = @"C:\src\sharpastro" + "\n" + @"C:\src\my org", Source = SettingSources.File, Many = true },
            new SettingRow { Key = "update-channel", Label = "Update channel", Detail = "stable or beta", Value = "stable", Saved = "beta", Source = SettingSources.File, Choices = ["stable", "beta"], Pending = true },
        ],
    };

    [Fact]
    public async Task ThePageShowsTheChecklistAndTheSettingsAndEachButtonIsAVerb()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sent = new List<(string Verb, string[] Args)>();
        var loaded = new TaskCompletionSource();
        chrome.Settings.Load = _ =>
        {
            loaded.TrySetResult();
            return Task.FromResult(Sample);
        };
        chrome.Settings.Send = (verb, args, _) =>
        {
            lock (sent)
            {
                sent.Add((verb, args));
            }
            return Task.FromResult("done");
        };
        chrome.Show(PilotPage.Settings);

        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 900));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await WaitAsync(() => chrome.Settings.Page is not null);
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 900));
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            File.WriteAllBytes(capture, PngWriter.Encode(renderer.Surface.Pixels, 1280, 900));
        }

        SettingsView<RgbaImage>.Summary(Sample).ShouldBe("1 of 6 setup steps left · 1 saved for the next start");
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
                return Task.FromResult("done");
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
            File.Delete(tokens);
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
