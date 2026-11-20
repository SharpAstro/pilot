using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A left press and release on any text of a clickable list row selects the row, whether the cell carries a tooltip or not
/// (#1221): the text selection looks through a region that only carries a tooltip to the row's own hit beneath it, so the
/// router gets the press. A press on a button is still the button's.
/// </summary>
public sealed class RowTextPressTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static JobRow Job(string id, JobState state, DateTimeOffset started, DateTimeOffset? ended = null) => new()
    {
        Id = id,
        Command = $"dotnet test {id}",
        Cwd = "/r/orbit",
        State = state,
        LogPath = $"/data/jobs/{id}.log",
        Timeout = TimeSpan.FromHours(1),
        StartedAt = started,
        EndedAt = ended,
    };

    private sealed class Window : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly InputRouter _router;
        private readonly RectF32 _size;
        private readonly SessionsScreen _sessions;

        public Window(int width = 1400, int height = 900)
        {
            _renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Play.ShowEverything();
            _router = new InputRouter(Chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            _size = new RectF32(0, 0, width, height);
            _sessions = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "test");
        }

        public PilotChrome<RgbaImage> Chrome { get; }

        public JobsScreen Jobs { get; } = new(JobLine.Build(
        [
            Job("j12", JobState.Succeeded, Live.At.AddMinutes(-60), Live.At.AddMinutes(-59)),
            Job("j13", JobState.Failed, Live.At.AddMinutes(-40), Live.At.AddMinutes(-35)),
            Job("j14", JobState.Succeeded, Live.At.AddMinutes(-30), Live.At.AddMinutes(-29)),
        ], Live.At), "live", Live.At);

        public void Render() => Chrome.Render(_sessions, null, null, Jobs, _size);

        public bool Send(InputEvent evt)
        {
            var handled = Chrome.HandleInput(evt) || _router.Handle(evt);
            Render();
            return handled;
        }

        public void Click(float x, float y)
        {
            Send(new InputEvent.MouseDown(x, y));
            Send(new InputEvent.MouseUp(x, y));
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static (float X, float Y) Middle(SelectableTextRegion r) => (r.X + r.Width / 2f, r.Y + r.Height / 2f);

    [Fact]
    public void APressOnAJobsCommandTextWhichCarriesATooltipSelectsTheRow()
    {
        using var window = new Window();
        window.Chrome.Show(PilotPage.Jobs);
        window.Render();
        // The selected job's detail below the list repeats its command: the three rows' own are the topmost ones.
        var commands = window.Chrome.Jobs.SelectableTextRegions.ToArray().Where(r => r.Text.Contains("dotnet test", StringComparison.Ordinal))
            .OrderBy(r => r.Y).Take(3).ToArray();
        commands.Length.ShouldBe(3);

        for (var row = 0; row < commands.Length; row++)
        {
            var (x, y) = Middle(commands[row]);
            window.Click(x, y);
            window.Chrome.Jobs.Selected.ShouldBe(row);
        }
    }

    [Fact]
    public void APressOnACellWithNoTooltipSelectsTheRowToo()
    {
        using var window = new Window();
        window.Chrome.Show(PilotPage.Jobs);
        window.Render();
        var ids = window.Chrome.Jobs.SelectableTextRegions.ToArray().Where(r => r.Text.Trim() is "j12" or "j13" or "j14").ToArray();
        ids.Length.ShouldBe(3);

        var (x, y) = Middle(ids[2]);
        window.Click(x, y);
        window.Chrome.Jobs.Selected.ShouldBe(2);
    }

    [Fact]
    public void APressOnAnotherListPageRowTextSelectsItAndNeverSwallowsTheClickBeneath()
    {
        using var window = new Window();
        window.Chrome.Show(PilotPage.Sessions);
        window.Render();
        var texts = window.Chrome.Sessions.SelectableTextRegions.ToArray().Where(r => r.Text.Trim().Length > 0).ToArray();
        texts.Length.ShouldBeGreaterThan(0);

        // Every text of the page, tooltip or not: a press that went nowhere must leave the row beneath to select itself.
        var selectedSomewhere = false;
        foreach (var region in texts.Reverse().Take(12))
        {
            var (x, y) = Middle(region);
            window.Click(x, y);
            selectedSomewhere |= window.Chrome.Sessions.Selected >= 0;
        }
        selectedSomewhere.ShouldBeTrue();
    }

    [Fact]
    public void APressOnAButtonIsStillTheButtons()
    {
        using var window = new Window();
        window.Chrome.Show(PilotPage.Jobs);
        window.Render();
        var regions = new List<ClickableRegion>();
        window.Chrome.Jobs.CollectPaintedRegions(regions);
        var at = regions.FindIndex(r => r is { Result: HitResult.ButtonHit } && r.Width > 4f && r.Height > 4f);
        if (at < 0)
        {
            window.Chrome.Show(PilotPage.Home);
            window.Render();
            regions.Clear();
            window.Chrome.Home.CollectPaintedRegions(regions);
            at = regions.FindIndex(r => r is { Result: HitResult.ButtonHit } && r.Width > 4f && r.Height > 4f);
        }
        at.ShouldBeGreaterThanOrEqualTo(0);
        var button = regions[at];
        window.Chrome.HandleInput(new InputEvent.MouseDown(button.X + button.Width / 2f, button.Y + button.Height / 2f)).ShouldBeFalse();
    }
}
