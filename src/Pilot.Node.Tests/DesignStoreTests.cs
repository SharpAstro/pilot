using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The design store (PIL1096): ids, the brief, the draft and the record, on a temporary folder.</summary>
public sealed class DesignStoreTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "p2ds-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero));

    private DesignStore Store() => new(_data, _time);

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void IdsCountUpAndADeletedDesignsIdIsNotHandedOutAgain()
    {
        var store = Store();
        store.New(null, null, "first").Id.ShouldBe("DSN-1");
        store.New(null, null, "second").Id.ShouldBe("DSN-2");

        store.Delete("DSN-2");
        // Even a store opened afresh, as after a restart, counts on from the counter and not from the folders left.
        Store().New(null, null, "third").Id.ShouldBe("DSN-3");
        Store().List().Select(r => r.Id).ShouldBe(["DSN-3", "DSN-1"]);
    }

    [Fact]
    public void ACounterThatCannotBeReadIsRebuiltAboveTheFoldersOnDisk()
    {
        var store = Store();
        store.New(null, null, "one");
        store.New(null, null, "two");
        File.WriteAllText(Path.Combine(_data, "designs", "ids.json"), "not json");

        Store().New(null, null, "three").Id.ShouldBe("DSN-3");
    }

    [Fact]
    public void ADesignHasNoTitleUnlessGivenAndItIsNeverMadeFromTheBrief()
    {
        var store = Store();
        store.New(null, null, "
  A login screen
with more below").Title.ShouldBeNull();
        store.New(null, "  ", new string('x', 300)).Title.ShouldBeNull();
        store.New("pilot", "  My title ", "brief").Title.ShouldBe("My title");
        store.New(null, new string('t', DesignTitle.MaxLength), "brief").Title.ShouldBe(new string('t', DesignTitle.MaxLength));
        Should.Throw<DesignException>(() => store.New(null, new string('t', DesignTitle.MaxLength + 1), "brief")).Message.ShouldContain("41 characters, over the limit of 40");
        store.New("pilot", null, "brief").Repo.ShouldBe("pilot");
        store.New("  ", null, "brief").Repo.ShouldBeNull();
        Should.Throw<DesignException>(() => store.New(null, null, "   "));
    }

    [Fact]
    public void TheBriefKeepsEachPieceVerbatimUnderItsTimeOldestFirst()
    {
        var store = Store();
        var row = store.New(null, null, "Make it   \"blue\"\n\n  and round <b>");
        _time.Advance(TimeSpan.FromMinutes(5));
        store.Brief(row.Id, "Also a dark mode");
        _time.Advance(TimeSpan.FromHours(1));
        store.Brief("dsn-1", "Last word");

        var brief = File.ReadAllText(Path.Combine(_data, "designs", "DSN-1", "BRIEF.md")).Replace("\r\n", "\n");
        brief.ShouldBe("""
            ## 2026-11-16T09:30:00Z

            Make it   "blue"

              and round <b>

            ## 2026-11-16T09:35:00Z

            Also a dark mode

            ## 2026-11-16T10:35:00Z

            Last word

            """.Replace("\r\n", "\n"));
        Should.Throw<DesignException>(() => store.Brief("DSN-9", "x"));
        Should.Throw<DesignException>(() => store.Brief("DSN-1", " "));
        Should.Throw<DesignException>(() => store.Brief("nonsense", "x"));
    }

    [Fact]
    public void ADraftFromNothingIsAStarterPrototypeWithOneScreenAndAnEmptyNotesFile()
    {
        var store = Store();
        var row = store.New(null, null, "brief");

        var draft = store.Draft(row.Id, replace: false);

        draft.ShouldBe(Path.Combine(_data, "designs", "DSN-1", "draft"));
        File.Exists(Path.Combine(draft, "NOTES.md")).ShouldBeFalse("NOTES.md is the designer's own, optional");
        var starter = DesignDocument.Parse(File.ReadAllText(Path.Combine(draft, "DESIGN.md")));
        starter.Objective.ShouldBe("");
        starter.Cells.ShouldHaveSingleItem().Frames.ShouldHaveSingleItem().Id.ShouldBe("home");
        File.ReadAllText(Path.Combine(draft, "prototype.json")).ShouldBe(DesignStore.StarterPrototype);
        using var document = JsonDocument.Parse(DesignFiles.AssembledJson(Path.Combine(draft, "prototype.json"), out _)!);
        var root = document.RootElement;
        var screens = root.GetProperty("screens");
        screens.GetArrayLength().ShouldBe(1);
        root.GetProperty("entry").GetString().ShouldBe(screens[0].GetProperty("id").GetString());
        screens[0].GetProperty("root").GetProperty("kind").GetString().ShouldBe("text");
        File.Exists(Path.Combine(draft, "screens", "home.json")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_data, "designs", "DSN-1", "versions")).ShouldBeFalse("a draft writes nothing under versions/");
    }

    [Fact]
    public void ADraftFromAVersionCopiesItsPrototypeAndNotes()
    {
        var store = Store();
        var row = store.New(null, null, "brief");
        var v2 = Path.Combine(_data, "designs", row.Id, "versions", "v2");
        Directory.CreateDirectory(v2);
        File.WriteAllText(Path.Combine(v2, "prototype.json"), """{"from":"v2"}""");
        File.WriteAllText(Path.Combine(v2, "NOTES.md"), "what changed");
        File.WriteAllText(Path.Combine(v2, "DESIGN.md"), "# T\n");
        Directory.CreateDirectory(Path.Combine(v2, "screens"));
        File.WriteAllText(Path.Combine(v2, "screens", "a.json"), "{}");
        File.WriteAllText(Path.Combine(_data, "designs", row.Id, "design.json"), JsonSerializer.Serialize(row with { Current = 2, Versions = 2 }, PilotJsonContext.Default.DesignRow));

        var draft = store.Draft(row.Id, replace: false);

        File.ReadAllText(Path.Combine(draft, "prototype.json")).ShouldBe("""{"from":"v2"}""");
        File.Exists(Path.Combine(draft, "NOTES.md")).ShouldBeFalse("only DESIGN.md and screens/ carry over");
        File.ReadAllText(Path.Combine(draft, "DESIGN.md")).ShouldBe("# T\n");
        File.ReadAllText(Path.Combine(draft, "screens", "a.json")).ShouldBe("{}");
        File.ReadAllText(Path.Combine(v2, "prototype.json")).ShouldBe("""{"from":"v2"}""");
    }

    [Fact]
    public void ADraftIsRefusedOverAnExistingOneUnlessReplacedAndReplacingStartsAgain()
    {
        var store = Store();
        var row = store.New(null, null, "brief");
        var draft = store.Draft(row.Id, replace: false);
        File.WriteAllText(Path.Combine(draft, "prototype.json"), "edited by the designer");

        Should.Throw<DesignException>(() => store.Draft(row.Id, replace: false)).Message.ShouldContain("--replace");
        File.ReadAllText(Path.Combine(draft, "prototype.json")).ShouldBe("edited by the designer");

        store.Draft(row.Id, replace: true);
        File.ReadAllText(Path.Combine(draft, "prototype.json")).ShouldBe(DesignStore.StarterPrototype);
        Should.Throw<DesignException>(() => store.Draft("DSN-7", replace: false));
    }

    [Fact]
    public void TheRecordRoundTripsAndEveryMemberAfterTheIdMayBeMissingFromAnOlderFile()
    {
        var store = Store();
        var row = new DesignRow
        {
            Id = "DSN-1", Title = "T", Repo = "pilot", Current = 3, Versions = 3, At = _time.GetUtcNow(), Why = "the owner asked", PrototypeSha256 = "ab12",
        };
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(row, PilotJsonContext.Default.DesignRow), PilotJsonContext.Default.DesignRow).ShouldNotBeNull();
        back.History.ShouldBeEmpty();
        back.Terminals.ShouldBeEmpty();
        back.DesignerPending.ShouldBeEmpty();
        (back with { History = row.History, Terminals = row.Terminals, DesignerPending = row.DesignerPending, DesignerTyped = row.DesignerTyped, DesignerSessions = row.DesignerSessions, Builds = row.Builds, GitHubEdits = row.GitHubEdits, GitHubIgnored = row.GitHubIgnored }).ShouldBe(row);

        var made = store.New("pilot", "T", "brief");
        (store.Find("DSN-1").ShouldNotBeNull() with { History = made.History, Terminals = made.Terminals, DesignerPending = made.DesignerPending, DesignerTyped = made.DesignerTyped, DesignerSessions = made.DesignerSessions, Builds = made.Builds, GitHubEdits = made.GitHubEdits, GitHubIgnored = made.GitHubIgnored }).ShouldBe(made);
        made.Current.ShouldBeNull();
        made.Versions.ShouldBe(0);

        File.WriteAllText(Path.Combine(_data, "designs", "DSN-1", "design.json"), """{"id":"DSN-1"}""");
        var bare = store.Find("DSN-1").ShouldNotBeNull();
        bare.Title.ShouldBeNull();
        bare.Current.ShouldBeNull();
        bare.PrototypeSha256.ShouldBeNull();
    }

    [Fact]
    public void ADetailReadsTheRecordTheBriefAndTheFolders()
    {
        var store = Store();
        var row = store.New("pilot", null, "the brief");
        var detail = store.Detail(row.Id);

        detail.Row.History.ShouldBeEmpty();
        (detail.Row with { History = row.History, Terminals = row.Terminals, DesignerPending = row.DesignerPending, DesignerTyped = row.DesignerTyped, DesignerSessions = row.DesignerSessions, Builds = row.Builds, GitHubEdits = row.GitHubEdits, GitHubIgnored = row.GitHubIgnored }).ShouldBe(row);
        detail.Brief.ShouldNotBeNull().ShouldContain("the brief");
        detail.Folder.ShouldBe(Path.Combine(_data, "designs", "DSN-1"));
        detail.DraftFolder.ShouldBe(Path.Combine(_data, "designs", "DSN-1", "draft"));
        detail.CurrentFolder.ShouldBeNull();
        detail.Versions.ShouldBeEmpty();
        Should.Throw<DesignException>(() => store.Detail("DSN-2"));
    }

    [Fact]
    public void TheListIsNewestFirstAndEmptyWithNoStore()
    {
        var store = Store();
        store.List().ShouldBeEmpty();
        store.New(null, null, "a");
        store.New(null, null, "b");
        for (var i = 0; i < 9; i++)
        {
            store.New(null, null, "c" + i);
        }

        store.List().Select(r => r.Id).Take(3).ShouldBe(["DSN-11", "DSN-10", "DSN-9"]);
    }
}
