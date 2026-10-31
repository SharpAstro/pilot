using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The token ledger: every message once, against its flight, leg, role, model, effort and round; kept across a restart; read as <c>pilot spend</c>.</summary>
public sealed class SpendTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-18T10:00:00Z");

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly ManualTime _time = new ManualTime(Start);
    private readonly NodeState _state;
    private readonly string _ledgerFile;

    public SpendTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _ledgerFile = Path.Combine(_profile.Folder, "spend.jsonl");
    }

    public void Dispose() => _profile.Dispose();

    private SessionTracker Tracker(SpendLedger ledger) =>
        new SessionTracker(_state, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance) { Ledger = ledger };

    private static LegRow Leg(int goArounds) =>
        new() { Flight = "PIL515", Name = "ledger", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, GoArounds = goArounds };

    private SpendEntry Entry(SpendLedger ledger, string id) => ledger.Entries().Single(e => e.Id == id);

    [Fact]
    public async Task EveryMessageIsCountedOnceByIdSubagentsIncludedAndAttributed()
    {
        _state.PutLeg(Leg(goArounds: 2));
        var ledger = new SpendLedger();
        var tracker = Tracker(ledger);
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "du1-pil515-ledger", "working", pid: 4242, status: "busy"),
            AgentRows.Background(AgentRows.SessionB, "du1-fix-pilot-515", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionC, "du1-watchtower", "working", pid: 4444, status: "idle"));
        var worker = _profile.TranscriptPath(AgentRows.SessionA);
        // A message streamed over two content blocks is two lines with one id; the later line carries the final usage.
        _profile.Append(worker,
            Lines.Assistant("m1", input: 10, cacheRead: 1_000, output: 5, model: "claude-sonnet-5-5", effort: "high", at: "2026-10-18T09:00:00.000Z"),
            Lines.Assistant("m1", input: 10, cacheRead: 1_000, output: 50, model: "claude-sonnet-5-5", effort: "high", at: "2026-10-18T09:00:01.000Z"),
            Lines.Assistant("m2", input: 20, cacheCreation: 400, output: 100, model: "claude-sonnet-5-5", effort: "high", at: "2026-10-18T09:05:00.000Z"));
        // The subagent repeats m2 (the same message seen from its side) and adds one of its own.
        _profile.Append(_profile.SubagentPath(AgentRows.SessionA, "ag1"),
            Lines.Assistant("m2", input: 20, cacheCreation: 400, output: 100, model: "claude-sonnet-5-5", effort: "high", at: "2026-10-18T09:05:00.000Z", sidechain: true),
            Lines.Assistant("s1", input: 7, output: 70, model: "claude-haiku-5-5", effort: null, at: "2026-10-18T09:06:00.000Z", sidechain: true));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionB), Lines.Assistant("f1", input: 3, output: 30, at: "2026-10-18T09:10:00.000Z"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionC), Lines.Assistant("w1", input: 1, output: 10, at: "2026-10-18T09:11:00.000Z"));

        await tracker.PollAsync(Ct);
        await tracker.PollAsync(Ct);

        ledger.Entries().Select(e => e.Id).Order().ShouldBe(["f1", "m1", "m2", "s1", "w1"]);
        var m1 = Entry(ledger, "m1");
        m1.Usage.ShouldBe(new MessageUsage(10, 0, 1_000, 50));
        m1.Session.ShouldBe(AgentRows.SessionA);
        (m1.Flight, m1.Leg, m1.Role, m1.Model, m1.Effort, m1.Round).ShouldBe(("PIL515", "ledger", "worker", "claude-sonnet-5-5", "high", 2));
        m1.At.ShouldBe(DateTimeOffset.Parse("2026-10-18T09:00:01Z"));
        Entry(ledger, "m2").Usage.ShouldBe(new MessageUsage(20, 400, 0, 100));
        var sub = Entry(ledger, "s1");
        (sub.Session, sub.Flight, sub.Role, sub.Model).ShouldBe((AgentRows.SessionA, "PIL515", "worker", "claude-haiku-5-5"));
        var fixer = Entry(ledger, "f1");
        (fixer.Role, fixer.Flight, fixer.Name).ShouldBe(("fixer", null, "du1-fix-pilot-515"));
        Entry(ledger, "w1").Role.ShouldBe("watchtower");
    }

    [Fact]
    public async Task ASessionKeepsTheRoundItStartedInAndPlannersAndReviewersAreTold()
    {
        _state.PutLeg(Leg(goArounds: 1));
        var ledger = new SpendLedger();
        var tracker = Tracker(ledger);
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "du1-pil515-ledger", "working", pid: 4242, status: "busy"),
            AgentRows.Background(AgentRows.SessionB, "du1-pil515-review", "working", pid: 4343, status: "busy"),
            AgentRows.Background(AgentRows.SessionC, "du1-pil515-plan", "working", pid: 4444, status: "busy"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("a1", input: 1, at: "2026-10-18T09:00:00.000Z"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionB), Lines.Assistant("b1", input: 1, at: "2026-10-18T09:00:00.000Z"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionC), Lines.Assistant("c1", input: 1, at: "2026-10-18T09:00:00.000Z"));
        await tracker.PollAsync(Ct);

        // The leg goes around again; the session already flying keeps its round, a later message of it too.
        _state.PutLeg(Leg(goArounds: 2));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("a2", input: 1, at: "2026-10-18T09:30:00.000Z"));
        await tracker.PollAsync(Ct);

        Entry(ledger, "a1").Round.ShouldBe(1);
        Entry(ledger, "a2").Round.ShouldBe(1);
        Entry(ledger, "b1").Role.ShouldBe("reviewer");
        Entry(ledger, "c1").Role.ShouldBe("planner");
        Entry(ledger, "b1").Leg.ShouldBeNull();
    }

    [Fact]
    public async Task TheLedgerIsReadBackFromItsFileAndAMessageIsNeverWrittenTwice()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "du1-pil515-ledger", "working", pid: 4242, status: "busy"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA),
            Lines.Assistant("m1", input: 10, output: 5, at: "2026-10-18T09:00:00.000Z"),
            Lines.Assistant("m2", input: 20, output: 7, at: "2026-10-18T09:05:00.000Z"));
        await Tracker(new SpendLedger(_ledgerFile)).PollAsync(Ct);
        var written = File.ReadAllLines(_ledgerFile);
        written.Length.ShouldBe(2);

        // The node starts again: a new tracker reads the same transcripts, and the ledger holds what it held.
        var restarted = new SpendLedger(_ledgerFile);
        restarted.Entries().Select(e => (e.Id, e.Flight, e.Role)).Order().ShouldBe([("m1", "PIL515", "worker"), ("m2", "PIL515", "worker")]);
        await Tracker(restarted).PollAsync(Ct);
        File.ReadAllLines(_ledgerFile).ShouldBe(written);

        // And what came after is appended, with the first two untouched.
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m3", input: 1, at: "2026-10-18T09:09:00.000Z"));
        await Tracker(new SpendLedger(_ledgerFile)).PollAsync(Ct);
        File.ReadAllLines(_ledgerFile).Length.ShouldBe(3);
        new SpendLedger(_ledgerFile).Entries().Count.ShouldBe(3);
    }

    [Fact]
    public async Task ALedgerOutlivesTheDayItsSessionIsListed()
    {
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "du1-pil515-ledger", "working", pid: 4242, status: "busy"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 10, at: "2026-10-18T09:00:00.000Z"));
        var ledger = new SpendLedger(_ledgerFile);
        var tracker = Tracker(ledger);
        await tracker.PollAsync(Ct);

        // The session ends and is dropped after the retention: the row goes, the ledger keeps the message.
        _agents.Json = AgentRows.List();
        await tracker.PollAsync(Ct);
        _time.Advance(SessionTrackerOptions.DefaultEndedRetention + TimeSpan.FromHours(1));
        await tracker.PollAsync(Ct);

        _state.Sessions().ShouldBeEmpty();
        new SpendLedger(_ledgerFile).Entries().ShouldHaveSingleItem().Id.ShouldBe("m1");
    }

    private static SpendLedger Filled()
    {
        var ledger = new SpendLedger();
        var role = new SessionRoleInfo(SessionRole.Worker, "PIL1", "a", null);
        void Put(string id, string flight, string roleName, DateTimeOffset at, long output) =>
            ledger.Record("s-" + id, "n", role with { Flight = flight }, roleName, 0, [new SpendMessage(id, new MessageUsage(0, 0, 0, output), at, "m", "low")], at);
        var noon = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Put("old", "PIL1", "worker", noon.AddDays(-8), 1_000);
        Put("week", "PIL1", "worker", noon.AddDays(-3), 100);
        Put("morning", "PIL2", "reviewer", noon.AddHours(-6), 100);
        Put("recent", "PIL3", "worker", noon.AddMinutes(-90), 100);
        return ledger;
    }

    [Fact]
    public void WindowsAreTheAccountsWhenItsResetIsKnownElseCountedBackFromNow()
    {
        var ledger = Filled();
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var usage = new UsageRow
        {
            ReadAt = now,
            FiveHour = new UsageWindow { UsedPercent = 10, ResetsAt = now.AddHours(1) },
            SevenDay = new UsageWindow { UsedPercent = 10, ResetsAt = now.AddDays(2) },
        };

        var five = ledger.Report("5h", now, TimeZoneInfo.Utc, usage).ShouldNotBeNull();
        (five.From, five.To, five.FromAccount).ShouldBe((now.AddHours(-4), now.AddHours(1), true));
        five.Messages.ShouldBe(1);

        // Without a reset, or with one already past, it is the span back from now.
        var counted = ledger.Report("5h", now, TimeZoneInfo.Utc, null).ShouldNotBeNull();
        (counted.From, counted.To, counted.FromAccount).ShouldBe((now.AddHours(-5), now, false));
        ledger.Report("5h", now, TimeZoneInfo.Utc, usage with { FiveHour = new UsageWindow { UsedPercent = 1, ResetsAt = now.AddMinutes(-1) } })!.From.ShouldBe(now.AddHours(-5));

        // The account's week began five days ago, so the entry three days back is in it and the one eight days back is not.
        var week = ledger.Report("week", now, TimeZoneInfo.Utc, usage).ShouldNotBeNull();
        (week.From, week.To).ShouldBe((now.AddDays(-5), now.AddDays(2)));
        week.Messages.ShouldBe(3);
        ledger.Report("week", now, TimeZoneInfo.Utc, null)!.Messages.ShouldBe(3);

        // Today is the local day: 06:00 and 10:30 are in it in UTC, but not for a zone 13 h ahead, whose day began at 11:00 UTC.
        var today = ledger.Report("today", now, TimeZoneInfo.Utc, null).ShouldNotBeNull();
        (today.From, today.To, today.Messages).ShouldBe((new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), 2));
        var ahead = TimeZoneInfo.CreateCustomTimeZone("ahead", TimeSpan.FromHours(13), "ahead", "ahead");
        var there = ledger.Report("today", now, ahead, null).ShouldNotBeNull();
        there.From.ShouldBe(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(13)));
        there.Messages.ShouldBe(0);

        ledger.Report("fortnight", now, TimeZoneInfo.Utc, null).ShouldBeNull();
    }

    [Fact]
    public void SharesAddUpToOneHundredByFlightAndByRole()
    {
        var ledger = new SpendLedger();
        var at = new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero);
        var n = 0;
        foreach (var (flight, roleName) in new[] { ("PIL1", "worker"), ("PIL2", "worker"), ("PIL3", "reviewer"), (null as string, "watchtower") })
        {
            ledger.Record("s" + n, "n", new SessionRoleInfo(SessionRole.Worker, flight, null, null), roleName, 0,
                [new SpendMessage("m" + n++, new MessageUsage(100, 0, 0, 0), at, "m", null)], at);
        }

        var report = ledger.Report("today", at.AddMinutes(5), TimeZoneInfo.Utc, null).ShouldNotBeNull();

        report.Flights.Select(r => r.Key).Order().ShouldBe(["-", "PIL1", "PIL2", "PIL3"]);
        report.Flights.Sum(r => r.Share).ShouldBe(100, 1e-9);
        report.Flights.ShouldAllBe(r => r.Share == 25);
        report.Roles.Sum(r => r.Share).ShouldBe(100, 1e-9);
        report.Roles.Select(r => (r.Key, r.Share)).ShouldBe([("worker", 50d), ("reviewer", 25d), ("watchtower", 25d)]);

        // Thirds do not divide: the tenths are shared out so the rows still make 100.
        var thirds = new SpendLedger();
        for (var i = 0; i < 3; i++)
        {
            thirds.Record("t" + i, "n", new SessionRoleInfo(SessionRole.Worker, "F" + i, null, null), "worker", 0, [new SpendMessage("t" + i, new MessageUsage(1, 0, 0, 0), at, "m", null)], at);
        }
        thirds.Report("today", at, TimeZoneInfo.Utc, null)!.Flights.Sum(r => r.Share).ShouldBe(100, 1e-9);
        new SpendLedger().Report("today", at, TimeZoneInfo.Utc, null)!.Flights.ShouldBeEmpty();
    }

    [Fact]
    public async Task PilotSpendShowsTheWindowsEdgesAndSharesAndTheLedgerSurvivesTheNodesRestart()
    {
        var node = await NodeHarness.StartAsync(Ct);
        string folder = node.Folder;
        try
        {
            var now = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Path.Combine(folder, "claude", "projects", "proj-a"));
            node.Agents.Json = AgentRows.List(
                AgentRows.Background(AgentRows.SessionA, "du1-pil515-ledger", "working", pid: 4242, status: "busy"),
                AgentRows.Background(AgentRows.SessionB, "du1-fix-pilot-515", "working", pid: 4343, status: "busy"));
            string Stamp(TimeSpan ago) => (now - ago).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            _profile.Append(Path.Combine(folder, "claude", "projects", "proj-a", AgentRows.SessionA + ".jsonl"),
                Lines.Assistant("m1", output: 300, at: Stamp(TimeSpan.FromSeconds(30))),
                Lines.Assistant("m2", output: 100, at: Stamp(TimeSpan.FromSeconds(20))));
            _profile.Append(Path.Combine(folder, "claude", "projects", "proj-a", AgentRows.SessionB + ".jsonl"),
                Lines.Assistant("f1", output: 200, at: Stamp(TimeSpan.FromSeconds(10))));
            await WaitForAsync(() => node.App.Services.GetRequiredService<SpendLedger>().Entries().Count == 3);

            var shown = await Spend(node, "--window", "5h");
            shown.ShouldContain("by flight");
            shown.ShouldContain("by role");

            await node.StopKeepingFolderAsync();
            node = await NodeHarness.StartAsync(Ct, folder: folder);
            // Nothing is listed now, and the transcripts are not read: the ledger alone answers.
            foreach (var window in new[] { "5h", "today", "week" })
            {
                var report = (await node.Client.GetSpendAsync(window, Ct)).Response.ShouldNotBeNull();
                var range = $"{report.From.ToLocalTime():yyyy-MM-dd HH:mm} to {report.To.ToLocalTime():yyyy-MM-dd HH:mm}";
                var text = await Spend(node, "--window", window);

                // The two sessions of the test node are in every window: the clock is the machine's and they are seconds old.
                // (Across local midnight the today window may miss them; the run is then repeated by the next one.)
                if (window != "today" || report.Messages > 0)
                {
                    report.Messages.ShouldBe(3);
                    report.Flights.Select(r => (r.Key, r.Tokens, r.Share)).ShouldBe([("PIL515", 400, 66.7), ("-", 200, 33.3)]);
                    report.Roles.Select(r => (r.Key, r.Tokens, r.Share)).ShouldBe([("worker", 400, 66.7), ("fixer", 200, 33.3)]);
                    text.ShouldContain("by flight, " + range);
                    text.ShouldContain("by role, " + range);
                    text.ShouldContain("PIL515");
                    text.ShouldContain("66.7 %");
                }
                text.ShouldStartWith("spend, ");
                text.ShouldContain(range);
            }

            (await Spend(node, "--window", "week", "--by", "role")).ShouldNotContain("by flight");
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    [Fact]
    public async Task PilotSpendRefusesAWindowThatIsNone()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        (await node.Client.GetSpendAsync("fortnight", Ct)).Success.ShouldBeFalse();
    }

    private static async Task<string> Spend(NodeHarness node, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "spend", .. args], node.Folder, output, error, Ct);
        exit.ShouldBe(0, error.ToString() + output);
        return output.ToString();
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var tries = 0; tries < 100 && !done(); tries++)
        {
            await Task.Delay(100, Ct);
        }
        done().ShouldBeTrue();
    }
}
