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

        // A refused pilot verb that pressed no card's button rings one of its own, with the command to run.
        process.Emit(ShellCall("m1", @"""C:\Users\x\AppData\Local\pilot2\bin\pilot.exe"" flight merge PIL1 abc123"));
        process.Emit(ShellResult("m1", AutoModeRefusal, error: true));
        await UntilAsync(() => _state.Cards(openOnly: true).Any(c => c.Kind == Watchtower.RefusedKind), "the refused command's card");

        var refused = _state.Cards(openOnly: true).Single(c => c.Kind == Watchtower.RefusedKind);
        refused.Bin.ShouldBe(CardBin.Ring);
        refused.Title.ShouldBe("The watchtower could not run pilot flight merge: auto mode: [Auto-Mode Bypass] refused it");
        refused.Detail.ShouldNotBeNull().ShouldContain("flight merge PIL1 abc123");
        _state.Cards(openOnly: true).Single(c => c.Id == card.Id).Bin.ShouldBe(CardBin.ForYou, "its press ran");
    }
}
