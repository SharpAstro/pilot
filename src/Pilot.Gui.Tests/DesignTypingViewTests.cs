using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL1171: a cell edited in place. The field in the accent, the cell tagged 'draft', and under it the hint, the why, Publish v&lt;n+1&gt; and Discard draft; captured at 1920 and in pilot-tui.</summary>
public sealed class DesignTypingViewTests
{
    private static readonly DateTimeOffset Now = PageData.Now;

    private static DesignsScreen WithDraft()
    {
        var sample = PageData.DesignsFor("sample", Now);
        var doc = sample.Open!.Doc!;
        var draft = doc with { Cells = [doc.Cells[0], doc.Cells[1] with { Objective = "Shows the settings, grouped, with the one that changed first. " + new string('x', 5) }] };
        return sample with { Open = sample.Open with { Draft = draft } };
    }

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _renderer = new(1920, 1080);
        public readonly List<(string Id, string Part, string Cell, string Text)> Edits = [];
        public readonly List<(string Id, string Why, (string Part, string Cell, string Text)? Pending)> Published = [];
        public readonly List<string> Discarded = [];

        public Rig(DesignsScreen? screen = null)
        {
            PageTime.Zone = TimeZoneInfo.Utc;
            Screen = screen ?? WithDraft();
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            Chrome.Show(PilotPage.Designs);
            View.OnEdit = (id, part, cell, text, _) => Edits.Add((id, part, cell, text));
            View.OnPublishTyped = (id, why, pending) => Published.Add((id, why, pending));
            View.OnDiscard = Discarded.Add;
            View.Open("DSN-1");
            Paint();
        }

        public DesignsScreen Screen { get; }
        public PilotChrome<RgbaImage> Chrome { get; }
        public DesignsView<RgbaImage> View => Chrome.Designs;

        public void Paint()
        {
            var data = PageData.For("sample");
            Chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, 1920, 1080), data.Machines, null, data.Spend, Screen);
        }

        public List<Layout.ArrangedNode<float>> Arranged(DesignsScreen? other = null) =>
            Layout.Engine.Arrange(View.Tree(other ?? Screen), new Rect<float>(0, 0, 1920, 1080), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).ToList();

        public byte[] Png() => SharpAstro.Png.PngWriter.Encode([.. _renderer.Surface.Pixels], 1920, 1080);

        public void Dispose() => _renderer.Dispose();
    }

    private static void Keep(string name, byte[] png)
    {
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(artifacts);
        File.WriteAllBytes(Path.Combine(artifacts, name), png);
    }

    private static bool Has(List<Layout.ArrangedNode<float>> all, string action) =>
        all.Any(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && act == action);

    [Fact]
    public void ACellInEditShowsTheFieldTheDraftTagTheHintTheWhyAndBothButtons()
    {
        using var rig = new Rig();
        rig.View.BeginEdit(1, "objective").ShouldBeTrue();
        rig.View.Editor.Text.ShouldBe(rig.Screen.Open!.Notebook!.Cells[1].Objective);
        rig.Paint();

        var all = rig.Arranged();
        all.Select(a => a.Node).OfType<Layout.Node.Leaf>().Any(l => l.Content is Layout.Content.TextEditor).ShouldBeTrue("the field");
        var texts = SentenceRuns.Texts(all.Select(a => a.Node));
        texts.ShouldContain("Objective of Settings");
        texts.ShouldContain("draft");
        texts.ShouldContain(DesignsView<RgbaImage>.EditHint);
        texts.ShouldContain("Publish v3");
        texts.ShouldContain("Discard draft");
        rig.View.WhyInput.Placeholder.ShouldBe("Why this change?");
        Has(all, "designs:typed-discard").ShouldBeTrue();
        // The cell that was not typed in keeps its published tag.
        texts.ShouldContain("published in v2");
        TextOverlap.Faults([.. all]).Select(f => f.Line).ShouldBeEmpty();
    }

    /// <summary>
    /// Each save of a cell's part carries the heading the cell had when the typing began, which the node holds the cell to (#1182 N4.1); a heading typed is the
    /// one held from then on, and the design's own parts carry none.
    /// </summary>
    [Fact]
    public void ASaveCarriesTheHeadingOfTheCellItWasTypedInto()
    {
        using var rig = new Rig();
        var headings = new List<string?>();
        rig.View.OnEdit = (_, _, _, _, heading) => headings.Add(heading);
        var first = rig.Screen.Open!.Notebook!.Cells[1].Heading;

        rig.View.BeginEdit(1, "objective").ShouldBeTrue();
        rig.View.Editor.Text = "Lists the settings, newest first.";
        rig.View.SaveEdit().ShouldBeTrue();
        rig.View.EndEdit();

        rig.View.BeginEdit(1, "heading").ShouldBeTrue();
        rig.View.Editor.Text = "All the settings";
        rig.View.SaveEdit().ShouldBeTrue();
        rig.View.Editor.Text = "Every setting";
        rig.View.SaveEdit().ShouldBeTrue();
        rig.View.EndEdit();

        rig.View.BeginEdit(-1, "objective").ShouldBeTrue();
        rig.View.Editor.Text = "The design's own words.";
        rig.View.SaveEdit().ShouldBeTrue();

        headings.ShouldBe([first, first, "All the settings", null]);
    }

    [Fact]
    public void TypingTheTitleOnTheBackRowSendsADesignEditOfTheTitlePart()
    {
        using var rig = new Rig();
        Has(rig.Arranged(), "designs:edit:-1:title").ShouldBeTrue("the title is typable like the other parts");
        rig.View.BeginEdit(-1, "title").ShouldBeTrue();
        rig.View.Editor.Text.ShouldBe("Design 0");
        rig.Paint();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();

        rig.View.Editor.Text = "Settings";
        rig.View.SaveEdit().ShouldBeTrue();
        rig.Edits.ShouldHaveSingleItem().ShouldBe(("DSN-1", "title", "", "Settings"));
    }

    [Fact]
    public void ADesignWithNoTitleOrAStoredOneOverTheLimitShowsItsIdAlone()
    {
        var sample = PageData.DesignsFor("sample", Now);
        foreach (var stored in new string?[] { null, "", new string('x', DesignTitle.MaxLength + 1) })
        {
            using var rig = new Rig(sample with { Open = DesignPages.Load(sample.Open!.Detail with { Row = sample.Open.Detail.Row with { Title = stored } }) });
            var all = rig.Arranged();
            all.Any(a => a.Node.Tooltip != null && a.Node.Tooltip.StartsWith("DSN-1", StringComparison.Ordinal) && !a.Node.Tooltip.Contains('·')).ShouldBeTrue($"the id alone for {stored?.Length.ToString() ?? "null"}");
            SentenceRuns.Texts(all.Select(a => a.Node)).ShouldNotContain(t => t.Contains(new string('x', 10), StringComparison.Ordinal));
        }
        DesignTitle.Shown(new string('x', DesignTitle.MaxLength)).ShouldNotBeNull();
    }

    [Fact]
    public void PublishIsOfferedOnceTheWhyIsUsableAndSendsTheWhyAndTheUnsavedPart()
    {
        using var rig = new Rig();
        rig.View.BeginEdit(1, "objective");
        Has(rig.Arranged(), "designs:typed-publish").ShouldBeFalse("no why yet");
        rig.View.PublishTyped().ShouldBeFalse();

        rig.View.WhyInput.Text = new string('x', DesignWhys.MaxLength);
        rig.View.WhyUsable.ShouldBeFalse("with 'owner: ' the line is over the limit");
        rig.View.WhyInput.Text = "two\nlines";
        rig.View.WhyUsable.ShouldBeFalse();

        rig.View.WhyInput.Text = "grouped settings";
        rig.View.Editor.Text = "Shows the settings, grouped.";
        Has(rig.Arranged(), "designs:typed-publish").ShouldBeTrue();
        rig.View.PublishTyped().ShouldBeTrue();

        var sent = rig.Published.ShouldHaveSingleItem();
        sent.Id.ShouldBe("DSN-1");
        sent.Why.ShouldBe("grouped settings");
        sent.Pending.ShouldBe(("objective", "2", "Shows the settings, grouped."));
    }

    [Fact]
    public void TypedTextIsSentAsAnEditOfThatPartAndNothingIsSentWhenItIsUnchanged()
    {
        using var rig = new Rig();
        rig.View.BeginEdit(0, "heading");
        rig.View.SaveEdit().ShouldBeFalse();

        rig.View.Editor.Text = "Overview";
        rig.View.SaveEdit().ShouldBeTrue();
        rig.Edits.ShouldHaveSingleItem().ShouldBe(("DSN-1", "heading", "1", "Overview"));
        rig.View.SaveEdit().ShouldBeFalse();
    }

    [Fact]
    public void AnOlderVersionIsReadNotTypedInto()
    {
        // Typing lands in the current draft by cell number, so a page on v1 of a design whose current is v2 offers no edit and says where to type.
        var sample = PageData.DesignsFor("sample", Now);
        var older = sample with { Open = sample.Open! with { Version = 1 } };
        using var rig = new Rig(older);
        rig.View.BeginEdit(1, "objective").ShouldBeFalse();
        rig.View.Editing.ShouldBeNull();
        var all = rig.Arranged();
        all.Any(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && act.StartsWith("designs:edit:", StringComparison.Ordinal)).ShouldBeFalse("no part is clickable");
        var texts = SentenceRuns.Texts(all.Select(a => a.Node));
        texts.ShouldNotContain("Add notes");
        texts.ShouldContain(t => t.StartsWith("This is v1. Open the current version, v2", StringComparison.Ordinal));
    }

    [Fact]
    public void PickingAnOlderVersionClosesTheEditorAfterSendingWhatWasTyped()
    {
        // R1.3 of the review: an editor open on the current version must not survive a pick of an older one, or its typing would land in the current draft by cell number.
        using var rig = new Rig();
        rig.View.BeginEdit(1, "objective").ShouldBeTrue();
        rig.View.Editor.Text = "Typed before the pick.";
        rig.View.PickVersion(1);
        rig.View.Editing.ShouldBeNull();
        rig.Edits.ShouldHaveSingleItem().ShouldBe(("DSN-1", "objective", "2", "Typed before the pick."));

        var older = rig.Screen with { Open = rig.Screen.Open! with { Version = 1, Draft = null } };
        var all = rig.Arranged(older);
        all.Select(a => a.Node).OfType<Layout.Node.Leaf>().Any(l => l.Content is Layout.Content.TextEditor).ShouldBeFalse("no editor on v1");
        var texts = SentenceRuns.Texts(all.Select(a => a.Node));
        texts.ShouldNotContain("Discard draft");
        texts.ShouldNotContain(DesignsView<RgbaImage>.EditHint);
        Has(all, "designs:typed-publish").ShouldBeFalse();
    }

    [Fact]
    public void AnEditorOpenWhenTheShownPageStopsBeingCurrentCloses()
    {
        // A newer version published while the owner types: the page on show is no longer the current one, so the editor and the panel go.
        using var rig = new Rig();
        rig.View.BeginEdit(1, "objective").ShouldBeTrue();
        rig.View.Editor.Text = "Typed a moment before v3 came.";
        var open = rig.Screen.Open!;
        var passed = rig.Screen with { Open = open with { Detail = open.Detail with { Row = open.Detail.Row with { Current = 3 } } } };
        var all = rig.Arranged(passed);
        rig.View.Editing.ShouldBeNull();
        // The words typed since the last autosave are sent as the autosave would have sent them, not dropped.
        rig.Edits.ShouldHaveSingleItem().ShouldBe(("DSN-1", "objective", "2", "Typed a moment before v3 came."));
        all.Select(a => a.Node).OfType<Layout.Node.Leaf>().Any(l => l.Content is Layout.Content.TextEditor).ShouldBeFalse();
        SentenceRuns.Texts(all.Select(a => a.Node)).ShouldNotContain("Discard draft");
    }

    [Fact]
    public void ACellsNumberTagCaptionAndNotesLabelSelectAndThePlaceholdersDoNotOnPurpose()
    {
        // The labels the owner copies ("cell 2, published in v2") select like the prose; the offers to type a missing part are the control that opens the editor, so they stay unselectable and read alike.
        var sample = PageData.DesignsFor("sample", Now);
        var doc = sample.Open!.Doc!;
        var bare = doc with { Objective = null, Cells = [doc.Cells[0] with { Notes = "Some notes." }, doc.Cells[1] with { Objective = null, Notes = "" }] };
        using var rig = new Rig(sample with { Open = sample.Open with { Doc = bare } });
        var texts = rig.Arranged().Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().ToList();
        foreach (var label in new[] { "1", "2", "published in v2", "Notes" })
        {
            texts.Where(t => t.Value == label).ShouldNotBeEmpty(label);
            texts.Where(t => t.Value == label).ShouldAllBe(t => t.Selectable, label);
        }
        texts.Where(t => t.Value.StartsWith("Proposed design", StringComparison.Ordinal)).ShouldAllBe(t => t.Selectable, "frame captions");
        foreach (var offer in new[] { DesignsView<RgbaImage>.AddObjective, DesignsView<RgbaImage>.AddNotes })
        {
            var found = texts.Where(t => t.Value == offer).ToList();
            found.ShouldNotBeEmpty(offer);
            found.ShouldAllBe(t => !t.Selectable, offer);
        }
    }

    [Fact]
    public void AMissingObjectiveIsOfferedForTyping()
    {
        // R1.6 of the review: the check asks for an objective on the design and on every cell, so a missing one has an 'Add objective' like 'Add notes'.
        var sample = PageData.DesignsFor("sample", Now);
        var doc = sample.Open!.Doc!;
        var bare = doc with { Objective = null, Cells = [doc.Cells[0], doc.Cells[1] with { Objective = null }] };
        using var rig = new Rig(sample with { Open = sample.Open with { Doc = bare } });
        var all = rig.Arranged();
        Has(all, "designs:edit:-1:objective").ShouldBeTrue("the design's objective");
        Has(all, "designs:edit:1:objective").ShouldBeTrue("cell 2's objective");
        // Counted over the page's words end to end: a sentence run may join the label to a neighbour's words, so a run is not one label.
        var words = string.Concat(all.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value));
        words.Split(DesignsView<RgbaImage>.AddObjective).Length.ShouldBe(3, "one 'Add objective' for the design and one for cell 2");
        rig.View.BeginEdit(1, "objective").ShouldBeTrue();
        rig.View.Editor.Text.ShouldBe("");
    }

    private static int AddObjectives(List<Layout.ArrangedNode<float>> all) =>
        string.Concat(all.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value))
            .Split(DesignsView<RgbaImage>.AddObjective).Length - 1;

    [Fact]
    public void ADesignWithAnObjectiveAndNoNotesOffersNoAddObjectiveAndOneEditorWhileItsObjectiveIsTyped()
    {
        // R3 blocking 1 of the review: the offer hung on the notes' if, so the usual design (an objective, no design notes) showed a false 'Add objective' and two editors once its objective was opened.
        var sample = PageData.DesignsFor("sample", Now);
        sample.Open!.Doc!.Notes.ShouldBe("", "the sample design has no notes of its own");
        using var rig = new Rig(sample);
        var all = rig.Arranged();
        AddObjectives(all).ShouldBe(0);
        all.Count(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:edit:-1:objective" }).ShouldBe(1, "the objective's own prose only");

        rig.View.BeginEdit(-1, "objective").ShouldBeTrue();
        rig.View.Editor.Text.ShouldBe(sample.Open.Doc.Objective);
        all = rig.Arranged();
        all.Select(a => a.Node).OfType<Layout.Node.Leaf>().Count(l => l.Content is Layout.Content.TextEditor).ShouldBe(1);
        SentenceRuns.Texts(all.Select(a => a.Node)).ShouldContain("Objective of the design");
        TextOverlap.Faults([.. all]).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void ADesignWithNotesAndNoObjectiveOffersAddObjective()
    {
        // The case R1.6 asked for: the check refuses a design with no objective, so one with notes and no objective must still be able to get it by typing.
        var sample = PageData.DesignsFor("sample", Now);
        var doc = sample.Open!.Doc! with { Objective = null, Notes = "The design's own notes." };
        using var rig = new Rig(sample with { Open = sample.Open with { Doc = doc } });
        var all = rig.Arranged();
        AddObjectives(all).ShouldBe(1, "the design's; every cell has one");
        Has(all, "designs:edit:-1:objective").ShouldBeTrue();
        Has(all, "designs:edit:-1:notes").ShouldBeTrue("the notes are shown and typable");
        rig.View.BeginEdit(-1, "objective").ShouldBeTrue();
        rig.View.Editor.Text.ShouldBe("");
    }

    [Fact]
    public void AnOlderVersionsNotesAreOnItsFirstCellOnlyAsTheDraftMadeFromItKeepsThem()
    {
        // R1.5 of the review: the store's draft of a version with no DESIGN.md keeps NOTES.md on the first cell; the page reads the version the same way, so cells 2 and up are not 'draft' with nothing typed.
        var sample = PageData.DesignsFor("sample", Now);
        var open = sample.Open! with { Doc = null };
        open.Notebook!.Cells[0].Notes.ShouldBe(open.Notes!.Trim());
        open.Notebook.Cells.Skip(1).ShouldAllBe(c => c.Notes == "");
        var draft = open.Notebook with { Objective = "", Cells = [.. open.Notebook.Cells.Select(c => c with { Objective = "" })] };
        (open with { Draft = draft }).HasDraftChange.ShouldBeFalse("the draft the store makes from the version changes nothing");
    }

    [Fact]
    public void DiscardAsksTheNodeAndClosesTheEditor()
    {
        using var rig = new Rig();
        rig.View.BeginEdit(1, "notes");
        rig.View.DiscardTyped().ShouldBeTrue();
        rig.Discarded.ShouldHaveSingleItem().ShouldBe("DSN-1");
        rig.View.Editing.ShouldBeNull();
    }

    [Fact]
    public void WhatTheNodeRefusesIsShownInTheCellAndNeverDropped()
    {
        using var rig = new Rig();
        rig.View.BeginEdit(1, "objective");
        rig.View.Typed("the objective cannot be empty");
        rig.Paint();

        SentenceRuns.Texts(rig.Arranged().Select(a => a.Node)).ShouldContain("the objective cannot be empty");
        rig.View.TypedNote.ShouldBe("the objective cannot be empty");
    }

    [Fact]
    public void ADesignWithNoDraftShowsNoPanelAndAClickOnProseOpensIt()
    {
        using var rig = new Rig(PageData.DesignsFor("sample", Now));
        var all = rig.Arranged();
        SentenceRuns.Texts(all.Select(a => a.Node)).ShouldNotContain("Discard draft");
        Has(all, "designs:edit:1:objective").ShouldBeTrue();
        Has(all, "designs:edit:0:heading").ShouldBeTrue();
    }

    [Fact]
    public void ACellInEditIsCapturedInPilotGuiAt1920WithNoTextOverlap()
    {
        using var rig = new Rig();
        rig.View.BeginEdit(1, "objective");
        rig.Paint();
        TextOverlap.Faults([.. rig.Arranged()]).Select(f => f.Line).ShouldBeEmpty();
        Keep("after-design-typing-pilot-gui-1920.png", rig.Png());
    }

    [Theory]
    [InlineData(100, 30)]
    [InlineData(160, 44)]
    public void ACellInEditIsCapturedInPilotTuiWithNoTextOverlap(int columns, int rows)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var screen = WithDraft();
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (columns, rows);
        chrome.Designs.OnEdit = (_, _, _, _, _) => { };
        chrome.Designs.OnPublishTyped = (_, _, _) => { };
        chrome.Designs.OnDiscard = _ => { };
        chrome.Designs.Open("DSN-1");
        var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, Now, _ => { }, null, columns);
        TerminalFrame.Build(rail, chrome.Designs.Tree(screen), null, columns, rows, CellMeasureContext.PixelAuthored);
        chrome.Designs.BeginEdit(1, "objective").ShouldBeTrue();
        var arranged = TerminalFrame.Build(rail, chrome.Designs.Tree(screen), null, columns, rows, CellMeasureContext.PixelAuthored);
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        TerminalWireframeSweepTests.Record(arranged, columns, rows, buffer);
        var lines = Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();
        Keep($"after-design-typing-pilot-tui-{columns}x{rows}.png", DesignsWaitingIdeasTests.Png(lines));
    }
}
