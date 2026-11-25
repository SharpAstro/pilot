using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The last of pilot #1325's Done whens. Every flight and leg verb with a predicate is walked over flights in each state (flying, held,
/// on the owner's hold, landed), legs in each layout, each review state and a PR or none: a card is raised offering the verb, its mark
/// is read, and the choice is answered through <see cref="NodeCommands"/> on fakes. A choice shown available is never refused by a
/// precondition (<see cref="PreconditionWords"/>); one shown unavailable is refused with exactly its mark. A refusal from beyond the
/// node's own state is ignored: the fake GitHub's answer (no PR on the fake), the in-memory git's (a plane it does not hold), the
/// worker launcher's. Those are not the node's preconditions, which are the only thing this walk is about. A second test keeps a new
/// verb from being offered on a card with no predicate.
/// </summary>
public sealed class ChoiceAvailabilityWalkTests : IClassFixture<ChoiceAvailabilityWalkTests.Walk>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    public sealed class Walk : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8]);
        private SeededRepos _repos = null!;

        internal NodeHarness Node { get; private set; } = null!;

        internal int Next;

        public async ValueTask InitializeAsync()
        {
            _repos = new SeededRepos(_root);
            SeedRepo.MakeOn(_repos, _root);
            Node = await NodeHarness.StartAsync(Ct, git: _repos.Git);
        }

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            _repos.Dispose();
        }
    }

    private readonly Walk _walk;

    public ChoiceAvailabilityWalkTests(Walk walk) => _walk = walk;

    /// <summary>The node's own preconditions, in the words <see cref="VerbPreconditions"/> and the hold rules use: a refusal matching one is a precondition's.</summary>
    private static readonly Regex PreconditionWords = new(
        @"has landed|is held|is on hold by|has legs not done|is flying: one leg at a time|comes first and is|a go-around is for a leg|; a go-around starts it again|has not been reviewed|, not passed|has an open design question|^\S+ is (flying|landed|held|planned|failed|done)$",
        RegexOptions.CultureInvariant);

    private static readonly string[] States = ["flying", "held", "onhold", "landed"];

    private static readonly string[] Legs = ["planned", "flying", "failed", "done", "mixed", "done-then-planned"];

    private static readonly string[] Reviews = ["none", "reviewing", "changes", "passed", "passed-pr"];

    public static TheoryData<string, string, string, string> Matrix()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var verb in VerbPreconditions.Covered.Order())
        {
            foreach (var state in States)
            {
                foreach (var legs in Legs)
                {
                    foreach (var review in Reviews)
                    {
                        data.Add(verb, state, legs, review);
                    }
                }
            }
        }
        return data;
    }

    private static LegRow Leg(string code, string name, int order, LegState state) => new()
    {
        Flight = code, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state,
        Worker = state == LegState.Flying ? $"du1-{code.ToLowerInvariant()}-{name}" : null,
    };

    private static (string Name, LegState State)[] Layout(string legs) => legs switch
    {
        "planned" => [("a", LegState.Planned)],
        "flying" => [("a", LegState.Flying)],
        "failed" => [("a", LegState.Failed)],
        "done" => [("a", LegState.Done)],
        "mixed" => [("a", LegState.Done), ("b", LegState.Flying), ("c", LegState.Planned)],
        _ => [("a", LegState.Done), ("b", LegState.Planned)],
    };

    private static ReviewRow? ReviewOf(string review) => review switch
    {
        "none" => null,
        "reviewing" => new ReviewRow { State = ReviewState.Reviewing, Reviewer = "du1-x-review", StartedAt = Start },
        "changes" => new ReviewRow { State = ReviewState.Changes, Reviewer = "du1-x-review", StartedAt = Start },
        _ => new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-x-review", StartedAt = Start },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task AChoiceShownAvailableIsNeverRefusedByAPreconditionAndOneShownUnavailableIsRefusedWithItsMark(string verb, string state, string legs, string review)
    {
        var node = _walk.Node;
        var code = $"WLK{Interlocked.Increment(ref _walk.Next)}";
        var layout = Layout(legs);
        var flight = new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"/r/{code}", Base = "main",
            State = state == "landed" ? FlightState.Landed : state == "held" ? FlightState.Held : FlightState.Flying, CreatedAt = Start,
            OnHold = state == "onhold" ? new FlightOnHold { By = "the owner", Reason = "wait", Since = Start } : null,
            Review = ReviewOf(review),
            PullRequestUrl = review == "passed-pr" ? "https://github.com/x/tianwen/pull/63" : null,
        };
        node.State.PutFlight(flight);
        for (var i = 0; i < layout.Length; i++)
        {
            node.State.PutLeg(Leg(code, layout[i].Name, i + 1, layout[i].State));
        }
        var target = verb is Verbs.GoAround or Verbs.StartLeg ? Verbs.LegTarget(code, layout[^1].Name) : Verbs.FlightTarget(code);
        var args = verb is Verbs.GoAround ? new[] { "why" } : verb is Verbs.Merge or Verbs.RerunChecks ? new[] { "abc1234" } : [];
        // A second choice no precondition refuses, so a card whose every choice waits on something that clears by itself is not closed under the walk.
        var card = node.State.RaiseCard(CardBin.ForYou, "walk-card", Verbs.FlightTarget(code), "a card", null,
        [
            new CardChoice { Label = "Press", Verb = verb, Target = target, Args = args },
            new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = Verbs.FlightTarget(code) },
        ]);
        var fresh = node.State.Cards().SingleOrDefault(c => c.Id == card.Id);
        if (fresh is null || fresh.State != CardState.Open)
        {
            // A card the node closes on raising offers nothing to refuse.
            return;
        }
        var mark = fresh.Choices.First(c => c.Label == "Press");

        var commands = node.App.Services.GetRequiredService<NodeCommands>();
        var answered = await commands.RunAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Press"], Who = "cli" }, Ct);

        var where = $"{verb} on {code} (flight {state}, legs {legs}, review {review})";
        if (mark.Status == ChoiceStatus.Unavailable)
        {
            answered.Ok.ShouldBeFalse(where + " is marked unavailable and was accepted");
            answered.Error.ShouldBe($"Press was refused: {mark.Note}", where);
        }
        else if (answered.Error is { } error)
        {
            PreconditionWords.IsMatch(error).ShouldBeFalse($"{where} is shown available and was refused by a precondition: {error}");
        }
    }

    // Verbs a card choice offers that no flight or leg predicate can speak for: they target a session, a job, a process, a card, the node, an idea
    // or a grant, so VerbPreconditions neither covers nor lists them.
    private static readonly HashSet<string> NotFlightOrLeg =
    [
        Verbs.Say, Verbs.Tell, Verbs.Set, Verbs.Update, Verbs.NoteIdea, Verbs.AnswerGrant, Verbs.EndPoller, Verbs.EndStray, Verbs.RestartWatchtower,
        Verbs.EndSession, Verbs.Stop, Verbs.CancelJob, Verbs.Dismiss,
    ];

    [Fact]
    [Trait("Category", "Invariant")]
    public void EveryFlightOrLegVerbACardOffersHasAPredicateOrAReasonItHasNone()
    {
        var verbs = typeof(Verbs).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        var offered = CardKindCatalog.All.Where(k => k.Choices is not null)
            .SelectMany(k => k.Choices!.Select(name => (k.Kind, Name: name, Verb: verbs.TryGetValue(name, out var v) ? v : name)))
            .ToList();

        var unknown = offered
            .Where(o => !NotFlightOrLeg.Contains(o.Verb) && !VerbPreconditions.Covered.Contains(o.Verb) && !VerbPreconditions.NoPrecondition.ContainsKey(o.Verb))
            .Select(o => $"{o.Name} (card {o.Kind})")
            .Distinct()
            .ToList();
        unknown.ShouldBeEmpty("a card offers a verb VerbPreconditions neither covers (Covered, with a predicate in For) nor lists with a reason it has none (NoPrecondition): "
            + string.Join(", ", unknown));

        VerbPreconditions.NoPrecondition.Where(p => string.IsNullOrWhiteSpace(p.Value)).ShouldBeEmpty("every no-precondition verb says why");
        VerbPreconditions.Covered.Intersect(VerbPreconditions.NoPrecondition.Keys).ShouldBeEmpty("a verb is covered or has none, not both");
        foreach (var verb in VerbPreconditions.Covered)
        {
            // For must answer the verb: a covered verb whose switch arm was removed would pass the guard and refuse nothing.
            var flight = new FlightRow { Code = "G1", Repo = "r", RepoPath = "/r", Issue = 1, Branch = "b", Worktree = "/w", Base = "main", State = FlightState.Landed, CreatedAt = Start };
            var leg = Leg("G1", "a", 1, LegState.Planned);
            VerbPreconditions.For(verb, flight, leg, [leg], []).ShouldNotBeNull($"{verb} is listed as covered but For has no refusal for a landed flight");
        }
    }
}
