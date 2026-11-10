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
        refused.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([(Watchtower.DoneMyselfLabel, Verbs.Tell)], "a ringing card offers the press that moves it");

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
}
