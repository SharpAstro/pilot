using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Night is red on black on every page, not only Flights (#406): the rail, the top bar and the page paint no pixel with green or blue in it.</summary>
public sealed class NightPagesTests
{
    private static JobRow Job(JobState state) => new()
    {
        Id = "j115", Command = "dotnet test src/Pilot.Gui.Tests -c Release", Cwd = "/r/pilot", Owner = "a1b2c3d4", OwnerName = "se1-pil406-night",
        Flight = "PIL406", Leg = "night-pages", State = state, LogPath = "/data/jobs/j115.log", Timeout = TimeSpan.FromHours(1),
        StartedAt = Live.At.AddSeconds(-125),
    };

    private static NodeSummary Summary(string stream) => new()
    {
        Connected = true, Status = "live", Stream = stream, LiveSessions = 3,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream, Autopilot = true },
    };

    private static FlightRow Flight(string code, FlightState state) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = code, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = state, CreatedAt = Live.At.AddMinutes(-3),
    };

    [Fact]
    public void NightPaintsNothingButRedOnBlack()
    {
        foreach (var page in new[] { PilotPage.Home, PilotPage.Sessions, PilotPage.Jobs, PilotPage.Settings, PilotPage.Repos, PilotPage.Fleet })
        {
            AssertRedOnBlack(page);
        }
    }

    private static void AssertRedOnBlack(PilotPage page)
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Night.On = true;
        chrome.Autopilot.OnToggle = _ => { };
        chrome.Autopilot.On = true;

        var sessions = new SessionsScreen(SessionLine.Order(
        [
            SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), Live.At),
            SessionLine.From(Live.Row("bbbb0000", "waiting", SessionState.Waiting), Live.At),
            SessionLine.From(Live.Row("cccc0000", "idle", SessionState.Idle), Live.At),
        ]), "test", Live.At);
        var flights = new FlightsScreen(FlightLine.Build([Flight("A", FlightState.Flying), Flight("B", FlightState.Held)], [], Live.At), "test", Live.At);
        var jobs = new JobsScreen([JobLine.From(Job(JobState.Running), Live.At), JobLine.From(Job(JobState.Queued) with { Id = "j116" }, Live.At), JobLine.From(Job(JobState.Failed) with { Id = "j117" }, Live.At)], "test", Live.At);
        var fleet = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop"))], Live.At);
        var repos = new ReposScreen([new RepoLine { Status = new RepoStatus { Name = "tianwen", Path = "/r/tianwen", Tracked = true }, Main = "main · clean" }], "test", Live.At);

        chrome.Show(page);
        chrome.Render(sessions, flights, CardsScreen.Empty, jobs, fleet, repos, new RectF32(0, 0, 1280, 800));

        var image = renderer.Surface;
        var painted = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            byte r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2];
            if (r == 0 && g == 0 && b == 0)
            {
                continue;
            }
            painted++;
            (g == 0 && b == 0).ShouldBeTrue($"{page}: pixel {i} ({i % image.Width},{i / image.Width}) is {r:x2}{g:x2}{b:x2}");
        }
        painted.ShouldBeGreaterThan(500, page.ToString());
    }
}
