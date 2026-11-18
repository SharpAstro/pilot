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
            View.OnEdit = (id, part, cell, text) => Edits.Add((id, part, cell, text));
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

        public List<Layout.ArrangedNode<float>> Arranged() =>
            Layout.Engine.Arrange(View.Tree(Screen), new Rect<float>(0, 0, 1920, 1080), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).ToList();

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
        chrome.Designs.OnEdit = (_, _, _, _) => { };
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
