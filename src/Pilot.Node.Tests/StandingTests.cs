using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Who may do what (#208), at the node: the person, an assistant, the lookout, a flight's session and a grant, one
/// test per row of the issue's table.
/// </summary>
public sealed class StandingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PastTheFence = "has not been reviewed";

    private static string Lookout(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static CommandRequest Command(string verb, string target, string who, bool agent = false, params string[] args) =>
        new() { Verb = verb, Target = target, Args = args, Who = who, Agent = agent };

    private static FlightRow Flight(string code, ReviewRow? review = null) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"/r/{code}", Base = "main",
        State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Review = review,
    };

    private static void Fly(NodeHarness node, string code, string worker, ReviewRow? review = null)
    {
        node.State.PutFlight(Flight(code, review));
        node.State.PutLeg(new LegRow { Flight = code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = worker });
    }

    private static string? Saved(NodeHarness node) => NodeSettings.Read(node.Options.SettingsFile).Get(NodeSettings.MaxJobsKey) is { } values ? string.Join(' ', values) : null;

    private static async Task<string?> Error(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull().Error;

    [Fact]
    public async Task AGuiClickAndAHumanRunCliMayMergeAsBefore()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight("TIA1"));

        // The fence lets them through to the flight, which says why the merge cannot go yet.
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", "gui", false, "cafe0000"))).ShouldNotBeNull().ShouldContain(PastTheFence);
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", "cli", false, "cafe0000"))).ShouldNotBeNull().ShouldContain(PastTheFence);
    }

    [Fact]
    public async Task TheLookoutIsRefusedEveryYourOnlyVerbAndADismissedRingCardButMayDismissOthers()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lookout = Lookout(node);
        node.State.PutFlight(Flight("TIA1"));

        foreach (var (verb, target, args) in new (string, string, string[])[]
        {
            (Verbs.Set, Verbs.NodeTarget, ["maintenance", "on"]),
            (Verbs.Setup, Verbs.NodeTarget, ["firewall"]),
            (Verbs.AnswerGrant, Verbs.NodeTarget, ["x", "allow"]),
            (Verbs.RevokeGrant, Verbs.NodeTarget, ["x"]),
            (Verbs.Update, Verbs.NodeTarget, ["install"]),
            (Verbs.RestartNode, Verbs.NodeTarget, ["settings changed"]),
            (Verbs.RestartWatchtower, Verbs.WatchtowerTarget, []),
            (Verbs.PostReview, "flight/TIA1", []),
            (Verbs.SyncRepos, Verbs.NodeTarget, []),
            (Verbs.DesignBuild, Verbs.DesignTarget("DSN-1"), []),
        })
        {
            (await Error(node, Command(verb, target, lookout, false, args))).ShouldBe($"{verb} is the controller's, not the watchtower's: leave its card open for them");
        }

        var ring = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR is ready");
        var forYou = node.State.RaiseCard(CardBin.ForYou, "landed", "flight/TIA1", "TIA1 landed");
        // A card's dismissal runs directly as the assistant's, a ring card's too.
        (await Error(node, Command(Verbs.Dismiss, Verbs.CardTarget(ring.Id), lookout))).ShouldBeNull();
        (await Error(node, Command(Verbs.Dismiss, Verbs.CardTarget(forYou.Id), lookout))).ShouldBeNull();
        node.State.Cards(openOnly: true).ShouldNotContain(c => c.Id == ring.Id || c.Id == forYou.Id);

        // An agent-run command named for the lookout is the lookout's still: the flag does not lift it.
        (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, lookout, true, "maintenance", "on"))).ShouldNotBeNull().ShouldContain("not the watchtower's");
    }

    [Fact]
    public async Task AFlightsSessionMayNotAnswerARingCardAndTheLookoutMayOnlyWithWhatItMaySendItself()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        var lookout = Lookout(node);
        Fly(node, "TIA1", worker);
        var choices = new[]
        {
            new CardChoice { Label = "Retry", Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["for-you", "retried"] },
            new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["cafe0000"] },
        };
        var ring = node.State.RaiseCard(CardBin.Ring, "stalled", "flight/TIA1", "TIA1 stalled", null, choices);
        var forYou = node.State.RaiseCard(CardBin.ForYou, "landed", "flight/TIA1", "TIA1 landed", null, choices);

        // A flight's session may not answer the ring, however the choice reads; any other card it may.
        (await Error(node, Command(Verbs.Answer, Verbs.CardTarget(ring.Id), worker, false, "Retry"))).ShouldNotBeNull().ShouldContain("answer is the controller's, not a flight session's");
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);

        // The lookout may answer a ring card with a choice it could send itself; the choice is run again as the lookout's, which merges as the assistant's would: the flight refuses it (a leg is not done) and the card stays open.
        (await Error(node, Command(Verbs.Answer, Verbs.CardTarget(ring.Id), lookout, false, "Merge"))).ShouldNotBeNull().ShouldContain("legs not done");
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);
        (await Error(node, Command(Verbs.Answer, Verbs.CardTarget(forYou.Id), lookout, false, "Merge"))).ShouldNotBeNull().ShouldContain("legs not done");
        (await Error(node, Command(Verbs.Answer, Verbs.CardTarget(ring.Id), lookout, false, "Retry"))).ShouldBeNull();
        node.State.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Closed);

        // The person may answer either.
        (await Error(node, Command(Verbs.Answer, Verbs.CardTarget(forYou.Id), "gui", false, "Retry"))).ShouldBeNull();
    }

    [Fact]
    public async Task TheLookoutAnswersALegWatchStallCardWithResumeAndWithGoAround()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        var lookout = Lookout(node);
        node.State.PutFlight(Flight("TIA1"));
        // The leg is done, so its own rule answers the go-around: that it got that far is what is under test, as the lookout's choice ran.
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done, Worker = worker });
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, worker, status: "busy"));
        var row = await Listed(node, worker);

        // The choices LegWatch gives a stalled worker's ring card: Resume says to the worker, Redo leg starts a fresh one.
        var choices = new[]
        {
            new CardChoice { Label = "Resume", Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = ["Carry on with your leg from where you left off."] },
            new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = "flight/TIA1/parser", Args = ["the last worker stalled (idle)"] },
        };
        var resume = node.State.RaiseCard(CardBin.Ring, "leg-stalled", "flight/TIA1/parser", "TIA1/parser's worker stopped", null, choices);

        (await Error(node, Command(Verbs.Answer, Verbs.CardTarget(resume.Id), lookout, true, "Resume"))).ShouldBeNull();
        node.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldContain("Carry on with your leg");
        node.State.Cards().Single(c => c.Id == resume.Id).State.ShouldBe(CardState.Closed);

        var around = node.State.RaiseCard(CardBin.Ring, "leg-stalled", "flight/TIA1/parser", "TIA1/parser's worker stalled", null, choices);

        var error = await Error(node, Command(Verbs.Answer, Verbs.CardTarget(around.Id), lookout, true, "Redo leg parser"));
        error.ShouldNotBeNull().ShouldContain("a go-around is for a leg flying or failed");
        error.ShouldNotContain("controller's");

        // Its dismissal of a ring card runs directly, as the assistant's.
        (await Error(node, Command(Verbs.Dismiss, Verbs.CardTarget(around.Id), lookout, true))).ShouldBeNull();
    }

    [Fact]
    public async Task AnUpdateCheckOnlyReadsSoNoSessionIsRefusedItAndAnAssistantsRaisesNoCard()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        Fly(node, "TIA1", worker);

        // Whatever the updater answers (a test node runs from a build of its own), it is not the standing's refusal, and no card waits.
        foreach (var (who, agent) in new[] { (Lookout(node), true), (worker, true), ("pilot-33", true), ("gui", false) })
        {
            (await Error(node, Command(Verbs.Update, Verbs.NodeTarget, who, agent, "check"))).ShouldNotBeNull().ShouldNotContain("controller's");
            node.State.Cards(openOnly: true).ShouldBeEmpty($"{who}'s update --check");
        }

        // An install is still the person's: the lookout and a flight's session are refused.
        (await Error(node, Command(Verbs.Update, Verbs.NodeTarget, Lookout(node), true, "install"))).ShouldNotBeNull().ShouldContain("controller's");
        (await Error(node, Command(Verbs.Update, Verbs.NodeTarget, worker, true, "install"))).ShouldNotBeNull().ShouldContain("controller's");
    }

    private static async Task<SessionRow> Listed(NodeHarness node, string name)
    {
        for (var i = 0; i < 100 && !node.State.Sessions().Any(s => s.Name == name); i++)
        {
            await Task.Delay(50, Ct);
        }
        return node.State.Sessions().Single(s => s.Name == name);
    }

    [Fact]
    public async Task TheWatchtowerActingOnThePersonsMessageIsAnAssistantAndOtherwiseTheLookout()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lookout = Lookout(node);
        node.State.PutFlight(Flight("TIA1"));
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, lookout, status: "busy"));
        var row = await Listed(node, lookout);
        const string why = "the person asked me in the chat to merge TIA1";

        // The lookout's merge runs as the assistant's whoever woke it (as far as the flight's own fence), with no card.
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", lookout, true, "cafe0000"))).ShouldNotBeNull().ShouldContain(PastTheFence);
        node.State.Cards().ShouldBeEmpty();
        (await Error(node, Command(Verbs.Tell, Verbs.WatchtowerTarget, "gui", false, "merge TIA1"))).ShouldBeNull();
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", lookout, true, "cafe0000") with { Why = why })).ShouldNotBeNull().ShouldContain(PastTheFence);

        // The node's own message to it (a card it says) starts a turn that is not the person's: the lookout again, which
        // merges all the same (the person on Remote Control talks to it past the node, #344).
        (await Error(node, Command(Verbs.Say, Verbs.SessionTarget(row.Id), "node", false, "a card opened"))).ShouldBeNull();
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", lookout, true, "cafe0000"))).ShouldNotBeNull().ShouldContain(PastTheFence);
        // So does a fixer on a PR whose review asked for changes, which was the lookout's never (DE1, 2026-10-29), but only within the
        // node's own fixer guards: the node marks it as the node's own, so another author's PR without Fix it is refused as the
        // person's (the review of #470; without that mark the refusal would be the untracked repo's, not the node's).
        node.State.PutPrReview(new PrReviewRow
        {
            Repo = "acme/widget", Number = 7, Author = "someone-else", Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "cafe0000",
            ClonePath = "/r", Worktree = "/r/.claude/worktrees/review-widget-7", Reviewer = "review-widget-7", State = PrReviewState.Reviewed, Verdict = "changes",
            StartedAt = DateTimeOffset.UnixEpoch,
        });
        var fix = await Error(node, Command(Verbs.FixPr, Verbs.PrTarget("acme/widget", 7), lookout, true));
        fix.ShouldNotBeNull().ShouldNotContain("not the watchtower's");
        fix.ShouldContain("not started by the node");
        // The node's settings stay the person's.
        (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, lookout, true, "maintenance", "on"))).ShouldNotBeNull().ShouldContain("not the watchtower's");
    }

    [Fact]
    public async Task TheWatchtowerWhoseTurnHasEndedIsTheLookoutEvenAfterAPersonsMessage()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lookout = Lookout(node);
        node.State.PutFlight(Flight("TIA1"));
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, lookout, status: "busy"));
        await Listed(node, lookout);
        (await Error(node, Command(Verbs.Tell, Verbs.WatchtowerTarget, "gui", false, "merge TIA1"))).ShouldBeNull();
        // A hold is an assistant's by the person's word, so it runs.
        (await Error(node, Command(Verbs.Ground, "flight/TIA1", lookout, true, "the person asked me to hold it"))).ShouldBeNull();

        // Its turn over (the session is idle again), what it sends is its own.
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, lookout, status: "idle"));
        var error = "";
        for (var i = 0; i < 100 && !error.Contains("not the watchtower's", StringComparison.Ordinal); i++)
        {
            await Task.Delay(50, Ct);
            error = await Error(node, Command(Verbs.Unground, "flight/TIA1", lookout, true)) ?? "";
        }
        error.ShouldContain("not the watchtower's");
    }

    [Fact]
    public async Task AFlightsSessionMayNotStopOrCompactAnotherFlightsSessionNorCancelItsJobNorReachOutsideItsFlight()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        const string other = "du1-tia2-parser";
        Fly(node, "TIA1", worker);
        Fly(node, "TIA2", other);
        node.State.PutSession(Session(worker, "a1b2c3d4-0000-4000-8000-000000000001"));
        node.State.PutSession(Session(other, "b1b2b3b4-0000-4000-8000-000000000002") with { Flight = "TIA2", Cwd = "/r/TIA2" });
        node.State.PutSession(new SessionRow
        {
            Id = "c1c2c3c4", SessionId = "c1c2c3c4-0000-4000-8000-000000000003", Name = "se1-assistant", Kind = SessionKind.Background, Cwd = "/r",
            Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
        });
        JobRow Job(string id, string? owner, string? ownerName, string? flight) => new()
        {
            Id = id, Command = "dotnet test", Cwd = node.Folder, Owner = owner, OwnerName = ownerName, Flight = flight, State = JobState.Running, LogPath = Path.Combine(node.Folder, id + ".log"),
            Timeout = TimeSpan.FromMinutes(5), StartedAt = DateTimeOffset.UnixEpoch,
        };
        node.State.PutJob(Job("j1", "a1b2c3d4", worker, "TIA1"));
        node.State.PutJob(Job("j2", "b1b2b3b4", other, "TIA2"));
        node.State.PutJob(Job("j3", null, null, null));

        // Another flight's session, and one that is no flight's, are not its to stop or compact; its own is not stopped by the fence.
        foreach (var verb in new[] { Verbs.Stop, Verbs.Compact })
        {
            (await Error(node, Command(verb, "session/b1b2b3b4", worker, true))).ShouldBe("session/b1b2b3b4 is not TIA1's session: a flight's session stops and compacts its own flight's only");
            (await Error(node, Command(verb, "session/c1c2c3c4", worker, true))).ShouldNotBeNull().ShouldContain("not TIA1's session");
            (await Error(node, Command(verb, "session/zzzzzzzz", worker, true))).ShouldNotBeNull().ShouldContain("not TIA1's session");
            ((await Error(node, Command(verb, "session/a1b2c3d4", worker, true))) ?? "").ShouldNotContain("not TIA1's session");
        }

        // A job it does not own is not its to cancel.
        (await Error(node, Command(Verbs.CancelJob, "job/j2", worker, true))).ShouldBe("job/j2 is not TIA1's job: a flight's session cancels, pauses and resumes its own flight's only");
        (await Error(node, Command(Verbs.CancelJob, "job/j3", worker, true))).ShouldNotBeNull().ShouldContain("not TIA1's job");
        ((await Error(node, Command(Verbs.CancelJob, "job/j1", worker, true))) ?? "").ShouldNotContain("not TIA1's job");

        // Pausing and resuming a job are fenced like cancelling it.
        foreach (var verb in new[] { Verbs.PauseJob, Verbs.ResumeJob })
        {
            (await Error(node, Command(verb, "job/j2", worker, true))).ShouldBe("job/j2 is not TIA1's job: a flight's session cancels, pauses and resumes its own flight's only");
            (await Error(node, Command(verb, "job/j3", worker, true))).ShouldNotBeNull().ShouldContain("not TIA1's job");
            ((await Error(node, Command(verb, "job/j1", worker, true))) ?? "").ShouldNotContain("not TIA1's job");
            ((await Error(node, Command(verb, "job/j2", "gui"))) ?? "").ShouldNotContain("not TIA1's job");
        }

        // Nothing that reaches outside the flight, whatever it targets.
        foreach (var (verb, target) in new[] { (Verbs.CreateIssue, "repo/tianwen"), (Verbs.CreateFlight, Verbs.NodeTarget), (Verbs.SyncRepos, Verbs.NodeTarget), (Verbs.DesignBuild, Verbs.DesignTarget("DSN-1")) })
        {
            (await Error(node, Command(verb, target, worker, true, "x"))).ShouldStartWith($"{verb} is the controller's, not a flight session's: ");
            // It says why the sender counts as a flight session (its plane, its name) and where the command works (pilot #1020).
            (await Error(node, Command(verb, target, worker, true, "x"))).ShouldNotBeNull().ShouldContain($"it runs in TIA1's plane and its name ({worker}) says so; run it from the repo's main checkout or the GUI");
        }
        // The person is not fenced by who they are.
        ((await Error(node, Command(Verbs.Stop, "session/b1b2b3b4", "gui"))) ?? "").ShouldNotContain("not TIA1's session");
        ((await Error(node, Command(Verbs.CancelJob, "job/j2", "gui"))) ?? "").ShouldNotContain("not TIA1's job");
    }

    [Fact]
    public async Task HoldAndUnholdAreThePersonsAloneAndAnAssistantsRunDirectly()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        var lookout = Lookout(node);
        Fly(node, "TIA1", worker);

        foreach (var verb in new[] { Verbs.Ground, Verbs.Unground })
        {
            // The watchtower on its own and a flight's session are refused, and nothing is held.
            (await Error(node, Command(verb, "flight/TIA1", lookout, true, "the owner is away"))).ShouldBe($"{verb} is the controller's, not the watchtower's: leave its card open for them");
            (await Error(node, Command(verb, "flight/TIA1", worker, true, "the owner is away"))).ShouldStartWith($"{verb} is the controller's, not a flight session's: ");
        }
        node.State.Flights().Single().OnHold.ShouldBeNull();

        // An assistant's hold runs.
        (await Error(node, Command(Verbs.Ground, "flight/TIA1", "pilot-33", true, "the owner is away"))).ShouldBeNull();
        node.State.Flights().Single().OnHold.ShouldNotBeNull().By.ShouldBe("pilot-33");
        (await Error(node, Command(Verbs.Unground, "flight/TIA1", "pilot-33", true))).ShouldBeNull();
        node.State.Flights().Single().OnHold.ShouldBeNull();

        // The person holds and releases as they please.
        (await Error(node, Command(Verbs.Ground, "flight/TIA1", "gui", false, "back on", "Monday"))).ShouldBeNull();
        node.State.Flights().Single().OnHold.ShouldNotBeNull().Reason.ShouldBe("back on Monday");
        (await Error(node, Command(Verbs.Unground, "flight/TIA1", "gui"))).ShouldBeNull();
        node.State.Flights().Single().OnHold.ShouldBeNull();
    }

    [Fact]
    public async Task AWorkersPlannersAndReviewersMergeIsRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        const string reviewer = "du1-tia1-review";
        const string planner = "du1-tia1-plan";
        Fly(node, "TIA1", worker, new ReviewRow { State = ReviewState.Reviewing, Reviewer = reviewer, StartedAt = DateTimeOffset.UtcNow });
        node.State.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = planner, Kind = SessionKind.Background, Cwd = "/r/TIA1",
            Role = SessionRole.Planner, Flight = "TIA1", ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
        });

        {
            foreach (var who in new[] { worker, reviewer, planner })
            {
                // From the CLI of its plane, the flag is set too: the session is known by its name, not by the flag.
                foreach (var agent in new[] { false, true })
                {
                    (await Error(node, Command(Verbs.Merge, "flight/TIA1", who, agent, "cafe0000"))).ShouldStartWith("merge is the controller's, not a flight session's: ");
                    (await Error(node, Command(Verbs.Land, "flight/TIA1", who, agent))).ShouldNotBeNull().ShouldContain("land is the controller's");
                }
                (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, who, true, "maintenance", "on"))).ShouldNotBeNull().ShouldContain("set is the controller's");
            }
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightsSessionMayNotTargetAnotherFlightButHasItsOwn()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        Fly(node, "TIA1", worker);
        Fly(node, "TIA2", "du1-tia2-parser");

        foreach (var verb in new[] { Verbs.Check, Verbs.FinishLeg, Verbs.AddArtifact, Verbs.GoAround })
        {
            (await Error(node, Command(verb, "flight/TIA2/parser", worker, true, "1"))).ShouldBe("flight/TIA2/parser is not TIA1's: a flight's session acts on its own flight only");
        }
        (await Error(node, Command(Verbs.OpenPr, "flight/TIA2", worker, true))).ShouldNotBeNull().ShouldContain("not TIA1's");

        // Its own flight's leg is not stopped by the fence: the leg's own rules answer (this one has no checklist to tick).
        (await Error(node, Command(Verbs.Check, "flight/TIA1/parser", worker, true, "1"))).ShouldBe("TIA1/parser has no checklist");
        (await Error(node, Command(Verbs.Check, "flight/tia1/parser", worker, true, "2"))).ShouldNotBeNull().ShouldNotContain("own flight only");
    }

    [Fact]
    public async Task APrReviewsReviewerActsOnItsOwnPrOnly()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight("TIA1"));
        node.State.PutPrReview(new PrReviewRow
        {
            Repo = "SharpAstro/tianwen", Number = 7, Url = "https://github.com/SharpAstro/tianwen/pull/7", Base = "main", Head = "cafe0000", ClonePath = "/r/tianwen",
            State = PrReviewState.Reviewing, Reviewer = "du1-review-tianwen-7", Worktree = "/r/review7", StartedAt = DateTimeOffset.UtcNow,
        });
        const string reviewer = "du1-review-tianwen-7";

        (await Error(node, Command(Verbs.PrVerdict, "pr/SharpAstro/tianwen#8", reviewer, true, "pass"))).ShouldNotBeNull().ShouldContain("not the PR this review is of");
        (await Error(node, Command(Verbs.Review, "flight/TIA1", reviewer, true, "pass", "x"))).ShouldNotBeNull().ShouldContain("not the PR this review is of");
        (await Error(node, Command(Verbs.Merge, "pr/SharpAstro/tianwen#7", reviewer, true))).ShouldNotBeNull().ShouldContain("merge is the controller's");
    }

    [Fact]
    public async Task AGrantsCommandsBehaveAsBeforeWhateverItsNameSays()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port);
        node.State.PutFlight(Flight("TIA1"));
        using var remote = NodeClient.Remote($"127.0.0.1:{port}");
        var ticket = (await remote.AskGrantAsync("se2-gui", Ct)).Response.ShouldNotBeNull();
        var card = node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        (await node.Client.SendAsync(Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", false, card.Choices[0].Label), Ct)).Success.ShouldBeTrue();
        using var granted = NodeClient.Remote($"127.0.0.1:{port}", (await remote.GrantTicketAsync(ticket.Id, Ct)).Response.ShouldNotBeNull().Token);

        // A merge from the LAN reaches the flight, even named for the watchtower or a worker here.
        (await granted.SendAsync(Command(Verbs.Merge, "flight/TIA1", "tests", false, "cafe0000"), Ct)).Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain(PastTheFence);
        var named = await granted.SendAsync(Command(Verbs.Merge, "flight/TIA1", Lookout(node), false, "cafe0000"), Ct);
        named.Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain(PastTheFence);
        named.Response.Who.ShouldBe($"se2-gui/{Lookout(node)}");
        // Never this machine's settings or grants, as before.
        (await granted.SendAsync(Command(Verbs.Set, Verbs.NodeTarget, "tests", false, "max-jobs", "3"), Ct)).Error.ShouldBe("set is for this machine only");
        (await granted.SendAsync(Command(Verbs.AnswerGrant, Verbs.NodeTarget, "tests", false, "x", "allow"), Ct)).Error.ShouldBe("answer-grant is for this machine only");
        Saved(node).ShouldBeNull();
    }

    [Fact]
    public async Task TheCliSendsTheAgentFlagWhenClaudeCodeIsInItsEnvironmentAndAnMcpClientAlways()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // The environment is read through IsAgent's own seam, never changed: other tests run a human's CLI beside this one.
        // A human at their own terminal: no CLAUDECODE, so the person's.
        PilotCommand.IsAgent(bySession: true, agent: null, claudeCode: null).ShouldBeFalse();
        PilotCommand.IsAgent(bySession: true, agent: null, claudeCode: "").ShouldBeFalse();
        (await Cli(node, agent: false, "settings", "set", "max-jobs", "3")).ShouldContain("ok: set");

        // A Claude session's tool: CLAUDECODE is set, so an assistant's.
        PilotCommand.IsAgent(bySession: true, agent: null, claudeCode: "1").ShouldBeTrue();

        // The GUI never sets it, whatever its environment holds; an MCP client always does, whatever its does not.
        PilotCommand.IsAgent(bySession: false, agent: null, claudeCode: "1").ShouldBeFalse();
        PilotCommand.IsAgent(bySession: true, agent: true, claudeCode: null).ShouldBeTrue();
    }

    private static SessionRow Session(string name, string sessionId, string[]? earlier = null) => new()
    {
        Id = sessionId[..8], SessionId = sessionId, EarlierSessionIds = earlier, Name = name, Kind = SessionKind.Background, Cwd = "/r/TIA1",
        Role = SessionRole.Worker, Flight = "TIA1", ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task AWorkerTheNodeResumedMergesAsItselfNotAnAssistant()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        const string current = "b1b2b3b4-0000-4000-8000-000000000000";
        const string earlier = "a1a2a3a4-0000-4000-8000-000000000000";
        Fly(node, "TIA1", worker);
        node.State.PutSession(Session(worker, current, [earlier]));

        // Through the real CLI sender path: the session id its tools carry, the id it has now or the one before the resume.
        foreach (var id in new[] { current, earlier })
        {
            var said = await CliIn(node, agent: true, id, node.Folder, "flight", "merge", "TIA1", "cafe0000");
            said.ShouldContain("merge is the controller's, not a flight session's");
        }
        node.State.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAgentInAFlightsPlaneTheNodeHasNotPolledYetIsThatFlightsSessionNotAnAssistant()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        var plane = Directory.CreateDirectory(Path.Combine(node.Folder, "TIA1")).FullName;
        node.State.PutFlight(Flight("TIA1") with { Worktree = plane });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = worker });

        // No session row matches the id, so the plane says who is asking: the leg's worker.
        (await CliIn(node, agent: true, "ffffffff-0000-4000-8000-000000000000", plane, "flight", "merge", "TIA1", "cafe0000"))
            .ShouldContain("merge is the controller's, not a flight session's");
        (await CliIn(node, agent: true, null, Path.Combine(plane, "sub"), "flight", "land", "TIA1"))
            .ShouldContain("land is the controller's, not a flight session's");

        // The same command from outside every plane is an assistant's, and runs at once; a human's CLI in the plane is the person's.
        var outside = await CliIn(node, agent: true, null, node.Folder, "flight", "merge", "TIA1", "cafe0000");
        outside.ShouldNotContain("not a flight session's");
        outside.ShouldNotContain("is the controller's");
        (await CliIn(node, agent: false, null, plane, "flight", "merge", "TIA1", "cafe0000")).ShouldNotContain("not a flight session's");
    }

    [Fact]
    public async Task ANewFlightsPlannerNoRowKnowsYetIsThatFlightsSessionAndMayNotMergeAnyFlight()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var plane = Directory.CreateDirectory(Path.Combine(node.Folder, "TIA1")).FullName;
        node.State.PutFlight(Flight("TIA1") with { Worktree = plane });
        node.State.PutFlight(Flight("TIA2"));
        node.State.Sessions().ShouldBeEmpty();

        // The CLI in the plane, no leg, no row and a session id the node has not polled: it is sent as the flight's planner.
        foreach (var target in new[] { "TIA1", "TIA2" })
        {
            (await CliIn(node, agent: true, "ffffffff-0000-4000-8000-000000000000", plane, "flight", "merge", target, "cafe0000"))
                .ShouldContain("merge is the controller's, not a flight session's: ");
        }
        (await CliIn(node, agent: true, null, plane, "flight", "land", "TIA2")).ShouldContain("land is the controller's, not a flight session's");

        // The node reads the planner's name as the flight's whether or not the CLI named it: refused with no session row.
        var machine = (await node.Client.GetNodeAsync(Ct)).MachinePrefix.ToLowerInvariant();
        (await Error(node, Command(Verbs.Merge, "flight/TIA2", $"{machine}-tia1-plan", true, "cafe0000"))).ShouldStartWith("merge is the controller's, not a flight session's: ");
        (await Error(node, Command(Verbs.Check, "flight/TIA2/parser", $"{machine}-tia1-plan", true, "1"))).ShouldBe("flight/TIA2/parser is not TIA1's: a flight's session acts on its own flight only");
        node.State.Cards().ShouldBeEmpty();

        // An assistant elsewhere is not fenced by that: its merge of the same flight runs at once, recorded.
        (await Error(node, Command(Verbs.Merge, "flight/TIA2", "pilot-33", true, "cafe0000"))).ShouldNotStartWith("merge is the controller's, not a flight session's: ");
    }

    private static Task<string> Cli(NodeHarness node, params string[] args) => Cli(node, agent: false, args);

    private static Task<string> Cli(NodeHarness node, bool agent, params string[] args) => CliIn(node, agent, sessionId: null, node.Folder, args);

    private static async Task<string> CliIn(NodeHarness node, bool agent, string? sessionId, string cwd, params string[] args)
    {
        var output = new StringWriter();
        await PilotCommand.RunAsync(["--node-socket", node.SocketPath, .. args], cwd, output, output, Ct, agent: agent, sessionId: sessionId);
        return output.ToString();
    }

    private static Task<string> Status(NodeHarness node) => Cli(node, "status");

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
