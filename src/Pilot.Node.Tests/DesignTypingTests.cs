using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The owner changes a design by typing (PIL1171): <c>design-edit</c> writes one prose part of DESIGN.md into the draft, a prose-only draft publishes with the
/// current version's renders as <c>owner: &lt;why&gt;</c>, <c>design-discard</c> removes the draft, and the designer is told what was typed at its next turn without
/// being woken. Temp folders, an in-memory node's parts and a fake launcher; no process starts.
/// </summary>
public sealed class DesignTypingTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly string _data = Path.Combine(Path.GetTempPath(), "p2typ-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly DesignStore _store;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly Designers _designers;
    private readonly DesignCommands _commands;

    public DesignTypingTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _store = new DesignStore(_data, _time);
        _designers = new Designers(_state, _store, _workers, new StartGate(_state, _time), new RoleModels(null), "DE1", _time)
        {
            Run = "pilot",
            Say = (command, _) =>
            {
                _said.Add(command);
                return Task.FromResult(_state.Complete(command, null));
            },
        };
        _commands = new DesignCommands(_state, _store, _designers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            TestDirs.DeleteTree(_data);
        }
    }

    private const string SampleDocument = """
        # A fleet screen

        **Objective:** Shows the sample: a home screen, its settings and a confirm modal.

        ## Home

        **Objective:** Shows the fleet at a glance.

        ```screen id=home
        ```

        **Notes:** Quiet.

        ## Settings

        **Objective:** Shows what can be changed.

        ```screen id=settings size=Phone
        ```

        ## Confirm

        **Objective:** Shows the question asked before a change.

        ```screen id=confirm
        ```

        """;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", name);

    private string Draft(string file = "") => Path.Combine(_store.FolderOf("DSN-1"), "draft", file);

    private CommandResult Run(string verb, params string[] args) =>
        _commands.Run(new CommandRequest { Verb = verb, Target = Verbs.DesignTarget("DSN-1"), Args = args, Who = "gui" });

    /// <summary>A design with a published v1 of the sample, no draft left.</summary>
    private void PublishedV1()
    {
        // Through the store, so no designer is asked for by the first brief.
        _store.New(null, null, "A fleet screen").Id.ShouldBe("DSN-1");
        _store.Draft("DSN-1", replace: false);
        File.Copy(Fixture("sample.prototype.json"), Draft("prototype.json"), overwrite: true);
        Directory.Delete(Draft("screens"), recursive: true);
        File.WriteAllText(Draft("DESIGN.md"), SampleDocument);
        _store.Render("DSN-1");
        _store.Publish("DSN-1", "brief").Current.ShouldBe(1);
        _store.Discard("DSN-1");
    }

    [Fact]
    public void EditWritesOnePartAndLeavesTheRestOfTheDocumentAsItWas()
    {
        PublishedV1();
        Directory.Exists(Draft()).ShouldBeFalse();
        var before = DesignDocument.Parse(SampleDocument);

        Run(Verbs.DesignEdit, "objective", "Settings", "Shows the settings, grouped.").Ok.ShouldBeTrue();

        // The draft was made from the version first, and only that one part moved.
        var text = File.ReadAllText(Draft("DESIGN.md"));
        var after = DesignDocument.Parse(text);
        after.Cells[1].Objective.ShouldBe("Shows the settings, grouped.");
        text.ShouldBe((before with { Cells = [before.Cells[0], before.Cells[1] with { Objective = "Shows the settings, grouped." }, before.Cells[2]] }).Format());
        File.Exists(Draft("prototype.json")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("objective", "", "Now says it plainly.")]
    [InlineData("notes", "", "A note on the whole.")]
    [InlineData("heading", "Home", "Overview")]
    [InlineData("objective", "home", "Shows the lot.")]
    [InlineData("notes", "1", "Loud now.")]
    [InlineData("notes", "Confirm", "New words.")]
    public void EachProsePartCanBeTypedAndOnlyThatPartChanges(string part, string cell, string text)
    {
        PublishedV1();
        var before = DesignDocument.Parse(SampleDocument);

        Run(Verbs.DesignEdit, part, cell, text).Ok.ShouldBeTrue();

        var after = DesignDocument.Parse(File.ReadAllText(Draft("DESIGN.md")));
        after.Cells.Count.ShouldBe(before.Cells.Count);
        var changed = 0;
        if (after.Title != before.Title) { changed++; }
        if (after.Objective != before.Objective) { changed++; after.Objective.ShouldBe(text); }
        if (after.Notes != before.Notes) { changed++; after.Notes.ShouldBe(text); }
        for (var i = 0; i < before.Cells.Count; i++)
        {
            after.Cells[i].Frames.ShouldBe(before.Cells[i].Frames);
            changed += (after.Cells[i].Heading != before.Cells[i].Heading ? 1 : 0) + (after.Cells[i].Objective != before.Cells[i].Objective ? 1 : 0) + (after.Cells[i].Notes != before.Cells[i].Notes ? 1 : 0);
        }
        changed.ShouldBe(1);
    }

    [Theory]
    [InlineData("objective", "Home", "")]
    [InlineData("heading", "", "A title")]
    [InlineData("heading", "Nowhere", "x")]
    [InlineData("frame", "Home", "x")]
    [InlineData("notes", "Home", "```screen id=evil\n```")]
    [InlineData("objective", "Home", "one\n\ntwo")]
    public void ATypedEditThatCannotBeKeptIsRefusedAndWritesNothing(string part, string cell, string text)
    {
        PublishedV1();

        var result = Run(Verbs.DesignEdit, part, cell, text);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        _store.Find("DSN-1")!.DesignerTyped.ShouldBeEmpty();
    }

    [Fact]
    public void AProseOnlyDraftPublishesWithTheCurrentRendersAndTheOwnersWhy()
    {
        PublishedV1();
        Run(Verbs.DesignEdit, "objective", "Home", "Shows the fleet, nodes first.").Ok.ShouldBeTrue();
        File.Exists(Draft("renders/render.json")).ShouldBeFalse();

        var published = Run(Verbs.DesignPublish, "sharper home objective", "owner");

        published.Ok.ShouldBeTrue(published.Error);
        var row = _store.Find("DSN-1")!;
        row.Current.ShouldBe(2);
        row.Why.ShouldBe("owner: sharper home objective");
        row.History.Last().Why.ShouldBe("owner: sharper home objective");
        var v2 = Path.Combine(_store.FolderOf("DSN-1"), "versions", "v2");
        File.ReadAllText(Path.Combine(v2, "DESIGN.md")).ShouldContain("Shows the fleet, nodes first.");
        Directory.EnumerateFiles(Path.Combine(v2, "renders"), "*.png").Count().ShouldBe(3);
        // The version before is as it was.
        File.ReadAllText(Path.Combine(_store.FolderOf("DSN-1"), "versions", "v1", "DESIGN.md")).ShouldContain("Shows the fleet at a glance.");
    }

    [Fact]
    public void ADraftWhosePrototypeDiffersAndWasNotRenderedIsRefusedLikeAnyDraft()
    {
        PublishedV1();
        Run(Verbs.DesignEdit, "notes", "Home", "Louder.").Ok.ShouldBeTrue();
        File.AppendAllText(Draft("prototype.json"), "\n");

        var published = Run(Verbs.DesignPublish, "louder", "owner");

        published.Ok.ShouldBeFalse();
        published.Error.ShouldContain("renders are not of the draft");
        _store.Find("DSN-1")!.Current.ShouldBe(1);
    }

    [Fact]
    public void AnOwnersWhyIsHeldToTheSameRulesWithItsPrefix()
    {
        PublishedV1();
        Run(Verbs.DesignEdit, "notes", "Home", "Louder.").Ok.ShouldBeTrue();

        Run(Verbs.DesignPublish, "", "owner").Ok.ShouldBeFalse();
        Run(Verbs.DesignPublish, "two\nlines", "owner").Ok.ShouldBeFalse();
        Run(Verbs.DesignPublish, new string('x', DesignWhys.MaxLength - DesignWhys.OwnerPrefix.Length + 1), "owner").Ok.ShouldBeFalse();
        Run(Verbs.DesignPublish, new string('x', DesignWhys.MaxLength - DesignWhys.OwnerPrefix.Length), "owner").Ok.ShouldBeTrue();
    }

    [Fact]
    public void DiscardRemovesTheDraftAndRefusesWhenThereIsNone()
    {
        PublishedV1();
        Run(Verbs.DesignEdit, "notes", "Home", "Louder.").Ok.ShouldBeTrue();
        Directory.Exists(Draft()).ShouldBeTrue();

        Run(Verbs.DesignDiscard).Ok.ShouldBeTrue();

        Directory.Exists(Draft()).ShouldBeFalse();
        Run(Verbs.DesignDiscard).Ok.ShouldBeFalse();
        _store.Find("DSN-1")!.Current.ShouldBe(1);
    }

    [Fact]
    public async Task ATypedEditIsQueuedForTheDesignerWithoutWakingIt()
    {
        PublishedV1();
        await _designers.Settled;
        var started = _workers.Started.Count;

        Run(Verbs.DesignEdit, "objective", "Settings", "Shows the settings, grouped.").Ok.ShouldBeTrue();
        _designers.Sweep();
        await _designers.Settled;

        _said.ShouldBeEmpty();
        _workers.Started.Count.ShouldBe(started);
        _store.Find("DSN-1")!.DesignerTyped.ShouldHaveSingleItem().ShouldBe("The owner typed into DESIGN.md: objective of Settings now reads: Shows the settings, grouped.");
    }

    [Fact]
    public async Task TheQueuedEditReachesTheDesignerWithItsNextSayInTheDesignersWords()
    {
        PublishedV1();
        _state.PutSession(new SessionRow
        {
            Id = "job1", SessionId = "job1-0000-4000-8000-000000000000", Name = _designers.NameOf("DSN-1"), Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Designer,
            ContextTokens = 1, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = Start,
        });
        Run(Verbs.DesignEdit, "heading", "Home", "Overview").Ok.ShouldBeTrue();
        Run(Verbs.DesignEdit, "notes", "", "Dark only.").Ok.ShouldBeTrue();
        _said.ShouldBeEmpty();

        Run(Verbs.DesignSay, "make the home screen calmer").Ok.ShouldBeTrue();
        await _designers.Settled;

        var said = _said.ShouldHaveSingleItem().Args![0];
        said.ShouldContain("The owner typed into DESIGN.md: heading of Home now reads: Overview");
        said.ShouldContain("The owner typed into DESIGN.md: notes of the design now reads: Dark only.");
        said.ShouldContain("make the home screen calmer");
        _store.Find("DSN-1")!.DesignerTyped.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADesignerStartedAfterTypingIsToldWhatWasTyped()
    {
        PublishedV1();
        Run(Verbs.DesignEdit, "notes", "Home", "Louder.").Ok.ShouldBeTrue();

        Run(Verbs.DesignBrief, "more words").Ok.ShouldBeTrue();
        await _designers.Settled;

        var start = _workers.Started.ShouldHaveSingleItem();
        start.Brief.ShouldContain("The owner typed into DESIGN.md: notes of Home now reads: Louder.");
        start.Brief.ShouldContain("re-read `draft/DESIGN.md` before you edit it");
    }
}
