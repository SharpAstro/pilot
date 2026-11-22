using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1158 R1.10: the design chat's host wiring as the node sees it. <see cref="DesignReads"/> sends through its <c>Send</c> seam, so the requests
/// its chat read (<c>design-chat</c> and its <c>after</c>) and its say (<c>design-say</c>, <c>[text]</c> or <c>[text, name, base64]</c>) make are
/// pinned here rather than only read.
/// </summary>
public sealed class DesignReadsWiringTests
{
    private readonly List<CommandRequest> _sent = [];
    private CommandResult? _answer;

    private DesignReads Reads() => new(null, "gui", () => { })
    {
        Send = (command, _) =>
        {
            _sent.Add(command);
            return Task.FromResult(ResponseEnvelope<CommandResult>.Ok(_answer ?? new CommandResult { Seq = 1, Verb = command.Verb, Target = command.Target, Who = command.Who ?? "", Ok = true }));
        },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheChatIsReadAfterTheLastIndexAndWholeForANegativeOne()
    {
        var page = new ChatPage { Entries = [], Total = 7 };
        _answer = new CommandResult { Seq = 1, Verb = Verbs.DesignChat, Target = Verbs.DesignTarget("DSN-1"), Who = "gui", Ok = true, DesignChat = page };
        var reads = Reads();

        (await reads.ChatAsync("DSN-1", 4, Ct)).Total.ShouldBe(7);
        await reads.ChatAsync("DSN-1", -1, Ct);

        _sent.Select(c => (c.Verb, c.Target, c.Who)).Distinct().ShouldBe([(Verbs.DesignChat, Verbs.DesignTarget("DSN-1"), "gui")]);
        _sent[0].Args.ShouldBe(["4"]);
        _sent[1].Args.ShouldBe([], "a negative after asks for the whole feed");
    }

    [Fact]
    public async Task ASayIsTheWordsAloneOrTheWordsThePictureNameAndItsBytesInBase64()
    {
        var reads = Reads();

        (await reads.SayAsync("DSN-1", "make it blue", null, Ct)).ShouldBeNull();
        (await reads.SayAsync("DSN-1", "like this", ("shot.png", [1, 2, 3]), Ct)).ShouldBeNull();

        _sent.ShouldAllBe(c => c.Verb == Verbs.DesignSay && c.Target == Verbs.DesignTarget("DSN-1"));
        _sent[0].Args.ShouldBe(["make it blue"]);
        _sent[1].Args.ShouldBe(["like this", "shot.png", Convert.ToBase64String(new byte[] { 1, 2, 3 })]);
    }

    [Fact]
    public async Task ARefusedSayAndAChatTheNodeDidNotSendAreSaidNotLost()
    {
        _answer = new CommandResult { Seq = 1, Verb = Verbs.DesignSay, Target = Verbs.DesignTarget("DSN-1"), Who = "gui", Ok = false, Error = "There is no design DSN-1" };
        var reads = Reads();

        (await reads.SayAsync("DSN-1", "hello", null, Ct)).ShouldBe("There is no design DSN-1");
        (await Should.ThrowAsync<InvalidOperationException>(() => reads.ChatAsync("DSN-1", 0, Ct))).Message.ShouldBe("There is no design DSN-1");
    }
}
