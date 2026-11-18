using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL1183: a cell's notes in edit. The editor is as wide as its cell, one label, a Markdown hint, a Publish that says what it waits for; and the sync state of a design with no issue.</summary>
public sealed class DesignTypingViewTestsNotes
{
    private static readonly DateTimeOffset Now = PageData.Now;

    private const string LongNotes = "# Disk alert\n\nOne settings screen, 640 wide, for the node's low-disk alert. It names the drive, the free space and the threshold, and says what happens when the threshold is crossed: new flights wait, running ones finish.\n\n- the threshold is a percentage\n- the drive is the one the store lives on";

    private static DesignsScreen WithNotes(Func<DesignRow, DesignRow>? change = null)
    {
        var sample = PageData.DesignsFor("sample", Now);
        var doc = sample.Open!.Doc!;
        var cell = doc.Cells[0] with { Notes = LongNotes };
        var withNotes = sample with { Open = sample.Open with { Doc = doc with { Cells = [cell, .. doc.Cells.Skip(1)] } } };
        if (change is null)
        {
            return withNotes;
        }
        var row = change(withNotes.Open!.Detail.Row);
        var detail = withNotes.Open.Detail with { Row = row };
        return withNotes with { Rows = [row, .. withNotes.Rows.Skip(1)], Open = withNotes.Open with { Detail = detail } };
    }

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly int _width;
        private readonly int _height;
        public readonly List<(string Id, string Reference)> Links = [];

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
            View.OnPublishTyped = (_, _, _) => { };
            View.OnLinkIssue = (id, reference) => Links.Add((id, reference));
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

        public byte[] Png() => SharpAstro.Png.PngWriter.Encode([.. _renderer.Surface.Pixels], _width, _height);

        public void Dispose() => _renderer.Dispose();
    }

    private static void Keep(string name, byte[] png) => Keep(name, (Action<string>)(path => File.WriteAllBytes(path, png)));

    private static void Keep(string name, Action<string> write)
    {
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(artifacts);
        write(Path.Combine(artifacts, name));
    }

    private static bool IsEditor(Layout.ArrangedNode<float> a) => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor };

    private static bool IsInput(Layout.ArrangedNode<float> a) => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput };

    private static List<string> Texts(IEnumerable<Layout.ArrangedNode<float>> all) => SentenceRuns.Texts(all.Select(a => a.Node));

    // Typing into the notes and then a Why in progress: the cell's editor with its panel.
    private static Rig Editing(int width, int height)
    {
        var rig = new Rig(WithNotes(), width, height);
        rig.View.BeginEdit(0, "notes").ShouldBeTrue();
        rig.View.Editor.Text = LongNotes + "\n\nmore words typed";
        rig.Paint();
        return rig;
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2880, 1620)]
    public void TheNotesEditorIsAsWideAsItsCellsContent(int width, int height)
    {
        using var rig = Editing(width, height);

        var all = rig.Arranged();
        var editor = all.Single(IsEditor);
        // The cell's content ends where the Why field under the editor ends: both are star-wide in the same column.
        var why = all.Single(IsInput);
        editor.Bounds.Right.ShouldBe(why.Bounds.Right, 1f);
        editor.Bounds.Width.ShouldBeGreaterThan(400f, "not the minimum width");
        // The notes were rendered as wide as the column too, so the page does not jump on switching.
        var before = new Rig(WithNotes(), width, height);
        var rendered = before.Arranged().Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.Contains("threshold", StringComparison.Ordinal)).Select(a => a.Bounds.X).Min();
        editor.Bounds.X.ShouldBeLessThanOrEqualTo(rendered + 1f);
        before.Dispose();
        // What is in view: the page scrolls, and what is past the bottom is laid out against the viewport's end in this plain arrange.
        TextOverlap.Faults([.. all.Where(a => a.Bounds.Y + a.Bounds.Height <= height)]).Select(f => f.Line).ShouldBeEmpty();
        Keep($"design-notes-edit-{width}.png", rig.Png());
    }

    [Fact]
    public void TheNotesEditorHasOneLabelAndAMarkdownHint()
    {
        using var rig = Editing(1920, 1080);

        var texts = Texts(rig.Arranged());
        texts.Count(t => t == "Notes").ShouldBe(1);
        texts.ShouldNotContain(t => t.StartsWith("Notes of", StringComparison.Ordinal));
        texts.ShouldContain(DesignsView<RgbaImage>.NotesHint);
        DesignsView<RgbaImage>.NotesHint.ShouldStartWith("Markdown");
        texts.ShouldContain("Why (required)");
    }

    [Fact]
    public void TheEditorGrowsToAShareOfThePageThenScrolls()
    {
        using var rig = Editing(1920, 1080);

        rig.View.Editor.MaxLines.ShouldBe((int)(1080 * DesignsView<RgbaImage>.EditorPageShare / 18f));
        var height = rig.Arranged().Single(IsEditor).Bounds.Height;
        height.ShouldBeLessThanOrEqualTo(1080 * DesignsView<RgbaImage>.EditorPageShare + 40f);
    }

    [Fact]
    public void ADimmedPublishSaysWhatItWaitsFor()
    {
        using var rig = Editing(1920, 1080);

        var publish = rig.Arranged().Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == "Publish v3");
        publish.Node.Tooltip.ShouldBe("Write a why to publish");

        rig.View.Editor.Text = LongNotes;
        rig.View.SaveEdit();
        rig.View.WhyInput.Text = "";
        rig.Paint();
    }

    [Fact]
    public void ADesignWithNoIssueShowsTheNeutralLineAndALinkButtonNamingTheCommand()
    {
        using var rig = new Rig(WithNotes(r => r with { Repo = null, IssueRepo = null, IssueNumber = null, SyncError = null }));

        var all = rig.Arranged();
        var line = SentenceRuns.Of(all.Select(a => a.Node)).Single(r => r.Text.StartsWith("Kept on ", StringComparison.Ordinal) && r.Text.EndsWith(" only", StringComparison.Ordinal));
        line.Color.ShouldNotBe(Palette.Current.Failed);
        var button = all.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:link" });
        button.Node.Tooltip.ShouldBe("pilot design issue DSN-1 <repo>#<n>");
        Texts(all).ShouldContain("Link an issue");
        TextOverlap.Faults([.. all]).Select(f => f.Line).ShouldBeEmpty();

        // Pressing it opens the field; typing a reference and pressing Link sends it.
        rig.View.LinkInput.Text = "SharpAstro/pilot#1171";
        rig.View.SubmitLink("DSN-1").ShouldBeTrue();
        rig.Links.ShouldHaveSingleItem().ShouldBe(("DSN-1", "SharpAstro/pilot#1171"));
    }

    [Fact]
    public void AnOldStoredNoRepoLineIsReadAsTheStateNotAsAFailure()
    {
        var old = "Not synced: DSN-1 names no repo, so v2 is kept on DE1 only. Link an issue with: pilot design issue DSN-1 <repo>#<n>";
        using var rig = new Rig(WithNotes(r => r with { Repo = null, IssueRepo = null, SyncError = old }));

        var texts = Texts(rig.Arranged());
        texts.ShouldNotContain(old);
        texts.ShouldContain(t => t.StartsWith("Kept on ", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedSyncKeepsTheErrorColour()
    {
        var failure = DesignSyncText.Failure(DesignSyncText.NoAnswer, 2, "DE1");
        using var rig = new Rig(WithNotes(r => r with { IssueRepo = "SharpAstro/pilot", IssueNumber = 1171, SyncedVersion = 1, SyncError = failure }));

        SentenceRuns.Of(rig.Arranged().Select(a => a.Node)).Single(r => r.Text == failure).Color.ShouldBe(Palette.Current.Failed);
    }

    [Fact]
    public void TheNotesEditorInPilotTuiIsAsWideAsItsCell()
    {
        const int columns = 160;
        const int rows = 44;
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        var screen = WithNotes();
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (columns, rows);
        chrome.Designs.OnEdit = (_, _, _, _) => { };
        chrome.Designs.OnPublishTyped = (_, _, _) => { };
        chrome.Designs.Open(screen.Open!.Detail.Row.Id);
        var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, PageData.Now, _ => { }, null, columns);
        TerminalFrame.Build(rail, chrome.Designs.Tree(screen), null, columns, rows, CellMeasureContext.PixelAuthored);
        chrome.Designs.BeginEdit(0, "notes").ShouldBeTrue();
        chrome.Designs.Editor.Text = LongNotes;
        // The frame at 160 x 44 shows the top of the page (the editor is below the frame's well, past the viewport), so the same tree is arranged in a window as wide and tall enough for the whole page.
        var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, chrome.Designs.Tree(screen), null), new Rect<int>(0, 0, columns, 400), CellMeasureContext.PixelAuthored);
        var editor =arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor });
        var why = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput });
        (editor.Bounds.X + editor.Bounds.Width).ShouldBe(why.Bounds.X + why.Bounds.Width);
        editor.Bounds.Width.ShouldBeGreaterThan(60);
        Keep("design-notes-edit-160x44.txt", path => File.WriteAllLines(path, arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .OrderBy(a => a.Bounds.Y).ThenBy(a => a.Bounds.X)
            .Select(a => $"{a.Bounds.Y,3},{a.Bounds.X,3}  {((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value}")
            .Append($"EDITOR x={editor.Bounds.X} y={editor.Bounds.Y} w={editor.Bounds.Width} h={editor.Bounds.Height}")));
    }
}
