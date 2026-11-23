using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1285's last acceptance criterion, end to end on the in-memory node (git is the in-memory model, GitHub is the fake, the workers are
/// the fake launcher, the wakes and the repeat detector read a fake clock): with autopilot on, a stalled flight is woken and an investigator
/// sends it round again, a repeated failure is found by the detector and an investigator files an issue plus a flight for it, and only a
/// decision that is truly the person's rings, once, saying what was tried. The first two ring nobody. With autopilot off, which is the
/// default, none of it happens. Every command goes through the node's own socket as the session that would send it.
/// </summary>
[Trait("Category", "Slow")] // a real node on a socket of its own: the commands go through the wire as the sessions would send them
public sealed class AutopilotV2Tests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    private string Repo => Path.Combine(_node.Folder, "tianwen");

    private string InvestigatorName => $"{_node.Options.Machine.ToLowerInvariant()}-investigate-tia7";

    public async ValueTask InitializeAsync()
    {
        _node = await NodeHarness.StartOnModelAsync(Ct);
        SeedRepo.MakeOn(_node.Repos!, _node.Folder);
    }

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private async Task<CommandResult> Send(string who, string verb, string target, string[] args, string? why = null, bool agent = true) =>
        (await _node.Client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who, Agent = agent, Why = why }, Ct)).Response.ShouldNotBeNull();

    private async Task AutopilotOn() =>
        (await Send("gui", Verbs.Set, Verbs.NodeTarget, ["autopilot", "on"], agent: false)).Error.ShouldBeNull();

    private int Rings() => _node.State.Cards().Count(c => c.Bin == CardBin.Ring);

    private FlightRow Flight(string code) => _node.State.Flights().Single(f => f.Code == code);

    [Fact]
    public async Task AStalledFlightIsMovedARepeatIsFiledAndOnlyThePersonsDecisionRingsOnce()
    {
        await AutopilotOn();
        var rungBefore = Rings();

        // 1. A stalled flight: the wake names it; the investigator sends its leg round again on the evidence, a Handled line with the why.
        (await Send("gui", Verbs.CreateFlight, Verbs.RepoTarget(Repo), ["7", "Parser"], agent: false)).Ok.ShouldBeTrue();
        await _node.PlannedAsync();
        _node.State.PutLeg(new LegRow { Flight = "TIA7", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = "du1-tia7-parser" });
        var later = new ManualTime(DateTimeOffset.UtcNow.AddHours(3));
        var failures = _node.App.Services.GetRequiredService<FailureLedger>();
        var wakes = new AutopilotWakes(_node.State, new RepeatDetector(_node.State, failures, later), later) { On = () => true, Stall = () => TimeSpan.FromMinutes(60) };
        wakes.Look();
        wakes.Pending.Select(w => w.Text).ShouldContain(t => t.Contains("Flight TIA7 has shown no progress"));
        var rounds = _node.State.Legs().Single(l => l.Flight == "TIA7").GoArounds;

        var around = await Send(InvestigatorName, Verbs.GoAround, Verbs.LegTarget("TIA7", "parser"), ["the worker hung on a prompt"], why: "job j962 shows the worker idle for 70 minutes on a permission prompt");

        around.Ok.ShouldBeTrue(around.Error);
        _node.State.Legs().Single(l => l.Flight == "TIA7").GoArounds.ShouldBe(rounds + 1);
        _node.State.Cards().ShouldContain(c => c.Kind == CardKinds.NodeRan && c.Detail!.Contains("job j962"));
        _node.State.Decisions().ShouldContain(d => d.By == InvestigatorName && d.Reason.Contains("job j962"));
        Rings().ShouldBe(rungBefore);

        // 2. A failure that came twice on the flight: the detector finds the pair and makes one task; the investigator files the issue and a flight.
        var at = later.GetUtcNow();
        failures.Add(new FailureRecord("tianwen", 11, "TIA7", "aaa1111", FailureWhere.Ci, 1001, "Tests.Parser.Reads", "expected 1 but was 2", "sig-parser", "", 0, at.AddMinutes(-30)));
        failures.Add(new FailureRecord("tianwen", 11, "TIA7", "bbb2222", FailureWhere.Ci, 1002, "Tests.Parser.Reads", "expected 1 but was 2", "sig-parser", "", 0, at.AddMinutes(-5)));
        var repeat = new RepeatDetector(_node.State, failures, later).Look().ShouldHaveSingleItem();
        repeat.Task.Kind.ShouldBe(CardKinds.RepeatTask);
        Rings().ShouldBe(rungBefore);

        var issue = await Send(InvestigatorName, Verbs.CreateIssue, Verbs.RepoTarget(Repo), ["Parser test fails twice", "the failure of j1001 and j1002"], why: "task " + repeat.Task.Id + ": runs 1001 and 1002 fail the same test");
        issue.Ok.ShouldBeTrue(issue.Error);
        var number = issue.Made.ShouldNotBeNull();
        _node.GitHub.IssuesCreated.ShouldContain(i => i.Title == "Parser test fails twice");
        var flight = await Send(InvestigatorName, Verbs.CreateFlight, Verbs.RepoTarget(Repo), [number, "Parser test fails twice"], why: $"issue #{number} files the cause found in task {repeat.Task.Id}");
        flight.Ok.ShouldBeTrue(flight.Error);
        Flight($"TIA{number}").Issue.ShouldBe(int.Parse(number));
        Rings().ShouldBe(rungBefore);

        // 3. A decision truly the person's: a card bound for the ring is held; the run's ring outcome lets it through once, with what was tried.
        var card = _node.State.RaiseCard(CardBin.Ring, "worker-idle", "flight/TIA7/node", "TIA7 needs a token", "the GitHub token expired (job j971)",
            [new CardChoice { Label = "Renew", Verb = Verbs.Say, Target = "session/abc" }]);
        _node.State.Cards().Single(c => c.Id == card.Id).Bin.ShouldBe(CardBin.ForYou);
        Rings().ShouldBe(rungBefore);
        var run = _node.App.Services.GetRequiredService<Flights.Investigator>().Runs.Single(r => r.Key == "TIA7");

        var ring = await Send(run.Name, Verbs.Investigated, "TIA7", [Flights.Investigator.RingOutcome, "Tried: a rerun of checks (j971). Needed: only you can renew the GitHub token."]);

        ring.Ok.ShouldBeTrue(ring.Error);
        Rings().ShouldBe(rungBefore + 1);
        _node.State.Cards().Single(c => c.Id == card.Id).Detail.ShouldNotBeNull().ShouldContain("Tried: a rerun of checks");

        // The digest says what was done, filed and escalated.
        var detail = new Digest(_node.State, later) { Autopilot = () => true }.Compose(later.GetUtcNow().AddHours(-6), later.GetUtcNow().AddHours(1)).Detail;
        detail.ShouldContain("Autopilot:");
        detail.ShouldContain("job j962");
        detail.ShouldContain("Tried: a rerun of checks");
    }

    [Fact]
    public async Task WithAutopilotOffByDefaultNothingIsWokenStartedHeldOrLetThrough()
    {
        (await Send("gui", Verbs.CreateFlight, Verbs.RepoTarget(Repo), ["7", "Parser"], agent: false)).Ok.ShouldBeTrue();
        await _node.PlannedAsync();
        _node.State.PutLeg(new LegRow { Flight = "TIA7", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = "du1-tia7-parser" });
        var later = new ManualTime(DateTimeOffset.UtcNow.AddHours(3));
        var wakes = new AutopilotWakes(_node.State, new RepeatDetector(_node.State, new FailureLedger(), later), later) { On = () => false };
        wakes.Look();
        wakes.Pending.ShouldBeEmpty();

        // An investigator's goal verbs are refused, whatever its why.
        var refused = await Send(InvestigatorName, Verbs.GoAround, Verbs.LegTarget("TIA7", "parser"), ["hung"], why: "job j962 shows the worker idle");
        refused.Ok.ShouldBeFalse();
        _node.State.Legs().Single(l => l.Flight == "TIA7").GoArounds.ShouldBe(0);

        // A ring is as before: it rings at once, held by no one.
        var card = _node.State.RaiseCard(CardBin.Ring, "worker-idle", "flight/TIA7/node", "TIA7 needs a token", "expired",
            [new CardChoice { Label = "Renew", Verb = Verbs.Say, Target = "session/abc" }]);
        _node.State.Cards().Single(c => c.Id == card.Id).Bin.ShouldBe(CardBin.Ring);
        _node.Workers.Started.ShouldNotContain(s => s.Name.Contains("-investigate-"));
        new Digest(_node.State, later).Compose(later.GetUtcNow().AddHours(-6), later.GetUtcNow().AddHours(1)).Detail.ShouldNotContain("Autopilot:");
    }
}
