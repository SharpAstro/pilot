using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Work items: what create-flight reads, how a flight records its tracker, and where a title comes from.</summary>
public sealed class TrackerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);

    public TrackerTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
    }

    private string Repo => Path.Combine(_root, "bullclip");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private sealed class FakeShortcut(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private FlightCommander Commander(string? token, FakeShortcut handler) =>
        new(_state, new Planes(new GitCli()), TimeProvider.System)
        {
            Trackers = new Trackers([new ShortcutTracker(new HttpClient(handler), () => token)]),
        };

    private Task<CommandResult> Create(FlightCommander commander, params string[] args) =>
        commander.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = args, Who = "tests" }, Ct);

    [Theory]
    [InlineData("254", TrackerKind.GitHub, 254)]
    [InlineData("#254", TrackerKind.GitHub, 254)]
    [InlineData("sc-101645", TrackerKind.Shortcut, 101645)]
    [InlineData("SC-101645", TrackerKind.Shortcut, 101645)]
    [InlineData("https://app.shortcut.com/bullclip/story/101645", TrackerKind.Shortcut, 101645)]
    [InlineData("https://app.shortcut.com/bullclip/story/101645/the-card-slug", TrackerKind.Shortcut, 101645)]
    public void AWorkItemIsReadFromANumberAScNumberOrAStoryUrl(string given, TrackerKind tracker, int id) =>
        WorkItem.Parse(given).Item.ShouldBe(new WorkItem(tracker, id));

    [Theory]
    [InlineData("")]
    [InlineData("sc-")]
    [InlineData("sc-abc")]
    [InlineData("0")]
    [InlineData("https://example.com/story/5")]
    [InlineData("https://app.shortcut.com/bullclip/epic/5")]
    [InlineData("TIA1611")]
    public void AnythingElseIsRefusedWithWhatIsAccepted(string given)
    {
        var (item, error) = WorkItem.Parse(given);
        item.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("sc-N");
    }

    [Fact]
    public void ItemsAreShownAsTheirTrackerWritesThem()
    {
        new WorkItem(TrackerKind.GitHub, 254).Display.ShouldBe("#254");
        new WorkItem(TrackerKind.Shortcut, 101645).Display.ShouldBe("sc-101645");
        new WorkItem(TrackerKind.GitHub, 254).Noun.ShouldBe("issue #254");
        new WorkItem(TrackerKind.Shortcut, 101645).Noun.ShouldBe("story sc-101645");
    }

    private static FlightRow Row(TrackerKind tracker) => new()
    {
        Code = "BUL101645",
        Repo = "bullclip",
        RepoPath = "/r/bullclip",
        Issue = 101645,
        Tracker = tracker,
        Branch = "flight/bul101645",
        Worktree = "/w/BUL101645",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void ATrackerSurvivesTheJsonRoundTrip()
    {
        var json = JsonSerializer.Serialize(Row(TrackerKind.Shortcut), PilotJsonContext.Default.FlightRow);
        json.ShouldContain("\"tracker\":\"shortcut\"");
        var back = JsonSerializer.Deserialize(json, PilotJsonContext.Default.FlightRow)!;
        back.Tracker.ShouldBe(TrackerKind.Shortcut);
        back.Item.ShouldBe(new WorkItem(TrackerKind.Shortcut, 101645));
    }

    [Fact]
    public void StateWrittenBeforeTrackersReadsAsGitHub()
    {
        var json = JsonSerializer.Serialize(Row(TrackerKind.GitHub), PilotJsonContext.Default.FlightRow);
        var old = JsonSerializer.Deserialize<JsonElement>(json).EnumerateObject().Where(p => p.Name != "tracker")
            .ToDictionary(p => p.Name, p => p.Value);
        var legacy = JsonSerializer.Serialize(old);
        legacy.ShouldNotContain("tracker");

        var back = JsonSerializer.Deserialize(legacy, PilotJsonContext.Default.FlightRow)!;
        back.Tracker.ShouldBe(TrackerKind.GitHub);
        back.Issue.ShouldBe(101645);
        back.Item.Display.ShouldBe("#101645");
    }

    [Fact]
    public async Task AShortcutFlightIsCodedFromTheRepoPrefixAndTheCardNumberWithSuffixes()
    {
        var commander = Commander(null, new FakeShortcut(HttpStatusCode.NotFound, ""));
        var first = await Create(commander, "sc-101645", "Add a thing");
        first.Ok.ShouldBeTrue(first.Error);
        first.Made.ShouldBe("BUL101645");
        var second = await Create(commander, "https://app.shortcut.com/bullclip/story/101645/slug", "Again");
        second.Made.ShouldBe("BUL101645a");

        var flight = _state.Flights().Single(f => f.Code == "BUL101645");
        flight.Tracker.ShouldBe(TrackerKind.Shortcut);
        flight.Issue.ShouldBe(101645);
        flight.Title.ShouldBe("Add a thing");

        // The branch carries sc-<id> for Shortcut's integration; the plane's folder stays the code.
        flight.Branch.ShouldBe("flight/bul101645-sc-101645");
        Path.GetFileName(flight.Worktree).ShouldBe("bul101645");
        Git(flight.Worktree, "rev-parse", "--abbrev-ref", "HEAD").ShouldBe("flight/bul101645-sc-101645");
    }

    [Fact]
    public async Task AGitHubFlightKeepsItsPlainBranch()
    {
        (await Create(Commander(null, new FakeShortcut(HttpStatusCode.OK, "{}")), "12")).Ok.ShouldBeTrue();
        _state.Flights().Single().Branch.ShouldBe("flight/bul12");
    }

    [Fact]
    public void AShortcutPrLinksTheStoryAndClosesNothing()
    {
        var row = Row(TrackerKind.Shortcut) with { Title = "Cards on Shortcut", FoldedIn = [new FoldedIssue { Number = 7, From = "BUL7" }] };
        var body = LegCommander.PrBody(row, []);
        body.ShouldContain("sc-101645");
        body.ShouldContain("sc-7 (folded in from BUL7)");
        body.ShouldNotContain("Closes #");
        LegCommander.PrTitle(row, "").ShouldBe("BUL101645: Cards on Shortcut [sc-101645]");
        LegCommander.PrTitle(row, "About sc-101645").ShouldBe("BUL101645: About sc-101645");
    }

    [Fact]
    public void AGitHubPrStillClosesTheIssue()
    {
        var row = Row(TrackerKind.GitHub) with { Title = "A thing" };
        LegCommander.PrBody(row, []).ShouldContain("Closes #101645.");
        LegCommander.PrTitle(row, "").ShouldBe("BUL101645: A thing");
    }

    [Fact]
    public async Task AShortcutTitleIsReadFromTheApiWithTheToken()
    {
        var handler = new FakeShortcut(HttpStatusCode.OK, "{\"id\":101645,\"name\":\"Cards on Shortcut\"}");
        var result = await Create(Commander("secret", handler), "sc-101645");
        result.Ok.ShouldBeTrue(result.Error);
        _state.Flights().Single().Title.ShouldBe("Cards on Shortcut");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.RequestUri!.ToString().ShouldBe("https://api.app.shortcut.com/api/v3/stories/101645");
        request.Headers.GetValues("Shortcut-Token").ShouldBe(["secret"]);
    }

    [Fact]
    public async Task AGivenTitleWinsAndNoRequestIsMade()
    {
        var handler = new FakeShortcut(HttpStatusCode.OK, "{\"name\":\"From Shortcut\"}");
        var result = await Create(Commander("secret", handler), "sc-7", "The controller's title");
        result.Ok.ShouldBeTrue(result.Error);
        _state.Flights().Single().Title.ShouldBe("The controller's title");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutATokenTheGivenTitleStandsIn()
    {
        var handler = new FakeShortcut(HttpStatusCode.OK, "{\"name\":\"From Shortcut\"}");
        var result = await Create(Commander(null, handler), "sc-7", "Given");
        result.Ok.ShouldBeTrue(result.Error);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AShortcutFlightWithNeitherTokenNorTitleIsRefusedNamingWhatIsMissing()
    {
        var result = await Create(Commander(null, new FakeShortcut(HttpStatusCode.OK, "{}")), "sc-7");
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("SHORTCUT_API_TOKEN");
        result.Error.ShouldContain("title");
        _state.Flights().ShouldBeEmpty();
        Directory.Exists(Path.Combine(Repo, ".claude", "worktrees")).ShouldBeFalse();
    }

    [Fact]
    public async Task AStoryTheTokenCannotReadIsRefusedWithoutATitle()
    {
        var result = await Create(Commander("bad", new FakeShortcut(HttpStatusCode.Unauthorized, "")), "sc-7");
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("sc-7");
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task ABareNumberStaysAGitHubIssueAndNeedsNoTitle()
    {
        var result = await Create(Commander(null, new FakeShortcut(HttpStatusCode.OK, "{}")), "12");
        result.Ok.ShouldBeTrue(result.Error);
        var flight = _state.Flights().Single();
        flight.Tracker.ShouldBe(TrackerKind.GitHub);
        flight.Code.ShouldBe("BUL12");
    }

    [Fact]
    public async Task ANonsenseItemIsRefused()
    {
        var result = await Create(Commander(null, new FakeShortcut(HttpStatusCode.OK, "{}")), "jira-5");
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("sc-N");
    }

    [Fact]
    public void TheShortcutTokenSettingIsSavedAndCleared()
    {
        var (settings, error) = NodeSettings.With(new NodeSettings(), NodeSettings.ShortcutTokenKey, ["abc123"]);
        error.ShouldBeNull();
        settings!.ShortcutToken.ShouldBe("abc123");
        NodeSettings.With(settings, NodeSettings.ShortcutTokenKey, ["default"]).Settings!.ShortcutToken.ShouldBeNull();
    }
}
