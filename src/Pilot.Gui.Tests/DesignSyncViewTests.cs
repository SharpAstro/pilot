using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL1171: the sync line under the design's title in each state, the version list naming each version's comment, and a design read from another node (read only, 'made on <node>').</summary>
public sealed class DesignSyncViewTests
{
    private static readonly DateTimeOffset Now = PageData.Now;

    private const string Repo = "SharpAstro/pilot";

    /// <summary>The sample design with its row changed by <paramref name="change"/> (and its versions marked written as <paramref name="written"/> says).</summary>
    private static DesignsScreen With(Func<DesignRow, DesignRow> change, Func<DesignVersionRow, DesignVersionRow>? versions = null)
    {
        var sample = PageData.DesignsFor("sample", Now);
        var row = change(sample.Rows[0]);
        var detail = sample.Open!.Detail with { Row = row, Versions = [.. sample.Open.Detail.Versions.Select(v => versions?.Invoke(v) ?? v)] };
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
            Chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            Chrome.Show(PilotPage.Designs);
            View.OnEdit = (_, _, _, _) => { };
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

    [Fact]
    public void WhileAVersionIsBeingWrittenTheLineSaysWhichAndWhere()
    {
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1 }));

        Says(rig, "Writing v2 to SharpAstro/pilot#1171...").ShouldBeTrue();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void OnceTheVersionIsOnTheIssueTheLineSaysSo()
    {
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 2 }));

        Says(rig, "v2 is on SharpAstro/pilot#1171").ShouldBeTrue();
        Says(rig, "Writing v").ShouldBeFalse();
    }

    [Fact]
    public void AFailedWriteIsInTheErrorColourAndSaysWhatIsKeptAndWhere()
    {
        var failure = DesignSyncText.Failure(DesignSyncText.NoAnswer, 4, "DE1");
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1, SyncError = failure }), 1920, 1080);

        failure.ShouldBe("Not synced: GitHub did not answer. v4 is saved on DE1 and will be sent again.");
        Says(rig, failure).ShouldBeTrue();
        ColourOf(rig, failure).ShouldBe(Palette.Current.Failed);
        Says(rig, "Writing v").ShouldBeFalse();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("design-sync-failed-1920.png", rig.Png());
    }

    [Fact]
    public void AFailedWriteWrapsAtANarrowWindowAndKeepsTheWholeLine()
    {
        var failure = DesignSyncText.Failure(DesignSyncText.NoAnswer, 12, "DE1");
        using var rig = new Rig(With(r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1, SyncError = failure }), 700, 900);

        Says(rig, "Not synced:").ShouldBeTrue();
        Says(rig, "will be sent again.").ShouldBeTrue();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("design-sync-failed-700.png", rig.Png());
    }

    [Fact]
    public void TheVersionListNamesEachVersionsCommentOrThatItIsNotOnTheIssueYet()
    {
        using var rig = new Rig(With(
            r => r with { IssueRepo = Repo, IssueNumber = 1171, SyncedVersion = 1 },
            v => v.Number == 1 ? v with { SyncedAt = Now } : v));
        rig.View.ToggleVersions();
        rig.Paint();

        Says(rig, "Comment on the issue: v1: brief").ShouldBeTrue();
        Says(rig, "Not on the issue yet").ShouldBeTrue();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("design-sync-versions-1920.png", rig.Png());
    }

    private static DesignsScreen ReadFromAnotherNode() => With(r => r with
    {
        Id = "GH-SharpAstro-pilot-1171",
        MadeOn = "DE1",
        IssueRepo = Repo,
        IssueNumber = 1171,
        SyncedVersion = 2,
    });

    [Fact]
    public void ADesignReadFromAnotherNodeIsListedWithTheNodeItWasMadeOn()
    {
        using var rig = new Rig(ReadFromAnotherNode());
        rig.View.Close();
        rig.Paint();

        rig.Texts().ShouldContain(t => t.Contains("made on DE1", StringComparison.Ordinal));
    }

    [Fact]
    public void ADesignReadFromAnotherNodeOpensAsADocumentWithNoTypingAndNoDesigner()
    {
        using var rig = new Rig(ReadFromAnotherNode());

        var texts = rig.Texts();
        Says(rig, "Read from SharpAstro/pilot#1171, made on DE1. Changed there, not here.").ShouldBeTrue();
        texts.ShouldContain("Home");
        // Its prose is not opened for typing: no click target to edit, and no chat to the designer.
        rig.Arranged().Any(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && act.StartsWith("designs:edit:", StringComparison.Ordinal)).ShouldBeFalse();
        Says(rig, "Chat with the designer").ShouldBeFalse();
        rig.View.BeginEdit(0, "objective").ShouldBeFalse();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("design-read-only-1920.png", rig.Png());
    }
}
