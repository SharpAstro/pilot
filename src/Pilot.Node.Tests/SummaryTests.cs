using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A model that answers what a test says and records what it was asked: no test runs <c>claude</c>.</summary>
internal sealed class FakeSummaryModel : ISummaryModel
{
    public static readonly SummaryAnswer Fine = new("## Needs you\nNothing.\n\n## Where it is\nParser is flying.", 1234, 0.0021);

    public List<string> Prompts { get; } = [];

    public SummaryAnswer Answer { get; set; } = Fine;

    /// <summary>Thrown by the next call, when set.</summary>
    public Exception? Fails { get; set; }

    /// <summary>Holds a call until the test completes it, when set.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<SummaryAnswer> AskAsync(string prompt, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
        return Fails is { } failure ? throw failure : Answer;
    }
}

/// <summary>
/// The summary (#221): records built only from what the node holds, under a fixed prompt, one summary call that only runs when asked,
/// and the answer kept per scope with its time, tokens and cost where every client sees it.
/// </summary>
public sealed class SummaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    // What the node holds of the PR: the only hashes a record may carry.
    private const string PrHead = "4f2c9a01d6e8b3c7a5f0e2d4c6b8a0f1e3d5c7b9";
    private const string ReviewedHead = "9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b";
    private const string OtherPrHead = "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b";

    private readonly ManualTime _time = new(Start);
    private readonly InMemoryJournal _journal = new();
    private readonly NodeState _state;
    private readonly FakeSummaryModel _model = new();
    private readonly Summaries _summaries;

    public SummaryTests()
    {
        _state = new NodeState(_journal, _time);
        _summaries = new Summaries(_state, _model, _time);
    }

    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, int issue = 1611) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = issue, Title = "Plate-solve the guide frame", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/r/.claude/worktrees/{code}", Base = "origin/main", State = state, CreatedAt = Start - TimeSpan.FromHours(3),
    };

    private static LegRow Leg(string flight, string name, int order, LegState state, string goal = "Make the thing", string? report = null) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = goal, DoneWhen = $"{name} tests pass", State = state, Report = report,
    };

    private static CardRow Card(string id, string subject, CardState state = CardState.Open, string title = "A card", CardBin bin = CardBin.Ring) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = subject, Title = title, At = Start, State = state,
        Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Status = "passing" }],
    };

    private static SessionRow Session(string id, string name, string flight, SessionState state, string? leg = null) => new()
    {
        Id = id, SessionId = id + "-0000-0000-0000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/r", Role = SessionRole.Worker, Flight = flight, Leg = leg,
        ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Start,
    };

    /// <summary>TIA1611 in the air with a PR and a review, two legs, cards and sessions; TIA9 landed; a flight and a card of another.</summary>
    private void Fill()
    {
        _state.PutFlight(Flight("TIA1611") with
        {
            PullRequest = 63, PullRequestUrl = "https://github.com/SharpAstro/tianwen/pull/63", GitHubHead = PrHead,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "se1-tia1611-review", StartedAt = Start - TimeSpan.FromHours(1), FinishedAt = Start - TimeSpan.FromMinutes(40), ReviewedHead = ReviewedHead, Report = "Read every leg's Done-when; all hold." },
        });
        _state.PutFlight(Flight("TIA9", FlightState.Landed, issue: 9) with { LandedAt = Start - TimeSpan.FromHours(2) });
        _state.PutFlight(Flight("DIR7", issue: 7) with { Repo = "DIR.Lib", Title = "Cache the glyph atlas" });

        _state.PutLeg(Leg("TIA1611", "solver", 1, LegState.Done, goal: "Port the plate solver", report: "SolverTests.Solves_Guide_Frame passed, 41 of 41."));
        _state.PutLeg(Leg("TIA1611", "guide", 2, LegState.Flying, goal: "Feed solved frames to the guider") with
        {
            Checklist = [new CheckItem { Text = "Wire the solver in", Done = true }, new CheckItem { Text = "Add GuiderTests", Done = false }],
            GoArounds = 1, Worker = "se1-tia1611-guide", StartedAt = Start - TimeSpan.FromMinutes(20),
        });
        _state.PutLeg(Leg("DIR7", "atlas", 1, LegState.Planned));

        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1611", "TIA1611's PR #63 is ready to merge", "Review passed.", [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1611", Status = "passing" }]);
        _state.RaiseCard(CardBin.Ring, "leg-stalled", "flight/TIA1611/guide", "The guide worker went quiet");
        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/DIR7", "DIR7 is asking for a decision");
        var closed = _state.RaiseCard(CardBin.ForYou, "landed", "flight/TIA1611", "An old thing nobody needs now");
        _state.CloseCard(closed.Id, "dismissed", "ada");
        _state.RaiseCard(CardBin.ForYou, "store", "snapshot", "The store could not write its snapshot");

        _state.PutSession(Session("a1b2c3d4", "se1-tia1611-guide", "TIA1611", SessionState.Waiting, leg: "guide") with { WaitingFor = "permission for Bash", Asking = "Bash: dotnet test" });
        _state.PutSession(Session("b2c3d4e5", "se1-tia1611-solver", "TIA1611", SessionState.Ended, leg: "solver"));
        _state.PutSession(Session("c3d4e5f6", "se1-dir7-plan", "DIR7", SessionState.Working));
    }

    [Fact]
    public void TheFleetRecordsOpenWithGoneStaleAndThePromptPutsStaleFirstUnderNeedsYou()
    {
        Fill();
        _state.PutFlight(Flight("OLD1", issue: 5) with { CreatedAt = Start - TimeSpan.FromHours(30) });

        var records = SummaryRecords.Fleet(_state, Start).ShouldNotBeNull();

        records.ShouldStartWith("Gone stale:");
        records.ShouldContain("- OLD1, open 1 d 6 h: waiting on ");
        records.IndexOf("Records of the node", StringComparison.Ordinal).ShouldBeGreaterThan(records.IndexOf("- OLD1", StringComparison.Ordinal));
        Flat(SummaryPrompt.Fleet(3, records)).ShouldContain("\"Gone stale\", list those first");
    }

    // The prompt wraps its lines for reading; what it says is the words, not where they break.
    private static string Flat(string text) => string.Join(' ', text.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void TheFleetRecordsHaveNoGoneStaleSectionWhenEverythingIsFresh()
    {
        Fill();

        SummaryRecords.Fleet(_state, Start).ShouldNotBeNull().ShouldStartWith("Records of the node");
    }

    [Fact]
    public void TheFleetRecordsHoldEachFlightInPlayWithItsLegsPrReviewCardsAndSessions()
    {
        Fill();

        var records = SummaryRecords.Fleet(_state, Start).ShouldNotBeNull();

        records.ShouldContain("2 flights in play");
        records.ShouldContain("Flight TIA1611: tianwen issue #1611 \"Plate-solve the guide frame\"");
        records.ShouldContain("Flight DIR7: DIR.Lib issue #7");
        records.ShouldNotContain("TIA9");
        records.ShouldContain("PR #63 https://github.com/SharpAstro/tianwen/pull/63, head 4f2c9a01");
        records.ShouldContain("Review: passed");
        records.ShouldContain("read head 9b8a7c6d");
        records.ShouldContain("solver: done");
        records.ShouldContain("SolverTests.Solves_Guide_Frame passed, 41 of 41.");
        records.ShouldContain("guide: flying, 1 go-around, worker se1-tia1611-guide");
        records.ShouldContain("Checklist, 1 of 2 ticked");
        records.ShouldContain("[x] Wire the solver in");
        records.ShouldContain("[ ] Add GuiderTests");
        records.ShouldContain("atlas: planned");
        records.ShouldContain("c1 (ring, pr-ready): TIA1611's PR #63 is ready to merge");
        records.ShouldContain("Choices: Merge (passing)");
        records.ShouldContain("c2 (ring, leg-stalled): The guide worker went quiet");
        records.ShouldContain("c3 (ring, pr-ready): DIR7 is asking for a decision");
        records.ShouldContain("se1-tia1611-guide (worker, leg guide): waiting on Bash: dotnet test");
        records.ShouldContain("se1-dir7-plan (worker): working");

        // Open cards only, and a session that ended is no longer working on it.
        records.ShouldNotContain("An old thing nobody needs now");
        records.ShouldNotContain("se1-tia1611-solver");

        // A card about no flight is not left out of what needs the person.
        records.ShouldContain("Open cards about no flight in play:");
        records.ShouldContain("The store could not write its snapshot");
    }

    [Fact]
    public void AFlightsRecordsAreItsOwnAndKeepWhatItsDoneLegsDid()
    {
        Fill();

        var records = SummaryRecords.Flight(_state, "tia1611", Start).ShouldNotBeNull();

        records.ShouldContain("Records of flight TIA1611");
        records.ShouldNotContain("DIR7");
        records.ShouldNotContain("TIA9");
        records.ShouldContain("TIA1611's PR #63 is ready to merge");
        records.ShouldNotContain("DIR7 is asking for a decision");
        records.ShouldNotContain("The store could not write its snapshot");
        // The fleet shows a done leg as a line; the one flight keeps its goal, since what has been done is what is asked.
        records.ShouldContain("Goal: Port the plate solver");
        SummaryRecords.Fleet(_state, Start)!.ShouldNotContain("Goal: Port the plate solver");
    }

    [Fact]
    public void ALandedFlightCanBeSummarisedButIsNotInPlay()
    {
        Fill();

        SummaryRecords.Flight(_state, "TIA9", Start).ShouldNotBeNull().ShouldContain("landed 2026-10-28 07:00 UTC");
        SummaryRecords.Flight(_state, "NOPE", Start).ShouldBeNull();
    }

    [Fact]
    public void ANodeWithNothingInPlayHasNoRecords()
    {
        SummaryRecords.Fleet(_state, Start).ShouldBeNull();

        _state.PutFlight(Flight("TIA9", FlightState.Landed, issue: 9) with { LandedAt = Start });
        _state.RaiseCard(CardBin.ForYou, Digest.Kind, Verbs.NodeTarget, "Digest: 1 landed");
        SummaryRecords.Fleet(_state, Start).ShouldBeNull();

        _state.RaiseCard(CardBin.Ring, "store", "snapshot", "The store could not write its snapshot");
        SummaryRecords.Fleet(_state, Start).ShouldNotBeNull();
    }

    [Fact]
    public void ARecordNeverCarriesAHashTheNodeDoesNotHold()
    {
        Fill();
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "DrawboardLtd/bullclip-backend", Number = 2479, Title = "Sync the sheet index", Url = "https://github.com/DrawboardLtd/bullclip-backend/pull/2479", Base = "main", Head = OtherPrHead,
            ClonePath = "/c", Worktree = "/c/w", Reviewer = "se1-review-bullclip-backend-2479", State = PrReviewState.Reviewed, Verdict = "changes", StartedAt = Start,
            Report = "One defect, proved by its test.", Findings = [new PrFinding { Path = "src/IndexSync.cs", Line = 84, Body = "The rebuild runs before the commit.", Inline = true }],
        });
        var held = new[] { PrHead, ReviewedHead, OtherPrHead };

        foreach (var records in new[] { SummaryRecords.Fleet(_state, Start)!, SummaryRecords.Flight(_state, "TIA1611", Start)!, SummaryRecords.Flight(_state, "DIR7", Start)! })
        {
            var hashes = Regex.Matches(records, @"\b[0-9a-f]{7,40}\b").Select(m => m.Value).ToList();
            hashes.ShouldAllBe(h => held.Any(full => full.StartsWith(h, StringComparison.Ordinal)), $"a hash the node does not hold:\n{records}");
            // Quoted short, as a person reads a commit; never in full.
            held.ShouldAllBe(full => !records.Contains(full, StringComparison.Ordinal));
        }

        var fleet = SummaryRecords.Fleet(_state, Start)!;
        fleet.ShouldContain("4f2c9a01");
        fleet.ShouldContain("9b8a7c6d");
        fleet.ShouldContain("DrawboardLtd/bullclip-backend#2479");
        fleet.ShouldContain("head 1a2b3c4d");
        fleet.ShouldContain("src/IndexSync.cs:84");

        // A flight with no PR holds no hash to quote.
        Regex.IsMatch(SummaryRecords.Flight(_state, "DIR7", Start)!, @"\b[0-9a-f]{7,40}\b").ShouldBeFalse();
    }

    [Fact]
    public void ALongReportIsCutWithAnEllipsisNotSilently()
    {
        _state.PutFlight(Flight("TIA1611"));
        _state.PutLeg(Leg("TIA1611", "solver", 1, LegState.Failed, report: new string('x', SummaryRecords.MaxReport + 500)));

        var records = SummaryRecords.Flight(_state, "TIA1611", Start)!;

        records.ShouldContain(new string('x', SummaryRecords.MaxReport) + " …");
        records.ShouldNotContain(new string('x', SummaryRecords.MaxReport + 1));
    }

    [Fact]
    public void ThePromptIsFixedInTwoSectionsAndLimitsTheSecond()
    {
        Fill();
        var fleet = SummaryPrompt.Fleet(2, SummaryRecords.Fleet(_state, Start)!);
        var one = SummaryPrompt.Flight("TIA1611", SummaryRecords.Flight(_state, "TIA1611", Start)!);

        foreach (var prompt in new[] { fleet, one })
        {
            prompt.IndexOf("## Needs you", StringComparison.Ordinal).ShouldBeGreaterThan(-1);
            prompt.IndexOf("## Where it is", StringComparison.Ordinal).ShouldBeGreaterThan(prompt.IndexOf("## Needs you", StringComparison.Ordinal));
            Flat(prompt).ShouldContain("write the single word: Nothing.");
            Flat(prompt).ShouldContain("commit hashes, test names and results only from the records; never invent them.");
            Flat(prompt).ShouldContain("Answer in exactly this format and nothing else");
            prompt.IndexOf("Records:", StringComparison.Ordinal).ShouldBeGreaterThan(prompt.IndexOf("## Where it is", StringComparison.Ordinal));
        }
        fleet.ShouldContain("At most 160 words");
        fleet.ShouldContain("about 2 flights in play");
        one.ShouldContain("At most 120 words");
        one.ShouldContain("about the flight TIA1611");
        fleet.ShouldEndWith(SummaryRecords.Fleet(_state, Start)!);
        one.ShouldEndWith(SummaryRecords.Flight(_state, "TIA1611", Start)!);
        SummaryPrompt.FleetWords.ShouldBe(160);
        SummaryPrompt.FlightWords.ShouldBe(120);
    }

    /// <summary>
    /// SE1's snapshot of 2026-11-06 (pilot#797): PIL784 and PIL797 flying, a card about a worker nudged and a card with step counts and
    /// model names in its words, and TIA2000, which landed with its PR merged, but whose leg review-notes-2 still had a worker and a ringing card, c3651.
    /// </summary>
    private CardRow FillSe1OfTheSixthOfNovember()
    {
        _state.PutFlight(Flight("PIL784", issue: 784) with { Repo = "pilot", Title = "The remaining JobTests wait on conditions", PullRequest = 793 });
        _state.PutLeg(Leg("PIL784", "job-tests", 1, LegState.Flying, goal: "Wait on conditions") with
        {
            Checklist = [.. Enumerable.Range(1, 7).Select(n => new CheckItem { Text = $"Step {n}", Done = n <= 6 })], Worker = "se1-pil784-job-tests",
            GoArounds = 2, StartedAt = Start - TimeSpan.FromMinutes(50),
        });
        _state.PutSession(Session("d1d1d1d1", "se1-pil784-job-tests", "PIL784", SessionState.Idle, leg: "job-tests"));
        _state.RaiseCard(CardBin.Ring, "leg-idle", "flight/PIL784/job-tests", "PIL784's job-tests worker is idle at 6 of 7 steps ticked (Opus, high), nudging se1-pilot-node-test-failures",
            "stale bound 45m passed; redo 1, go-around 2");

        _state.PutFlight(Flight("PIL797", issue: 797) with { Repo = "pilot", Title = "Cards and the summary say what happened" });
        _state.PutLeg(Leg("PIL797", "summary-plain", 1, LegState.Flying, goal: "Plain summary") with { Worker = "se1-pil797-summary-plain", StartedAt = Start - TimeSpan.FromMinutes(5) });

        _state.PutFlight(Flight("TIA2000", FlightState.Landed, issue: 2000) with { PullRequest = 512, LandedAt = Start - TimeSpan.FromHours(1) });
        _state.PutLeg(Leg("TIA2000", "review-notes-2", 4, LegState.Flying, goal: "Act on the review notes") with
        {
            Checklist = [.. Enumerable.Range(1, 6).Select(n => new CheckItem { Text = $"Step {n}" })], Worker = "se1-tia2000-review-notes-2", StartedAt = Start - TimeSpan.FromHours(2),
        });
        _state.PutSession(Session("e2e2e2e2", "se1-tia2000-review-notes-2", "TIA2000", SessionState.Idle, leg: "review-notes-2"));
        // Raised after the landing closed the flight's cards: the stale c3651.
        return _state.RaiseCard(CardBin.Ring, "leg-idle", "flight/TIA2000/review-notes-2", "TIA2000's review-notes-2 worker is idle at 0 of 6 steps",
            "Start a fresh worker on review-notes-2");
    }

    [Fact]
    public void AGoldenOfTheSe1SnapshotWithAStaleCardGivesTheModelNothingToAskOfTheLandedFlightAndTheFixedPlainWordsRules()
    {
        var stale = FillSe1OfTheSixthOfNovember();

        var records = SummaryRecords.Fleet(_state, Start).ShouldNotBeNull();
        var prompt = SummaryPrompt.Fleet(2, records);

        // The open card of the landed flight is there only under the heading that makes it a stale card; nothing else of TIA2000 is.
        var landedAt = records.IndexOf(SummaryRecords.LandedCards, StringComparison.Ordinal);
        landedAt.ShouldBeGreaterThan(-1);
        var tail = records[landedAt..];
        var landedSection = tail.Contains("\n\n", StringComparison.Ordinal) ? tail[..tail.IndexOf("\n\n", StringComparison.Ordinal)] : tail;
        landedSection.ShouldContain($"{stale.Id} (ring, leg-idle)");
        var needsEligible = records.Remove(landedAt, landedSection.Length);
        needsEligible.ShouldNotContain("TIA2000");
        needsEligible.ShouldNotContain("review-notes-2");
        needsEligible.ShouldNotContain(stale.Id + " ");
        // Only the flights in play are records; the landed flight's leg and worker are not.
        records.ShouldContain("Flight PIL784:");
        records.ShouldContain("Flight PIL797:");
        records.ShouldNotContain("Flight TIA2000:");
        records.ShouldNotContain("se1-tia2000-review-notes-2");
        // The model is told, in words, that such a card is never a need.
        prompt.ShouldContain("Say nothing about a flight that has landed");
        prompt.ShouldContain("never a need");

        // The prompt's own words, pinned: one plain sentence per item saying what to do, the internal words banned, Where it is a short list.
        prompt[..prompt.IndexOf("Records:", StringComparison.Ordinal)].ShouldBe(
            """
            You summarise flights of background coding workers for the person who runs them. Below are records
            about 2 flights in play and what else waits on the person, exactly as the node holds them. Answer in exactly this format and nothing else:

            ## Needs you
            - One bullet per thing that stays stuck unless the person acts: a question only they can answer, a
              permission prompt, a card in the ring, a PR waiting for their merge, a leg that failed, a worker
              that stopped or waits. Each bullet is one plain sentence that names the flight (or leg) and says
              what the person should do, such as "Merge PIL784's PR #812" or "Tell PIL797's worker to carry on".
              If there is nothing, write the single word: Nothing. When the records open with "Gone stale", list
              those first, oldest first, each with how long it has waited and what to do.

            ## Where it is
            At most 160 words as a short list, one line per flight, the one that matters most first (what needs the
            person, then what is flying, then what is held or planned), each line saying in plain words what has
            been done or what is happening now. Quote commit hashes, test names and results only from the records;
            never invent them.

            Rules for every line of both sections:
            - Plain words a person who did not watch the node would understand. Never use the node's internal words: ticked, stale bound, redo N, go-around N, model and effort names (Opus, Sonnet, Haiku, high, medium), step counts ("3 of 7 steps"), session names ("se1-pil797-summary-plain").
              Say "restarted twice", not "go-around 2"; say "no worker is running", not "0 of 6 steps ticked".
            - Say what to do, not what the record says. Do not restate a card's title or its detail; one sentence is enough.
            - Say nothing about a flight that has landed, its legs, its workers or its PR. A landed flight needs no one, even if the
              records still show an open card about it: that card is stale, never a need.
            - Do not count flights, steps or cards. Do not repeat a line under both headings.

            ## Resolved
            - One line per open card in the records whose cause is gone (its PR merged or closed, its checks green
              again, its conflict gone, its review given, its flight landed), written as its id alone, such as c12,
              never a card that waits for the person's decision. The node checks each against what it holds before it
              closes anything. If there is none, write the single word: None

            """);
    }

    [Fact]
    public void ARecordsWithOnlyAStaleCardOfALandedFlightIsStillMadeSoItCanBeProposedResolved()
    {
        _state.PutFlight(Flight("TIA2000", FlightState.Landed, issue: 2000));
        var stale = _state.RaiseCard(CardBin.Ring, "leg-idle", "flight/TIA2000/review-notes-2", "A leg is idle");

        var records = SummaryRecords.Fleet(_state, Start).ShouldNotBeNull();

        records.ShouldContain(SummaryRecords.LandedCards);
        records.ShouldContain($"{stale.Id} (ring, leg-idle)");
        records.ShouldNotContain("Open cards about no flight in play");
    }

    [Fact]
    public async Task SummarisingTheFleetAsksOnceAndKeepsTheAnswerWithItsTimeTokensAndCost()
    {
        Fill();

        var (row, error) = await _summaries.MakeAsync(SummaryRow.FleetScope, Ct);

        error.ShouldBeNull();
        _model.Prompts.ShouldHaveSingleItem().ShouldBe(SummaryPrompt.Fleet(2, SummaryRecords.Fleet(_state, Start)!));
        row.ShouldBe(new SummaryRow { Scope = "*", Markdown = FakeSummaryModel.Fine.Text, MadeAt = Start, Tokens = 1234, Cost = 0.0021 });
        _state.Summaries().ShouldHaveSingleItem().ShouldBe(row);
        _state.Snapshot().Summaries.ShouldHaveSingleItem().ShouldBe(row);
    }

    [Fact]
    public async Task EachScopeKeepsItsOwnAndANewerOneReplacesTheOlder()
    {
        Fill();
        await _summaries.MakeAsync(SummaryRow.FleetScope, Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        _model.Answer = new SummaryAnswer("## Needs you\n- c1: merge it.\n\n## Where it is\nGuide is flying.", 900, null);
        await _summaries.MakeAsync("TIA1611", Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        _model.Answer = new SummaryAnswer("## Needs you\nNothing.\n\n## Where it is\nAll quiet.", 700, 0.001);
        await _summaries.MakeAsync(SummaryRow.FleetScope, Ct);

        var kept = _state.Summaries();

        kept.Select(s => s.Scope).ShouldBe(["*", "TIA1611"]);
        kept[0].Markdown.ShouldContain("All quiet.");
        kept[0].MadeAt.ShouldBe(Start + TimeSpan.FromMinutes(10));
        (kept[0].Tokens, kept[0].Cost).ShouldBe((700L, 0.001));
        (kept[1].Tokens, kept[1].Cost, kept[1].MadeAt).ShouldBe((900L, null, Start + TimeSpan.FromMinutes(5)));
        _model.Prompts[1].ShouldContain("At most 120 words");
        _model.Prompts[1].ShouldContain("Records of flight TIA1611");
    }

    [Fact]
    public async Task ASummaryIsJournalledAndSurvivesARestart()
    {
        Fill();
        await _summaries.MakeAsync("DIR7", Ct);

        var after = new NodeState(_journal, _time);

        after.Summaries().ShouldBe(_state.Summaries());
        after.Summaries().ShouldHaveSingleItem().Scope.ShouldBe("DIR7");
    }

    [Fact]
    public async Task ASummaryReachesEveryClientAsADeltaAndInTheSnapshot()
    {
        Fill();
        var mirror = new NodeMirror();
        mirror.Apply(_state.Snapshot());
        var sink = new ListSink();
        using var subscription = _state.Subscribe(since: mirror.Seq, stream: mirror.Stream, sink);

        await _summaries.MakeAsync("TIA1611", Ct);

        foreach (var json in sink.Messages)
        {
            mirror.Apply(System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.NodeEvent)!);
        }
        mirror.Summaries["TIA1611"].ShouldBe(_state.Summaries().Single());
        var late = new NodeMirror();
        late.Apply(_state.Snapshot());
        late.Summaries.Values.ShouldBe(_state.Summaries());
    }

    [Fact]
    public async Task AFleetWithNothingInPlayIsAnsweredWithNoCall()
    {
        var (row, error) = await _summaries.MakeAsync(SummaryRow.FleetScope, Ct);

        error.ShouldBeNull();
        _model.Prompts.ShouldBeEmpty();
        row.ShouldNotBeNull().Markdown.ShouldBe(Summaries.EmptyFleet);
        (row.Tokens, row.Cost).ShouldBe((0L, null));
        row.Markdown.ShouldContain("## Needs you\nNothing.");
        _state.Summaries().ShouldHaveSingleItem().ShouldBe(row);
    }

    [Fact]
    public async Task AFailedCallKeepsNothingAndSaysWhy()
    {
        Fill();
        _model.Fails = new InvalidOperationException("claude exited 1: not logged in");

        var (row, error) = await _summaries.MakeAsync(SummaryRow.FleetScope, Ct);

        row.ShouldBeNull();
        error.ShouldBe("The summary could not be made: claude exited 1: not logged in");
        _state.Summaries().ShouldBeEmpty();

        // And it can be asked again: a failure does not leave the scope marked as busy.
        _model.Fails = null;
        (await _summaries.MakeAsync(SummaryRow.FleetScope, Ct)).Error.ShouldBeNull();
    }

    [Fact]
    public async Task AScopeIsNeverSummarisedTwiceAtOnce()
    {
        Fill();
        _model.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _summaries.MakeAsync("TIA1611", Ct);

        var (row, error) = await _summaries.MakeAsync("TIA1611", Ct);
        row.ShouldBeNull();
        error.ShouldBe("TIA1611 is being summarised already");
        _model.Gate.SetResult();
        (await first).Error.ShouldBeNull();
        _model.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheVerbIsRefusedForAnUnknownFlightOrATargetItDoesNotTake()
    {
        Fill();

        var unknown = await _summaries.RunAsync(new CommandRequest { Verb = Verbs.Summarise, Target = "flight/NOPE", Who = "ada" }, Ct);
        var leg = await _summaries.RunAsync(new CommandRequest { Verb = Verbs.Summarise, Target = "flight/TIA1611/guide", Who = "ada" }, Ct);
        var session = await _summaries.RunAsync(new CommandRequest { Verb = Verbs.Summarise, Target = "session/a1b2c3d4", Who = "ada" }, Ct);

        unknown.Error.ShouldBe("There is no flight NOPE");
        leg.Error.ShouldNotBeNull().ShouldContain("summarise takes the target node or flight/<code>");
        session.Error.ShouldNotBeNull().ShouldContain("summarise takes the target node or flight/<code>");
        _model.Prompts.ShouldBeEmpty();
        _state.Summaries().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheVerbReturnsTheMarkdownAndAFlightCodeMayBeInAnyCase()
    {
        Fill();

        var result = await _summaries.RunAsync(new CommandRequest { Verb = Verbs.Summarise, Target = "flight/tia1611", Who = "ada" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe(FakeSummaryModel.Fine.Text);
        _state.Summaries().ShouldHaveSingleItem().Scope.ShouldBe("TIA1611");
    }

    [Fact]
    public async Task ASummariseOverTheSocketKeepsItOnTheNodeAndSendsItOnTheFeed()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight("TIA1611"));
        node.State.PutLeg(Leg("TIA1611", "solver", 1, LegState.Flying));
        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        var mirror = new NodeMirror();
        mirror.Apply((await events.ReadAsync(Ct))!);

        var sent = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Summarise, Target = Verbs.NodeTarget, Who = "tests" }, Ct);

        sent.Success.ShouldBeTrue(sent.Error);
        sent.Response!.Ok.ShouldBeTrue(sent.Response.Error);
        sent.Response.Made.ShouldBe(FakeSummaryModel.Fine.Text);
        node.Summary.Prompts.ShouldHaveSingleItem().ShouldContain("Flight TIA1611");
        while (mirror.Seq < node.State.Seq)
        {
            mirror.Apply((await events.ReadAsync(Ct))!);
        }
        var kept = mirror.Summaries["*"];
        (kept.Markdown, kept.Tokens, kept.Cost).ShouldBe((FakeSummaryModel.Fine.Text, 1234L, 0.0021));
        node.State.Summaries().ShouldHaveSingleItem().ShouldBe(kept);
    }

    [Fact]
    public void ClaudesJsonAnswerIsReadForItsTextTokensAndCost()
    {
        var answer = SummaryAnswer.Parse("""
            {"type":"result","subtype":"success","is_error":false,"duration_ms":4100,"result":"  ## Needs you\nNothing.\n\n## Where it is\nQuiet.\n","total_cost_usd":0.0042,
             "usage":{"input_tokens":10,"cache_creation_input_tokens":2000,"cache_read_input_tokens":300,"output_tokens":90,"service_tier":"standard"}}
            """);

        answer.Text.ShouldBe("## Needs you\nNothing.\n\n## Where it is\nQuiet.");
        answer.Tokens.ShouldBe(2400);
        answer.Cost.ShouldBe(0.0042);
        SummaryAnswer.Parse("""{"is_error":false,"result":"ok"}""").ShouldBe(new SummaryAnswer("ok", 0, null));
    }

    [Theory]
    [InlineData("", "claude printed nothing")]
    [InlineData("Error: not logged in", "claude did not answer in JSON: Error: not logged in")]
    [InlineData("[]", "claude did not answer in JSON: []")]
    [InlineData("""{"is_error":true,"result":"Credit balance is too low"}""", "claude failed: Credit balance is too low")]
    [InlineData("""{"is_error":false,"result":"   "}""", "claude answered nothing")]
    [InlineData("""{"is_error":false}""", "claude answered nothing")]
    public void AnAnswerThatIsNoAnswerSaysWhy(string output, string why) =>
        Should.Throw<InvalidOperationException>(() => SummaryAnswer.Parse(output)).Message.ShouldBe(why);

    [Fact]
    public void TheCallIsOneSummaryPromptWithNoToolsNoMcpAndNoSavedSession()
    {
        var args = new ClaudeSummaryModel(Path.GetTempPath(), new RoleModels(null)).Arguments;

        args.ShouldContain("-p");
        args[(args.ToList().IndexOf("--model") + 1)].ShouldBe("sonnet");
        args[(args.ToList().IndexOf("--tools") + 1)].ShouldBe("");
        args.ShouldContain("--strict-mcp-config");
        args.ShouldNotContain("--mcp-config");
        args.ShouldContain("--no-session-persistence");
        args[(args.ToList().IndexOf("--output-format") + 1)].ShouldBe("json");
    }

    private sealed class ListSink : INodeEventSink
    {
        public List<byte[]> Messages { get; } = [];

        public int Capacity => 1000;

        public bool TryWrite(byte[] message)
        {
            Messages.Add(message);
            return true;
        }

        public void Overflowed()
        {
        }
    }
}
