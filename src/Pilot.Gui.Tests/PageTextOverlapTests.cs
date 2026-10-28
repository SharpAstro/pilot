using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Every page's arranged tree, with sample data, at the widths and DPIs the issue names: no text is drawn over other text,
/// and none reaches past the scroll or the page that holds it (<see cref="TextOverlap"/>, the one check the live GUI reads too).
/// A case that fails is skipped with its fault line (<see cref="PageTextOverlapSkips"/>), so the suite holds what is fixed
/// and each skip names what is not: DIR.Lib#169's stack measure, or a layout of pilot's own (PIL380 page-fixes).
/// </summary>
public sealed class PageTextOverlapTests
{
    private static readonly int[] Widths = [700, 900, 1300, 1900];
    private static readonly float[] Dpis = [1f, 1.25f, 1.5f];
    private const int Height = 900;

    // The rail's width in design units, as PilotChrome lays it down the left of the window.
    private const float RailWidth = 52f;

    internal static readonly string[] Pages =
    [
        "home", "sessions", "flights table", "flights canvas", "flight page", "jobs", "fleet", "settings", "repos", "chat",
        "cards panel", "transcript", "wizard repos", "wizard issues",
    ];

    internal static readonly string[] States = ["sample", "long", "many", "empty"];

    public static IEnumerable<TheoryDataRow<string, string, int, float>> Cases()
    {
        foreach (var page in Pages)
        {
            foreach (var state in States)
            {
                foreach (var width in Widths)
                {
                    foreach (var dpi in Dpis)
                    {
                        yield return new TheoryDataRow<string, string, int, float>(page, state, width, dpi)
                        {
                            Skip = PageTextOverlapSkips.Reasons.GetValueOrDefault(Key(page, state, width, dpi)),
                            TestDisplayName = Key(page, state, width, dpi),
                        };
                    }
                }
            }
        }
    }

    internal static string Key(string page, string state, int width, float dpi) => $"{page} | {state} | {width} wide | DPI {dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NoTextIsDrawnOverOtherTextOrPastItsClip(string page, string state, int width, float dpi)
    {
        var faults = await FaultsOf(page, state, width, dpi);
        var dump = Environment.GetEnvironmentVariable("PILOT_TEXT_OVERLAP_DUMP");
        if (dump is { Length: > 0 })
        {
            // One line a fault, for classifying a run: the case, then the fault's own line.
            lock (DumpLock)
            {
                File.AppendAllLines(dump, faults.Select(f => $"{Key(page, state, width, dpi)}\t{f.Line}"));
            }
        }
        faults.Select(f => f.Line).ShouldBeEmpty($"{Key(page, state, width, dpi)} [{faults.Count} faults]");
    }

    private static readonly Lock DumpLock = new();

    /// <summary>The check over <paramref name="page"/> in <paramref name="state"/>, arranged as the chrome arranges it: the page in the room the rail and the panel leave.</summary>
    internal static async Task<IReadOnlyList<TextFault>> FaultsOf(string page, string state, int width, float dpi)
    {
        using var renderer = new RgbaImageRenderer((uint)width, Height);
        var data = PageData.For(state);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        chrome.Play.ShowEverything();
        chrome.Flights.CanCommand = true;
        chrome.Jobs.CanCommand = true;
        chrome.Cards.CanTell = true;
        chrome.Nodes.Choices = data.Nodes;
        chrome.Autopilot.OnToggle = _ => { };
        chrome.About.Newer = () => "2.0.9-beta.1";
        chrome.About.Restart = () => { };
        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);

        // The cards panel takes its width off the page when it has cards, as the chrome decides it.
        var cards = data.Cards;
        var panel = cards.HasAny ? CardsView<RgbaImage>.Width * dpi : 0f;
        var pageWidth = width - RailWidth * dpi - panel;
        Rect<float> PageRect() => new(0, 0, pageWidth, Height);

        Layout.Node tree;
        Rect<float> rect = PageRect();
        switch (page)
        {
            case "home":
                tree = chrome.Home.Tree(new HomeScreen(data.Flights, data.Sessions, data.Jobs, cards, data.Machines));
                break;
            case "sessions":
                tree = chrome.Sessions.Tree(data.Sessions);
                break;
            case "flights table":
                chrome.Flights.Mode = FlightsViewMode.List;
                tree = chrome.Flights.Tree(data.Flights);
                break;
            case "flights canvas":
                chrome.Flights.Mode = FlightsViewMode.Canvas;
                tree = chrome.Flights.Tree(data.Flights);
                break;
            case "flight page":
                chrome.Flights.OpenDetail(data.Flights.Lines.Count > 0 ? data.Flights.Lines[0].Code : "NONE1");
                tree = chrome.Flights.Tree(data.Flights);
                break;
            case "jobs":
                tree = chrome.Jobs.Tree(data.Jobs);
                break;
            case "fleet":
                tree = chrome.Fleet.Tree(data.Fleet);
                break;
            case "settings":
                tree = chrome.Settings.Tree(PageData.Now);
                break;
            case "repos":
                chrome.Repos.ExpandAll(data.Repos);
                tree = chrome.Repos.Tree(data.Repos);
                break;
            case "chat":
                tree = await OpenChat(chrome, data);
                break;
            case "cards panel":
                tree = chrome.Cards.Tree(cards);
                rect = new Rect<float>(0, 0, CardsView<RgbaImage>.Width * dpi, Height);
                break;
            case "transcript":
                tree = await OpenTranscript(chrome, data);
                break;
            case "wizard repos":
                tree = await OpenWizard(chrome, data, toIssues: false);
                break;
            case "wizard issues":
                tree = await OpenWizard(chrome, data, toIssues: true);
                break;
            default:
                throw new ArgumentException($"no page '{page}'", nameof(page));
        }

        var arranged = Layout.Engine.Arrange(tree, rect, ctx);
        return TextOverlap.Faults(arranged);
    }

    private static async Task<Layout.Node> OpenChat(PilotChrome<RgbaImage> chrome, PageData data)
    {
        var chat = chrome.Chat;
        var feed = data.Chat;
        chat.Load = (after, _) => Task.FromResult(after < 0 ? new ChatPage { Entries = feed, Total = feed.Length } : new ChatPage { Entries = [], Total = feed.Length });
        chat.OnSend = _ => Task.FromResult<string?>(null);
        chat.Open();
        for (var i = 0; i < 100 && chat.Entries.Count < feed.Length; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        chat.Close();
        chat.Entries.Count.ShouldBe(feed.Length);
        return chat.Tree();
    }

    private static async Task<Layout.Node> OpenTranscript(PilotChrome<RgbaImage> chrome, PageData data)
    {
        var entries = data.Transcript;
        var view = chrome.Sessions;
        view.Transcript.Load = (_, _, after, _) => Task.FromResult(after < 0
            ? new TranscriptPage { Total = entries.Length, Entries = entries }
            : new TranscriptPage { Total = entries.Length, Entries = [] });
        var line = data.Sessions.Lines.Count > 0 ? data.Sessions.Lines[0] : SessionLine.From(PageData.Row("aaaa0000", "worker", SessionState.Working), PageData.Now);
        view.Transcript.Open(line);
        for (var i = 0; i < 100 && view.Transcript.Entries.Count < entries.Length; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        view.Transcript.Entries.Count.ShouldBe(entries.Length);
        return view.Tree(data.Sessions);
    }

    private static async Task<Layout.Node> OpenWizard(PilotChrome<RgbaImage> chrome, PageData data, bool toIssues)
    {
        var wizard = chrome.Flights.Wizard;
        wizard.LoadRepos = _ => Task.FromResult(data.WizardRepos);
        wizard.LoadIssues = (_, _) => Task.FromResult(data.WizardIssues);
        wizard.Open();
        for (var i = 0; i < 100 && wizard.RepoResults.Count < data.WizardRepos.Length; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        if (toIssues && data.WizardRepos.Length > 0)
        {
            wizard.Toggle(data.WizardRepos[0]);
            wizard.ToIssues();
            for (var i = 0; i < 100 && wizard.IssueResults.Count < data.WizardIssues.Length; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
        else if (data.WizardRepos.Length > 1)
        {
            wizard.Toggle(data.WizardRepos[1]);
        }
        return chrome.Flights.Tree(data.Flights);
    }
}
