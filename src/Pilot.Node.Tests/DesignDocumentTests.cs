using Pilot.Contracts;
using Pilot.Node.Designs;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A design's document (PIL1171): DESIGN.md parses into a title, an objective and cells; formats back unchanged; a draft's screens load from
/// <c>screens/&lt;id&gt;.json</c> under <c>prototype.json</c>'s header; the check says what the document lacks; an older version still loads. Unit tests on temp folders.
/// </summary>
public sealed class DesignDocumentTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-designdoc-" + Guid.NewGuid().ToString("N")[..8]);

    public DesignDocumentTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        TestDirs.DeleteTree(_folder);
    }

    private const string Sample = """
        # Disk alert

        **Objective:** Shows the alert a node raises when its disk is nearly full.

        ## The alert

        **Objective:** Shows what is full and what to do about it.

        ```screen id=alert size=Laptop
        The alert card
        ```

        ```screen id=terminal size=80x24
        ```

        **Notes:** Red only for the number.

        Second paragraph of notes.

        ## Dismissed

        **Objective:** Shows the page after the alert is dismissed.

        ```screen id=dismissed
        ```

        """;

    private static string Text(string raw) => raw.Replace("\r\n", "\n");

    private void Write(string relative, string text)
    {
        var file = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private const string Header = """{ "width": 640, "height": 360, "entry": "alert", "screens": [] }""";

    private static string Screen(string id) => $$"""{ "id": "{{id}}", "title": "{{id}}", "root": { "kind": "text", "text": "{{id}}" } }""";

    private string Prototype => Path.Combine(_folder, "prototype.json");

    private void CleanDraft()
    {
        Write("prototype.json", Header);
        Write("screens/alert.json", $$"""{ "id": "alert", "title": "alert", "root": { "kind": "vstack", "children": [ { "kind": "text", "text": "Open terminal", "action": "goto:terminal" }, { "kind": "text", "text": "Open dismissed", "action": "goto:dismissed" } ] } }""");
        Write("screens/terminal.json", Screen("terminal"));
        Write("screens/dismissed.json", Screen("dismissed"));
        Write("DESIGN.md", Text(Sample));
    }

    [Fact]
    public void ADocumentParsesIntoTitleObjectiveAndCellsWithFramesAndNotes()
    {
        var document = DesignDocument.Parse(Text(Sample));

        document.Title.ShouldBe("Disk alert");
        document.Objective.ShouldBe("Shows the alert a node raises when its disk is nearly full.");
        document.Cells.Select(c => c.Heading).ShouldBe(["The alert", "Dismissed"]);
        var alert = document.Cells[0];
        alert.Objective.ShouldBe("Shows what is full and what to do about it.");
        alert.Frames.ShouldBe([new DesignFrame("alert", "Laptop", "The alert card"), new DesignFrame("terminal", "80x24")]);
        alert.Notes.ShouldBe("Red only for the number.\n\nSecond paragraph of notes.");
        document.Cells[1].Frames.ShouldHaveSingleItem().ShouldBe(new DesignFrame("dismissed"));
        document.Cells[1].Notes.ShouldBe("");
    }

    [Fact]
    public void FormatWritesAParsedDocumentBackUnchanged()
    {
        var text = Text(Sample);

        DesignDocument.Parse(text).Format().ShouldBe(text);
        DesignDocument.Parse(DesignDocument.Starter("Fleet").Format()).Format().ShouldBe(DesignDocument.Starter("Fleet").Format(), "a starter reads back as itself");
    }

    [Fact]
    public void ProseThatIsNoPartOfTheShapeIsKeptAsTheNotesOfTheCellItFallsIn()
    {
        var document = DesignDocument.Parse("# T\n\n**Objective:** O\n\nIntro words.\n\n## A\n\n**Objective:** a\n\nLoose words.\n\n```text\nnot a frame\n```\n\n```screen id=a\n```\n");

        document.Notes.ShouldBe("Intro words.");
        document.Cells.ShouldHaveSingleItem().Notes.ShouldBe("Loose words.\n\n```text\nnot a frame\n```");
        document.Cells[0].Frames.ShouldHaveSingleItem().Id.ShouldBe("a");
        DesignDocument.Parse(document.Format()).Format().ShouldBe(document.Format());
    }

    [Fact]
    public void ADraftsScreensLoadFromTheirFilesUnderThePrototypesHeader()
    {
        CleanDraft();

        var loaded = DesignChecker.Load(Prototype, out var problems);

        problems.ShouldBeEmpty();
        loaded.ShouldNotBeNull().Screens.Select(s => s.Id).ShouldBe(["alert", "terminal", "dismissed"], "in the order the frame blocks name them");
        loaded.Width.ShouldBe(640);
        DesignChecker.Problems(Prototype, requireDocument: true).ShouldBeEmpty();
    }

    [Fact]
    public void ACellWithNoObjectiveIsRefusedWithTheSentenceAsWritten()
    {
        CleanDraft();
        Write("DESIGN.md", Text(Sample).Replace("**Objective:** Shows the page after the alert is dismissed.", "**Objective:**"));

        var problems = DesignChecker.Problems(Prototype, requireDocument: true);

        problems.ShouldHaveSingleItem().ShouldBe("DESIGN.md | Dismissed | objective: This screen has no objective. Say in one or two lines what it shows.");
    }

    [Fact]
    public void TheDesignNeedsAnObjectiveToo()
    {
        CleanDraft();
        Write("DESIGN.md", Text(Sample).Replace("**Objective:** Shows the alert a node raises when its disk is nearly full.\n\n", ""));

        DesignChecker.Problems(Prototype, requireDocument: true).ShouldHaveSingleItem().ShouldStartWith("DESIGN.md | Disk alert | objective: This design has no objective");
    }

    [Fact]
    public void AFrameBlockNamingNoScreenFileAndAScreenFileNoBlockNamesAreFindings()
    {
        CleanDraft();
        Write("DESIGN.md", Text(Sample).Replace("```screen id=dismissed", "```screen id=missing"));

        var problems = DesignChecker.Problems(Prototype, requireDocument: true);

        problems.ShouldContain("DESIGN.md | Dismissed | frame: the block names screen 'missing', which has no screen file (screens/missing.json)");
        problems.ShouldContain("DESIGN.md | - | screen: screens/dismissed.json is named by no frame block; add a cell for it or remove it");
        problems.Length.ShouldBe(2);
    }

    [Fact]
    public void ASizeThatIsNeitherATargetNorColsByRowsIsAFinding()
    {
        CleanDraft();
        Write("DESIGN.md", Text(Sample).Replace("size=Laptop", "size=Huge").Replace("size=80x24", "size=Tablet"));

        var problems = DesignChecker.Problems(Prototype, requireDocument: true);

        problems.ShouldHaveSingleItem().ShouldStartWith("DESIGN.md | The alert | size: 'Huge' is neither Desktop, Laptop, Tablet, Phone nor <cols>x<rows>");
        DesignDocument.IsValidSize("100x30").ShouldBeTrue();
        DesignDocument.IsValidSize("phone").ShouldBeTrue();
        DesignDocument.IsValidSize("0x24").ShouldBeFalse();
    }

    [Fact]
    public void ADraftWithoutADocumentIsOneFindingNamingTheFile()
    {
        CleanDraft();
        File.Delete(Path.Combine(_folder, "DESIGN.md"));

        DesignChecker.Problems(Prototype, requireDocument: true).ShouldHaveSingleItem().ShouldStartWith("DESIGN.md | - | document: DESIGN.md is missing");
        DesignChecker.Problems(Prototype).ShouldBeEmpty("a version written before the document is not refused for lacking one");
    }

    [Fact]
    public void AVersionFromAnOlderReleaseWithItsScreensInPrototypeJsonStillLoadsAndPlays()
    {
        Write("prototype.json", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", "sample.prototype.json")));
        Write("NOTES.md", "How it was.");

        var loaded = DesignChecker.Load(Prototype, out var problems);

        problems.ShouldBeEmpty();
        loaded.ShouldNotBeNull().Screens.Select(s => s.Id).ShouldBe(["home", "settings", "confirm"]);
        DesignChecker.Problems(Prototype).ShouldBeEmpty();
    }

    [Fact]
    public void ADraftFromAnOlderVersionSplitsItsScreensAndKeepsItsNotesInTheStartersDocument()
    {
        var store = new DesignStore(_folder);
        var row = store.New(null, "Old one", "brief");
        var v1 = Path.Combine(store.FolderOf(row.Id), "versions", "v1");
        Directory.CreateDirectory(v1);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", "sample.prototype.json"), Path.Combine(v1, "prototype.json"));
        File.WriteAllText(Path.Combine(v1, "NOTES.md"), "How it was.");
        store.Update(row.Id, r => r with { Current = 1, Versions = 1 });

        var draft = store.Draft(row.Id, replace: false);

        Directory.EnumerateFiles(Path.Combine(draft, "screens")).Select(Path.GetFileName).Order().ShouldBe(["confirm.json", "home.json", "settings.json"]);
        var document = DesignDocument.Parse(File.ReadAllText(Path.Combine(draft, "DESIGN.md")));
        document.Title.ShouldBe("Old one");
        document.Cells.SelectMany(c => c.Frames).Select(f => f.Id).ShouldBe(["home", "settings", "confirm"]);
        document.Cells[0].Notes.ShouldBe("How it was.");
        DesignChecker.Load(Path.Combine(draft, "prototype.json"), out var problems).ShouldNotBeNull().Screens.Select(s => s.Id).ShouldBe(["home", "settings", "confirm"]);
        problems.ShouldBeEmpty();
        // The empty objectives are what the designer is then asked to write.
        store.Check(row.Id).ShouldContain(p => p.Contains("This screen has no objective"));
    }

    [Fact]
    public void PublishCopiesTheDocumentThePrototypeItsScreensAndTheRendersAndRefusesADraftWithoutADocument()
    {
        var store = new DesignStore(_folder);
        var row = store.New(null, "T", "brief");
        var draft = store.Draft(row.Id, replace: false);
        File.Delete(Path.Combine(draft, "DESIGN.md"));
        Should.Throw<DesignException>(() => store.Publish(row.Id, "first")).Message.ShouldContain("DESIGN.md");

        File.WriteAllText(Path.Combine(draft, "DESIGN.md"), "# T\n\n**Objective:** O.\n\n## Home\n\n**Objective:** Shows it.\n\n```screen id=home\n```\n");
        store.Render(row.Id);
        store.Publish(row.Id, "first");

        var v1 = Path.Combine(store.FolderOf(row.Id), "versions", "v1");
        Directory.EnumerateFileSystemEntries(v1).Select(Path.GetFileName).Order().ShouldBe(["DESIGN.md", "prototype.json", "renders", "screens"]);
        File.Exists(Path.Combine(v1, "screens", "home.json")).ShouldBeTrue();
        new FileInfo(Path.Combine(v1, "screens", "home.json")).IsReadOnly.ShouldBeTrue();
        DesignStore.Sha256Of(Path.Combine(v1, "prototype.json")).ShouldBe(store.Find(row.Id)!.PrototypeSha256);
    }
}
