using Pilot.Contracts;

namespace Pilot.Gui.Tests;

/// <summary>
/// The data every page is drawn from in <see cref="PageTextOverlapTests"/>, one set per state: <b>sample</b> is what the GUI shows
/// from the contract's fixtures (<see cref="SessionsCapture"/>), <b>long</b> has titles, paths and reports far past a column,
/// <b>many</b> has rows enough to scroll, and <b>empty</b> has nothing.
/// </summary>
internal sealed class PageData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private const string LongTitle = "Every page is checked for text drawn over text, in tests and live, at every width and DPI, with a title that runs on and on past any column it is given";
    private const string LongPath = @"C:\Users\ada\src\example-org\orbit\.claude\worktrees\ORB204\src\Orbit.Lib.Tests\Fixtures\a-very-long-folder-name-that-has-no-break";
    private const string LongWord = "Pilot.Ui.PageTextOverlapTests.EveryPageIsHeldApartAtEveryWidthAndDpiWithSampleDataThatIsVeryLong";
    private const string LongReport = "The leg is done and every step ticked. I brought the two commits of fix/chat-rows-wrap onto the branch, added the shared check in Pilot.Ui, and ran the whole Pilot.Gui.Tests suite in Release: 2891 passed, 14 skipped. Two cases fail for DIR.Lib's stack measure (SharpAstro/DIR.Lib#169) and are skipped with their fault line. Nothing was pushed; the controller lands the flight.";

    public required string State { get; init; }
    public required NodeMark[] Nodes { get; init; }
    public required SessionsScreen Sessions { get; init; }
    public required FlightsScreen Flights { get; init; }
    public required JobsScreen Jobs { get; init; }
    public required FleetScreen Fleet { get; init; }
    public required ReposScreen Repos { get; init; }
    public required PullRequestsScreen PullRequests { get; init; }
    public required CardsScreen Cards { get; init; }
    public required IReadOnlyList<MachineLine>? Machines { get; init; }
    public required TranscriptEntry[] Chat { get; init; }
    public required TranscriptEntry[] Transcript { get; init; }
    public required RepoInfo[] WizardRepos { get; init; }
    public required IssueInfo[] WizardIssues { get; init; }

    private static readonly NodeMark Here = new("node.sock", "node.sock", "SE1", true);
    private static readonly NodeMark Other = new("10.0.0.9:47731", "10.0.0.9:47731", "LE1", false);

    public static SessionRow Row(string id, string name, SessionState state) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = @"C:\work\pilot",
        Role = SessionRole.AdHoc,
        ContextTokens = 1000,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 2000,
        StartedAt = Now.AddHours(-2),
        LastResponseAt = Now.AddMinutes(-3),
    };

    public static PageData For(string state) => state switch
    {
        "sample" => Sample(),
        "long" => Build(state, sessions: 8, flights: 3, legs: 4, jobs: 6, cards: 5, repos: 4, chat: 10, transcript: 12, wizard: 6, nodes: 3, lengthen: true),
        "many" => Build(state, sessions: 60, flights: 40, legs: 3, jobs: 50, cards: 14, repos: 40, chat: 60, transcript: 80, wizard: 60, nodes: 12, lengthen: false),
        "empty" => Empty(),
        _ => throw new ArgumentException($"no state '{state}'", nameof(state)),
    };

    private static PageData Empty() => new()
    {
        State = "empty",
        Nodes = [Here],
        Sessions = new SessionsScreen([], "test", Now),
        Flights = new FlightsScreen([], "test", Now),
        Jobs = new JobsScreen([], "test", Now),
        Fleet = new FleetScreen([], Now),
        Repos = new ReposScreen([], "test", Now),
        PullRequests = PullRequestsFor("empty"),
        Cards = CardsScreen.Empty,
        Machines = null,
        Chat = [],
        Transcript = [],
        WizardRepos = [],
        WizardIssues = [],
    };

    private static PageData Sample()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var repos = SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var built = Build("sample", sessions: 0, flights: 4, legs: 3, jobs: 0, cards: 3, repos: 0, chat: 8, transcript: 10, wizard: 5, nodes: 2, lengthen: false);
        return new PageData
        {
            State = "sample",
            Nodes = [Here, Other],
            Sessions = sessions,
            // The sample snapshot has no flights or cards: the generated ones stand in, beside its sessions and jobs.
            Flights = built.Flights,
            Jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), "sample", snapshot.At),
            Fleet = built.Fleet,
            Repos = SessionsCapture.ReposScreenFor(snapshot, repos, "sample"),
            PullRequests = PullRequestsFor("sample"),
            Cards = built.Cards,
            Machines = null,
            Chat = built.Chat,
            Transcript = built.Transcript,
            WizardRepos = built.WizardRepos,
            WizardIssues = built.WizardIssues,
        };
    }

    // Row i of a list, its words stretched when `lengthen` is on: a title past a column, a path with no break to wrap at, a report of a paragraph.
    private static PageData Build(string state, int sessions, int flights, int legs, int jobs, int cards, int repos, int chat, int transcript, int wizard, int nodes, bool lengthen)
    {
        string Title(string what, int i) => lengthen ? $"{what} {i}: {LongTitle}" : $"{what} {i}";
        var path = lengthen ? LongPath : @"C:\work\pilot";

        var sessionRows = Enumerable.Range(0, sessions).Select(i => Row($"s{i:0000}0000", lengthen ? $"se1-pil380-{LongWord}-{i}" : $"se1-pil380-worker-{i}", (SessionState)(i % 3)) with
        {
            Cwd = path,
            Repo = lengthen ? LongWord : "pilot",
            Role = (SessionRole)(i % 6),
            Flight = $"PIL{380 + i % 5}",
            Leg = lengthen ? "overlap-check-with-a-leg-name-that-is-long" : "overlap-check",
            Model = "claude-opus-5-5",
            Effort = "xhigh",
            WaitingFor = i % 3 == 1 ? (lengthen ? LongTitle : "the controller") : null,
            Asking = i % 3 == 1 ? (lengthen ? LongReport : "Merge PR #390?") : null,
            ApiError = lengthen && i % 4 == 3 ? "API Error: 529 overloaded_error: " + LongTitle : null,
        }).ToList();
        var now = Now;
        var sessionsScreen = new SessionsScreen(SessionLine.Build(sessionRows, now), "test", now);

        var flightRows = Enumerable.Range(0, flights).Select(i => new FlightRow
        {
            Code = $"PIL{380 + i}",
            Repo = lengthen ? LongWord : "pilot",
            RepoPath = path,
            Issue = 380 + i,
            Title = Title("Flight", i),
            Branch = lengthen ? $"flight/pil{380 + i}/{LongWord}" : $"flight/pil{380 + i}",
            Worktree = lengthen ? LongPath : $@"C:\work\pilot\.claude\worktrees\pil{380 + i}",
            Base = "origin/main",
            State = (FlightState)(i % 3),
            Connections = i > 0 && i % 4 == 0 ? [$"PIL{380 + i - 1}"] : [],
            CreatedAt = now.AddHours(-i - 1),
            PullRequest = i % 2 == 0 ? 390 + i : null,
            PullRequestUrl = i % 2 == 0 ? $"https://github.com/SharpAstro/pilot/pull/{390 + i}" : null,
            LandedAt = i % 3 == 2 ? now.AddMinutes(-30) : null,
            Queued = i % 3 == 0 ? (lengthen ? "waiting for an Opus slot: 3 of 3 flying: " + LongWord + ", " + LongPath : "waiting for an Opus slot: 3 of 3 flying") : null,
            GitHubLabel = i % 9 == 0 ? "pilot: priority" : null,
            Review = i % 2 == 0 ? new ReviewRow
            {
                State = (ReviewState)(i % 3),
                Reviewer = lengthen ? LongWord : "reviewer",
                Report = lengthen ? LongReport + "\n\n" + LongReport : "Reviewed at head: clean.",
                StartedAt = now.AddMinutes(-20),
                FinishedAt = now.AddMinutes(-10),
                Depth = ReviewDepth.Standard,
                DepthWhy = lengthen ? LongTitle : "small diff",
            } : null,
            Overlaps = i % 2 == 1 ? [new OverlapRow { Other = $"PIL{380 + i - 1}", Why = lengthen ? LongReport : "both change TopBar.cs", Answer = OverlapAnswer.Pending, At = now.AddMinutes(-5) }] : [],
        }).ToList();
        var legRows = flightRows.SelectMany(f => Enumerable.Range(0, legs).Select(j => new LegRow
        {
            Flight = f.Code,
            Name = lengthen ? $"leg-{j}-{LongWord}" : $"leg-{j}",
            Order = j,
            Goal = lengthen ? LongReport : $"Goal of leg {j}",
            Checklist = [.. Enumerable.Range(0, lengthen ? 4 : 2).Select(k => new CheckItem { Text = lengthen ? LongTitle : $"Step {k}", Done = k % 2 == 0 })],
            DoneWhen = lengthen ? LongReport : "It passes",
            State = (LegState)(j % 4),
            Worker = j % 4 == 1 ? sessionRows.ElementAtOrDefault(j)?.Name : null,
            Report = j % 4 is 2 or 3 ? (lengthen ? LongReport : "Done.") : null,
            StartedAt = now.AddMinutes(-60),
        })).ToList();
        var admission = new AdmissionRow
        {
            OpusCount = 3, OpusCap = 3, OneFlightPerRepo = true, Counted = ["pil380 worker", "pil381 reviewer", "pil383 planner on LE1"],
            PeersNotCounted = lengthen ? "peers not counted: LE1 did not answer: " + LongReport : null,
        };
        var flightsScreen = new FlightsScreen(FlightLine.Build(flightRows, sessionRows, now, legRows, admission), "test", now) { Admission = admission };

        var jobRows = Enumerable.Range(0, jobs).Select(i => new JobRow
        {
            Id = $"j{i}",
            Command = lengthen ? $"dotnet test src/Pilot.Gui.Tests -c Release --filter FullyQualifiedName~{LongWord} -- {LongTitle}" : "dotnet test src/Pilot.Gui.Tests -c Release",
            Cwd = path,
            Owner = sessionRows.ElementAtOrDefault(i)?.Id,
            OwnerName = sessionRows.ElementAtOrDefault(i)?.Name,
            Flight = $"PIL{380 + i % 5}",
            Leg = "overlap-check",
            State = (JobState)(i % 5),
            Step = lengthen ? LongTitle : "Passed Pilot.Gui.Tests",
            Error = lengthen && i % 5 == 2 ? LongReport : null,
            LogPath = path + "\\j.log",
            Timeout = TimeSpan.FromMinutes(30),
            StartedAt = now.AddMinutes(-i - 5),
            QueuedAt = now.AddMinutes(-i - 6),
            EndedAt = i % 5 is 1 or 2 or 3 ? now.AddMinutes(-i) : null,
        }).ToList();
        var jobsScreen = new JobsScreen(JobLine.Build(jobRows, now), "test", now);

        var fleetLines = Enumerable.Range(0, nodes).Select(i => FleetLine.From(i == 0 ? Here.Where : $"10.0.0.{i + 8}:47731", here: i == 0, canCommand: i % 2 == 0, null,
            new NodeSummary
            {
                Connected = i % 4 != 3,
                Status = i % 4 != 3 ? "live" : "waiting for the node on 10.0.0.9:47731; retrying every 2s",
                Stream = $"s{i}",
                Account = lengthen ? $"someone.with.a.long.name.{i}@example-organisation.org (Organisation account, max 20x, resets Friday)" : $"ada{i}@example.org",
                LiveSessions = 3 + i,
                WaitingOnYou = i % 3,
                Flying = i % 4,
                Held = i % 2,
                JobsRunning = i % 3,
                JobsQueued = i % 2,
                Rings = i % 3,
                Node = new NodeInfo
                {
                    WireVersion = 1, Version = "2.0.9", MachineName = lengthen ? $"DESKTOP-{LongWord}-{i}" : $"DESKTOP-{i}", MachinePrefix = $"SE{i + 1}", ProcessId = 1, StartedAt = now,
                    DataFolder = path, SocketPath = path, Stream = $"s{i}",
                },
            }, now)).ToList();
        var fleetScreen = new FleetScreen(fleetLines, now);

        var repoStatuses = Enumerable.Range(0, repos).Select(i => new RepoStatus
        {
            Name = lengthen ? $"{LongWord}-{i}" : $"repo-{i}",
            Path = path,
            GitHub = $"SharpAstro/repo-{i}",
            Tracked = i % 4 != 3,
            Flights = i % 3,
            DefaultBranch = "main",
            Branch = lengthen ? "feature/" + LongWord : "main",
            Dirty = i % 2 == 0,
            Ahead = i % 3,
            Behind = i % 2,
            Worktrees = [new WorktreeStatus { Path = path + "\\wt", Branch = lengthen ? "flight/" + LongWord : "flight/pil380", Flight = flightRows.ElementAtOrDefault(i)?.Code, Dirty = true, Files = 3, Added = 120, Removed = 40 }],
            PullRequests = [new PullRequestStatus { Number = 390 + i, Title = lengthen ? LongTitle : $"PR {i}", Head = lengthen ? "flight/" + LongWord : "flight/pil380", Draft = i % 2 == 0, Checks = "3/3 passed", Flight = flightRows.ElementAtOrDefault(i)?.Code, Labels = lengthen ? ["needs-review", LongWord] : ["needs-review"] }],
            Errors = lengthen && i % 2 == 0 ? [LongReport] : [],
            UpdatedAt = now,
        }).ToList();
        var reposScreen = new ReposScreen(RepoLine.Build(repoStatuses, flightRows, sessionRows, [], legRows, now), "test", now);

        var cardRows = Enumerable.Range(0, cards).Select(i => new CardRow
        {
            Id = $"c{i}",
            Bin = (CardBin)(i % 3),
            Kind = CardKinds.SessionWaiting,
            Subject = $"flight/PIL{380 + i % 3}/overlap-check",
            Title = lengthen ? $"PIL{380 + i % 3}: {LongTitle}" : $"PIL{380 + i % 3} waits on you",
            Detail = lengthen ? LongReport : "Merge PR #390?",
            Choices = [new CardChoice { Label = lengthen ? "Merge it now with all the checks green" : "Merge", Verb = "merge", Target = "PIL380" }, new CardChoice { Label = "Not yet", Verb = "dismiss", Target = "c" }],
            At = now.AddMinutes(-i),
            State = i % 5 == 4 ? CardState.Closed : CardState.Open,
            Outcome = i % 5 == 4 ? "merged" : null,
            AskedBy = lengthen ? LongWord : "se1-pil380-worker",
            Why = lengthen ? LongReport : null,
        }).ToList();
        var cardsScreen = cards == 0 ? CardsScreen.Empty : CardsScreen.From(cardRows, sessionRows, now);

        var chatEntries = ChatFeed(chat, lengthen);
        var transcriptEntries = ChatFeed(transcript, lengthen);

        var wizardRepos = Enumerable.Range(0, wizard).Select(i => new RepoInfo { Name = lengthen ? $"{LongWord}-{i}" : $"repo-{i}", Path = path + $"\\r{i}", GitHub = $"SharpAstro/repo-{i}", Flights = i }).ToArray();
        var wizardIssues = Enumerable.Range(0, wizard).Select(i => new IssueInfo { Number = 100 + i, Title = lengthen ? $"{LongTitle} {LongTitle}" : $"Issue {i}", Labels = lengthen ? ["enhancement", LongWord] : ["bug"], UpdatedAt = now.AddDays(-i) }).ToArray();

        return new PageData
        {
            State = state,
            Nodes = [.. Enumerable.Range(0, nodes).Select(i => i == 0 ? Here : new NodeMark($"10.0.0.{i + 8}:47731", $"10.0.0.{i + 8}:47731", $"LE{i}", false))],
            Sessions = sessionsScreen,
            Flights = flightsScreen,
            Jobs = jobsScreen,
            Fleet = fleetScreen,
            Repos = reposScreen,
            PullRequests = PullRequestsFor(state),
            Cards = cardsScreen,
            Machines = null,
            Chat = chatEntries,
            Transcript = transcriptEntries,
            WizardRepos = wizardRepos,
            WizardIssues = wizardIssues,
        };
    }

    // The Pull requests page's read: a handful of every kind (sample), titles, repos and chips far past a column (long), rows enough to
    // scroll across many repos (many), and a read with no open PR (empty).
    private static PullRequestsScreen PullRequestsFor(string state)
    {
        var lengthen = state == "long";
        var kinds = Enum.GetValues<PullRequestKind>();
        var (repos, perRepo) = state switch { "empty" => (0, 0), "sample" => (3, 3), "long" => (3, 3), _ => (15, 4) };
        var rows = new List<PullRequestRow>();
        for (var r = 0; r < repos; r++)
        {
            var repo = lengthen ? $"example-org/{LongWord}-{r}" : $"SharpAstro/repo-{r}";
            for (var n = 0; n < perRepo; n++)
            {
                var kind = kinds[(r * perRepo + n) % kinds.Length];
                var flight = (r + n) % 2 == 0 ? $"PIL{380 + r}" : null;
                var label = kind == PullRequestKind.ChecksRunning ? "Checks running 4/7" : kind == PullRequestKind.ChecksFailed ? "Checks failed: build" : kind.ToString();
                rows.Add(new PullRequestRow
                {
                    Repo = repo,
                    Number = 100 + r * 10 + n,
                    Title = lengthen ? LongTitle : $"A change in {repo} ({n})",
                    Url = $"https://github.com/{repo}/pull/{100 + r * 10 + n}",
                    Author = lengthen ? LongWord : "ada",
                    Draft = kind == PullRequestKind.Draft,
                    CreatedAt = Now.AddDays(-2),
                    UpdatedAt = Now.AddMinutes(-(5 + r * 7 + n)),
                    Head = lengthen ? "flight/" + LongWord : "feature/x",
                    Base = "main",
                    Kind = kind,
                    Label = lengthen ? $"{label}: {LongWord}" : label,
                    NeedsMe = kind is PullRequestKind.Ready or PullRequestKind.Conflicts or PullRequestKind.ChangesRequested,
                    Flight = flight,
                    Review = flight is null ? null : n % 3 == 0 ? PullRequestReview.Passed : n % 3 == 1 ? PullRequestReview.Changes : PullRequestReview.Reviewing,
                });
            }
        }
        var row = new PullRequestsRow { Orgs = ["SharpAstro", "example-org"], ReadAt = Now.AddSeconds(-40), Viewer = "ada", PullRequests = [.. rows] };
        return new PullRequestsScreen(row, "live from the node on node.sock", Now);
    }

    // A feed of n entries: replies, a person's messages, tool calls with their results, as the chat and a transcript show them.
    private static TranscriptEntry[] ChatFeed(int n, bool lengthen)
    {
        var entries = new List<TranscriptEntry>();
        var at = Now.AddHours(-1);
        while (entries.Count < n)
        {
            var i = entries.Count;
            switch (i % 5)
            {
                case 0:
                    entries.Add(new TranscriptEntry { Index = i, Kind = TranscriptKind.Assistant, Text = lengthen ? LongReport + "\n\n- **Rebase:** " + LongTitle + "\n- **Tests:** " + LongPath : $"Tests pass ({i}). Opening the draft PR.", At = at.AddSeconds(i) });
                    break;
                case 1:
                    entries.Add(new TranscriptEntry { Index = i, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = lengthen ? $"git push --set-upstream origin flight/{LongWord}" : "git push", Description = lengthen ? LongTitle : "Push the branch", ToolUseId = $"t{i}", At = at.AddSeconds(i) });
                    break;
                case 2:
                    entries.Add(new TranscriptEntry { Index = i, Kind = i % 10 == 2 ? TranscriptKind.ToolResult : TranscriptKind.Error, Text = lengthen ? LongReport : "ok", ToolUseId = $"t{i - 1}" });
                    break;
                case 3:
                    entries.Add(new TranscriptEntry { Index = i, Kind = TranscriptKind.User, Text = lengthen ? $"Merge PIL383's PR #390 once CI is green (my answer to: {LongTitle})" : "Merge it.", At = at.AddSeconds(i) });
                    break;
                default:
                    entries.Add(new TranscriptEntry { Index = i, Kind = TranscriptKind.Thinking, Text = lengthen ? LongReport : "Thinking it over.", At = at.AddSeconds(i) });
                    break;
            }
        }
        return [.. entries];
    }
}
