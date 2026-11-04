using DIR.Lib;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The headers of a flight's page and of a session's transcript, with the control that goes up (pilot #723), drawn by the CPU
/// renderer. PNGs are written only where PIL723_SHOTS names a folder; PIL723_PHASE (before or after, default after) prefixes the names.
/// </summary>
public sealed class UpControlCaptureTests
{
    private static readonly DateTimeOffset At = Live.At;

    [Fact]
    public void TheFlightPageAndTheTranscriptHeadersAreCaptured()
    {
        var folder = Environment.GetEnvironmentVariable("PIL723_SHOTS");
        var phase = Environment.GetEnvironmentVariable("PIL723_PHASE") is { Length: > 0 } p ? p : "after";
        var flight = new FlightRow
        {
            Code = "PIL723", Repo = "pilot", RepoPath = "/r/pilot", Issue = 723, Title = "In-page Back goes up one level", Branch = "flight/pil723",
            Worktree = "/r/pilot/.claude/worktrees/PIL723", Base = "main", State = FlightState.Flying, CreatedAt = At.AddHours(-3),
        };
        var flights = new FlightsScreen(FlightLine.Build([flight], [], At, []), "sample", At);
        var sessions = new SessionsScreen([SessionLine.From(Live.Row("aaaa0000", "pil723-up", SessionState.Working), At)], "sample", At);
        var jobs = new JobsScreen([], "sample", At);

        foreach (var (label, flightPage) in new[] { ("flight-page", true), ("transcript", false) })
        {
            const int width = 1280, height = 360;
            using var renderer = new RgbaImageRenderer(width, height);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Sessions.Transcript.Load = (_, _, _, _) => Task.FromResult(new TranscriptPage { Total = 0, Entries = [] });
            chrome.Play.ShowEverything();
            if (flightPage)
            {
                chrome.Show(PilotPage.Flights);
                chrome.Flights.OpenDetail("PIL723");
            }
            else
            {
                chrome.Show(PilotPage.Sessions);
                chrome.Sessions.Transcript.Open(sessions.Lines[0]);
            }

            chrome.Render(sessions, flights, CardsScreen.Empty, jobs, null, null, new RectF32(0, 0, width, height));
            var png = PngWriter.Encode(renderer.Surface.Pixels, width, height);
            png.Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
            if (folder is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(folder).FullName, $"{phase}-{label}.png"), png);
            }
        }
    }
}
