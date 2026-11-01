using DIR.Lib;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Night reaches the text inputs, the chat's replies when it is toggled live, and the rail's glyphs (#406).</summary>
public sealed class NightInputsTests
{
    private static readonly DateTimeOffset At = Live.At;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly SessionsScreen NoSessions = new([], "test", At);

    private static TranscriptEntry[] Transcript() =>
    [
        new() { Index = 0, Kind = TranscriptKind.User, Text = "launch a flight", At = At },
        new() { Index = 1, Kind = TranscriptKind.Assistant, Text = "Started **PIL12** with *care* for [issue 12](https://example.org/12) and `pilot flight new`.", At = At.AddSeconds(9) },
    ];

    [Theory]
    [InlineData("🔴", "B!")]
    [InlineData("🔄", "B~")]
    [InlineData("🕓", "B…")]
    [InlineData("📋", "B")]
    public void TheJobsTabAtNightKeepsAMarkForWhatItsJobsAreDoing(string standard, string night)
    {
        PilotChrome<RgbaImage>.NightGlyph(PilotPage.Jobs, standard).ShouldBe(night);
    }

    [Fact]
    public async Task ANightPageBuiltInAnotherFlowLeavesThisFlowsPaletteAlone()
    {
        var before = Palette.Current;
        await Task.Run(() =>
        {
            Palette.Current = Palette.Night;
            ReferenceEquals(Palette.Current, Palette.Night).ShouldBeTrue();
        }, Ct);

        ReferenceEquals(Palette.Current, before).ShouldBeTrue();
    }

    private static string Folder()
    {
        var job = Environment.GetEnvironmentVariable("CLAUDE_JOB_DIR");
        var dir = job is { Length: > 0 } ? Path.Combine(job, "tmp") : Path.Combine(Path.GetTempPath(), "pil406-night");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (RgbaImageRenderer Renderer, PilotChrome<RgbaImage> Chrome) Make()
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Autopilot.On = true;
        return (renderer, chrome);
    }

    private static void AssertRedOnBlack(RgbaImageRenderer renderer, string what, string? png = null)
    {
        var image = renderer.Surface;
        if (png is not null)
        {
            File.WriteAllBytes(Path.Combine(Folder(), png), PngWriter.Encode(image.Pixels, image.Width, image.Height));
        }
        var painted = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            byte r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2];
            if (r == 0 && g == 0 && b == 0)
            {
                continue;
            }
            painted++;
            (g == 0 && b == 0).ShouldBeTrue($"{what}: pixel {i} ({i % image.Width},{i / image.Width}) is {r:x2}{g:x2}{b:x2}");
        }
        painted.ShouldBeGreaterThan(500, what);
    }

    private static async Task ShowChat(PilotChrome<RgbaImage> chrome)
    {
        var transcript = Transcript();
        chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = after < 0 ? transcript : [], Total = transcript.Length });
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Chat);
        for (var i = 0; i < 100 && chrome.Chat.Entries.Count < transcript.Length; i++)
        {
            await Task.Delay(50, Ct);
        }
        chrome.Chat.Entries.Count.ShouldBe(transcript.Length);
    }

    [Fact]
    public async Task TheChatComposerAndItsRepliesAreRedOnBlack()
    {
        var (renderer, chrome) = Make();
        using var _ = renderer;
        chrome.Night.On = true;
        await ShowChat(chrome);
        chrome.Render(NoSessions, new RectF32(0, 0, 1280, 800));
        AssertRedOnBlack(renderer, "Chat with a composer", "night-chat-composer-1280.png");
    }

    [Fact]
    public async Task TogglingNightOnRepaintsTheRepliesAlreadyDrawn()
    {
        var (renderer, chrome) = Make();
        using var _ = renderer;
        await ShowChat(chrome);
        chrome.Render(NoSessions, new RectF32(0, 0, 1280, 800));
        chrome.Night.On = true;
        chrome.Render(NoSessions, new RectF32(0, 0, 1280, 800));
        AssertRedOnBlack(renderer, "Chat after Night went on");
    }

    [Fact]
    public async Task TogglingNightOffRepaintsTheRepliesAlreadyDrawn()
    {
        var (renderer, chrome) = Make();
        using var _ = renderer;
        chrome.Night.On = true;
        await ShowChat(chrome);
        chrome.Render(NoSessions, new RectF32(0, 0, 1280, 800));
        chrome.Night.On = false;
        chrome.Render(NoSessions, new RectF32(0, 0, 1280, 800));
        var image = renderer.Surface;
        var coloured = false;
        for (var i = 0; i < image.Width * image.Height && !coloured; i++)
        {
            coloured = image.Pixels[i * 4 + 2] != 0;
        }
        coloured.ShouldBeTrue("the standard palette is back");
    }

    [Fact]
    public void TheSessionsSayBoxIsRedOnBlack()
    {
        var (renderer, chrome) = Make();
        using var _ = renderer;
        chrome.Night.On = true;
        chrome.Sessions.OnSay = (_, _) => Task.FromResult<string?>(null);
        var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), At)]), "test", At);
        chrome.Show(PilotPage.Sessions);
        chrome.Sessions.Select(0);
        chrome.Sessions.SayInput.Text = "go on";
        chrome.Render(sessions, new RectF32(0, 0, 1280, 800));
        AssertRedOnBlack(renderer, "Sessions with a say box", "night-sessions-say-1280.png");
    }

    [Fact]
    public async Task TheSettingsFieldsAreRedOnBlack()
    {
        var (renderer, chrome) = Make();
        using var _ = renderer;
        chrome.Night.On = true;
        var page = new SettingsPage
        {
            File = "settings.json",
            Settings = [new SettingRow { Key = "machine", Label = "Machine prefix", Detail = "Two letters and a number", Value = "DE1", Source = SettingSources.Default }],
        };
        chrome.Settings.Load = _ => Task.FromResult(page);
        chrome.Show(PilotPage.Settings);
        chrome.Render(NoSessions, new RectF32(0, 0, 1280, 800));
        for (var i = 0; i < 100 && chrome.Settings.Page is null; i++)
        {
            await Task.Delay(50, Ct);
        }
        chrome.Settings.Page.ShouldNotBeNull();
        chrome.Render(NoSessions, new RectF32(0, 0, 1280, 800));
        AssertRedOnBlack(renderer, "Settings with a field", "night-settings-field-1280.png");
    }

    [Fact]
    public void TheNewFlightWizardsInputsAreRedOnBlack()
    {
        var (renderer, chrome) = Make();
        using var _ = renderer;
        chrome.Night.On = true;
        RepoInfo[] repos = [new() { Name = "tianwen", Path = @"C:\src\tianwen", GitHub = "SharpAstro/tianwen" }];
        chrome.Flights.Wizard.LoadRepos = _ => Task.FromResult(repos);
        chrome.Flights.Wizard.LoadIssues = (_, _) => Task.FromResult<IssueInfo[]>([new() { Number = 1, Title = "GPU stacking" }]);
        chrome.Show(PilotPage.Flights);
        chrome.Flights.Wizard.Open();
        chrome.Render(NoSessions, new FlightsScreen([], "test", At), new RectF32(0, 0, 1280, 800));
        chrome.Flights.Wizard.IsOpen.ShouldBeTrue();
        AssertRedOnBlack(renderer, "the wizard's repo filter", "night-wizard-1280.png");
    }

    [Fact]
    public void EveryRailTabDrawsAGlyphTheUiFaceHasAtNight()
    {
        foreach (var (page, icon, label, _, _) in PilotChrome<RgbaImage>.Pages)
        {
            var glyph = PilotChrome<RgbaImage>.NightGlyph(page, icon);
            // The colour emoji are astral; the plane and the cog are in the face, and the rest become a letter (Spend a dollar sign, ASCII).
            glyph.ShouldBeOneOf(["H", "C", "S", "R", "B", "F", "P", "I", "$", "✈", "⚙"], label);
        }
    }
}
