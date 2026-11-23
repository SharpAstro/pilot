using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Designs page's target switcher (PIL1097), drawn on the CPU from the targets fixture with no node: the prototype laid out at Phone and at Desktop
/// (the 'when target is compact' node drawn at Phone and absent at Desktop, its regular twin the other way round), the sides swapped, a bad custom size
/// refused, and the choice kept per design in gui-settings.json.
/// </summary>
public sealed class DesignTargetsPageTests
{
    private const string Rail = "Side rail: Home, Flights, Jobs";
    private const string Bar = "Bottom bar: Home, Flights, Jobs";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        private readonly RectF32 _bounds = new(0, 0, 1400, 900);
        private readonly RgbaImageRenderer _renderer = new(1400, 900);
        private readonly DesignsScreen _screen;

        public Rig(Action<System.Text.Json.Nodes.JsonObject>? edit = null)
        {
            _screen = PageData.TargetsDesigns(Now, edit);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            Chrome.Show(PilotPage.Designs);
            Chrome.Designs.Open("DSN-7");
        }

        public PilotChrome<RgbaImage> Chrome { get; }
        public DesignsView<RgbaImage> View => Chrome.Designs;

        public void Paint()
        {
            var data = PageData.For("sample");
            Chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, _bounds, data.Machines, null, data.Spend, _screen);
        }

        /// <summary>The texts of the prototype the player is playing, as laid out at its design size.</summary>
        public List<string> Played()
        {
            var size = View.PlayerOf()!.DesignSize;
            return SentenceRuns.Texts(Layout.Engine.Arrange(View.PlayerOf()!.BuildTree(), new Rect<float>(0, 0, size.Width, size.Height), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).Select(a => a.Node));
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory("pilot-design-target-").FullName, "gui-settings.json");

    [Fact]
    public void ADocumentOfItsOwnSizeWithNoTargetOpensAtThatSizeNotDesktop()
    {
        using var rig = new Rig(doc =>
        {
            doc.Remove("targets");
            doc["width"] = 720;
            doc["height"] = 1000;
        });
        rig.Paint();
        rig.View.Surface.Width.ShouldBe(720);
        rig.View.Surface.Height.ShouldBe(1000);
        rig.View.Surface.Scale.ShouldBe(1d);
        rig.View.PickTarget("Desktop").ShouldBeTrue();
        rig.View.Surface.Width.ShouldBe(1440);
        rig.View.PickOwn().ShouldBeTrue();
        rig.View.Surface.Width.ShouldBe(720);
    }

    [Fact]
    public void ADocumentNamingACustomSizeTargetOpensAtThatSizeAndASavedChoiceStillWins()
    {
        using var rig = new Rig(doc => doc["targets"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "Pane", ["width"] = 640, ["height"] = 480, ["scale"] = 2 }));
        rig.Paint();
        rig.View.Surface.Width.ShouldBe(640);
        rig.View.Surface.Height.ShouldBe(480);
        rig.View.Surface.Scale.ShouldBe(2d);
        rig.View.PickTarget("Phone");
        rig.View.Surface.Width.ShouldBe(390);
    }

    [Fact]
    public void AtPhoneTheCompactNodeIsDrawnAndTheRegularOneIsNot()
    {
        using var rig = new Rig();
        rig.Paint();
        rig.View.PickTarget("Phone").ShouldBeTrue();
        rig.Paint();

        rig.View.Surface.Width.ShouldBe(390);
        var texts = rig.Played();
        texts.ShouldContain(Bar);
        texts.ShouldNotContain(Rail);
    }

    [Fact]
    public void AtDesktopTheRegularNodeIsDrawnAndTheCompactOneIsNot()
    {
        using var rig = new Rig();
        rig.Paint();
        rig.View.PickTarget("Phone").ShouldBeTrue();
        rig.Paint();
        rig.View.PickTarget("Desktop").ShouldBeTrue();
        rig.Paint();

        var texts = rig.Played();
        texts.ShouldContain(Rail);
        texts.ShouldNotContain(Bar);
    }

    [Fact]
    public void LandscapeSwapsTheTwoSidesAndAWideCompactTargetStopsBeingCompact()
    {
        using var rig = new Rig();
        rig.View.PickTarget("Phone");
        rig.Paint();
        rig.View.Surface.Width.ShouldBe(390);
        rig.View.Surface.Height.ShouldBe(844);

        rig.View.SwapSides();
        rig.Paint();

        rig.View.Surface.Width.ShouldBe(844);
        rig.View.Surface.Height.ShouldBe(390);
        rig.View.PlayerOf()!.DesignSize.ShouldBe((844f, 390f));
        rig.Played().ShouldContain(Rail);
    }

    [Fact]
    public void LandscapeStaysLandscapeWhenAnotherTargetIsPickedAndATypedSizeIsShownAsTyped()
    {
        using var rig = new Rig();
        rig.View.PickTarget("Phone");
        rig.View.SwapSides();
        rig.View.PickTarget("Desktop");
        rig.Paint();
        rig.View.Surface.Width.ShouldBe(1440);
        rig.View.Surface.Height.ShouldBe(900);

        rig.View.SwapSides();
        rig.View.PickTarget("Tablet");
        rig.View.Surface.Width.ShouldBe(820);
        rig.View.Surface.Height.ShouldBe(1180);

        rig.View.SwapSides();
        rig.View.CustomInput.Text = "1024x768";
        rig.View.CommitCustom().ShouldBeTrue();
        rig.View.Surface.Width.ShouldBe(1024);
        rig.View.Surface.Height.ShouldBe(768);

        rig.View.SwapSides();
        rig.View.CustomInput.Text = "600x900";
        rig.View.CommitCustom().ShouldBeTrue();
        rig.View.Surface.Width.ShouldBe(600);
        rig.View.Surface.Height.ShouldBe(900);
    }

    [Fact]
    public void ARotatedSquareCustomSizeHasNoOrientationSoAPickShowsTheTargetsOwnShape()
    {
        using var rig = new Rig();
        rig.View.CustomInput.Text = "500x500";
        rig.View.CommitCustom().ShouldBeTrue();
        rig.View.SwapSides();

        rig.View.PickTarget("Desktop");
        rig.View.Surface.Width.ShouldBe(1440);
        rig.View.Surface.Height.ShouldBe(900);
    }

    [Fact]
    public void ACustomSizeIsTypedOnThePageAndABadEntryKeepsTheLastTargetAndSaysWhy()
    {
        using var rig = new Rig();
        rig.View.PickTarget("Tablet");
        rig.Paint();

        rig.View.CustomInput.Text = "500x700@2";
        rig.View.CommitCustom().ShouldBeTrue();
        rig.Paint();
        rig.View.Surface.ShouldBe(new DesignSurface("500x700", 500, 700, 2));

        foreach (var bad in new[] { "", "wide", "0x100", "100x", "99999x100", "100x100@0", "100x100@x" })
        {
            rig.View.CustomInput.Text = bad;
            rig.View.CommitCustom().ShouldBeFalse(bad);
            rig.View.CustomProblem.ShouldNotBeNullOrEmpty(bad);
            rig.View.Surface.Width.ShouldBe(500, bad);
        }
    }

    [Fact]
    public void TheChoiceIsKeptPerDesignInTheGuiSettingsAndReadBackOnReopen()
    {
        var path = TempFile();
        try
        {
            using (var file = GuiSettingsFile.Open(path))
            using (var rig = new Rig())
            {
                rig.Chrome.KeepColumnWidths(file, () => { });
                rig.View.PickTarget("Phone");
                rig.View.SwapSides();
                rig.Paint();
                file.Flush();
            }
            var json = File.ReadAllText(path);
            json.ShouldContain("\"designTargets\"");
            json.ShouldContain("\"DSN-7\"");

            using (var file = GuiSettingsFile.Open(path))
            using (var rig = new Rig())
            {
                file.DesignTargets["DSN-7"].ShouldBe(new SavedDesignTarget("Phone", null, null, null, true));
                rig.Chrome.KeepColumnWidths(file, () => { });
                rig.Paint();
                rig.View.Surface.Width.ShouldBe(844);
                rig.View.Surface.Height.ShouldBe(390);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ACustomChoiceSurvivesAReopenAndAnOlderFileWithNoTargetsReadsAsBefore()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ \"night\": true, \"tables\": {} }");
            using (var file = GuiSettingsFile.Open(path))
            using (var rig = new Rig())
            {
                file.Night.ShouldBeTrue();
                file.DesignTargets.ShouldBeEmpty();
                rig.Chrome.KeepColumnWidths(file, () => { });
                rig.Paint();
                // The document names Desktop first and nothing was chosen: that is the target shown.
                rig.View.Surface.Name.ShouldBe("Desktop");
                rig.View.CustomInput.Text = "600x400@1.5";
                rig.View.CommitCustom().ShouldBeTrue();
                file.Flush();
            }

            using (var file = GuiSettingsFile.Open(path))
            {
                file.DesignTargets["DSN-7"].ShouldBe(new SavedDesignTarget(null, 600, 400, 1.5, false));
                file.Night.ShouldBeTrue();
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
