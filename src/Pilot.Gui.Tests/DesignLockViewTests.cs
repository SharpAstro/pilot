using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL1171: a design locked on another node is read only, 'being edited on &lt;node&gt;'; a GitHub edit waits above the cell it touches with Apply and Dismiss.</summary>
public sealed class DesignLockViewTests
{
    private static readonly DateTimeOffset Now = PageData.Now;

    private const string Repo = "SharpAstro/pilot";

    private static DesignsScreen With(Func<DesignRow, DesignRow> change)
    {
        var sample = PageData.DesignsFor("sample", Now);
        var row = change(sample.Rows[0]);
        var detail = sample.Open!.Detail with { Row = row };
        return sample with { Rows = [row, .. sample.Rows.Skip(1)], Open = sample.Open with { Detail = detail } };
    }

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly int _width;
        private readonly int _height;

        public Rig(DesignsScreen screen, int width = 1920, int height = 1080)
        {
            _width = width;
            _height = height;
            _renderer = new RgbaImageRenderer((uint)width, (uint)height);
            PageTime.Zone = TimeZoneInfo.Utc;
            Screen = screen;
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Show(PilotPage.Designs);
            View.OnEdit = (_, _, _, _, _) => { };
            View.Open(screen.Rows[0].Id);
            Paint();
        }

        public DesignsScreen Screen { get; }
        public PilotChrome<RgbaImage> Chrome { get; }
        public DesignsView<RgbaImage> View => Chrome.Designs;

        public void Paint()
        {
            var data = PageData.For("sample");
            Chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, _width, _height), data.Machines, null, data.Spend, Screen);
        }

        public List<Layout.ArrangedNode<float>> Arranged() =>
            Layout.Engine.Arrange(View.Tree(Screen), new Rect<float>(0, 0, _width, _height), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).ToList();

        public List<string> Texts() => SentenceRuns.Texts(Arranged().Select(a => a.Node));

        public byte[] Png() => SharpAstro.Png.PngWriter.Encode([.. _renderer.Surface.Pixels], _width, _height);

        public void Dispose() => _renderer.Dispose();
    }

    private static void Keep(string name, byte[] png)
    {
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(artifacts);
        File.WriteAllBytes(Path.Combine(artifacts, name), png);
    }

    private static RGBAColor32 ColourOf(Rig rig, string part) =>
        rig.Arranged().Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().First(t => t.Value.Length > 2 && part.Contains(t.Value, StringComparison.Ordinal)).Color;

    private static bool Says(Rig rig, string line) => rig.Texts().Any(t => t.Contains(line, StringComparison.Ordinal));

    private static DesignsScreen LockedElsewhere() => With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1, LockedBy = "DE1" });

    [Fact]
    public void ADesignLockedOnAnotherNodeOpensReadOnlyAndSaysWhichNode()
    {
        using var rig = new Rig(LockedElsewhere());

        Says(rig, "Being edited on DE1.").ShouldBeTrue();
        rig.Arranged().Any(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && act.StartsWith("designs:edit:", StringComparison.Ordinal)).ShouldBeFalse();
        Says(rig, "Chat with the designer").ShouldBeFalse();
        rig.View.BeginEdit(0, "objective").ShouldBeFalse();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("design-locked-1920.png", rig.Png());
    }

    [Fact]
    public void TheLockLineWrapsAtANarrowWindow()
    {
        using var rig = new Rig(LockedElsewhere(), 700, 900);

        Says(rig, "Being edited on DE1.").ShouldBeTrue();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("design-locked-700.png", rig.Png());
    }

    [Fact]
    public void ADesignThisNodeHoldsIsNotReadOnly()
    {
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1, LockedBy = "DE1", LockHeld = true }));

        Says(rig, "Being edited on").ShouldBeFalse();
        rig.View.BeginEdit(0, "objective").ShouldBeTrue();
    }

    [Fact]
    public void AGitHubEditWaitsAboveTheCellItTouchesWithApplyAndDismiss()
    {
        var edit = new DesignGitHubEdit { Part = "objective", Cell = "1", Text = "Words typed on GitHub." };
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1, LockHeld = true, GitHubEdits = [edit] }));
        rig.View.OnGitHub = (_, _, _) => { };
        rig.Paint();

        Says(rig, DesignLockText.Waiting("objective of cell 1")).ShouldBeTrue();
        Says(rig, "Words typed on GitHub.").ShouldBeTrue();
        var texts = rig.Texts();
        texts.ShouldContain(DesignsView<RgbaImage>.ApplyLabel);
        texts.ShouldContain(DesignsView<RgbaImage>.DismissLabel);
        rig.Arranged().Any(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:gh-apply:objective:1" }).ShouldBeTrue();
        rig.Arranged().Any(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:gh-dismiss:objective:1" }).ShouldBeTrue();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("design-github-edit-1920.png", rig.Png());
    }

    [Fact]
    public void AnIgnoredGitHubDifferenceIsSaidWithItsReasonAtANarrowWindow()
    {
        const string line = "A frame of cell 1 was changed on GitHub: a frame's link or block is not applied, a screen is changed through the designer.";
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1, GitHubIgnored = [line] }), 700, 900);

        Says(rig, "a frame's link or block is not applied").ShouldBeTrue();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void AFailedLabellingIsInTheErrorColour()
    {
        var failed = DesignLockText.LabelFailed("GitHub did not answer");
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1, LockError = failed }));

        Says(rig, "The issue could not be labelled").ShouldBeTrue();
        ColourOf(rig, failed).ShouldBe(Palette.Current.Failed);
    }
}
