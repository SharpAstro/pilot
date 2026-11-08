using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The black box keeps every decision: recorded through the state, read back by route and by <c>pilot decisions</c>, and still there after a restart.</summary>
public sealed class DecisionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2d-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            TestDirs.DeleteTree(_folder);
        }
    }

    private static DecisionRow Record(NodeState state, string subject, string kind, string choice = "yes", string by = "autopilot-1", string byKind = DecisionByKinds.Autopilot) =>
        state.RecordDecision(subject, kind, $"Which for {subject}?", ["yes", "no"], choice, by, byKind, " because it is so ");

    [Fact]
    public void DecisionRecordedThroughTheStateComesBackWithEveryField()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);

        var made = Record(state, "flight/PIL583", DecisionKinds.Design, "no", "sebastian", DecisionByKinds.Person);

        var back = state.Decisions().ShouldHaveSingleItem();
        Same(back, made);
        back.Subject.ShouldBe("flight/PIL583");
        back.Kind.ShouldBe("design");
        back.Question.ShouldBe("Which for flight/PIL583?");
        back.Options.ShouldBe(["yes", "no"]);
        back.Choice.ShouldBe("no");
        back.By.ShouldBe("sebastian");
        back.ByKind.ShouldBe("person");
        back.Reason.ShouldBe("because it is so");
        back.Seq.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task DecisionRoundTripsThroughTheStreamsJson()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var made = Record(state, "pr/acme/widget#7", DecisionKinds.Merge);

        var json = System.Text.Json.JsonSerializer.Serialize<NodeEvent>(new DecisionEvent { Seq = made.Seq, At = made.At, Decision = made }, PilotJsonContext.Default.NodeEvent);

        json.ShouldContain("\"kind\":\"decision\"");
        Same(((DecisionEvent)System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.NodeEvent)!).Decision, made);
        await Task.CompletedTask;
    }

    [Fact]
    public void DecisionSurvivesARestartOverTheSameDataFolderAndAFileJournalCompaction()
    {
        DecisionRow first;
        using (var journal = new FileJournal(_folder, maxDeltas: 4))
        {
            var state = new NodeState(journal, TimeProvider.System);
            first = Record(state, "flight/PIL1", DecisionKinds.Design);
            // Enough deltas for the journal to compact more than once: the decision is in no snapshot.
            for (var i = 0; i < 12; i++)
            {
                state.Execute(new CommandRequest { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [$"n{i}"], Who = "tests" });
            }
            Record(state, "flight/PIL2", DecisionKinds.Merge);
            journal.CompactionError.ShouldBeNull();
        }

        File.Exists(Path.Combine(_folder, FileJournal.DecisionsFile)).ShouldBeTrue();

        using var again = new FileJournal(_folder, maxDeltas: 4);
        var restarted = new NodeState(again, TimeProvider.System);
        restarted.Decisions().Select(d => d.Subject).ShouldBe(["flight/PIL1", "flight/PIL2"]);
        Same(restarted.Decisions().First(), first);

        // And a third start still has them, once each.
        Record(restarted, "flight/PIL3", DecisionKinds.Dismiss);
        again.Dispose();
        using var third = new FileJournal(_folder, maxDeltas: 4);
        new NodeState(third, TimeProvider.System).Decisions().Select(d => d.Subject).ShouldBe(["flight/PIL1", "flight/PIL2", "flight/PIL3"]);
    }

    [Fact]
    public void DecisionsFileLineCutByACrashIsDroppedOnLoad()
    {
        using (var journal = new FileJournal(_folder))
        {
            Record(new NodeState(journal, TimeProvider.System), "flight/PIL1", DecisionKinds.Design);
        }
        File.AppendAllText(Path.Combine(_folder, FileJournal.DecisionsFile), "{\"subject\":\"flight/PI");

        using var again = new FileJournal(_folder);
        new NodeState(again, TimeProvider.System).Decisions().ShouldHaveSingleItem().Subject.ShouldBe("flight/PIL1");
    }

    [Fact]
    public void DecisionsFilterByFlightKindAndSince()
    {
        var time = new StepTime(DateTimeOffset.Parse("2026-11-01T10:00:00Z"));
        var state = new NodeState(new InMemoryJournal(), time);
        Record(state, "flight/PIL1", DecisionKinds.Design);
        time.Advance(TimeSpan.FromHours(2));
        Record(state, "card/design/PIL1", DecisionKinds.Design);
        Record(state, "flight/PIL2", DecisionKinds.Merge);
        Record(state, "flight/PIL10", DecisionKinds.Merge);

        state.Decisions(flight: "PIL1").Select(d => d.Subject).ShouldBe(["flight/PIL1"]);
        state.Decisions(kind: "design").Count().ShouldBe(2);
        state.Decisions(since: time.GetUtcNow()).Select(d => d.Subject).ShouldBe(["card/design/PIL1", "flight/PIL2", "flight/PIL10"]);
        state.Decisions(flight: "PIL2", kind: "design").ShouldBeEmpty();
    }

    [Fact]
    public async Task DecisionsRouteFiltersByFlightKindAndSince()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        Record(node.State, "flight/PIL1", DecisionKinds.Design);
        Record(node.State, "flight/PIL2", DecisionKinds.Merge);
        Record(node.State, "flight/PIL2", DecisionKinds.Design);

        (await node.Client.GetDecisionsAsync(null, null, null, Ct)).Response!.Decisions.Length.ShouldBe(3);
        (await node.Client.GetDecisionsAsync("PIL2", null, null, Ct)).Response!.Decisions.Select(d => d.Kind).ShouldBe(["merge", "design"]);
        (await node.Client.GetDecisionsAsync(null, "design", null, Ct)).Response!.Decisions.Select(d => d.Subject).ShouldBe(["flight/PIL1", "flight/PIL2"]);
        (await node.Client.GetDecisionsAsync(null, null, before, Ct)).Response!.Decisions.Length.ShouldBe(3);
        (await node.Client.GetDecisionsAsync(null, null, DateTimeOffset.UtcNow.AddHours(1), Ct)).Response!.Decisions.ShouldBeEmpty();
    }

    [Fact]
    public async Task PilotDecisionsKindDesignListsOnlyDesignDecisions()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Record(node.State, "flight/PIL1", DecisionKinds.Design, "go ahead", "sebastian", DecisionByKinds.Person);
        Record(node.State, "flight/PIL2", DecisionKinds.Merge, "merge now");

        var text = await Run(node, "--kind", "design");

        text.ShouldContain("flight/PIL1");
        text.ShouldContain("go ahead of [yes, no]");
        text.ShouldContain("by sebastian (person)");
        text.ShouldContain("because it is so");
        text.ShouldNotContain("flight/PIL2");
        text.ShouldNotContain("merge now");

        var json = await Run(node, "--kind", "design", "--json");
        System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.DecisionReport)!.Decisions.ShouldHaveSingleItem().Subject.ShouldBe("flight/PIL1");

        (await Run(node, "--since", "2h")).ShouldContain("flight/PIL2");
        (await Run(node, "--flight", "PIL9")).ShouldContain("No decisions");
    }

    [Theory]
    [InlineData("2h", 2 * 60)]
    [InlineData("30m", 30)]
    [InlineData("1d", 24 * 60)]
    public void SinceReadsASpanBackFromNow(string text, int minutes)
    {
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00+00:00");

        PilotCommand.ParseSince(text, now).ShouldBe(now.AddMinutes(-minutes));
        PilotCommand.ParseSince("not a time", now).ShouldBeNull();
        PilotCommand.ParseSince("2026-10-27T09:00:00+00:00", now).ShouldBe(DateTimeOffset.Parse("2026-10-27T09:00:00+00:00"));
    }

    /// <summary>Record equality compares the options array by reference, so compare it by value here.</summary>
    private static void Same(DecisionRow actual, DecisionRow expected)
    {
        actual.Options.ShouldBe(expected.Options);
        (actual with { Options = [] }).ShouldBe(expected with { Options = [] });
    }

    private sealed class StepTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static async Task<string> Run(NodeHarness node, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "decisions", .. args], node.Folder, output, error, Ct);
        exit.ShouldBe(0, error.ToString() + output);
        return output.ToString();
    }
}
