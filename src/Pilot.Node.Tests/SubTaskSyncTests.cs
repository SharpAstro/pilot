using System.Net;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight's legs kept as sub-tasks of its work item, for the repos that opt in (<c>sub-task-repos</c>): a leg planned gets one,
/// a leg done ticks it, a leg removed closes it, a refusing tracker never fails the leg, and a person's own sub-task is listed on the flight row.
/// </summary>
[Trait("Category", "Slow")]
public sealed class SubTaskSyncTests : LegCommanderTestsBase
{
    private readonly StringWriter _log = new();

    public SubTaskSyncTests()
    {
        _legs.Error = _log;
        // The flight's work item, issue 1, exists: the fake numbers a sub-issue after the highest issue it knows.
        _github.Issues[1] = "Planets";
        // The test base's flight commander has no GitHub of its own: its trackers are the one for the fake.
        _flights.Trackers = new Trackers([new GitHubTracker(_github)]);
    }

    private void OptIn(params string[] repos) => _legs.Models = new RoleModels(new NodeSettings { SubTaskRepos = repos });

    private async Task FinishDone(string name)
    {
        await Leg(Verbs.StartLeg, $"flight/TIA1/{name}");
        if (L(name).State == LegState.Planned)
        {
            await Leg(Verbs.ValidateLeg, $"flight/TIA1/{name}", "go");
        }
        foreach (var i in Enumerable.Range(1, L(name).Checklist.Length))
        {
            await Leg(Verbs.Check, $"flight/TIA1/{name}", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        (await Leg(Verbs.FinishLeg, $"flight/TIA1/{name}", "done")).Ok.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARepoNotOptedInWritesNothingToItsTracker()
    {
        OptIn("some-other-repo");
        await FlightWithTwoLegs();

        _github.SubIssuesCreated.ShouldBeEmpty();
        _state.Legs("TIA1").ShouldAllBe(l => l.SubTask == null);
        _state.Flights().Single().OtherSubTasks.ShouldBeNull();

        await Leg(Verbs.RemoveLeg, "flight/TIA1/render");
        await FinishDone("parser");
        _github.SubIssuesCreated.ShouldBeEmpty();
        _github.ClosedIssues.ShouldBeEmpty();
        _github.ClosedNotPlanned.ShouldBeEmpty();
    }

    [Fact]
    public async Task APlannedLegGetsASubIssueTitledWithItsNameAndGoalAndRecordsIt()
    {
        OptIn("tianwen");
        await FlightWithTwoLegs();

        _github.SubIssuesCreated.Select(s => (s.Parent, s.Title)).ShouldBe([(1, "TIA1/parser: Parse the ephemeris"), (1, "TIA1/render: Draw the planets")]);
        var parser = L("parser").SubTask.ShouldNotBeNull();
        parser.Id.ShouldBe("2");
        parser.Url.ShouldBe("https://github.com/owner/repo/issues/2");
        L("render").SubTask.ShouldNotBeNull().Id.ShouldBe("3");
    }

    [Fact]
    public async Task APlannedLegOfAMultiLineGoalIsTitledOnOneLine()
    {
        OptIn("tianwen");
        await FlightWithTwoLegs();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=docs", "goal=Write the docs\nand the changelog", "done-when=The docs exist")).Ok.ShouldBeTrue();

        _github.SubIssuesCreated[^1].Title.ShouldBe("TIA1/docs: Write the docs and the changelog");
    }

    [Fact]
    public async Task PlanningTheSameLegAgainKeepsItsSubIssue()
    {
        OptIn("tianwen");
        await FlightWithTwoLegs();
        var before = L("parser").SubTask;

        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse the ephemeris and the almanac", "done-when=The parser tests pass")).Ok.ShouldBeTrue();

        _github.SubIssuesCreated.Count.ShouldBe(2);
        L("parser").Goal.ShouldBe("Parse the ephemeris and the almanac");
        L("parser").SubTask.ShouldBe(before);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALegDoneClosesItsSubIssueAndTheOtherStaysOpen()
    {
        OptIn("tianwen");
        await FlightWithTwoLegs();
        var parser = L("parser").SubTask!;

        await FinishDone("parser");

        _github.ClosedIssues.Select(c => c.Number).ShouldBe([int.Parse(parser.Id)]);
        _github.SubIssues[1].Single(i => i.Id == parser.Id).Done.ShouldBeTrue();
        _github.SubIssues[1].Single(i => i.Id == L("render").SubTask!.Id).Done.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedLegTheControllerMarksDoneAnywayClosesItsSubIssueToo()
    {
        OptIn("tianwen");
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "no ephemeris here")).Ok.ShouldBeTrue();
        _github.ClosedIssues.ShouldBeEmpty();

        var accepted = await _legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["accepted", "the ephemeris is the issue"], Who = "controller" }, Ct);

        accepted.Ok.ShouldBeTrue();
        _github.ClosedIssues.Select(c => c.Number).ShouldBe([int.Parse(L("parser").SubTask!.Id)]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALegRemovedClosesItsSubIssueAsNotPlannedAndTheLegGoesOn()
    {
        OptIn("tianwen");
        await FlightWithTwoLegs();
        var render = L("render").SubTask!;

        (await Leg(Verbs.RemoveLeg, "flight/TIA1/render", "not needed")).Ok.ShouldBeTrue();

        _state.Legs("TIA1").Select(l => l.Name).ShouldBe(["parser"]);
        _github.ClosedNotPlanned.Select(c => c.Number).ShouldBe([int.Parse(render.Id)]);
        // Its closed sub-issue stays in GitHub's list: it is told from a person's by the flight's code its title leads with.
        _state.Flights().Single().OtherSubTasks.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARefusingTrackerIsLoggedNeverFailsTheLegAndIsTriedAgainAtTheNextChange()
    {
        OptIn("tianwen");
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _github.SubIssueFails = "GitHub answered 403";

        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse the ephemeris", "done-when=The parser tests pass")).Ok.ShouldBeTrue();

        L("parser").SubTask.ShouldBeNull();
        _log.ToString().ShouldContain("sub-tasks of TIA1: could not add a sub-task for parser in #1: GitHub answered 403");

        // The tracker answers again: the next change writes the leg that was missed, and the new one.
        _github.SubIssueFails = null;
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw the planets", "done-when=The capture shows them")).Ok.ShouldBeTrue();

        _github.SubIssuesCreated.Select(s => s.Title).ShouldBe(["TIA1/parser: Parse the ephemeris", "TIA1/render: Draw the planets"]);
        _state.Legs("TIA1").ShouldAllBe(l => l.SubTask != null);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARefusalToCloseALegDoneIsLoggedAndTheLegIsDoneAllTheSame()
    {
        OptIn("tianwen");
        await FlightWithTwoLegs();
        _github.CloseIssueFails = "GitHub answered 500";

        await FinishDone("parser");

        L("parser").State.ShouldBe(LegState.Done);
        _log.ToString().ShouldContain("could not mark the sub-task of parser done");
        _log.ToString().ShouldContain("GitHub answered 500");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASubIssueAPersonAddedIsListedOnTheFlightRowAndChangesNoLeg()
    {
        OptIn("tianwen");
        _github.SubIssues[1] = [new SubTaskItem("90", "Check the almanac too", false, "https://github.com/owner/repo/issues/90")];
        await FlightWithTwoLegs();

        var others = _state.Flights().Single().OtherSubTasks.ShouldNotBeNull();
        others.ShouldBe([new SubTaskItem("90", "Check the almanac too", false, "https://github.com/owner/repo/issues/90")]);
        _state.Legs("TIA1").Select(l => l.Name).ShouldBe(["parser", "render"]);

        // Refreshed at each sync: the person closes theirs, and it is read as done.
        _github.SubIssues[1][0] = _github.SubIssues[1][0] with { Done = true };
        await FinishDone("parser");

        _state.Flights().Single().OtherSubTasks.ShouldNotBeNull().Select(i => (i.Id, i.Done)).ShouldBe([("90", true)]);
    }

    [Fact]
    public void AFlightJournaledBeforeSubTasksReadsBackWithNone()
    {
        var json = JsonSerializer.Serialize(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        }, PilotJsonContext.Default.FlightRow);
        json.ShouldNotContain("OtherSubTasks");
        JsonSerializer.Deserialize(json, PilotJsonContext.Default.FlightRow)!.OtherSubTasks.ShouldBeNull();

        var with = new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
            OtherSubTasks = [new SubTaskItem("90", "Check the almanac too", false, "u")],
        };
        JsonSerializer.Deserialize(JsonSerializer.Serialize(with, PilotJsonContext.Default.FlightRow), PilotJsonContext.Default.FlightRow)!.OtherSubTasks.ShouldBe(with.OtherSubTasks);
    }

    /// <summary>A Shortcut that keeps one story's tasks: a POST adds one, a PUT sets it complete, a DELETE takes it off, a GET reads the story.</summary>
    private sealed class StoryShortcut : HttpMessageHandler
    {
        private const string Story = "https://api.app.shortcut.com/api/v3/stories/7";

        private readonly List<(long Id, string Description, bool Complete)> _tasks = [(500, "a person's own task", false)];

        public List<(HttpMethod Method, string Url)> Asked { get; } = [];

        public IReadOnlyList<(long Id, string Description, bool Complete)> Tasks => _tasks;

        public bool Refuse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add((request.Method, url));
            JsonElement? body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement;
            if (Refuse && request.Method != HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (request.Method == HttpMethod.Post && url == Story + "/tasks")
            {
                var id = _tasks.Max(t => t.Id) + 1;
                _tasks.Add((id, body!.Value.GetProperty("description").GetString()!, false));
                return Json(HttpStatusCode.Created, $$"""{"id":{{id}}}""");
            }
            if (url.StartsWith(Story + "/tasks/", StringComparison.Ordinal) && long.TryParse(url[(Story.Length + "/tasks/".Length)..], out var task))
            {
                var at = _tasks.FindIndex(t => t.Id == task);
                if (request.Method == HttpMethod.Delete)
                {
                    _tasks.RemoveAt(at);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (request.Method == HttpMethod.Put)
                {
                    _tasks[at] = _tasks[at] with { Complete = body!.Value.GetProperty("complete").GetBoolean() };
                    return Json(HttpStatusCode.OK, "{}");
                }
            }
            if (request.Method == HttpMethod.Get && url == Story)
            {
                var tasks = string.Join(',', _tasks.Select(t => $$"""{"id":{{t.Id}},"description":{{JsonSerializer.Serialize(t.Description)}},"complete":{{(t.Complete ? "true" : "false")}}}"""));
                return Json(HttpStatusCode.OK, $$"""{"id":7,"name":"Cards","app_url":"https://app.shortcut.com/bullclip/story/7/cards","tasks":[{{tasks}}]}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    }

    private async Task<StoryShortcut> ShortcutFlight()
    {
        var handler = new StoryShortcut();
        _flights.Trackers = new Trackers([new GitHubTracker(_github), new ShortcutTracker(new HttpClient(handler), () => "secret")]);
        OptIn("tianwen");
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["sc-7", "Cards"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _state.Flights().Single().Code.ShouldBe("TIA7");
        return handler;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AShortcutStoryKeepsATaskPerLegTicksItWhenDoneDeletesItWhenRemovedAndListsAPersonsOwn()
    {
        var shortcut = await ShortcutFlight();
        (await Leg(Verbs.PlanLeg, "flight/TIA7", "name=parser", "goal=Parse the ephemeris", "done-when=The parser tests pass", "item=Write it")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA7", "name=render", "goal=Draw the planets", "done-when=The capture shows them")).Ok.ShouldBeTrue();

        var parser = _state.Legs("TIA7").Single(l => l.Name == "parser").SubTask.ShouldNotBeNull();
        shortcut.Tasks.Select(t => (t.Id.ToString(), t.Description)).ShouldBe([("500", "a person's own task"), (parser.Id, "TIA7/parser: Parse the ephemeris"), ("502", "TIA7/render: Draw the planets")]);
        _state.Flights().Single().OtherSubTasks.ShouldNotBeNull().Select(i => i.Title).ShouldBe(["a person's own task"]);
        _github.SubIssuesCreated.ShouldBeEmpty();

        await Leg(Verbs.StartLeg, "flight/TIA7/parser");
        await Leg(Verbs.Check, "flight/TIA7/parser", "1");
        (await Leg(Verbs.FinishLeg, "flight/TIA7/parser", "done")).Ok.ShouldBeTrue();
        shortcut.Tasks.Single(t => t.Id.ToString() == parser.Id).Complete.ShouldBeTrue();
        shortcut.Tasks.Single(t => t.Id == 502).Complete.ShouldBeFalse();

        (await Leg(Verbs.RemoveLeg, "flight/TIA7/render")).Ok.ShouldBeTrue();
        shortcut.Tasks.Select(t => t.Id).ShouldBe([500, long.Parse(parser.Id)]);
        _state.Flights().Single().OtherSubTasks.ShouldNotBeNull().Select(i => i.Title).ShouldBe(["a person's own task"]);
    }

    [Fact]
    public async Task AShortcutThatRefusesIsLoggedAndTheLegIsPlannedAllTheSame()
    {
        var shortcut = await ShortcutFlight();
        shortcut.Refuse = true;

        (await Leg(Verbs.PlanLeg, "flight/TIA7", "name=parser", "goal=Parse the ephemeris", "done-when=The parser tests pass")).Ok.ShouldBeTrue();

        _state.Legs("TIA7").Single().SubTask.ShouldBeNull();
        _log.ToString().ShouldContain("could not add a sub-task for parser in sc-7");
        _log.ToString().ShouldContain("503");
    }
}
