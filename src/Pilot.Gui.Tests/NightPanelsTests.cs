using DIR.Lib;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Night reaches the watchtower chat and the For-you cards panel too (#406): at two widths, with the panel open, no pixel has green or blue in it.</summary>
public sealed class NightPanelsTests
{
    private static readonly DateTimeOffset At = Live.At;

    private static TranscriptEntry[] Transcript() =>
    [
        new() { Index = 0, Kind = TranscriptKind.User, Text = "launch a flight for issue 12", At = At },
        new() { Index = 1, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "pilot flight new", Description = "Start the flight", ToolUseId = "t1", At = At.AddSeconds(3) },
        new() { Index = 2, Kind = TranscriptKind.ToolResult, Text = "result-line-1\nresult-line-2", ToolUseId = "t1" },
        new() { Index = 3, Kind = TranscriptKind.Assistant, Text = "Started **PIL12** with *care* for [issue 12](https://example.org/12) with `pilot flight new`.\n\n- the flight is flying\n- its planner is working\n\n```\ndotnet test -c Release\n```", At = At.AddSeconds(9) },
        new() { Index = 4, Kind = TranscriptKind.Thinking, Text = "Waiting on the planner.", At = At.AddSeconds(12) },
        new() { Index = 5, Kind = TranscriptKind.Divider, Text = "A fresh watchtower started: its brief changed", At = At.AddMinutes(5) },
    ];

    private static CardRow Card(string id, CardBin bin, string title) => new()
    {
        Id = id, Bin = bin, Kind = "landed", Subject = "flight/X", Title = title, Detail = "Merged and green; nothing left to do.", At = At, State = CardState.Open,
    };

    private static SessionRow Tower() => new()
    {
        Id = "bbbb0002", SessionId = "bbbb0002-0000-4000-8000-000000000000", Name = "se1-watchtower", Kind = SessionKind.Background, Cwd = "/w",
        Role = SessionRole.Watchtower, State = SessionState.Idle, ContextTokens = 50_000, ContextWindow = 200_000, SpendWeighted = 0, StartedAt = At, RemoteControlUrl = "https://claude.ai/code/session_x",
    };

    private static FlightRow Flight(string code, FlightState state) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = code, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = state, CreatedAt = At.AddMinutes(-3),
    };

    private static string CaptureFolder()
    {
        var job = Environment.GetEnvironmentVariable("CLAUDE_JOB_DIR");
        var dir = job is { Length: > 0 } ? Path.Combine(job, "tmp") : Path.Combine(Path.GetTempPath(), "pil406-night");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("Home", 900)]
    [InlineData("Home", 1800)]
    [InlineData("Flights", 900)]
    [InlineData("Flights", 1800)]
    [InlineData("Chat", 900)]
    [InlineData("Chat", 1800)]
    public async Task NightPaintsThePanelAndTheChatRedOnBlack(string pageName, int width)
    {
        var page = Enum.Parse<PilotPage>(pageName);
        const int height = 800;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Night.On = true;
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Autopilot.On = true;
        var transcript = Transcript();
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = after < 0 ? transcript : [], Total = transcript.Length });

        var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), At)]), "test", At);
        var flights = new FlightsScreen(FlightLine.Build([Flight("A", FlightState.Flying), Flight("B", FlightState.Held)], [], At), "test", At);
        var cards = CardsScreen.From(
            [Card("c1", CardBin.ForYou, "PIL406 landed"), Card("c2", CardBin.ForYou, "TIA1818 compiles against stale siblings"), Card("c3", CardBin.Ring, "PIL222 could not open its PR")],
            [Tower()]);

        chrome.Show(page);
        if (page == PilotPage.Chat)
        {
            for (var i = 0; i < 100 && chrome.Chat.Entries.Count < transcript.Length; i++)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            chrome.Chat.Entries.Count.ShouldBe(transcript.Length);
        }
        chrome.Render(sessions, flights, cards, null, null, null, new RectF32(0, 0, width, height));
        if (page != PilotPage.Chat)
        {
            chrome.ShowsCards.ShouldBeTrue("the For-you panel is open");
        }

        var image = renderer.Surface;
        File.WriteAllBytes(Path.Combine(CaptureFolder(), $"night-{page.ToString().ToLowerInvariant()}-{width}.png"), PngWriter.Encode(image.Pixels, width, height));

        var painted = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            byte r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2];
            if (r == 0 && g == 0 && b == 0)
            {
                continue;
            }
            painted++;
            (g == 0 && b == 0).ShouldBeTrue($"{page} at {width}: pixel {i} ({i % image.Width},{i / image.Width}) is {r:x2}{g:x2}{b:x2}");
        }
        painted.ShouldBeGreaterThan(500, page.ToString());
    }
}
