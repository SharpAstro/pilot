using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The In play | Everything choice survives a restart (a small per-user file), and a jump to something that is done
/// (a landed flight, an ended session, an old job) shows that row and says it is outside In play, until the selection
/// moves off it.
/// </summary>
public sealed partial class InPlayTests
{
    private static PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer, string? file = null)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer), file) { FontPath = SessionsCapture.FontPath };
        chrome.LinkCardTranscripts(() => Sessions(), () => Flights(), jobs: () => Jobs());
        chrome.LinkRepos();
        // These tests are about the table's rows and keys; the map has its own (FlightsCanvasTests).
        chrome.Flights.Mode = FlightsViewMode.List;
        return chrome;
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "pilot-inplay-" + Guid.NewGuid().ToString("N"), "gui.json");

    // A key on the page shown, after a frame was drawn, as the host does: the list takes keys once it has been laid out.
    private static void Press(PilotChrome<RgbaImage> chrome, InputKey key)
    {
        chrome.Render(Sessions(), Flights(), null, Jobs(), new RectF32(0, 0, 1280, 800));
        chrome.PageWidget.HandleInput(new InputEvent.KeyDown(key, InputModifier.None)).ShouldBeTrue();
    }

    [Fact]
    public void TheChoiceIsWrittenWhenTheToggleFlipsAndReadBackByANewWindow()
    {
        var file = TempFile();
        try
        {
            using var first = NewRenderer();
            var chrome = Chrome(first, file);
            chrome.Play.InPlay.ShouldBeTrue();
            File.Exists(file).ShouldBeFalse();

            chrome.Play.ShowEverything();
            File.ReadAllText(file).ShouldContain("\"inPlay\": false");

            // A restart: a new chrome on the same file opens on Everything, and its pill says so.
            using var second = NewRenderer();
            var restarted = Chrome(second, file);
            restarted.Play.InPlay.ShouldBeFalse();
            restarted.Play.Line().ShouldBe("In play [Everything]");
            Describe(restarted.Flights.Tree(Flights())).ShouldContain("LAND3");

            restarted.Play.ShowInPlay();
            using var third = NewRenderer();
            Chrome(third, file).Play.InPlay.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void WithNothingSavedOrAFileThatCannotBeReadTheWindowStartsInPlay()
    {
        var file = TempFile();
        using var renderer = NewRenderer();
        Chrome(renderer, file).Play.InPlay.ShouldBeTrue();
        Chrome(renderer).Play.InPlay.ShouldBeTrue();

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            foreach (var content in new[] { "", "not json", "{", "[]", "{}", "{ \"inPlay\": \"no\" }", "{ \"inPlay\": null }", "\0\0\0" })
            {
                File.WriteAllText(file, content);
                new PlayFilter(file).InPlay.ShouldBeTrue(content);
            }

            File.WriteAllText(file, "{ \"inPlay\": false, \"other\": 1 }");
            new PlayFilter(file).InPlay.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void AChoiceThatCannotBeWrittenStillHoldsForTheWindow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-inplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // The "file" is a folder: reading and writing both fail, and neither throws.
            var play = new PlayFilter(folder);
            play.InPlay.ShouldBeTrue();
            play.ShowEverything();
            play.InPlay.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AJumpToALandedFlightShowsItAndSaysItIsOutsideInPlay()
    {
        using var renderer = NewRenderer();
        var chrome = Chrome(renderer);

        chrome.Home.OnFlight!(Flights().Lines.Single(l => l.Code == "LAND2"));

        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("LAND2");
        chrome.Play.InPlay.ShouldBeTrue();
        var open = Describe(chrome.Flights.Tree(Flights()));
        open.ShouldContain("LAND2 is outside In play");
        open.ShouldContain("2 landed hidden");

        // Back to the table: the flight's row is there, selected, and still named as outside In play.
        chrome.Flights.CloseDetail();
        var table = Describe(chrome.Flights.Tree(Flights()));
        table.ShouldContain("LAND2 is outside In play");
        table.ShouldNotContain("LAND3");
        chrome.Flights.Selected.ShouldBe(3);

        // Once the selection moves off it, it stops being forced.
        Press(chrome, InputKey.Up);
        var moved = Describe(chrome.Flights.Tree(Flights()));
        moved.ShouldNotContain("outside In play");
        moved.ShouldNotContain("LAND2");
        moved.ShouldContain("3 landed hidden");
    }

    [Fact]
    public void AJumpToAFlightInPlayNamesNothingOutsideAndEverythingNeedsNoNote()
    {
        using var renderer = NewRenderer();
        var chrome = Chrome(renderer);

        chrome.Home.OnFlight!(Flights().Lines.Single(l => l.Code == "FLY1"));
        Describe(chrome.Flights.Tree(Flights())).ShouldNotContain("outside In play");

        chrome.Play.ShowEverything();
        chrome.Home.OnFlight!(Flights().Lines.Single(l => l.Code == "LAND2"));
        Describe(chrome.Flights.Tree(Flights())).ShouldNotContain("outside In play");
    }

    [Fact]
    public void AJumpToAnEndedSessionShowsItAndSaysItIsOutsideInPlay()
    {
        using var renderer = NewRenderer();
        var chrome = Chrome(renderer);
        var ended = Sessions().Lines.Single(l => l.Name == "s-ended");

        chrome.Home.OnSession!(ended);

        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.IsOpen.ShouldBeTrue();
        chrome.Sessions.Transcript.Key.ShouldBe(ended.Key);
        Describe(chrome.Sessions.Tree(Sessions())).ShouldContain("s-ended is outside In play");

        chrome.Sessions.Transcript.Close();
        var table = Describe(chrome.Sessions.Tree(Sessions()));
        table.ShouldContain("s-ended is outside In play");
        table.ShouldNotContain("s-failed");
        chrome.Sessions.SelectedId.ShouldBe(ended.Key);

        // Moving the selection off it lets In play leave it out again.
        Press(chrome, InputKey.Up);
        var moved = Describe(chrome.Sessions.Tree(Sessions()));
        moved.ShouldNotContain("outside In play");
        moved.ShouldNotContain("s-ended");
    }

    [Fact]
    public void AJobsEndedSessionAndAReposEndedSessionShowAsOutsideInPlayToo()
    {
        using var renderer = NewRenderer();
        var chrome = Chrome(renderer);
        var job = Jobs().Lines.Single(l => l.Id == "jnew").Row with { Owner = "aaaa0004" };

        chrome.Jobs.OnSession!(job);

        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.Key.ShouldBe(Sessions().Lines.Single(l => l.Name == "s-ended").Key);
        Describe(chrome.Sessions.Tree(Sessions())).ShouldContain("s-ended is outside In play");

        using var other = NewRenderer();
        var repos = Chrome(other);
        repos.Repos.OnSession!(Sessions().Lines.Single(l => l.Name == "s-failed"));
        repos.Page.ShouldBe(PilotPage.Sessions);
        Describe(repos.Sessions.Tree(Sessions())).ShouldContain("s-failed is outside In play");
    }

    [Fact]
    public void AJumpToAnOldJobShowsItAndSaysItIsOutsideInPlay()
    {
        using var renderer = NewRenderer();
        var chrome = Chrome(renderer);

        chrome.Home.OnJob!(Jobs().Lines.Single(l => l.Id == "jold"));

        chrome.Page.ShouldBe(PilotPage.Jobs);
        var text = Describe(chrome.Jobs.Tree(Jobs()));
        text.ShouldContain("jold is outside In play");
        text.ShouldContain("dotnet test jold");
        text.ShouldContain("1 finished hidden");
        chrome.Jobs.Selected.ShouldBeGreaterThanOrEqualTo(0);

        // Once the selection moves off it, it stops being forced.
        Press(chrome, InputKey.Up);
        var moved = Describe(chrome.Jobs.Tree(Jobs()));
        moved.ShouldNotContain("outside In play");
        moved.ShouldNotContain("dotnet test jold");
        moved.ShouldContain("2 finished hidden");
    }
}
