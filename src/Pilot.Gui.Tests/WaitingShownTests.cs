using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A session waiting on its person says what it waits on and how to answer it: its transcript ends with a pinned banner
/// (what, since when, what it asks, and a link to answer it in the browser, else where it can be answered, else that it is
/// gone), and its row in the table carries the same link.
/// </summary>
public sealed class WaitingShownTests
{
    private const string Url = "https://claude.ai/code/session_waiting";
    private static readonly DateTimeOffset Now = Live.At.AddHours(10);

    private static SessionRow Row(Func<SessionRow, SessionRow>? with = null)
    {
        var row = new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "push-it", Kind = SessionKind.Interactive, Cwd = "/r/pilot",
            Role = SessionRole.AdHoc, State = SessionState.Waiting, WaitingFor = "permission to use Bash", WaitingSince = Now - new TimeSpan(9, 37, 0),
            Asking = "Bash: git push origin main", RemoteControlUrl = Url, ContextTokens = 50_000, ContextWindow = 200_000, SpendWeighted = 1_000,
            LastResponseAt = Now.AddHours(-9), StartedAt = Live.At,
        };
        return with?.Invoke(row) ?? row;
    }

    private static SessionsScreen Screen(SessionRow row) => new(SessionLine.Build([row], Now), "test", Now);

    // A view with its transcript open on the row's session and the entries read: a long history, so the banner is shown to
    // be pinned below it rather than at the end of what scrolls.
    private static async Task<SessionsView<RgbaImage>> Open(RgbaImageRenderer renderer, SessionsScreen screen, List<string> opened)
    {
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        sessions.OnOpen = opened.Add;
        sessions.Transcript.Load = (_, _, after, _) => Task.FromResult(new TranscriptPage
        {
            Total = 60,
            Entries = after < 0 ? [.. Enumerable.Range(0, 60).Select(i => new TranscriptEntry { Index = i, Kind = TranscriptKind.Assistant, Text = $"Step {i}." })] : [],
        });
        sessions.Tree(screen);
        sessions.Transcript.Open(screen.Lines[0]);
        for (var i = 0; i < 100 && sessions.Transcript.Entries.Count < 60; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return sessions;
    }

    private static string Described(SessionsView<RgbaImage> sessions, SessionsScreen screen) =>
        CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 160, 60), CellMeasureContext.PixelAuthored));

    private static List<ClickableRegion> Regions(SessionsView<RgbaImage> sessions, SessionsScreen screen)
    {
        sessions.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        sessions.CollectPaintedRegions(regions);
        return regions;
    }

    private static bool HasAction(List<ClickableRegion> regions, string action) => regions.Any(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);

    // The page's text as read: each word is a leaf of its own in the description, so the leaves are joined (a word ends in
    // its space) and the whitespace collapsed before a sentence is looked for.
    private static bool Says(string described, string text) =>
        string.Join(' ', string.Join(' ', System.Text.RegularExpressions.Regex.Matches(described, "Leaf Text \"([^\"]*)\"").Select(m => m.Groups[1].Value))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Contains(text, StringComparison.Ordinal);

    [Fact]
    public async Task AWaitingSessionsTranscriptEndsWithABannerSayingWhatItWaitsOnSinceWhenAndWhatItAsks()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var screen = Screen(Row());
        var sessions = await Open(renderer, screen, []);

        var described = Described(sessions, screen);

        // Pinned: shown whatever the history above it, which fills the pane.
        Says(described, "Waiting on a permission prompt since 9 h 37 m: Bash: git push origin main").ShouldBeTrue(described);
        Says(described, "Answer in the browser").ShouldBeTrue(described);
        sessions.Transcript.Close();
    }

    [Fact]
    public async Task TheAnswerLinkOpensTheSessionsRemoteControlUrlAndIsMarkedAsLeadingAway()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var screen = Screen(Row());
        var opened = new List<string>();
        var sessions = await Open(renderer, screen, opened);

        var link = Regions(sessions, screen).Single(r => r.Result is HitResult.ButtonHit { Action: "transcript:answer" });
        link.Cursor.ShouldBe(CursorKind.Pointer);
        link.OnClick!.Invoke(InputModifier.None);

        opened.ShouldBe([Url]);
        Says(Described(sessions, screen), "Answer in the browser" + Followable.Away).ShouldBeTrue();
        sessions.Transcript.Close();
    }

    [Fact]
    public async Task WithoutARemoteControlUrlTheBannerSaysWhereItCanBeAnsweredAndOffersNoLink()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var interactive = Screen(Row(r => r with { RemoteControlUrl = null }));
        var sessions = await Open(renderer, interactive, []);

        var described = Described(sessions, interactive);
        Says(described, "Waiting on a permission prompt since 9 h 37 m").ShouldBeTrue(described);
        Says(described, "answer it in its terminal").ShouldBeTrue(described);
        Says(described, "Answer in the browser").ShouldBeFalse(described);
        HasAction(Regions(sessions, interactive), "transcript:answer").ShouldBeFalse();
        sessions.Transcript.Close();

        var background = Screen(Row(r => r with { RemoteControlUrl = null, Kind = SessionKind.Background }));
        sessions = await Open(renderer, background, []);
        described = Described(sessions, background);
        Says(described, "no terminal").ShouldBeTrue(described);
        Says(described, "Answer in the browser").ShouldBeFalse(described);
        sessions.Transcript.Close();
    }

    [Fact]
    public async Task ASessionWhoseProcessIsGoneSaysSoAndOffersNoLinkToAnswerIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var screen = Screen(Row(r => r with { Gone = true }));
        var sessions = await Open(renderer, screen, []);

        var described = Described(sessions, screen);

        Says(described, "the session is gone; its last state was waiting").ShouldBeTrue(described);
        Says(described, "Answer in the browser").ShouldBeFalse(described);
        Says(described, "Waiting on a permission prompt").ShouldBeFalse(described);
        HasAction(Regions(sessions, screen), "transcript:answer").ShouldBeFalse();
        sessions.Transcript.Close();
    }

    [Fact]
    public async Task ASessionNotWaitingHasNoBanner()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var screen = Screen(Row(r => r with { State = SessionState.Idle, WaitingFor = null, WaitingSince = null, Asking = null }));
        var sessions = await Open(renderer, screen, []);

        var described = Described(sessions, screen);

        Says(described, "Waiting on").ShouldBeFalse(described);
        Says(described, "Answer in the browser").ShouldBeFalse(described);
        Says(described, "is gone").ShouldBeFalse(described);
        sessions.Transcript.Close();
    }

    [Fact]
    public void AWaitingRowWithARemoteControlUrlHasThePillAndItOpensTheUrl()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var opened = new List<string>();
        sessions.OnOpen = opened.Add;
        var screen = Screen(Row());

        var described = Described(sessions, screen);
        Says(described, "Answer in the browser" + Followable.Away).ShouldBeTrue(described);

        var pill = Regions(sessions, screen).Single(r => r.Result is HitResult.ButtonHit { Action: "session:a1b2c3d4:answer" });
        pill.Cursor.ShouldBe(CursorKind.Pointer);
        pill.OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe([Url]);
    }

    [Fact]
    public void ARowHasNoPillUnlessItWaitsHasAUrlAndItsProcessIsThere()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        sessions.OnOpen = _ => { };

        foreach (var row in new[]
        {
            Row(r => r with { RemoteControlUrl = null }),
            Row(r => r with { State = SessionState.Working, WaitingFor = null, WaitingSince = null, Asking = null }),
            Row(r => r with { Gone = true }),
        })
        {
            var screen = Screen(row);
            Says(Described(sessions, screen), "Answer in the browser").ShouldBeFalse();
            HasAction(Regions(sessions, screen), "session:a1b2c3d4:answer").ShouldBeFalse();
        }
    }
}
