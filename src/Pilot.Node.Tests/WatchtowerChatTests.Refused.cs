using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A press or a pilot command of the stream watchtower that Claude Code refused rings the person (COD70, c9919, 2026-11-10: the ring judge left the
/// card in For you because the watchtower could press it, the press was refused, and it waited seven hours with nothing ringing).
/// </summary>
public sealed partial class WatchtowerChatTests
{
    private const string AutoModeRefusal =
        "Permission for this action was denied by the Claude Code auto mode classifier. Reason: [Auto-Mode Bypass]. If you have other tasks that don't depend on this action, continue working on those.";

    private static string ShellCall(string id, string command) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name = "Bash", input = new { command } } } } });

    private static string ShellResult(string id, string text, bool error) =>
        JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = text, is_error = error } } } });

    [Fact]
    public async Task AWatchtowersRefusedPressMovesTheCardToTheRingAsAWarningThatNamesTheButton()
    {
        var process = await StreamIdleAsync();
        var card = _state.RaiseCard(CardBin.ForYou, "review-changes", "flight/COD70", "COD70's review found things to fix",
            RingJudge.QuietPrefix + "the watchtower can send the leg round for it without the person.\n\n## Blocking\n\n- the rule applies to tmap too",
            [new CardChoice { Label = "Fix the review's findings", Verb = Verbs.GoAround, Target = "flight/COD70/review-notes-2" }]);

        process.Emit(ShellCall("r1", $"C:/Users/x/AppData/Local/pilot2/bin/pilot.exe answer {card.Id} 1 --why round-3"));
        process.Emit(ShellResult("r1", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Bin == CardBin.Ring, "the card to ring");

        var rung = _state.Cards(openOnly: true).Single(c => c.Id == card.Id);
        rung.Title.ShouldBe(Watchtower.RefusedPrefix + "COD70's review found things to fix");
        rung.Detail.ShouldNotBeNull().ShouldStartWith("What to do: press \"Fix the review's findings\" yourself.");
        rung.Detail.ShouldContain("auto mode: [Auto-Mode Bypass]");
        rung.Detail.ShouldContain("the rule applies to tmap too");
        rung.Detail.ShouldNotContain(RingJudge.QuietPrefix, Case.Sensitive, "the quiet verdict was wrong once the press was refused");
        rung.Choices.Select(c => c.Label).ShouldBe(["Fix the review's findings"]);
        rung.Summary.ShouldBe("The watchtower's press was refused by Claude Code's permission check, so nothing moves. Press \"Fix the review's findings\" yourself.");

        // The watchtower tries again and is refused again: still one lead, the review's words once.
        process.Emit(ShellCall("r2", $"pilot answer {card.Id} \"Fix the review's findings\" 2>&1 && pilot status"));
        process.Emit(ShellResult("r2", AutoModeRefusal, error: true));
        // Lines are read in order: once a later refusal's card is up, the second refusal has been handled.
        process.Emit(ShellCall("r3", "pilot cards"));
        process.Emit(ShellResult("r3", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Any(c => c.Kind == Watchtower.RefusedKind), "the later refusal's card");
        var again = _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Detail.ShouldNotBeNull();
        again.Split("What to do:").Length.ShouldBe(2, "one lead, not one per refusal");
        again.Split("the rule applies to tmap too").Length.ShouldBe(2);
        _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Title.ShouldBe(Watchtower.RefusedPrefix + "COD70's review found things to fix");
    }

    [Fact]
    public async Task ARefusedMcpCallsLineKeepsItsWhyAndTheCardsSubjectIsUnchanged()
    {
        var process = await StreamIdleAsync();
        process.Emit(JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = "w1", name = "mcp__pilot__flight_allow-round", input = new { code = "PIL1347", why = "round 3's note is not blocking" } } } },
        }));
        process.Emit(ShellResult("w1", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Any(c => c.Kind == Watchtower.RefusedKind), "the refused call's card");

        var refused = _state.Cards(openOnly: true).Single(c => c.Kind == Watchtower.RefusedKind);
        refused.Title.ShouldBe("The watchtower could not run pilot flight allow-round");
        refused.Detail.ShouldNotBeNull().ShouldContain("pilot flight allow-round PIL1347 --why \"round 3's note is not blocking\"");
        RefusedCommand.Read("pilot flight allow-round PIL1347 --why \"round 3's note is not blocking\"").Command.ShouldNotBeNull().Args.ShouldBe(["round 3's note is not blocking"]);
    }

    [Fact]
    public async Task APressThroughPilotsMcpToolThatIsRefusedRingsTheCardToo()
    {
        var process = await StreamIdleAsync();
        var card = _state.RaiseCard(CardBin.ForYou, "review-changes", "flight/COD70", "COD70's review found things to fix", null,
            [new CardChoice { Label = "Fix the review's findings", Verb = Verbs.GoAround, Target = "flight/COD70/review-notes-2" }]);

        process.Emit(JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = "p1", name = "mcp__pilot__answer", input = new { choice = new[] { "1" }, why = "round-3", card = card.Id } } } },
        }));
        process.Emit(ShellResult("p1", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Bin == CardBin.Ring, "the card to ring");

        // The choice as the schema advertises it (Words, an array of strings), with an option beside it, and the card before it.
        _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Detail.ShouldNotBeNull().ShouldStartWith("What to do: press \"Fix the review's findings\" yourself.");
    }

    [Fact]
    public async Task ACardWhoseTitleDoesNotFitAfterThePrefixGetsAShortOneAndKeepsItsOwnInTheDetail()
    {
        var process = await StreamIdleAsync();
        const string Long = "PIL1234/a-rather-long-leg-name's worker has been idle for 3 min with 0 of 6 steps";
        var card = _state.RaiseCard(CardBin.ForYou, "worker-idle", "flight/PIL1234/a-rather-long-leg-name", Long, null,
            [new CardChoice { Label = "Nudge it to finish", Verb = Verbs.Say, Target = "session/s1" }]);

        process.Emit(ShellCall("l1", $"pilot answer {card.Id} 1"));
        process.Emit(ShellResult("l1", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Bin == CardBin.Ring, "the card to ring");

        var rung = _state.Cards(openOnly: true).Single(c => c.Id == card.Id);
        rung.Title.ShouldBe(Watchtower.RefusedPrefix + "\"Nudge it to finish\"");
        rung.Title.Length.ShouldBeLessThanOrEqualTo(70);
        rung.Detail.ShouldNotBeNull().ShouldContain("The card was: " + Long);
    }

    [Fact]
    public async Task ARefusedPilotCommandRingsACardOfItsOwnAndARefusedOtherCallOrAPressThatRanRingsNothing()
    {
        var process = await StreamIdleAsync();
        var card = _state.RaiseCard(CardBin.ForYou, "stray-session", "session/s1", "s1 is running but should not be", null,
            [new CardChoice { Label = "Stop it", Verb = Verbs.EndSession, Target = "session/s1" }]);

        // A press that ran and a refused call that is not the node's: nothing rings.
        process.Emit(ShellCall("ok", $"pilot answer {card.Id} 1"));
        process.Emit(ShellResult("ok", "ok: answer card/" + card.Id, error: false));
        process.Emit(ShellCall("git", "git push origin main"));
        process.Emit(ShellResult("git", AutoModeRefusal, error: true));
        // The brief's own shape for git elsewhere, in this very repo: a folder named pilot is not the node's CLI.
        process.Emit(ShellCall("gitc", "git -C C:/Users/x/source/repos/sharpastro/pilot push origin main"));
        process.Emit(ShellResult("gitc", AutoModeRefusal, error: true));
        // A quoted argument's lines are not commands: a commit body with a line "Pilot is now quiet…", a comment with "pilot answer …".
        process.Emit(ShellCall("msg", "git commit -m \"Quiet the second ring\n\nPilot is now quiet when a card rings twice.\""));
        process.Emit(ShellResult("msg", AutoModeRefusal, error: true));
        process.Emit(ShellCall("gh", $"gh pr comment 1 --body 'Tried:\npilot answer {card.Id} 1'"));
        process.Emit(ShellResult("gh", AutoModeRefusal, error: true));

        // A refused pilot verb that pressed no card's button rings one of its own, with the command to run.
        process.Emit(ShellCall("m1", @"""C:\Users\x\AppData\Local\pilot2\bin\pilot.exe"" flight merge PIL1 abc123"));
        process.Emit(ShellResult("m1", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Any(c => c.Kind == Watchtower.RefusedKind), "the refused command's card");

        var refused = _state.Cards(openOnly: true).Single(c => c.Kind == Watchtower.RefusedKind);
        refused.Bin.ShouldBe(CardBin.Ring);
        refused.Title.ShouldBe("The watchtower could not run pilot flight merge");
        refused.Detail.ShouldNotBeNull().ShouldContain("flight merge PIL1 abc123");
        refused.Choices.Select(c => (c.Label, c.Verb)).ShouldBe(
            [("Run it: pilot flight merge PIL1", Verbs.Merge), (Watchtower.DoneCheckedLabel, Verbs.DoneChecked)], "a ringing card offers to run the command, then the press that says it was done and has the node check it");

        // Another flight's refused merge is a card of its own; the person's own no, and a verb's words, ring nothing more.
        process.Emit(ShellCall("m2", "pilot flight merge PIL2 def456"));
        process.Emit(ShellResult("m2", AutoModeRefusal, error: true));
        process.Emit(ShellCall("no", "pilot flight land PIL1"));
        process.Emit(ShellResult("no", "The user doesn't want to proceed with this tool use. The tool use was rejected.", error: true));
        process.Emit(ShellCall("say", "pilot say hello there"));
        process.Emit(ShellResult("say", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Count(c => c.Kind == Watchtower.RefusedKind) == 3, "the second merge's and the say's cards");
        _state.Cards(openOnly: true).Where(c => c.Kind == Watchtower.RefusedKind).Select(c => c.Title).ShouldBe(
            ["The watchtower could not run pilot flight merge",
             "The watchtower could not run pilot flight merge",
             "The watchtower could not run pilot say"], ignoreOrder: true);
        _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Bin.ShouldBe(CardBin.ForYou, "its press ran");
    }

    // c30387, 2026-11-25: the watchtower's allow-round, refused by Claude Code.
    private const string AllowRoundLine = "pilot flight allow-round PIL1347 \"Watchtower: one blocking point is left, round 3's other note is not blocking.\" 2>&1 | head -20";

    private static string TowerOf(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private async Task<CardRow> RefusedCardAsync(string line)
    {
        var process = await StreamIdleAsync();
        return await RefusedOnAsync(process, "rc", line, 1);
    }

    // The line refused on a stream that is up; the newest card once <paramref name="count"/> refused cards are open.
    private async Task<CardRow> RefusedOnAsync(WatchtowerStreamTests.FakeProcess process, string id, string line, int count)
    {
        process.Emit(ShellCall(id, line));
        process.Emit(ShellResult(id, AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Count(c => c.Kind == Watchtower.RefusedKind) == count, "the refused command's card");
        return _state.Cards(openOnly: true).Where(c => c.Kind == Watchtower.RefusedKind).OrderByDescending(c => c.Id.Length).ThenByDescending(c => c.Id, StringComparer.Ordinal).First();
    }

    // PIL1347 as it was held: three rounds with changes, its one leg done, the review asking for changes.
    private static void HeldFlight(NodeHarness node, string plane)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "PIL1347", Repo = "pilot", RepoPath = plane, Issue = 1347, Branch = "flight/pil1347", Worktree = plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Title = "Held flight",
            Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "du1-pil1347-review", Report = "## Blocking\n- the third round's point", StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow },
            ReviewRounds = [.. Enumerable.Range(1, ReviewConvergence.RunawayRounds).Select(n => new ReviewRound { Round = n, Verdict = ReviewState.Changes, Findings = $"point of round {n}" })],
        });
        node.State.PutLeg(new LegRow { Flight = "PIL1347", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done, Worker = "du1-pil1347-parser", Report = "done" });
    }

    private static async Task TowerBusyAsync(NodeHarness node)
    {
        var name = TowerOf(node);
        node.Agents.Json = Sessions.AgentRows.List(Sessions.AgentRows.Interactive(Sessions.AgentRows.SessionA, 4242, name, status: "busy"));
        for (var i = 0; i < 100 && !node.State.Sessions().Any(s => s.Name == name && s.State == SessionState.Working); i++)
        {
            await Task.Delay(50, Ct);
        }
        node.State.Sessions().ShouldContain(s => s.Name == name && s.State == SessionState.Working);
    }

    private static Task<CommandResult> PressAsync(NodeHarness node, CardRow card, string who, bool agent) =>
        node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["1"], Who = who, Agent = agent }, Ct)
            .ContinueWith(t => t.Result.Response.ShouldNotBeNull(), Ct);

    [Fact]
    public async Task ARefusedAllowRoundRingsACardWhoseFirstButtonRunsItAsThePersonAndTheWatchtowerIsToldAfterwards()
    {
        var refused = await RefusedCardAsync(AllowRoundLine);
        refused.Choices.Select(c => c.Label).ShouldBe(["Run it: pilot flight allow-round PIL1347", Watchtower.DoneCheckedLabel]);
        refused.Choices[0].Verb.ShouldBe(Verbs.AllowRound);
        refused.Choices[0].Target.ShouldBe("flight/PIL1347");
        refused.Choices[0].Args.ShouldBe(["Watchtower: one blocking point is left, round 3's other note is not blocking."]);

        using var repos = new SeededRepos(Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8]));
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        HeldFlight(node, repos.Seed("plane"));
        await TowerBusyAsync(node);
        // The card as the node raised it, on the node that holds the flight.
        var card = node.State.RaiseCard(refused.Bin, refused.Kind, refused.Subject, refused.Title, refused.Detail, refused.Choices);
        var started = node.Workers.Started.Count;
        var calls = node.Delivery.Calls.Count;

        var result = await PressAsync(node, card, "gui", agent: false);

        result.Error.ShouldBeNull();
        node.State.Flights().Single().ReviewRoundsAllowed.ShouldBe(ReviewConvergence.RunawayRounds + 1);
        node.Workers.Started.Count.ShouldBeGreaterThan(started);
        node.Workers.Started.Last().Brief.ShouldContain("round 3's other note is not blocking");
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards().Single(c => c.Id == card.Id).Outcome.ShouldBe("Run it: pilot flight allow-round PIL1347");
        node.Delivery.Calls.Count.ShouldBeGreaterThan(calls, "the watchtower is told it was run");
    }

    [Fact]
    public async Task TheWatchtowersOwnPressOfTheRunIsRefusedAndTheCardStaysOpen()
    {
        var refused = await RefusedCardAsync(AllowRoundLine);
        using var repos = new SeededRepos(Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8]));
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        HeldFlight(node, repos.Seed("plane"));
        var card = node.State.RaiseCard(refused.Bin, refused.Kind, refused.Subject, refused.Title, refused.Detail, refused.Choices);

        var result = await PressAsync(node, card, TowerOf(node), agent: true);

        result.Error.ShouldNotBeNull().ShouldContain("Claude Code refused that very command");
        node.State.Flights().Single().ReviewRoundsAllowed.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task TwoPilotCommandsJoinedByAndAndAVerbNoRowHasGetNoRunButtonAndTheCommandOnALineOfItsOwn()
    {
        var process = await StreamIdleAsync();
        var joined = await RefusedOnAsync(process, "j1", "pilot flight allow-round PIL1347 one more round && pilot flight merge PIL1347 abc123", 1);

        joined.Choices.Select(c => c.Label).ShouldBe([Watchtower.DoneCheckedLabel]);
        var lines = joined.Detail.ShouldNotBeNull().Split('\n');
        lines.ShouldContain("pilot flight allow-round PIL1347 one more round");
        joined.Detail.ShouldContain("cannot be pressed");
        joined.Detail.ShouldContain("terminal");

        var unknown = await RefusedOnAsync(process, "j2", "pilot flight teleport PIL1347", 2);
        unknown.Choices.ShouldBeEmpty("a command the node cannot read has no Done: it could not be checked");
        unknown.Detail.ShouldNotBeNull().Split('\n').ShouldContain("pilot flight teleport PIL1347");
        unknown.Detail.ShouldContain("cannot see pilot flight teleport done");
    }

    [Fact]
    public async Task ARefusedSayHasNoDoneButtonAndSaysTheNodeCannotSeeItDone()
    {
        var say = await RefusedCardAsync("pilot say hello there");

        say.Choices.ShouldBeEmpty();
        say.Detail.ShouldNotBeNull().ShouldContain("cannot see");
        say.Detail.ShouldContain("dismiss");
    }

    [Fact]
    public async Task TheCheckedDoneWithTheEffectNotThereLeavesTheCardOpenAndTellsTheWatchtowerNothing()
    {
        var refused = await RefusedCardAsync(AllowRoundLine);
        var done = refused.Choices.Single(c => c.Verb == Verbs.DoneChecked);
        done.Args.Take(2).ShouldBe([Verbs.AllowRound, "flight/PIL1347"]);

        using var repos = new SeededRepos(Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8]));
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        HeldFlight(node, repos.Seed("plane"));
        await TowerBusyAsync(node);
        var card = node.State.RaiseCard(refused.Bin, refused.Kind, refused.Subject, refused.Title, refused.Detail, refused.Choices);
        var calls = node.Delivery.Calls.Count;

        var result = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [Watchtower.DoneCheckedLabel], Who = "gui" }, Ct);

        result.Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain("PIL1347's review is still held at its third round");
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Open);
        node.Delivery.Calls.Count.ShouldBe(calls, "the watchtower was told nothing");
    }

    [Fact]
    public async Task TheCheckedDoneWithTheEffectThereClosesTheCardAndTellsTheWatchtower()
    {
        var refused = await RefusedCardAsync(AllowRoundLine);
        using var repos = new SeededRepos(Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8]));
        await using var node = await NodeHarness.StartAsync(Ct, git: repos.Git);
        HeldFlight(node, repos.Seed("plane"));
        await TowerBusyAsync(node);
        var card = node.State.RaiseCard(refused.Bin, refused.Kind, refused.Subject, refused.Title, refused.Detail, refused.Choices);
        // The person's allow-round from a terminal lifted the cap (the same record the command writes).
        node.State.PutFlight(node.State.Flights().Single() with { ReviewRoundsAllowed = ReviewConvergence.RunawayRounds + 1 });
        var calls = node.Delivery.Calls.Count;

        var result = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [Watchtower.DoneCheckedLabel], Who = "gui" }, Ct);

        result.Response.ShouldNotBeNull().Error.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Closed);
        node.Delivery.Calls.Count.ShouldBeGreaterThan(calls, "the watchtower is told what was seen");
    }
}
