using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A refused pilot command read back into the node command it would have sent (pilot #1353), and the table held to the CLI.</summary>
public sealed class RefusedCommandTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private static PressedCommand Pressable(string line)
    {
        var reading = RefusedCommand.Read(line);
        reading.CanPress.ShouldBeTrue($"{line}: {reading.Blocked} {reading.Detail}");
        return reading.Command.ShouldNotBeNull();
    }

    [Fact]
    public void AnAllowRoundWithItsQuotedWhyAndAReaderPipeIsOneCommandThatMayBePressed()
    {
        // c30387, 2026-11-25: the redirect and the pipe into head are not a second command.
        var command = Pressable("/c/Users/x/AppData/Local/Programs/pilot2/pilot.exe flight allow-round PIL1347 \"Watchtower: one blocking point is left, a regression from round 2's own fix; the other round 3 note is not blocking.\" 2>&1 | head -20");

        command.Verb.ShouldBe(Verbs.AllowRound);
        command.Target.ShouldBe("flight/PIL1347");
        command.Args.ShouldBe(["Watchtower: one blocking point is left, a regression from round 2's own fix; the other round 3 note is not blocking."]);
        command.Why.ShouldBeNull();
    }

    [Fact]
    public void ALegFinishThatNamesItsLegAsItsFirstWordIsTheLegsFinish()
    {
        // c24577: the leg as the first word; the node refuses the outcome it does not know, as it does for any press.
        var command = Pressable("pilot leg finish PIL1157/job-fails-empty-terms true");

        command.Verb.ShouldBe(Verbs.FinishLeg);
        command.Target.ShouldBe("flight/PIL1157/job-fails-empty-terms");
        command.Args.ShouldBe(["true"]);
    }

    [Fact]
    public void APowerShellCallToAQuotedPilotExeWithWhyTakesItIntoWhy()
    {
        var command = Pressable("& \"C:\\Users\\x\\AppData\\Local\\pilot2\\bin\\pilot.exe\" flight merge PIL1 abc123 --why \"job j9 is green\"");

        command.Verb.ShouldBe(Verbs.Merge);
        command.Target.ShouldBe("flight/PIL1");
        command.Args.ShouldBe(["abc123"]);
        command.Why.ShouldBe("job j9 is green");
    }

    [Fact]
    public void AnMcpBuiltLineTakesItsWhyIntoTheWordsOfACommandWhoseWhyIsItsWords()
    {
        var allow = Pressable("pilot flight allow-round PIL1347 --why \"round 3's note is not blocking\"");
        allow.Args.ShouldBe(["round 3's note is not blocking"]);
        allow.Why.ShouldBeNull();

        var goAround = Pressable("pilot leg go-around \"the tests were red\" --leg PIL1/a --why \"round two\"");
        goAround.Verb.ShouldBe(Verbs.GoAround);
        goAround.Target.ShouldBe("flight/PIL1/a");
        goAround.Args.ShouldBe(["the tests were red"]);
        goAround.Why.ShouldBe("round two");
    }

    [Fact]
    public void AMergeMethodIsTheOneTheCliSends()
    {
        Pressable("pilot flight merge PIL1 abc --rebase").Args.ShouldBe(["abc", "rebase"]);
        Pressable("pilot flight merge PIL1 abc --method=merge").Args.ShouldBe(["abc", "merge"]);
        Pressable("pilot flight merge PIL1 abc").Args.ShouldBe(["abc"]);
    }

    [Theory]
    [InlineData("pilot flight ground PIL1 \"owner hold", PressBlock.Unparsable)]
    [InlineData("pilot flight connect PIL1 PIL2 --because x", PressBlock.Unparsable)]
    [InlineData("pilot flight merge PIL1", PressBlock.Unparsable)]
    [InlineData("pilot flight ci-budget PIL1 maybe", PressBlock.Unparsable)]
    [InlineData("pilot leg finish done report", PressBlock.Unparsable)]
    [InlineData("pilot status", PressBlock.NotANodeVerb)]
    [InlineData("pilot flight overlap PIL1 a.cs", PressBlock.NotANodeVerb)]
    [InlineData("git status", PressBlock.NotANodeVerb)]
    public void ALineThatCannotBePressedSaysWhy(string line, PressBlock why)
    {
        var reading = RefusedCommand.Read(line);

        reading.CanPress.ShouldBeFalse();
        reading.Blocked.ShouldBe(why);
        reading.Detail.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ASecondPilotCallIsMoreThanOneCommandAndTheFirstIsStillRead()
    {
        foreach (var line in new[] { "pilot flight land PIL1 && pilot status", "pilot flight land PIL1 | pilot status", "pilot flight land PIL1\npilot status", "pilot flight land PIL1; & \"C:\\x\\pilot.exe\" cards" })
        {
            var reading = RefusedCommand.Read(line);

            reading.Blocked.ShouldBe(PressBlock.MoreThanOneCommand, line);
            reading.CanPress.ShouldBeFalse();
            reading.Command.ShouldNotBeNull().Target.ShouldBe("flight/PIL1");
        }
    }

    [Fact]
    public void ARedirectOrAPipeIntoAnotherReaderIsNotASecondCommand()
    {
        Pressable("pilot flight land PIL1 2>&1 | head -20").Target.ShouldBe("flight/PIL1");
        Pressable("pilot flight land PIL1 > out.txt").Target.ShouldBe("flight/PIL1");
        // A pilot word inside a quoted argument is not a call.
        Pressable("pilot flight ground PIL1 \"then run pilot status\"").Args.ShouldBe(["then run pilot status"]);
    }

    /// <summary>
    /// The table is the node's own, so a CLI that changes what a command sends must change its row: each row's sample goes through the real CLI
    /// to a node that has no flights or sessions (so each is refused before git or gh), and what the node was sent is what the parser makes.
    /// </summary>
    [Fact]
    [Trait("Category", "Invariant")]
    public async Task EveryRowsSampleReachesTheNodeAsTheParserMakesIt()
    {
        RefusedCommand.Samples.Select(s => s.Path).ShouldBe(RefusedCommand.Paths, "each row has a sample");
        foreach (var (path, argv) in RefusedCommand.Samples)
        {
            var before = _node.Sent.All.Count;
            var line = RefusedCommand.SampleLine(argv);
            var expected = Pressable(line);

            var output = new StringWriter();
            var error = new StringWriter();
            await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. argv], _node.Folder, output, error, Ct);

            var sent = _node.Sent.All.Skip(before).Where(c => c.Verb == expected.Verb).ToList();
            sent.Count.ShouldBe(1, $"{path}: the CLI sent one {expected.Verb}: {output}{error}");
            sent[0].Target.ShouldBe(expected.Target, path);
            (sent[0].Args ?? []).ShouldBe(expected.Args, path);
            sent[0].Why.ShouldBe(expected.Why, path);
        }
    }
}
