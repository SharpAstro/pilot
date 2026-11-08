using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What the stream watchtower waits on the person for (PIL855), over a fake claude process: each permission prompt and question is on the
/// chat page, is a card in For you whose buttons answer it, is answered once whichever the person uses, and a safe tool is answered by the node.
/// </summary>
public sealed partial class WatchtowerChatTests
{
    private static string CanUse(string id, string tool, string input) =>
        $$$"""{"type":"control_request","request_id":"{{{id}}}","request":{"subtype":"can_use_tool","tool_name":"{{{tool}}}","input":{{{input}}},"tool_use_id":"toolu_{{{id}}}"}}""";

    private const string RepoQuestion =
        """{"questions":[{"question":"Which repo?","header":"Repo","multiSelect":false,"options":[{"label":"tianwen","description":"the sky"},{"label":"pilot","description":"this one"}]}]}""";

    private const string ManyQuestion =
        """{"questions":[{"question":"Which flights?","header":"Flights","multiSelect":true,"options":[{"label":"PIL1","description":"a"},{"label":"PIL2","description":"b"}]}]}""";

    private static string[] ResponsesTo(WatchtowerStreamTests.FakeProcess process, string id) =>
        [.. process.Lines.Where(l => l.Contains("control_response") && JsonDocument.Parse(l).RootElement.GetProperty("response").GetProperty("request_id").GetString() == id)];

    [Fact]
    public async Task AMultiSelectQuestionIsAnsweredInTheChatWithSeveralLabelsAndItsCardOffersOnlyToDecline()
    {
        var process = await StreamIdleAsync();
        process.Emit(CanUse("m1", "AskUserQuestion", ManyQuestion));
        await UntilAsync(() => StreamChat().Page(-1).Asks is { Length: 1 }, "the question");

        // The page lists the ask as the session has it; its card is raised by the node's handler a moment later.
        await UntilAsync(() => _state.Cards(openOnly: true).Any(c => c.Kind == "watchtower-ask"), "the ask's card");
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "watchtower-ask");
        card.Choices.Select(c => c.Label).ShouldBe([Watchtower.DeclineLabel]);
        card.Detail.ShouldNotBeNull().ShouldContain("answer it in the chat");
        StreamChat().Page(-1).Asks![0].Questions![0].MultiSelect.ShouldBeTrue();

        // An answer with an array of labels is several, joined by a comma; one that is no JSON object is refused and the ask stays.
        (await _tower.AnswerAskAsync(["m1", "answer", "not json"], Ct)).ShouldNotBeNull();
        StreamChat().Page(-1).Asks.ShouldNotBeNull();
        (await _tower.AnswerAskAsync(["m1", "answer", """{"Which flights?":["PIL1","PIL2"]}"""], Ct)).ShouldBeNull();

        JsonDocument.Parse(ResponsesTo(process, "m1").Single()).RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedInput")
            .GetProperty("answers").GetProperty("Which flights?").GetString().ShouldBe("PIL1, PIL2");
    }

    [Fact]
    public async Task ADeclinedQuestionAndAnAskTheProcessCallsOffCloseTheirCardsAndAToolInsideTheWatchtowersFolderIsAnsweredByTheNode()
    {
        var process = await StreamIdleAsync();
        var inside = Path.Combine(_options.WatchtowerFolder, "notes.md").Replace("\\", "\\\\");

        process.Emit(CanUse("safe", "Read", $$"""{"file_path":"{{inside}}"}"""));
        await UntilAsync(() => ResponsesTo(process, "safe").Length == 1, "the node's own answer to a read inside its folder");
        StreamChat().Page(-1).Asks.ShouldBeNull();
        _state.Cards(openOnly: true).Where(c => c.Kind == "watchtower-ask").ShouldBeEmpty();

        // The same read outside it is a prompt for the person.
        process.Emit(CanUse("outside", "Read", """{"file_path":"/somewhere/else.md"}"""));
        process.Emit(CanUse("q2", "AskUserQuestion", RepoQuestion));
        await UntilAsync(() => StreamChat().Page(-1).Asks is { Length: 2 } && _state.Cards(openOnly: true).Count(c => c.Kind == "watchtower-ask") == 2, "the prompt and the question, and their cards");
        (await _tower.AnswerAskAsync(["q2", "deny"], Ct)).ShouldBeNull();
        JsonDocument.Parse(ResponsesTo(process, "q2").Single()).RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString().ShouldBe("deny");

        // The turn ends with the prompt unanswered: claude waits on nothing now, so its card goes too.
        process.Emit(ResultLine);
        await UntilAsync(() => StreamChat().Page(-1).Asks is null, "the unanswered prompt to be called off");
        _state.Cards(openOnly: true).Where(c => c.Kind == "watchtower-ask").ShouldBeEmpty();
        _state.Cards().Single(c => c.Subject == Watchtower.AskSubject("outside")).Outcome.ShouldBe("cancelled");
    }

    /// <summary>
    /// PIL855's review: an ask nobody answers leaves the watchtower blocked with nothing else to show it. Its card waits in For you while
    /// the person may be at the chat, and rings once the wait passes the ring time, as any session waiting on them does.
    /// </summary>
    /// <summary>The card of a permission prompt carries the whole command it allows: nothing past 1,500 characters is cut (PIL855's review).</summary>
    [Fact]
    public async Task APromptsCardCarriesTheWholeCommandItAllows()
    {
        var process = await StreamIdleAsync();
        var command = "echo " + new string('x', 2000) + " TAIL";
        process.Emit(CanUse("big", "Bash", JsonSerializer.Serialize(new { command })));
        await UntilAsync(() => _state.Cards(openOnly: true).Any(c => c.Subject == Watchtower.AskSubject("big")), "its card");

        _state.Cards(openOnly: true).Single(c => c.Subject == Watchtower.AskSubject("big")).Detail.ShouldNotBeNull().ShouldContain(" TAIL");
    }

    [Fact]
    public async Task AnAskNobodyAnswersRingsOnceItHasWaitedPastTheRingTime()
    {
        var process = await StreamIdleAsync();
        process.Emit(CanUse("long", "Read", """{"file_path":"/somewhere/else.md"}"""));
        await UntilAsync(() => _state.Cards(openOnly: true).Any(c => c.Subject == Watchtower.AskSubject("long")), "its card");

        _time.Advance(TimeSpan.FromMinutes(5));
        await _tower.LookAsync(Ct);
        _state.Cards(openOnly: true).Single(c => c.Subject == Watchtower.AskSubject("long")).Bin.ShouldBe(CardBin.ForYou);

        _time.Advance(_tower.AskRingAfter);
        await _tower.LookAsync(Ct);
        var card = _state.Cards(openOnly: true).Single(c => c.Subject == Watchtower.AskSubject("long"));
        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldStartWith("It has waited on you for over");
    }
}
