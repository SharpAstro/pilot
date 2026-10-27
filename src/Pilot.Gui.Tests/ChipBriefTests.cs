using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The brief a Home chip shows on hover (#258): pure text over the lines the feed holds, at most four lines, a long one
/// whole for the tooltip to wrap, a title that has no end ending in an ellipsis.
/// </summary>
public sealed class ChipBriefTests
{
    private static readonly DateTimeOffset At = Live.At;

    private static FlightRow Flight(FlightState state = FlightState.Flying, string? title = "Hover a chip for a brief", params string[] connections) => new()
    {
        Code = "PIL258",
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 258,
        Title = title,
        Branch = "flight/pil258",
        Worktree = "/r/pilot/.claude/worktrees/pil258",
        Base = "main",
        State = state,
        Connections = connections,
        CreatedAt = At.AddHours(-2),
    };

    private static LegRow Leg(string name, int order, LegState state) => new()
    {
        Flight = "PIL258",
        Name = name,
        Order = order,
        Goal = "g",
        DoneWhen = "d",
        State = state,
    };

    private static FlightLine LineOf(FlightRow flight, params LegRow[] legs) =>
        FlightLine.Build([flight], [], At, legs).Single();

    private static string[] Brief(FlightRow flight, params LegRow[] legs) => [.. ChipBrief.Flight(LineOf(flight, legs))];

    private static SessionRow Session(SessionState state, SessionRole role = SessionRole.Worker) => Live.Row("a1b2c3d4", "se1-pil258-chip-briefs", state) with
    {
        Role = role,
        Flight = "PIL258",
        Leg = "chip-briefs",
        LastResponseAt = At.AddMinutes(-3),
    };

    private static JobRow Job(JobState state, DateTimeOffset? queued = null) => new()
    {
        Id = "j115",
        Command = "dotnet test src/Pilot.Gui.Tests -c Release",
        Cwd = "/r/pilot",
        Owner = "a1b2c3d4",
        OwnerName = "se1-pil258-chip-briefs",
        Flight = "PIL258",
        Leg = "chip-briefs",
        State = state,
        LogPath = "/data/jobs/j115.log",
        Timeout = TimeSpan.FromHours(1),
        StartedAt = At.AddSeconds(-125),
        QueuedAt = queued,
    };

    [Fact]
    public void A_flying_flight_says_its_title_its_state_and_the_leg_flying()
    {
        var brief = Brief(Flight(), Leg("parser", 1, LegState.Done), Leg("chip-briefs", 2, LegState.Flying) with { GoArounds = 1 });

        brief.Length.ShouldBe(3);
        brief[0].ShouldBe("Hover a chip for a brief");
        brief[1].ShouldBe("PIL258 · flying");
        brief[2].ShouldBe("leg chip-briefs flying (go-around 1) · 2 legs · 1 done · chip-briefs flying (go-around 1)");
    }

    [Fact]
    public void A_held_flight_says_why_it_is_held()
    {
        var brief = Brief(Flight(FlightState.Held, connections: "PIL5"), Leg("parser", 1, LegState.Planned));

        brief[1].ShouldBe("PIL258 · held: waiting for PIL5");
        brief[2].ShouldStartWith("leg parser planned");
    }

    [Fact]
    public void A_flight_with_a_pr_says_its_number_and_review_state()
    {
        var flight = Flight() with
        {
            PullRequest = 262,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "se1-pil258-review", StartedAt = At },
        };

        var brief = Brief(flight, Leg("chip-briefs", 1, LegState.Done));

        brief.Length.ShouldBe(4);
        brief[3].ShouldBe("PR #262 · review passed");
    }

    [Fact]
    public void A_flight_with_a_pr_and_no_review_yet_says_so()
    {
        Brief(Flight() with { PullRequest = 262 }, Leg("chip-briefs", 1, LegState.Done))[3].ShouldBe("PR #262 · no review yet");
    }

    [Fact]
    public void A_flight_with_no_title_says_its_issue()
    {
        var brief = Brief(Flight(title: null));

        brief[0].ShouldBe("issue #258");
        brief[2].ShouldBe("no legs yet");
    }

    [Fact]
    public void A_blank_title_says_so_rather_than_nothing()
    {
        ChipBrief.Title("   ").ShouldBe("(no title)");
    }

    [Fact]
    public void A_very_long_title_ends_in_an_ellipsis_at_a_word_and_the_rest_stays_whole()
    {
        var title = string.Join(' ', Enumerable.Repeat("a very long flight title that goes on", 12));
        var flight = Flight(title: title) with { PullRequest = 262 };

        var brief = Brief(flight, Leg("chip-briefs", 1, LegState.Flying));

        brief.Length.ShouldBe(4);
        brief[0].Length.ShouldBeLessThanOrEqualTo(ChipBrief.MaxTitle);
        brief[0].ShouldEndWith("…");
        title.ShouldStartWith(brief[0][..^1]);
        brief[1].ShouldBe("PIL258 · flying");
    }

    [Fact]
    public void A_title_that_fits_is_left_whole()
    {
        var title = new string('x', ChipBrief.MaxTitle);

        ChipBrief.Title(title).ShouldBe(title);
    }

    [Fact]
    public void A_title_of_one_long_word_is_cut_with_an_ellipsis()
    {
        var cut = ChipBrief.Title(new string('x', 400));

        cut.Length.ShouldBe(ChipBrief.MaxTitle);
        cut.ShouldEndWith("…");
    }

    [Fact]
    public void A_session_waiting_on_you_says_the_tool_it_asks_and_what_it_waits_on()
    {
        var row = Session(SessionState.Waiting) with
        {
            Asking = "Bash: git push origin main",
            WaitingFor = "approve Bash",
            WaitingSince = At.AddMinutes(-1),
        };

        var brief = ChipBrief.Session(SessionLine.From(row, At));

        brief.ShouldBe([
            "worker · PIL258 / chip-briefs · waiting on you",
            "asks to run Bash: git push origin main",
            "waiting on you: approve Bash",
        ]);
    }

    [Fact]
    public void A_session_waiting_on_you_with_no_tool_says_the_question()
    {
        var row = Session(SessionState.Waiting, SessionRole.Planner) with { WaitingFor = "Which repo should it fly in?" };

        var brief = ChipBrief.Session(SessionLine.From(row, At));

        brief.ShouldContain("waiting on you: Which repo should it fly in?");
    }

    [Fact]
    public void A_session_whose_process_is_gone_says_nothing_can_answer_it()
    {
        var row = Session(SessionState.Waiting) with { WaitingFor = "approve Bash", Gone = true };

        ChipBrief.Session(SessionLine.From(row, At)).ShouldContain("its process is gone: nothing can answer it");
    }

    [Fact]
    public void An_idle_session_says_since_when()
    {
        var brief = ChipBrief.Session(SessionLine.From(Session(SessionState.Idle, SessionRole.Reviewer), At));

        brief.ShouldBe([
            "reviewer · PIL258 / chip-briefs · idle",
            "idle: last response 3m ago",
        ]);
    }

    [Fact]
    public void A_session_with_no_flight_says_just_its_role()
    {
        var row = Live.Row("a1b2c3d4", "stargazer", SessionState.Working) with { Role = SessionRole.Watchtower };

        ChipBrief.Session(SessionLine.From(row, At))[0].ShouldBe("watchtower · working");
    }

    [Fact]
    public void A_running_job_says_what_it_runs_whom_it_is_for_and_how_long_it_has_run()
    {
        var brief = ChipBrief.Job(JobLine.From(Job(JobState.Running) with { Step = "restoring" }, At));

        brief.ShouldBe([
            "dotnet test src/Pilot.Gui.Tests -c Release",
            "for PIL258/chip-briefs · j115",
            "running for 2m 5s, started 2m ago",
            "step: restoring",
        ]);
    }

    [Fact]
    public void A_queued_job_says_how_long_it_has_waited_and_for_what()
    {
        var brief = ChipBrief.Job(JobLine.From(Job(JobState.Queued, queued: At.AddMinutes(-7)) with { Step = "waiting for a slot" }, At));

        brief.ShouldBe([
            "dotnet test src/Pilot.Gui.Tests -c Release",
            "for PIL258/chip-briefs · j115",
            "queued 7m ago",
            "waits on: waiting for a slot",
        ]);
    }

    [Fact]
    public void A_job_with_a_very_long_command_keeps_it_whole_for_the_tooltip_to_wrap()
    {
        var command = "dotnet test " + string.Join(' ', Enumerable.Repeat("src/Some.Very.Long.Project.Tests", 12));

        var brief = ChipBrief.Job(JobLine.From(Job(JobState.Running) with { Command = command }, At));

        brief[0].ShouldBe(command);
        brief.Count.ShouldBeLessThanOrEqualTo(ChipBrief.MaxLines);
    }

    [Fact]
    public void Every_kind_of_chip_carries_its_brief_of_at_most_four_lines()
    {
        var flight = Flight() with { PullRequest = 262 };
        var flights = new FlightsScreen([LineOf(flight, Leg("chip-briefs", 1, LegState.Flying))], "sample", At);
        var sessions = new SessionsScreen([SessionLine.From(Session(SessionState.Working), At)], "sample", At);
        var jobs = new JobsScreen([JobLine.From(Job(JobState.Running) with { Step = "restoring" }, At)], "sample", At);
        var home = new HomeScreen(flights, sessions, jobs, CardsScreen.Empty);

        foreach (var chip in home.FlightChips.Concat(home.SessionChips).Concat(home.JobChips))
        {
            chip.Brief.ShouldNotBeEmpty();
            chip.Brief.Split('\n').Length.ShouldBeLessThanOrEqualTo(ChipBrief.MaxLines);
        }
        home.FlightChips.Single().Brief.ShouldContain("Hover a chip for a brief");
        home.SessionChips.Single().Brief.ShouldContain("worker · PIL258 / chip-briefs · working");
        home.JobChips.Single().Brief.ShouldContain("running for 2m 5s");
    }
}
