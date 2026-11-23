using System.Collections.Immutable;
using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// A card toast's choices (PIL1252) go through the card's Presses under its press key, as the panel's do: dead with "…" on the one pressed
/// while the node works, the node's refusal in the toast body, and drawn in the library's toast button style. Every row is the toast's own
/// Row, arranged on the CPU with no window, and the captures (narrow and wide, standard and night, a long label and a refusal) go to
/// PILOT_JOB_ARTIFACTS or PILOT_TEST_CAPTURE when set.
/// </summary>
public sealed class ToastPressTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;
    // The row the choices and Dismiss share, from the library's own metrics so no width is retyped here: the toast, less the padding on both
    // sides, the card's 1-unit edge, the icon and the gap beside it.
    private static float RowWidth =>
        NotificationsHost.Style.ToastWidth - 2f * NotificationsHost.Style.Padding - 2f - NotificationsLayout.ToastIconSize - NotificationsHost.Style.Gap;

    // The gap between the choices and before Dismiss in that row: the style's own, read once so the rows built here and the width sums agree with it.
    private static float RowGap => NotificationsHost.Style.Gap;

    // The library lays Dismiss after the choices in the same row, which does not wrap: its real width, drawn as the library draws it.
    private static float DismissWidth()
    {
        using var measure = new RgbaImageRenderer(1, 1);
        var ctx = new PixelMeasureContext<RgbaImage>(measure, SessionsCapture.FontPath, 1f);
        return Layout.Engine.Arrange(Layout.Builder.HStack([NotificationsLayout.PlainButton("Dismiss", NotificationsHost.Colors(), NotificationsHost.Style, null!, () => { })]), new Rect<float>(0, 0, RowWidth, 100f), ctx)
            .Skip(1).Max(a => a.Bounds.X + a.Bounds.Width);
    }
    private const string LongLabel = "Start it and then wait for the review of the whole flight before going on to land it";
    private const string Why = "refused: the flight is held, so nothing can be started on it until its owner lifts the hold";

    // The node's own merge and review cards carry one choice (LegCommander.Review, LegCommander.Lay): oneChoice builds a card as the node does.
    private static CardRow Card(string label = "Start it", bool oneChoice = false) => new()
    {
        Id = "c1", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/ORBc1", Title = "TIA1818's PR #63 is ready to merge", Summary = "A short line on what happened.",
        // Raised after the window's first look (frame 0 at T0): a ring toasts only when it is newer than that look (#1254 N1.1).
        At = T0.AddSeconds(0.5), State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = label, Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" },
            .. oneChoice ? (CardChoice[])[] : [new CardChoice { Label = "Go around", Verb = Verbs.StartLeg, Target = "flight/TIA1818/other" }],
        ],
    };

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _measure = new(1, 1);
        private readonly RgbaImageRenderer _renderer;
        private readonly PageData _data = PageData.For("sample");
        private readonly int _width, _height;
        public PilotChrome<RgbaImage> Chrome { get; }

        public Rig(CardRow card, int width = 1920, int height = 1080)
        {
            _width = width;
            _height = height;
            _renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Jobs);
            Frame(CardsScreen.Empty, 0);
            Frame(CardsScreen.From([card]), 1);
        }

        public void Frame(CardsScreen cards, int seconds) =>
            Chrome.Render(_data.Sessions with { AsOf = T0.AddSeconds(seconds) }, _data.Flights, cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, _width, _height), _data.Machines);

        /// <summary>The toast's own row as the feed draws it now, arranged in a toast's width.</summary>
        public ImmutableArray<Layout.ArrangedNode<float>> Row(float? width = null)
        {
            var n = Chrome.Notifications.Toasts.Toasts.Single(t => t.Notification.Kind == DIR.Lib.Notifications.NotificationKind.Staying).Notification;
            // The body, then the choices as the library lays them in Dismiss's row (Notification.Buttons).
            var node = Layout.Builder.VStack(n.Row.ShouldNotBeNull()(), Layout.Builder.HStack([.. n.Buttons.ShouldNotBeNull()()]).WithGap(RowGap));
            return Layout.Engine.Arrange(node, new Rect<float>(0, 0, width ?? RowWidth, 1400), new PixelMeasureContext<RgbaImage>(_measure, SessionsCapture.FontPath, 1f));
        }

        /// <summary>Only the choices' row, arranged as above, so a check on how far it runs is not met by the body's full-width nodes.</summary>
        public ImmutableArray<Layout.ArrangedNode<float>> ButtonRow(float? width = null)
        {
            var n = Chrome.Notifications.Toasts.Toasts.Single(t => t.Notification.Kind == DIR.Lib.Notifications.NotificationKind.Staying).Notification;
            var node = Layout.Builder.HStack([.. n.Buttons.ShouldNotBeNull()()]).WithGap(RowGap);
            return Layout.Engine.Arrange(node, new Rect<float>(0, 0, width ?? RowWidth, 1400), new PixelMeasureContext<RgbaImage>(_measure, SessionsCapture.FontPath, 1f));
        }

        /// <summary>
        /// The toast on the last frame: the largest region the notifications layer painted around the toast's own Dismiss (the one part a hit
        /// names as a toast; a staying toast's card itself is a plain chrome region). Null when no toast's Dismiss is painted.
        /// </summary>
        public RectF32? ToastRect()
        {
            var regions = new List<ClickableRegion>();
            Chrome.NotificationsLayer.CollectPaintedRegions(regions);
            var at = regions.FindIndex(r => r.Result is NotificationHit { Part: NotificationPart.Dismiss });
            if (at < 0)
            {
                return null;
            }
            var dismiss = regions[at];
            var (cx, cy) = (dismiss.X + dismiss.Width / 2f, dismiss.Y + dismiss.Height / 2f);
            // The Dismiss region itself contains its own centre, so this is never empty.
            var toast = regions
                .Where(r => r.X <= cx && cx <= r.X + r.Width && r.Y <= cy && cy <= r.Y + r.Height)
                .MaxBy(r => r.Width * r.Height);
            return new RectF32(toast.X, toast.Y, toast.Width, toast.Height);
        }

        /// <summary>The bounds the notifications layer painted for the node that carries this hover text on the last frame, or null.</summary>
        public RectF32? PaintedRectWithTooltip(string tooltip)
        {
            var regions = new List<ClickableRegion>();
            Chrome.NotificationsLayer.CollectPaintedRegions(regions);
            var at = regions.FindLastIndex(r => r.Tooltip == tooltip);
            return at < 0 ? null : new RectF32(regions[at].X, regions[at].Y, regions[at].Width, regions[at].Height);
        }

        /// <summary>
        /// The ground the last frame draws behind this ink inside <paramref name="within"/>: finds the pixels of exactly the ink there (glyph
        /// cores hit it), then reads a small window around each, clipped to the same rect, and takes the most common colour that is not the ink.
        /// Null when too few pixels there carry the ink for the text to count as drawn, or when no other colour is around them.
        /// Bounded: at most <paramref name="maxSamples"/> ink pixels are sampled, each over a (2r+1) square.
        /// </summary>
        public (RGBAColor32 Ground, int InkPixels)? GroundBehind(RGBAColor32 ink, RectF32 within, int minInkPixels = 40, int maxSamples = 400, int radius = 4)
        {
            var pixels = _renderer.Surface.Pixels;
            var x0 = Math.Max(0, (int)MathF.Ceiling(within.X));
            var y0 = Math.Max(0, (int)MathF.Ceiling(within.Y));
            var x1 = Math.Min(_width - 1, (int)MathF.Floor(within.X + within.Width) - 1);
            var y1 = Math.Min(_height - 1, (int)MathF.Floor(within.Y + within.Height) - 1);
            var inkAt = new List<(int X, int Y)>();
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var i = (y * _width + x) * 4;
                    if (pixels[i] == ink.Red && pixels[i + 1] == ink.Green && pixels[i + 2] == ink.Blue)
                    {
                        inkAt.Add((x, y));
                    }
                }
            }
            if (inkAt.Count < minInkPixels)
            {
                return null;
            }
            var step = Math.Max(1, inkAt.Count / maxSamples);
            var histogram = new Dictionary<(byte, byte, byte), int>();
            for (var k = 0; k < inkAt.Count; k += step)
            {
                var (cx, cy) = inkAt[k];
                for (var y = Math.Max(y0, cy - radius); y <= Math.Min(y1, cy + radius); y++)
                {
                    for (var x = Math.Max(x0, cx - radius); x <= Math.Min(x1, cx + radius); x++)
                    {
                        var i = (y * _width + x) * 4;
                        var c = (pixels[i], pixels[i + 1], pixels[i + 2]);
                        if (c != (ink.Red, ink.Green, ink.Blue))
                        {
                            histogram[c] = histogram.GetValueOrDefault(c) + 1;
                        }
                    }
                }
            }
            if (histogram.Count == 0)
            {
                return null;
            }
            var top = histogram.MaxBy(kv => kv.Value).Key;
            return (new RGBAColor32(top.Item1, top.Item2, top.Item3, 255), inkAt.Count);
        }

        public void Save(string name)
        {
            if (Dir() is { } dir)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(_renderer.Surface.Pixels, _width, _height));
            }
        }

        public void Dispose()
        {
            _renderer.Dispose();
            _measure.Dispose();
        }
    }

    private static string? Dir() => Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } a ? a
        : Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } c ? Path.GetDirectoryName(c) : null;

    private static string[] Actions(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action)];

    [Fact]
    public void AnIdleToastHasBothChoicesAsButtonsAndNoRefusal()
    {
        using var rig = new Rig(Card());
        var row = rig.Row();

        Actions(row).ShouldBe(["toast:c1:1", "toast:c1:2"]);
        var runs = ArrangedText.Runs(row).Select(r => r.Trim()).ToList();
        runs.ShouldContain("Start it");
        runs.ShouldContain("Go around");
        ArrangedText.Concat(row).ShouldNotContain("…");
    }

    [Fact]
    public void WhileTheNodeWorksThePressedChoiceReadsWithDotsAndNoChoiceTakesAPress()
    {
        using var rig = new Rig(Card());
        // What Program notes for a press of the panel's button: the card's press key, the panel's hit id for the choice.
        rig.Chrome.Cards.NoteWorking("c1", "card:c1:1");
        var row = rig.Row();

        Actions(row).ShouldBeEmpty("a press is with the node: the toast's choices take none");
        var runs = ArrangedText.Runs(row).Select(r => r.Trim()).ToList();
        runs.ShouldContain("Start it …");
        runs.ShouldContain("Go around", "the other choice is dead but not marked");
    }

    [Fact]
    public void ARefusedPressPutsTheNodesWordsInTheToastBodyWrappedAndTheChoicesComeBack()
    {
        using var rig = new Rig(Card());
        rig.Chrome.Cards.NoteWorking("c1", "card:c1:1");
        // The node answered: the press is no longer with it, and its words stay.
        rig.Chrome.Cards.NoteWorking("c1", null);
        rig.Chrome.Cards.Presses.NoteRefused(CardsView<RgbaImage>.PressKeyOf(null, "c1"), Why);
        var row = rig.Row();

        ArrangedText.Concat(row).ShouldContain("the flight is held");
        ArrangedText.Runs(row).Count(r => Why.Contains(r.Trim()) && r.Trim().Length > 0).ShouldBeGreaterThan(1, "the words wrap onto lines, none is cut");
        Actions(row).ShouldContain("toast:c1:2");
        TextOverlap.Faults(row).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void ALongLabelIsShownWholeInsideTheToastAndNothingOverlaps()
    {
        var width = RowWidth;
        using var rig = new Rig(Card(LongLabel));
        var row = rig.Row(width);

        // The library's button row wraps (DIR.Lib 11.94): the label is the choice's whole one, with no ellipsis and no cut.
        ArrangedText.Concat(row).ShouldNotContain("…");
        ArrangedText.Concat(row).Replace(" ", "").ShouldContain(LongLabel.Replace(" ", ""));
        Actions(row).ShouldContain("toast:c1:1");
        rig.ButtonRow(width).Skip(1).Where(a => a.Bounds.X + a.Bounds.Width > width + 0.5f).Select(a => $"{a.Node.GetType().Name}@{a.Bounds.X:0},{a.Bounds.Y:0},{a.Bounds.Width:0}x{a.Bounds.Height:0} hit={a.Node.Hit}")
            .ShouldBeEmpty($"nothing runs past the toast at {width}");
        TextOverlap.Faults(row).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void TheRealMergeLabelIsShownWholeAndBothChoicesWithDismissFitTheToastIdleAndPressed()
    {
        // The node's own Merge label (LegCommander.MergeLabel) is 12 characters: the cut must not turn it into "Merge the…".
        const string merge = "Merge the PR";
        // The toast's body row is as wide as the style makes it (RowWidth), not a round number: a looser bound would let a row overflow the real toast.
        // The card is built as the node builds it (LegCommander.Review.cs, LegCommander.Lay.cs): one "Merge the PR" choice.
        // Dismiss is drawn by the library as a plain button after the choices in the same row: its real width is measured, not a guessed room.
        var dismiss = DismissWidth();
        using var idle = new Rig(Card(merge, oneChoice: true));
        var live = idle.ButtonRow().Skip(1).Max(a => a.Bounds.X + a.Bounds.Width);
        (live + RowGap + dismiss).ShouldBeLessThanOrEqualTo(RowWidth, "the live choice and Dismiss fit one toast row");
        using var pressed = new Rig(Card(merge, oneChoice: true));
        pressed.Chrome.Cards.NoteWorking("c1", "card:c1:1");
        var twin = pressed.ButtonRow().Skip(1).Max(a => a.Bounds.X + a.Bounds.Width);
        (twin + RowGap + dismiss).ShouldBeLessThanOrEqualTo(RowWidth, "the pressed twin, with its dots, is wider than the live button and still fits");
    }

    [Fact]
    public void ALongLabelKeepsTheToastItsHeightWhenAPressMakesTheChoicesDead()
    {
        using var rig = new Rig(Card(LongLabel));
        var idle = rig.Row();
        rig.Chrome.Cards.NoteWorking("c1", "card:c1:1");
        var working = rig.Row();

        working.Max(a => a.Bounds.Y + a.Bounds.Height).ShouldBe(idle.Max(a => a.Bounds.Y + a.Bounds.Height), 0.5f, "the dead twin is one line as the live button is");
    }

    [Fact]
    public void ALongRefusalCarriesItsWholeTextOnHover()
    {
        var long_ = string.Join(' ', Enumerable.Repeat("refused: the flight is held and nothing can be started on it", 6));
        using var rig = new Rig(Card());
        rig.Chrome.Cards.Presses.NoteRefused(CardsView<RgbaImage>.PressKeyOf(null, "c1"), long_);
        var row = rig.Row(240f);

        row.Select(a => a.Node.Tooltip?.ToString()).ShouldContain(t => t != null && t.Contains(long_));
    }

    [Fact]
    public void TheNightPalettesInkThePilotDrawsInTheToastReadsAtAAOnTheGroundItSitsOn()
    {
        var was = Palette.Current;
        try
        {
            Palette.Current = Palette.Night;
            // The ink is the colour of the refusal's drawn text leaves; the ground is read off the night frame's own pixels around the ink's
            // pixels. (NotificationsHost.Colors().Card proved nothing: in night it is the page's own ground, painted with no toast at all.)
            var drawnInk = new List<RGBAColor32>();
            using (var rig = new Rig(Card(), 700, 600))
            {
                rig.Chrome.Night.On = true;
                // The window sets the palette from the night switch as it draws a frame, so one is drawn before the row is read (the rig's
                // own frames ran with night off and left the standard palette current).
                rig.Frame(CardsScreen.From([Card()]), 2);
                Palette.Current.ShouldBeSameAs(Palette.Night, "the frame drawn after the switch is a night frame");
                rig.Chrome.Cards.Presses.NoteRefused(CardsView<RgbaImage>.PressKeyOf(null, "c1"), Why);
                var row = rig.Row();
                var line = row.Where(n => n.Node.Tooltip?.ToString() == Why).ToList();
                line.ShouldNotBeEmpty("the refusal line carries the node's words on hover");
                var b = line[0].Bounds;
                drawnInk.AddRange(row
                    .Where(a => a.Bounds.X >= b.X - 0.5f && a.Bounds.Y >= b.Y - 0.5f
                        && a.Bounds.X + a.Bounds.Width <= b.X + b.Width + 0.5f && a.Bounds.Y + a.Bounds.Height <= b.Y + b.Height + 0.5f)
                    .Select(a => (a.Node as Layout.Node.Leaf)?.Content as Layout.Content.Text)
                    .Where(t => t is { } && t.Value.Trim().Length > 0)
                    .Select(t => t!.Color));
                drawnInk.ShouldNotBeEmpty("the refusal's words are drawn as text whose colour is read here");
                // Two frames after the refusal is noted: the toast is drawn with it, and the ground is read from those pixels.
                rig.Frame(CardsScreen.From([Card()]), 3);
                rig.Frame(CardsScreen.From([Card()]), 4);
                // The pixels are read only inside the refusal line as the layer painted it (the node that carries the node's words on hover),
                // and that line is shown to sit inside the refused toast: a colour the page or the toast's buttons share is never pooled in.
                var toast = rig.ToastRect();
                toast.ShouldNotBeNull("the refused toast is drawn on the night frame");
                toast.Value.Width.ShouldBeGreaterThan(NotificationsHost.Style.ToastWidth * 0.9f, "the region read as the toast is the whole card, not its Dismiss");
                var refusal = rig.PaintedRectWithTooltip(Why);
                refusal.ShouldNotBeNull("the refusal line is painted with the node's words on hover");
                var (t, r) = (toast.Value, refusal.Value);
                (r.X >= t.X - 2f && r.Y >= t.Y - 2f && r.X + r.Width <= t.X + t.Width + 2f && r.Y + r.Height <= t.Y + t.Height + 2f)
                    .ShouldBeTrue($"the refusal line {r} lies inside the refused toast {t}");
                // Only the refusal is measured: it is the one ink the pilot draws on a ground it fills itself. The summary (p.Dim), the choices and the
                // title are drawn on the library's own tinted fill (under AA in night): a DIR.Lib issue, not a pair the pilot draws on the card colour.
                // The ink is read off the drawn refusal text (its leaves, wrapped or not), not recomputed here, so a change of the ink the chrome passes turns this red.
                foreach (var ink in drawnInk.Distinct())
                {
                    var found = rig.GroundBehind(ink, r);
                    found.ShouldNotBeNull($"the refusal ink {ink} is drawn inside the refusal line {r} of the toast {t}");
                    Contrast.Ratio(ink, found.Value.Ground).ShouldBeGreaterThanOrEqualTo(Contrast.AA,
                        $"refusal ink {ink} on the ground the frame draws behind it inside the refusal line {r} of the toast {t}, {found.Value.Ground} ({found.Value.InkPixels} ink pixels there)");
                }
            }
        }
        finally
        {
            Palette.Current = was;
        }
    }

    [Fact]
    public void TheCapturesAreWrittenNarrowAndWideInTheStandardAndNightPalettes()
    {
        var dir = Dir();
        if (dir is null)
        {
            return;
        }
        var was = Palette.Current;
        try
        {
            foreach (var (name, palette) in new[] { ("standard", Palette.Standard), ("night", Palette.Night) })
            {
                foreach (var (width, height) in new[] { (700, 600), (1920, 1080) })
                {
                    Palette.Current = palette;
                    using var rig = new Rig(Card(LongLabel), width, height);
                    rig.Chrome.Night.On = ReferenceEquals(palette, Palette.Night);
                    rig.Chrome.Cards.Presses.NoteRefused(CardsView<RgbaImage>.PressKeyOf(null, "c1"), Why);
                    rig.Frame(CardsScreen.From([Card(LongLabel)]), 2);
                    rig.Frame(CardsScreen.From([Card(LongLabel)]), 3);
                    rig.Save($"pil1252-toast-{name}-{width}");
                }
            }
        }
        finally
        {
            Palette.Current = was;
        }
    }
}
