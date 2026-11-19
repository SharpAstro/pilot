using System.Collections.Immutable;
using DIR.Lib;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Every page, the cards panel and an open card, drawn by the chrome (rail, page and cards panel) into DIR.Lib's <see cref="WireframeRenderer"/>
/// at five widths, two heights and two scales: <see cref="Wireframe.Check"/> fails a case on text over text, text half on a control, controls over
/// controls, anything off the surface and text a clip cut with no ellipsis.
/// <para>States: <b>long</b> carries the long titles, repo and branch names, paths and reports (eight sessions, four repos); <b>many</b> carries
/// the many rows (sixty sessions, fifty jobs, forty flights and repos) and twelve fleet nodes, a quarter of them down ("waiting for the node on ...").</para>
/// </summary>
public static class WireframeSweepTests
{
    /// <summary>
    /// The sizes, as (width, DPI, height): at 900 high, every width at DPI 1.5 and the three widths 1000, 1400 and 2560 at DPI 1; at 1440 high (the "long"
    /// state only), the widths 1280 and 2560 at both scales. Every width, both heights and both scales are drawn, not their full product: the whole
    /// product (640 cases) took about a minute, past the 30 s the class has (the repo's runner uses two threads); this is 320.
    /// </summary>
    internal static readonly (int Width, float Dpi, int Height)[] LongSizes =
    [
        (1000, 1f, 900), (1400, 1f, 900), (2560, 1f, 900),
        (1000, 1.5f, 900), (1280, 1.5f, 900), (1400, 1.5f, 900), (1920, 1.5f, 900), (2560, 1.5f, 900),
        (1280, 1f, 1440), (2560, 1f, 1440), (1280, 1.5f, 1440), (2560, 1.5f, 1440),
    ];

    internal static readonly (int Width, float Dpi, int Height)[] ManySizes = [.. LongSizes.Where(s => s.Height == 900)];
    private static readonly string[] States = ["long", "many"];

    internal static readonly string[] Pages =
    [
        "home", "sessions", "flights table", "flights canvas", "flight page", "jobs", "fleet", "settings", "repos", "pull requests", "chat", "spend", "designs", "design page", PageTextOverlapTests.DesignPagePhone, PageTextOverlapTests.DesignPageDesktop, PageTextOverlapTests.DesignPageNoVersion,
        PageTextOverlapTests.IdeasPage, "transcript", "cards panel", PageTextOverlapTests.CardDetailPage, PageTextOverlapTests.NotificationsPage,
    ];

    internal static IEnumerable<(string Page, string State, int Width, float Dpi, int Height)> CaseTuples() =>
        from state in States from size in state == "long" ? LongSizes : ManySizes from page in Pages select (page, state, size.Width, size.Dpi, size.Height);

    internal static IEnumerable<TheoryDataRow<string, string, int, float, int>> RowsOf(string state, int height, float dpi) =>
        CaseTuples().Where(c => c.State == state && c.Height == height && c.Dpi == dpi)
            .Select(c => new TheoryDataRow<string, string, int, float, int>(c.Page, c.State, c.Width, c.Dpi, c.Height) { TestDisplayName = Key(c.Page, c.State, c.Width, c.Dpi, c.Height) });

    internal static string Key(string page, string state, int width, float dpi, int height) =>
        $"{page} | {state} | {width} wide | DPI {dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)} | {height} high";

    private const string ZoomAction = "map:zoom";
    private const string PalettePanelAction = "FlightMapLayerPaletteGrip:Panel";

    /// <summary>
    /// What the check leaves alone, all of it placed by the flight map's data and nothing else:
    /// <list type="bullet">
    /// <item>the regions a flight's position puts down (a continent's header <c>map:&lt;node&gt;</c>, a row, a destination, a leg or a plane:
    /// <c>map:...</c>), which lie wherever the flights are, under the map's own overlays by design; the zoom pill (<c>map:zoom</c>) is the one
    /// <c>map:</c> region still judged, and the LAYERS palette and every other control are judged as ever;</item>
    /// <item>on the flights canvas, a label the map drew (a flight code, a leg's name, a state) that lies under the zoom pill or the LAYERS panel,
    /// both painted over the map after it: a label sits where its flight is, and the overlay floats over whatever the pan put there. The overlays'
    /// own text, and a label on any other page, stay judged.</item>
    /// <item>what the page painted under the open For you bar (#1184): the bar lies over the page's right side rather than taking its width,
    /// so the page's controls and text there are covered, not on show. The bar is an opaque surface and also the region that keeps a click
    /// on it from the page beneath, and the check counts a surface's own region as a control, not as a cover, so every page control under
    /// it read as one the bar collides with. The bar's own rows and the cards panel in it, painted after it, stay judged, as does all of
    /// the page beside it.</item>
    /// </list>
    /// </summary>
    internal static WireframeOptions OptionsFor(string page, WireframeFrame frame, RectF32? panel = null)
    {
        var overlays = page == "flights canvas"
            ? frame.Regions.Where(r => r.Result is HitResult.ButtonHit { Action: ZoomAction or PalettePanelAction }).ToArray()
            : [];
        var bar = OpenBar(frame, panel);
        var handles = frame.Regions.Where(r => r.Result is HitResult.ButtonHit { Action: NotificationsHost.HandleAction }).ToArray();
        return new()
        {
            IgnoreRegion = r => (r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("map:", StringComparison.Ordinal) && a != ZoomAction)
                || Under(bar, r.Order, r.Visible) || IsHandle(r) || UnderHandle(handles, r.Order, r.Visible),
            IgnoreText = t => overlays.Any(o => o.Order > t.Order && Overlaps(o.Visible, t.Visible)) || Under(bar, t.Order, t.Visible) || UnderHandle(handles, t.Order, t.Visible),
        };
    }

    /// <summary>
    /// The open bar's own region, from the cards panel it holds (<paramref name="panel"/>, the chrome's <c>CardsBounds</c>): the region with the
    /// panel's left and right edges that reaches above it (the bar's Collapse row), the latest painted. Null while the bar is collapsed.
    /// </summary>
    internal static WireframeRegion? OpenBar(WireframeFrame frame, RectF32? panel) =>
        panel is { } p
            ? frame.Regions
                .Where(r => r.Result is HitResult.ChromeHit
                    && MathF.Abs(r.Visible.X - p.X) <= 1.5f && MathF.Abs(r.Visible.X + r.Visible.Width - (p.X + p.Width)) <= 1.5f
                    && r.Visible.Y < p.Y - 1.5f && r.Visible.Y + r.Visible.Height >= p.Y + p.Height - 1.5f)
                .OrderByDescending(r => r.Order)
                .Select(r => (WireframeRegion?)r)
                .FirstOrDefault()
            : null;

    private static bool IsHandle(WireframeRegion r) => r.Result is HitResult.ButtonHit { Action: NotificationsHost.HandleAction };

    // The collapsed bar's edge handle (PIL1205) floats over the page's right edge, one tab at the window's middle and no column reserved, so
    // what the page painted under it is covered by design, as under the open bar. The handle's own place and tint are judged by
    // NotificationsCaptureTests and NotificationsHandleTests.
    private static bool UnderHandle(WireframeRegion[] handles, int order, RectF32 visible) => handles.Any(h => order < h.Order && Overlaps(h.Visible, visible));

    // Painted before the bar and under it: covered.
    private static bool Under(WireframeRegion? bar, int order, RectF32 visible) => bar is { } b && order < b.Order && Overlaps(b.Visible, visible);

    private static bool Overlaps(RectF32 a, RectF32 b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>One case: the findings of the page drawn at the size, fail it.</summary>
    internal static async Task Run(string page, string state, int width, float dpi, int height)
    {
        var key = Key(page, state, width, dpi, height);
        var findings = await FindingsOf(page, state, width, dpi, height);
        if (Environment.GetEnvironmentVariable("PILOT_WIREFRAME_DUMP") is { Length: > 0 } dump)
        {
            lock (DumpLock)
            {
                File.AppendAllLines(dump, findings.Select(f => $"{key}	{f.Rule}	{f.Message}"));
            }
        }
        var unknown = Unknown(key, findings);
        // A picture to start the fix from, as the overlap check gives: the page as the chrome paints it, beside the test's output.
        var capture = unknown.IsEmpty ? null : await PageTextOverlapTests.CaptureOf(page, state, width, dpi, height, key, Path.Combine(AppContext.BaseDirectory, "wireframe"));
        Assert.True(unknown.IsEmpty, Message(key, unknown) + (capture is null ? "" : $"{Environment.NewLine}  capture: {capture}"));
    }

    private static readonly Lock DumpLock = new();

    /// <summary>DIR.Lib#275: the notifications list cuts a word wider than its line with no ellipsis (and has no list frame without the strip).</summary>
    internal const int NotificationsWordIssue = 275;

    /// <summary>
    /// Findings known and not fixed here, each with its reason and the issue that fixes it (a DIR.Lib issue where the fix is DIR.Lib's): the
    /// case key and the finding's message as patterns, <c>*</c> for any text, as <see cref="TextOverlapAllowList"/> has them.
    /// <see cref="WireframeSweepKnownTests"/> fails an entry that no longer matches a finding: once its fault is fixed, the entry goes.
    /// </summary>
    internal static readonly IReadOnlyList<AllowedTextFault> Known =
    [
        new("notifications | long | *", @"text ""C:\Users\ada\src\example-org*",
            "A history row's text that is one word wider than the row (the long state's folder path; a job's command with a long --filter is the "
            + "same) is laid out whole, cut by the row with no ellipsis and run under the list's scroll bar: DIR.Lib's notifications list wraps a "
            + "row's text at its spaces only", NotificationsWordIssue),
    ];

    internal static ImmutableArray<WireframeFinding> Unknown(string key, ImmutableArray<WireframeFinding> findings) =>
        [.. findings.Where(f => !Known.Any(k => k.Covers(key, f.Message)))];

    internal static string Message(string key, ImmutableArray<WireframeFinding> findings) =>
        $"{key} [{findings.Length} findings]" + string.Concat(findings.Select(f => $"{Environment.NewLine}  {key}: [{f.Rule}] {f.Message}"));

    /// <summary>The findings of one case: the chrome's whole frame, drawn once after <see cref="WireframeRenderer.Begin"/>.</summary>
    internal static async Task<ImmutableArray<WireframeFinding>> FindingsOf(string page, string state, int width, float dpi, int height)
    {
        using var prepared = await PageTextOverlapTests.Prepare(page, state, width, dpi, null, height, (w, h) => new WireframeRenderer(w, h));
        var renderer = (WireframeRenderer)prepared.Renderer;
        var data = prepared.Data;
        renderer.Begin();
        prepared.Chrome.Render(data.Sessions, data.Flights, PageTextOverlapTests.CardsOf(data, page), data.Jobs, data.Fleet, data.Repos, data.PullRequests,
            new RectF32(0, 0, width, height), data.Machines, null, data.Spend, PageTextOverlapTests.DesignsOf(data, page));
        return Wireframe.Check(renderer.Frame, OptionsFor(page, renderer.Frame, prepared.Chrome.CardsBounds));
    }
}

/// <summary>The sweep's own tests: a tiny hand-built widget, drawn on a <see cref="WireframeRenderer"/>, fails a case in each way the check exists for.</summary>
public sealed class WireframeSweepSelfTests
{
    private const string FixtureKey = "fixture | sample | 200 wide | DPI 1 | 100 high";
    private static readonly RGBAColor32 Ink = new(10, 10, 10, 255);

    private static ImmutableArray<WireframeFinding> Draw(Action<WireframeRenderer, string> draw)
    {
        using var renderer = new WireframeRenderer(200, 100);
        renderer.Begin();
        draw(renderer, SessionsCapture.FontPath);
        return Wireframe.Check(renderer.Frame);
    }

    private static void Text(WireframeRenderer r, string font, string text, int x, int y, int w, int h) =>
        r.DrawText(text, font, 12f, Ink, new RectInt(new PointInt(x + w, y + h), new PointInt(x, y)), TextAlign.Near, TextAlign.Near);

    private static void Fails(ImmutableArray<WireframeFinding> findings, WireframeRules rule, params string[] texts)
    {
        var unknown = WireframeSweepTests.Unknown(FixtureKey, findings);
        unknown.Any(f => f.Rule == rule).ShouldBeTrue(Wireframe.Describe(findings));
        var message = WireframeSweepTests.Message(FixtureKey, unknown);
        message.ShouldContain(FixtureKey);
        foreach (var text in texts)
        {
            message.ShouldContain(text);
        }
    }

    [Fact]
    public void TextOverTextFailsACase() =>
        Fails(Draw((r, f) =>
        {
            Text(r, f, "title of the page", 0, 0, 150, 20);
            Text(r, f, "subtitle drawn over it", 20, 5, 150, 20);
        }), WireframeRules.TextOverText, "title of the page", "subtitle drawn over it");

    [Fact]
    public void TextHalfOnAControlFailsACase() =>
        Fails(Draw((r, f) =>
        {
            r.NoteRegion(new ClickableRegion(0, 0, 40, 20, new HitResult.ButtonHit("go")));
            Text(r, f, "half on the button", 0, 2, 150, 16);
        }), WireframeRules.TextOverRegion, "half on the button");

    [Fact]
    public void TextCutByAClipWithoutAnEllipsisFailsACase() =>
        Fails(Draw((r, f) =>
        {
            r.PushClip(new RectInt(new PointInt(60, 30), new PointInt(0, 0)));
            Text(r, f, "a path that a clip cuts off", 0, 5, 200, 20);
            r.PopClip();
        }), WireframeRules.CutText, "a path that a clip cuts off");

    [Fact]
    public void TextCutByAClipWithAnEllipsisPasses() =>
        Draw((r, f) =>
        {
            r.PushClip(new RectInt(new PointInt(60, 30), new PointInt(0, 0)));
            Text(r, f, "a path that a clip cuts off…", 0, 5, 200, 20);
            r.PopClip();
        }).Where(x => x.Rule == WireframeRules.CutText).ShouldBeEmpty();

    [Fact]
    public void ThePageUnderTheOpenBarIsCoveredAndTheBarsOwnRowsStayJudged()
    {
        using var renderer = new WireframeRenderer(200, 100);
        renderer.Begin();
        var font = SessionsCapture.FontPath;
        // The page: a button and a line of text at its right side.
        renderer.NoteRegion(new ClickableRegion(150, 10, 40, 20, new HitResult.ButtonHit("page:go")));
        Text(renderer, font, "the page's text", 100, 40, 90, 16);
        // The open bar over it, as the chrome paints it: its surface and the region that keeps a click from the page, the panel below its row,
        // and a button of the bar's own with its label half off it.
        renderer.FillRectangle(new RectInt(new PointInt(200, 100), new PointInt(140, 0)), Ink);
        renderer.NoteRegion(new ClickableRegion(140, 0, 60, 100, new HitResult.ChromeHit()));
        renderer.NoteRegion(new ClickableRegion(145, 60, 20, 20, new HitResult.ButtonHit("bar:go")));
        Text(renderer, font, "bar text", 145, 62, 50, 16);
        var panel = new RectF32(140, 20, 60, 80);

        // Without the bar named, the page's button and text collide with its region.
        Wireframe.Check(renderer.Frame, WireframeSweepTests.OptionsFor("home", renderer.Frame))
            .ShouldContain(x => x.Message.Contains("page:go", StringComparison.Ordinal) || x.Message.Contains("the page's text", StringComparison.Ordinal));

        WireframeSweepTests.OpenBar(renderer.Frame, panel).ShouldNotBeNull().Visible.ShouldBe(new RectF32(140, 0, 60, 100));
        var findings = Wireframe.Check(renderer.Frame, WireframeSweepTests.OptionsFor("home", renderer.Frame, panel));
        // What the page painted under the bar is covered; the bar's own label half off its button is still a finding.
        findings.ShouldNotContain(x => x.Message.Contains("page:go", StringComparison.Ordinal) || x.Message.Contains("the page's text", StringComparison.Ordinal),
            Wireframe.Describe(findings));
        findings.ShouldContain(x => x.Rule == WireframeRules.TextOverRegion && x.Message.Contains("bar text", StringComparison.Ordinal), Wireframe.Describe(findings));
    }
}

/// <summary>The sweep's known findings are its to-do: each has a reason and an issue, and none outlives its fault.</summary>
public sealed class WireframeSweepKnownTests
{
    [Fact]
    public void EveryEntryHasAReasonAndAnIssue()
    {
        foreach (var entry in WireframeSweepTests.Known)
        {
            entry.Reason.ShouldNotBeNullOrWhiteSpace(entry.Case);
            entry.Issue.ShouldBeGreaterThan(0, entry.Case);
        }
    }

    /// <summary>An entry that matches no finding of the cases it names is stale: its fault was fixed, so it goes.</summary>
    [Fact]
    public async Task NoEntryOutlivesItsFault()
    {
        foreach (var entry in WireframeSweepTests.Known)
        {
            var found = false;
            foreach (var (page, state, width, dpi, height) in WireframeSweepTests.CaseTuples())
            {
                var key = WireframeSweepTests.Key(page, state, width, dpi, height);
                if (!entry.CoversCase(key))
                {
                    continue;
                }
                if ((await WireframeSweepTests.FindingsOf(page, state, width, dpi, height)).Any(f => entry.Covers(key, f.Message)))
                {
                    found = true;
                    break;
                }
            }
            found.ShouldBeTrue($"the known finding '{entry.Case}' / '{entry.Fault}' matches no finding any more: remove it");
        }
    }
}

// The sweep runs as six classes, one a state, height and scale, so xunit's own class-level parallelism shares the 320 cases.

/// <summary>The "long" state at 900 high, DPI 1.</summary>
public sealed class WireframeSweepLong900Dpi1Tests
{
    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() => WireframeSweepTests.RowsOf("long", 900, 1f);

    [Theory, MemberData(nameof(Cases))]
    public Task NoFindings(string page, string state, int width, float dpi, int height) => WireframeSweepTests.Run(page, state, width, dpi, height);
}

/// <summary>The "long" state at 900 high, DPI 1.5.</summary>
public sealed class WireframeSweepLong900Dpi15Tests
{
    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() => WireframeSweepTests.RowsOf("long", 900, 1.5f);

    [Theory, MemberData(nameof(Cases))]
    public Task NoFindings(string page, string state, int width, float dpi, int height) => WireframeSweepTests.Run(page, state, width, dpi, height);
}

/// <summary>The "long" state at 1440 high, DPI 1.</summary>
public sealed class WireframeSweepLong1440Dpi1Tests
{
    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() => WireframeSweepTests.RowsOf("long", 1440, 1f);

    [Theory, MemberData(nameof(Cases))]
    public Task NoFindings(string page, string state, int width, float dpi, int height) => WireframeSweepTests.Run(page, state, width, dpi, height);
}

/// <summary>The "long" state at 1440 high, DPI 1.5.</summary>
public sealed class WireframeSweepLong1440Dpi15Tests
{
    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() => WireframeSweepTests.RowsOf("long", 1440, 1.5f);

    [Theory, MemberData(nameof(Cases))]
    public Task NoFindings(string page, string state, int width, float dpi, int height) => WireframeSweepTests.Run(page, state, width, dpi, height);
}

/// <summary>The "many" state at 900 high, DPI 1.</summary>
public sealed class WireframeSweepMany900Dpi1Tests
{
    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() => WireframeSweepTests.RowsOf("many", 900, 1f);

    [Theory, MemberData(nameof(Cases))]
    public Task NoFindings(string page, string state, int width, float dpi, int height) => WireframeSweepTests.Run(page, state, width, dpi, height);
}

/// <summary>The "many" state at 900 high, DPI 1.5.</summary>
public sealed class WireframeSweepMany900Dpi15Tests
{
    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() => WireframeSweepTests.RowsOf("many", 900, 1.5f);

    [Theory, MemberData(nameof(Cases))]
    public Task NoFindings(string page, string state, int width, float dpi, int height) => WireframeSweepTests.Run(page, state, width, dpi, height);
}
