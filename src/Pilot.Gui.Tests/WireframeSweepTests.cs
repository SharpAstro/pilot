using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Every page, the cards panel and an open card, drawn by the chrome (rail, page and cards panel) into DIR.Lib's <see cref="WireframeRenderer"/>
/// at five widths, two heights and two scales: <see cref="Wireframe.Check"/> fails a case on text over text, text half on a control, controls over
/// controls, anything off the surface and text a clip cut with no ellipsis, unless the finding is in <see cref="WireframeKnownFindings"/>.
/// <para>States: <b>long</b> carries the long titles, repo and branch names, paths and reports (eight sessions, four repos); <b>many</b> carries
/// the many rows (sixty sessions, fifty jobs, forty flights and repos) and twelve fleet nodes, a quarter of them down ("waiting for the node on ...").</para>
/// </summary>
public sealed class WireframeSweepTests
{
    private static readonly int[] Widths = [1000, 1280, 1400, 1920, 2560];
    private static readonly int[] Heights = [900, 1440];
    private static readonly float[] Dpis = [1f, 1.5f];
    private static readonly string[] States = ["long", "many"];

    internal static readonly string[] Pages =
    [
        "home", "sessions", "flights table", "flights canvas", "flight page", "jobs", "fleet", "settings", "repos", "pull requests", "chat", "spend",
        PageTextOverlapTests.IdeasPage, "transcript", "cards panel", PageTextOverlapTests.CardDetailPage,
    ];

    internal static IEnumerable<(string Page, string State, int Width, float Dpi, int Height)> CaseTuples() =>
        from page in Pages from state in States from width in Widths from height in Heights from dpi in Dpis select (page, state, width, dpi, height);

    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() =>
        CaseTuples().Select(c => new TheoryDataRow<string, string, int, float, int>(c.Page, c.State, c.Width, c.Dpi, c.Height) { TestDisplayName = Key(c.Page, c.State, c.Width, c.Dpi, c.Height) });

    internal static string Key(string page, string state, int width, float dpi, int height) =>
        $"{page} | {state} | {width} wide | DPI {dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)} | {height} high";

    /// <summary>
    /// What the check leaves alone, by page: labels placed by data (the flight map's and the Spend chart's), where a rule about text over text does
    /// not apply. Every other text of those pages is judged.
    /// </summary>
    internal static WireframeOptions OptionsFor(string page) => new();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NoFindingsBeyondTheKnownOnes(string page, string state, int width, float dpi, int height)
    {
        var key = Key(page, state, width, dpi, height);
        var findings = await FindingsOf(page, state, width, dpi, height);
        if ((Environment.GetEnvironmentVariable("PILOT_WIREFRAME_DUMP") ?? @"C:\Users\SebastianGodelet\.claude\jobs\9f734a48\tmp\wf.tsv") is { Length: > 0 } dump)
        {
            lock (DumpLock)
            {
                File.AppendAllLines(dump, findings.Select(f => $"{key}\t{f.Rule}\t{f.Message}"));
            }
        }
        var unknown = Unknown(key, findings);
        Assert.True(unknown.IsEmpty, Message(key, unknown));
    }

    private static readonly Lock DumpLock = new();

    internal static ImmutableArray<WireframeFinding> Unknown(string key, ImmutableArray<WireframeFinding> findings) =>
        [.. findings.Where(f => !WireframeKnownFindings.Covers(key, f.Message))];

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
            new RectF32(0, 0, width, height), data.Machines, null, data.Spend);
        return Wireframe.Check(renderer.Frame, OptionsFor(page));
    }
}

/// <summary>The known list holds only findings that still happen.</summary>
public sealed class WireframeKnownFindingsTests
{
    [Fact]
    public void EveryEntryHasAReason()
    {
        foreach (var entry in WireframeKnownFindings.Entries)
        {
            entry.Reason.ShouldNotBeNullOrWhiteSpace(entry.Case);
            entry.Finding.ShouldNotBe("*", entry.Case + ": a blanket entry");
        }
    }

    [Fact]
    public async Task NoEntryOutlivesItsFinding()
    {
        foreach (var entry in WireframeKnownFindings.Entries)
        {
            var found = false;
            foreach (var (page, state, width, dpi, height) in WireframeSweepTests.CaseTuples())
            {
                var key = WireframeSweepTests.Key(page, state, width, dpi, height);
                if (!entry.CoversCase(key))
                {
                    continue;
                }
                var findings = await WireframeSweepTests.FindingsOf(page, state, width, dpi, height);
                if (findings.Any(f => entry.Covers(key, f.Message)))
                {
                    found = true;
                    break;
                }
            }
            found.ShouldBeTrue($"the known entry '{entry.Case}' / '{entry.Finding}' matches no finding any more: remove it");
        }
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
    public void AKnownEntryDropsTheFindingItCoversAndNoOther()
    {
        var findings = Draw((r, f) =>
        {
            Text(r, f, "title of the page", 0, 0, 150, 20);
            Text(r, f, "subtitle drawn over it", 20, 5, 150, 20);
        });
        var entry = new KnownWireframeFinding("fixture | *", "text \"subtitle*", "the fixture");
        findings.Where(x => entry.Covers(FixtureKey, x.Message)).ShouldNotBeEmpty();
        entry.Covers("other | sample | 200 wide | DPI 1 | 100 high", findings[0].Message).ShouldBeFalse();
    }
}
