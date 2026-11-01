using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The autopilot's own merge of a passed flight is a decision in the black box, with the rule that fired as the reason (PIL583).</summary>
public sealed class DecisionAutopilotMergeTests : ReviewTestsBase
{
    [Fact]
    public async Task DecisionRecordedWhenTheAutopilotMergesAPassedFlightFromItsPrReadyCard()
    {
        var (flight, _) = await Passed("30");
        _github.Checks = ChoiceStatus.Passing;
        var on = NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!;
        _legs.Models = new RoleModels(on);

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        var merges = _state.Decisions(flight.Code, DecisionKinds.Merge).Where(d => d.ByKind == DecisionByKinds.Autopilot).ToArray();
        var decision = merges.First(d => d.Choice == "Merge");
        decision.Subject.ShouldBe($"flight/{flight.Code}");
        decision.Options.ShouldBe(["Merge", "Wait"]);
        decision.By.ShouldBe("node");
        decision.Reason.ShouldContain("autopilot on, review passed, checks");
    }
}

/// <summary>A person's go-around of a done leg, answered from a PR-ready card, is one decision (not two), with the card's options.</summary>
public sealed class DecisionGoAroundTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string Subject = "flight/TIA1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2k-" + Guid.NewGuid().ToString("N")[..8]);

    private string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A temp folder a scanner still holds is the OS's to clear.
        }
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private CardRow Ready(NodeHarness node, params CardChoice[] choices)
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        Git(Repo, "config", "user.email", "t@example.com");
        Git(Repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-q", "-m", "first");
        Git(Repo, "branch", "flight/tia1");
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Repo, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(_root, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, Subject, "TIA1's PR #63", "Planets: 1 leg done.", choices, link: PrUrl);
    }

    [Fact]
    public async Task DecisionRecordedOnceForAPersonsGoAroundAnsweredFromTheCard()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node,
            new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = Verbs.LegTarget("TIA1", "parser"), Args = ["the PR conflicts with its base: rebase it"] },
            new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [PrHead] });

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(ready.Id), Args = ["Redo leg parser"], Who = "gui" }, Ct)).Response.ShouldNotBeNull();

        result.Error.ShouldBeNull();
        var decision = node.State.Decisions().ShouldHaveSingleItem();
        decision.Kind.ShouldBe(DecisionKinds.GoAround);
        decision.Subject.ShouldBe(Subject);
        decision.Options.ShouldBe(["Redo leg parser", "Merge"]);
        decision.Choice.ShouldBe("Redo leg parser");
        (decision.By, decision.ByKind).ShouldBe(("gui", DecisionByKinds.Person));
        decision.Reason.ShouldNotBeNullOrWhiteSpace();
    }
}

/// <summary>A question of design answered by one of the card's options, and one answered in free text, are each one decision.</summary>
public sealed class DecisionDesignAnswerTests : PrFixTestsBase
{
    private const string Question = "README.md:1 design: should this be a service at all?";

    [Fact]
    public async Task DecisionRecordedForADesignQuestionAnsweredByOneOfTheCardsOptions()
    {
        await Review("acme/widget#7", "changes", Question);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.DesignKind);
        _state.UpdateChoices(card.Id, [.. card.Choices,
            new CardChoice { Label = "Keep a library", Verb = Verbs.FixPr, Target = card.Subject, Args = ["Keep a library"] },
            new CardChoice { Label = "Make a service", Verb = Verbs.FixPr, Target = card.Subject, Args = ["Make a service"] }]);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep a library")).Error.ShouldBeNull();

        var decision = _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        decision.Subject.ShouldBe("pr/acme/widget#7");
        decision.Question.ShouldContain("should this be a service at all?");
        decision.Options.ShouldBe(["Keep a library", "Make a service"]);
        decision.Choice.ShouldBe("Keep a library");
        decision.ByKind.ShouldBe(DecisionByKinds.Person);
        decision.Reason.ShouldBe("Keep a library");
    }

    [Fact]
    public async Task DecisionRecordedForADesignQuestionAnsweredInFreeText()
    {
        await Review("acme/widget#7", "changes", Question);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library,", "no", "service")).Error.ShouldBeNull();

        var decision = _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        decision.Options.ShouldBeEmpty();
        decision.Choice.ShouldBe("Keep it a library, no service");
        decision.By.ShouldBe("controller");
        decision.Reason.ShouldBe("Keep it a library, no service");
    }
}
