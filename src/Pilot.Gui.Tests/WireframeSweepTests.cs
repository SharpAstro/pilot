using System.Collections.Immutable;
using DIR.Lib;
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
        PageTextOverlapTests.IdeasPage, "transcript", "cards panel", PageTextOverlapTests.CardDetailPage,
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
    /// </list>
    /// </summary>
    internal static WireframeOptions OptionsFor(string page, WireframeFrame frame)
    {
        var overlays = page == "flights canvas"
            ? frame.Regions.Where(r => r.Result is HitResult.ButtonHit { Action: ZoomAction or PalettePanelAction }).ToArray()
            : [];
        return new()
        {
            IgnoreRegion = r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("map:", StringComparison.Ordinal) && a != ZoomAction,
            IgnoreText = t => overlays.Any(o => o.Order > t.Order && Overlaps(o.Visible, t.Visible)),
        };
    }

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

    internal static ImmutableArray<WireframeFinding> Unknown(string key, ImmutableArray<WireframeFinding> findings) =>
        [.. findings];

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
        return Wireframe.Check(renderer.Frame, OptionsFor(page, renderer.Frame));
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
