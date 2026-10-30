using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The <c>pilot</c> CLI against a real node: one direct flight flown from it alone, and its refusals.</summary>
public sealed class CliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8]);
    private NodeHarness _node = null!;

    private string Repo => Path.Combine(_root, "tianwen");

    [Fact]
    public void PilotNodesPrintsAWatchtowerLineMarkedWhenChurning()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var calm = new WatchtowerHealth { State = SessionState.Idle, Id = "3a1b2c3d4e", StartedAt = now.AddHours(-2), ContextTokens = 41_000, ContextWindow = 200_000, StartsLast24h = 3 };

        NodeTargets.WatchtowerLine(calm, now).ShouldBe("watchtower idle · 3a1b2c3d · 2 h · 41k/200k · 3 started in 24 h");
        NodeTargets.WatchtowerLine(calm with { Churning = true, LastEndedBecause = "process gone" }, now)
            .ShouldEndWith("last ended: process gone  " + WatchtowerWords.ChurningMark);
        NodeTargets.WatchtowerLine(null, now).ShouldBe("no watchtower kept");
    }

    [Fact]
    public async Task PilotNodesStillListsAPeerThatDoesNotAnswer()
    {
        // Nothing listens on port 1.
        (await NodeTargets.WatchtowerLineAsync("127.0.0.1:1", TimeSpan.FromSeconds(1), Ct)).ShouldBe("watchtower: not reached (the node did not answer)");
    }

    public async ValueTask InitializeAsync()
    {
        // A repo with a bare origin, so a flight's push is a real one: the seed, built once a run and copied.
        SeedRepo.Make(_root);
        _node = await NodeHarness.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
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

    private async Task<(int Exit, string Output, string Error)> Pilot(string cwd, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], cwd, output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task Ok(string cwd, params string[] args)
    {
        var (exit, output, error) = await Pilot(cwd, args);
        exit.ShouldBe(0, $"pilot {string.Join(' ', args)}: {output}{error}");
    }

    private async Task<(int Exit, string Output)> Logs(string folder, params string[] args)
    {
        var before = RollingLog.FolderOverride;
        RollingLog.FolderOverride = folder;
        try
        {
            var (exit, output, _) = await Pilot(_root, ["logs", .. args]);
            return (exit, output);
        }
        finally
        {
            RollingLog.FolderOverride = before;
        }
    }

    private static string LogFolder(string root, string name = "logs") => Path.Combine(root, name);

    private static void Seed(string folder, string file, int from, int count)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, file), Enumerable.Range(from, count).Select(i => $"2026-10-28 10:00:00.000 line {i}"));
    }

    [Fact]
    public async Task PilotLogsPrintsBothLogsUnderTheirPathsAndOneWhenNamed()
    {
        var folder = LogFolder(_root);
        Seed(folder, "gui.log", 1, 3);
        Seed(folder, "node.log", 1, 2);

        var (exit, both) = await Logs(folder);
        exit.ShouldBe(0);
        both.ShouldContain($"== gui: {Path.Combine(folder, "gui.log")} ==");
        both.ShouldContain($"== node: {Path.Combine(folder, "node.log")} ==");
        both.ShouldContain("line 3");

        var (_, gui) = await Logs(folder, "gui");
        gui.ShouldContain("== gui:");
        gui.ShouldNotContain("== node:");
        var (_, node) = await Logs(folder, "node");
        node.ShouldContain("== node:");
        node.ShouldNotContain("== gui:");
    }

    [Fact]
    public async Task PilotLogsTailTakesTheLastLinesAndReachesIntoTheRolledFile()
    {
        var folder = LogFolder(_root);
        Seed(folder, "gui.log", 6, 6);
        Seed(folder, "gui.1.log", 1, 5);

        var (_, last) = await Logs(folder, "gui", "--tail", "2");
        last.ShouldContain("line 11");
        last.ShouldContain("line 10");
        last.ShouldNotContain("line 9");

        var (_, reaching) = await Logs(folder, "gui", "--tail", "7");
        var lines = reaching.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.TrimEnd('\r')).ToArray();
        lines.Length.ShouldBe(7);
        lines[0].ShouldEndWith("line 5");
        lines[^1].ShouldEndWith("line 11");

        // Within the current file the rolled one is not read.
        (await Logs(folder, "gui", "--tail", "6")).Output.ShouldNotContain("line 5");
    }

    [Fact]
    public async Task PilotLogsSaysAMissingLogAndExitsZero()
    {
        var folder = LogFolder(_root, "no-logs");
        var (exit, output) = await Logs(folder, "gui");
        exit.ShouldBe(0);
        output.ShouldContain($"no gui log yet at {Path.Combine(folder, "gui.log")}");

        Seed(folder, "node.log", 1, 1);
        var (both, text) = await Logs(folder);
        both.ShouldBe(0);
        text.ShouldContain("no gui log yet at");
        text.ShouldContain("== node:");
    }

    [Fact]
    public async Task TheSessionsListShowsTheNodesCodeBeforeANameWithoutAMachinePrefix()
    {
        var code = _node.Options.Machine.ToLowerInvariant();
        SessionRow Row(string id, string name) => new()
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        };
        _node.State.PutSession(Row("a1b2c3d4", "stargazer"));
        _node.State.PutSession(Row("b2c3d4e5", "zz9-other-worker"));

        var (exit, output, error) = await Pilot(_root, "sessions");

        exit.ShouldBe(0, error);
        output.ShouldContain($"a1b2c3d4  {code}-stargazer  ");
        output.ShouldContain("b2c3d4e5  zz9-other-worker  ");
        output.ShouldNotContain($"{code}-zz9");
    }

    [Fact]
    public void TheSessionsTextLeavesAPrefixedNameAsItIsAndAnUnprefixedOneAsItIsWithoutACode()
    {
        SessionRow Row(string name) => new()
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/r",
            Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        };

        PilotCommand.Sessions([Row("stargazer")], "SE1").ShouldStartWith("a1b2c3d4  se1-stargazer  ");
        PilotCommand.Sessions([Row("de1-stargazer")], "SE1").ShouldStartWith("a1b2c3d4  de1-stargazer  ");
        PilotCommand.Sessions([Row("stargazer")]).ShouldStartWith("a1b2c3d4  stargazer  ");
    }

    [Fact]
    public async Task ADirectFlightIsFlownFromTheCliAloneUpToItsLanding()
    {
        // The controller, in the repo: a flight and its one leg.
        await Ok(Repo, "flight", "create", "1611", "Planets", "on", "the", "GPU");
        var flight = _node.State.Flights().Single();
        flight.Code.ShouldBe("TIA1611");
        await Ok(_root, "leg", "plan", "TIA1611", "gpu", "--goal", "Draw the planets on the GPU", "--done-when", "The capture shows them",
            "--check", "dotnet test", "--budget", "4000000", "--item", "Write the shader", "--item", "Capture it");
        await Ok(_root, "leg", "start", "TIA1611", "gpu");
        var worker = _node.Workers.Started.Single();
        worker.Brief.ShouldContain("check <step number>");
        worker.Brief.ShouldContain("leg finish done");

        // The worker, in its plane: it ticks its own steps, records an artifact and finishes, naming no leg.
        var plane = Path.Combine(flight.Worktree, "src");
        Directory.CreateDirectory(plane);
        File.WriteAllText(Path.Combine(plane, "shader.glsl"), "void main() {}\n");
        File.WriteAllBytes(Path.Combine(plane, "capture.png"), [0x89, 0x50, 0x4E, 0x47]);
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "The planet shader");
        await Ok(plane, "check", "1");
        await Ok(plane, "check", "capture");
        await Ok(plane, "artifact", "capture.png", "The planets");
        await Ok(plane, "leg", "finish", "done", "shader in, capture taken");

        var leg = _node.State.Legs("TIA1611").Single();
        leg.State.ShouldBe(LegState.Done);
        leg.Artifacts.ShouldHaveSingleItem().Location.ShouldBe(Path.Combine(plane, "capture.png"));
        Path.GetFullPath(_node.Checks.Runs.Single().Cwd).ShouldBe(Path.GetFullPath(flight.Worktree));
        // Finished by its own worker: left to say its report, stopped only after a grace.
        _node.Workers.Stopped.ShouldBeEmpty();
        // The black box says the worker ticked them; the node holds it open, so it is read shared.
        using (var box = new StreamReader(new FileStream(Path.Combine(_node.Options.DataFolder, "store", FileJournal.BlackBoxFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            var checks = box.ReadToEnd().Split('\n').Where(l => l.Contains("\"verb\":\"check\"", StringComparison.Ordinal)).ToList();
            checks.Count.ShouldBe(2);
            checks.ShouldAllBe(l => l.Contains($"\"who\":\"{leg.Worker}\"", StringComparison.Ordinal));
        }

        // The controller: the PR, pushed for real, then the merge on the head reviewed, and the flight lands.
        await Ok(_root, "flight", "pr", "TIA1611");
        Git(Repo, "ls-remote", "origin", "flight/tia1611").ShouldNotBeEmpty();
        var created = _node.GitHub.Created.Single();
        (created.Branch, created.Base, created.Title).ShouldBe(("flight/tia1611", "main", "TIA1611: Planets on the GPU"));
        created.Body.ShouldContain("Closes #1611.");
        created.Body.ShouldContain("**gpu**: Draw the planets on the GPU");
        _node.State.Flights().Single().PullRequest.ShouldBe(71);

        // The PR opened a fresh reviewer in the plane; its pass, from there, offers the merge.
        var reviewer = _node.Workers.Started.Last();
        (reviewer.Name, reviewer.Plane).ShouldBe(("se1-tia1611-review".Replace("se1", _node.Options.Machine.ToLowerInvariant()), "tia1611"));
        reviewer.Brief.ShouldContain("Done when: The capture shows them");
        (await Pilot(_root, "cards")).Output.ShouldNotContain("ready to merge");
        await Ok(plane, "review", "pass", "the capture shows the planets; dotnet test passes");
        _node.State.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);

        // The PR rang: a card with the merge on the head it was opened at; answering it merges and lands the flight.
        var (_, cards, _) = await Pilot(_root, "cards");
        cards.ShouldContain("c1  RING  TIA1611's PR #71 is ready to merge");
        cards.ShouldContain("1. Merge: pilot answer c1 1");
        var (_, cardsMarkdown, _) = await Pilot(_root, "cards", "--output-format", "markdown");
        cardsMarkdown.ShouldContain("## c1 · RING · TIA1611's PR #71 is ready to merge");
        cardsMarkdown.ShouldContain("`pilot answer c1 1`");
        (await Pilot(_root, "cards", "--output-format", "rich")).Output.ShouldBe(cardsMarkdown);
        (await Pilot(_root, "cards", "--output-format", "auto")).Output.ShouldBe(cards);
        await Ok(_root, "answer", "c1", "1");
        // By rebase, the node's default merge method, so every commit's subject reaches main (tianwen#1783 was squashed).
        _node.GitHub.Merged.ShouldBe([(71, "cafe0000cafe0000", "rebase")]);
        _node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        var after = _node.State.Cards();
        after.Single(c => c.Id == "c1").Outcome.ShouldBe("Merge");
        after.Single(c => c.Kind == "landed").Bin.ShouldBe(CardBin.ForYou);

        // Landed, it leaves status, which says how many it left out; --all shows it with its legs.
        var (_, inTheAir, _) = await Pilot(_root, "status");
        inTheAir.ShouldNotContain("TIA1611");
        inTheAir.ShouldContain("No flights in the air.");
        inTheAir.ShouldContain("1 landed flight not shown: pilot status --all");
        (await Pilot(_root, "status", "--output-format", "markdown")).Output.ShouldContain("_1 landed flight not shown: `pilot status --all`_");
        var (_, status, _) = await Pilot(_root, "status", "--all");
        status.ShouldContain("TIA1611  Planets on the GPU  (tianwen #1611, landed)");
        status.ShouldContain("1. gpu: done, 2/2 steps");
        status.ShouldContain("artifact The planets:");
        status.ShouldNotContain("not shown");
        (await Pilot(_root, "status", "--all", "--output-format", "markdown")).Output.ShouldContain("### TIA1611 · Planets on the GPU");
    }

    [Fact]
    public async Task OutsideAPlaneALegMustBeNamedAndAPlaneWithoutAFlyingLegSaysSo()
    {
        await Ok(Repo, "flight", "create", "7");
        var plane = _node.State.Flights().Single().Worktree;

        var outside = await Pilot(_root, "check", "1");
        (outside.Exit, outside.Error).ShouldBe((2, "pilot: this folder is in no flight's plane; name the leg with --leg <code>/<leg>" + Environment.NewLine));

        var outsideWait = await Pilot(_root, "leg", "wait", "--for-file", "x.flag");
        (outsideWait.Exit, outsideWait.Error).ShouldBe((2, "pilot: this folder is in no flight's plane; name the leg with --leg <code>/<leg>" + Environment.NewLine));

        var idle = await Pilot(plane, "leg", "finish", "done");
        idle.Error.ShouldContain("TIA7 has no leg flying");

        await Ok(_root, "leg", "plan", "TIA7", "one", "--goal", "Do the work", "--done-when", "Its check passes", "--item", "a step");
        (await Pilot(_root, "check", "1", "--leg", "TIA7/one")).Exit.ShouldBe(0);
        _node.State.Legs("TIA7").Single().Checklist.Single().Done.ShouldBeTrue();
    }

    [Fact]
    public async Task PilotJobsNamesTheJobHoldingTheQueueOnlyWhileAJobWaits()
    {
        JobRow Job(string id, JobState state) => new()
        {
            Id = id, Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = state,
            LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        // Recovery runs in the background and fails any Running or Queued row it finds as left behind: let it finish first.
        await _node.App.Services.GetRequiredService<Pilot.Node.Flights.JobRunner>().Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _node.State.PutJob(Job("j1", JobState.Running));

        (await Pilot(_root, "jobs")).Output.ShouldNotContain("holds the queue");

        _node.State.PutJob(Job("j2", JobState.Queued));
        (await Pilot(_root, "jobs")).Output.ShouldContain("j1 holds the queue (se1-tia1-parser), held for 5m");
    }

    [Fact]
    public async Task PilotJobCancelForceSendsCancelJobWithForceAndTheJobEndsForced()
    {
        var runner = _node.App.Services.GetRequiredService<Pilot.Node.Flights.JobRunner>();
        runner.MinFreeMemory = 0;
        await runner.Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // Not a bare sleep, which the node refuses: the echo makes it a job.
        await Ok(_root, "run", "--", "sleep 30; echo slept");
        var id = _node.State.Jobs().Single().Id;
        for (var i = 0; _node.State.Jobs().Single().State != JobState.Running; i++)
        {
            i.ShouldBeLessThan(500, "the job did not start");
            await Task.Delay(20, Ct);
        }

        var (exit, output, error) = await Pilot(_root, "job", id, "cancel", "--force");

        exit.ShouldBe(0, $"{output}{error}");
        JobRow ended;
        for (var i = 0; (ended = _node.State.Jobs().Single()).EndedAt is null; i++)
        {
            i.ShouldBeLessThan(300, "the job did not end");
            await Task.Delay(100, Ct);
        }
        ended.State.ShouldBe(JobState.Cancelled);
        ended.Error.ShouldNotBeNull().ShouldStartWith("cancelled (forced) by ");
    }

    [Fact]
    public async Task PilotJobCancelWithoutForceIsAPlainCancel()
    {
        var runner = _node.App.Services.GetRequiredService<Pilot.Node.Flights.JobRunner>();
        runner.MinFreeMemory = 0;
        await runner.Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Ok(_root, "run", "--", "sleep 30; echo slept");
        var id = _node.State.Jobs().Single().Id;
        for (var i = 0; _node.State.Jobs().Single().State != JobState.Running; i++)
        {
            i.ShouldBeLessThan(500, "the job did not start");
            await Task.Delay(20, Ct);
        }

        await Ok(_root, "job", id, "cancel");
        JobRow ended;
        for (var i = 0; (ended = _node.State.Jobs().Single()).EndedAt is null; i++)
        {
            i.ShouldBeLessThan(300, "the job did not end");
            await Task.Delay(100, Ct);
        }

        ended.Error.ShouldStartWith("cancelled by ");
    }

    [Fact]
    public async Task APausedJobReadsPausedWithItsTimeInJobsAndJob()
    {
        await _node.App.Services.GetRequiredService<Pilot.Node.Flights.JobRunner>().Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _node.State.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PausedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var jobs = await Pilot(_root, "jobs");
        jobs.Output.ShouldContain("j1  paused since ");
        jobs.Output.ShouldNotContain("running");
        (await Pilot(_root, "job", "j1")).Output.ShouldContain("j1  paused since ");
    }

    /// <summary>A flight whose first leg is done and whose second, render, has started: its validator is flying.</summary>
    private async Task<string> FlightWithAValidatorFlying(string number)
    {
        await Ok(Repo, "flight", "create", number);
        var flight = _node.State.Flights().Single();
        await Ok(_root, "leg", "plan", flight.Code, "parser", "--goal", "Parse", "--done-when", "It parses");
        await Ok(_root, "leg", "plan", flight.Code, "render", "--goal", "Draw", "--done-when", "It draws", "--item", "Draw it");
        await Ok(_root, "leg", "start", flight.Code, "parser");
        _node.Workers.Started.Clear();
        // The node advances by itself: render has an earlier leg, so its validator starts, not its worker.
        await Ok(flight.Worktree, "leg", "finish", "done", "parsed");
        var validator = _node.Workers.Started.ShouldHaveSingleItem();
        validator.Brief.ShouldContain("leg validate go");
        validator.Brief.ShouldContain("leg validate no-go");
        return flight.Worktree;
    }

    [Fact]
    public async Task AValidatorAnsweringGoFromThePlaneStartsTheLegsWorker()
    {
        var plane = await FlightWithAValidatorFlying("8");

        await Ok(plane, "leg", "validate", "go", "files", "and", "types", "all", "there");

        var render = _node.State.Legs("TIA8").Single(l => l.Name == "render");
        (render.State, render.Validation).ShouldBe((LegState.Flying, LegValidation.Go));
        _node.Workers.Started.Count.ShouldBe(2);
        _node.Workers.Started.Last().Name.ShouldEndWith("-tia8-render");
        render.Worker.ShouldBe(_node.Workers.Started.Last().Name);
    }

    [Fact]
    public async Task AValidatorAnsweringNoGoFromThePlaneRingsAndTheLegStaysPlanned()
    {
        var plane = await FlightWithAValidatorFlying("9");

        await Ok(plane, "leg", "validate", "no-go", "Planet.cs is gone");

        var render = _node.State.Legs("TIA9").Single(l => l.Name == "render");
        (render.State, render.Validation).ShouldBe((LegState.Planned, LegValidation.NoGo));
        _node.Workers.Started.Count.ShouldBe(1);
        var card = _node.State.Cards(openOnly: true).Single(c => c.Kind == LegCommander.NoGoKind);
        (card.Bin, card.Detail).ShouldBe((CardBin.Ring, "Planet.cs is gone"));

        // Named, not by the folder, and refused when nothing is waiting.
        var again = await Pilot(plane, "leg", "validate", "go", "--leg", "TIA9/render");
        again.Exit.ShouldBe(1);
    }

    /// <summary>The node under test is not a pilot-node, so it refuses: the refusal shows <c>node restart</c> reached the node as <see cref="Verbs.RestartNode"/>, why words and all.</summary>
    [Fact]
    public async Task NodeRestartIsSentToTheNodeWhichRefusesWhenItIsNotAPilotNode()
    {
        var (exit, output, _) = await Pilot(_root, "node", "restart", "the", "settings", "changed");

        exit.ShouldBe(1);
        output.ShouldContain("is not pilot-node, so it cannot start a successor of itself");
        _node.State.Cards().ShouldNotContain(c => c.Kind == "restart");
        (await Pilot(_root, "node")).Exit.ShouldBe(2);
        var help = await Pilot(_root, "node", "restart", "--help");
        help.Output.ShouldContain("carry on");
        help.Output.ShouldContain("--why");
    }

    [Fact]
    public async Task ARefusalSaysWhyAndUsageAndAMissingNodeHaveTheirOwnExitCodes()
    {
        var refused = await Pilot(_root, "flight", "land", "TIA9");
        (refused.Exit, refused.Output.Trim()).ShouldBe((1, "refused: land flight/TIA9: There is no flight TIA9"));

        (await Pilot(_root, "flight", "launch")).Exit.ShouldBe(2);
        (await Pilot(_root)).Exit.ShouldBe(2);

        var output = new StringWriter();
        var error = new StringWriter();
        var gone = await PilotCommand.RunAsync(["--node-socket", Path.Combine(_root, "none.sock"), "status"], _root, output, error, Ct);
        gone.ShouldBe(3);
        error.ToString().ShouldContain("no node answers");
    }

    [Fact]
    public async Task TheWatchtowersCommandsAreSpelledAndItsCardsRaised()
    {
        await Ok(_root, "ring", "flight/TIA1", "TIA1 forks", "--detail", "SQLite or JSON");
        await Ok(_root, "flag", "node", "A quiet note");
        await Ok(_root, "handled", "node", "Went around TIA1/parser");

        _node.State.Cards().Select(c => (c.Bin, c.Title)).ShouldBe(
            [(CardBin.Handled, "Went around TIA1/parser"), (CardBin.ForYou, "A quiet note"), (CardBin.Ring, "TIA1 forks")]);
        var tell = await Pilot(_root, "tell", "hello");
        (tell.Exit, tell.Output.Trim()).ShouldBe((1, "refused: tell watchtower: this node keeps no watchtower (--no-watchtower)"));
    }

    [Fact]
    public void ACommandFromAClaudeSessionIsFromThatSessionByName()
    {
        var snapshot = new SnapshotEvent
        {
            Seq = 1,
            At = DateTimeOffset.UnixEpoch,
            Stream = "s",
            Notes = [],
            Sessions =
            [
                new SessionRow
                {
                    Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background,
                    Cwd = "/w", Role = SessionRole.Watchtower, ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0,
                    StartedAt = DateTimeOffset.UnixEpoch,
                },
            ],
        };

        PilotCommand.Caller(snapshot, "A1B2C3D4-0000-4000-8000-000000000000").ShouldBe("du1-watchtower");
        PilotCommand.Caller(snapshot, "ffffffff-0000-4000-8000-000000000000").ShouldBe("cli");
        PilotCommand.Caller(snapshot, null).ShouldBe("cli");
    }

    [Fact]
    public void ACommandFromASessionIdTheNodeResumedPastIsStillThatSession()
    {
        var snapshot = new SnapshotEvent
        {
            Seq = 1,
            At = DateTimeOffset.UnixEpoch,
            Stream = "s",
            Notes = [],
            Sessions =
            [
                new SessionRow
                {
                    Id = "b1b2b3b4", SessionId = "b1b2b3b4-0000-4000-8000-000000000000", EarlierSessionIds = ["A1A2A3A4-0000-4000-8000-000000000000"],
                    Name = "du1-tia1-parser", Kind = SessionKind.Background, Cwd = "/w", Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 1,
                    State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
                },
            ],
        };

        PilotCommand.Caller(snapshot, "b1b2b3b4-0000-4000-8000-000000000000").ShouldBe("du1-tia1-parser");
        PilotCommand.Caller(snapshot, "a1a2a3a4-0000-4000-8000-000000000000").ShouldBe("du1-tia1-parser");
    }

    private static SnapshotEvent SnapshotWith(MachineRow? machine) => new()
    {
        Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [], Machine = machine,
    };

    private static MachineRow Machine(double cpu, long used, long total, MachineGpu? gpu = null) => new()
    {
        SampledAt = DateTimeOffset.UnixEpoch, CpuPercent = cpu, Cores = 8, MemoryUsed = used, MemoryTotal = total, Gpu = gpu,
    };

    [Fact]
    public void StatusSaysTheMachineInALineWithItsGpu()
    {
        const long Gb = 1L << 30;
        var machine = Machine(34, 21 * Gb, 32 * Gb, new MachineGpu { Percent = 81, MemoryUsed = (long)(6.1 * Gb), MemoryTotal = 12 * Gb });

        machine.Line().ShouldBe("cpu 34% · gpu 81% (6.1/12 GB) · mem 21/32 GB");
        PilotCommand.Status(SnapshotWith(machine)).ShouldContain("machine  cpu 34% · gpu 81% (6.1/12 GB) · mem 21/32 GB");
        StatusMarkdown.Build(SnapshotWith(machine)).ShouldContain("- **machine** cpu 34% · gpu 81% (6.1/12 GB) · mem 21/32 GB");
    }

    [Fact]
    public void StatusSaysTheRedoAndModelOfALegsCurrentWorker()
    {
        var flight = new FlightRow
        {
            Code = "TIA1", Repo = "o/r", RepoPath = "r", Issue = 1, Branch = "flight/tia1", Worktree = "w", Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, GoArounds = 3, AttemptModel = "opus", AttemptEffort = "xhigh" };
        var snapshot = SnapshotWith(null) with { Flights = [flight], Legs = [leg] };

        PilotCommand.Status(snapshot).ShouldContain("go-around 3, redo 3, on Opus at xhigh");
        StatusMarkdown.Build(snapshot).ShouldContain("go-around 3, redo 3, on Opus at xhigh");
    }

    [Fact]
    public void StatusLeavesTheGpuOutOfTheMachineLineWhenTheNodeHasNone()
    {
        const long Gb = 1L << 30;
        var machine = Machine(7, 3 * Gb / 2, 16 * Gb);

        machine.Line().ShouldBe("cpu 7% · mem 1.5/16 GB");
        PilotCommand.Status(SnapshotWith(machine)).ShouldContain("machine  cpu 7% · mem 1.5/16 GB");
        StatusMarkdown.Build(SnapshotWith(machine)).ShouldContain("- **machine** cpu 7% · mem 1.5/16 GB");
        // A GPU whose memory the platform does not say still shows its share.
        Machine(7, Gb, 16 * Gb, new MachineGpu { Percent = 40 }).Line().ShouldBe("cpu 7% · gpu 40% · mem 1/16 GB");
    }

    [Fact]
    public void StatusPrintsNoMachineLineWhenTheSnapshotHasNoRow()
    {
        PilotCommand.Status(SnapshotWith(null)).ShouldNotContain("machine");
        StatusMarkdown.Build(SnapshotWith(null)).ShouldNotContain("machine");
    }

    [Fact]
    public async Task StatusWritesMarkdownForOutputFormatMarkdownAndPlainTextWhenNoHostCanRenderAuto()
    {
        await Ok(Repo, "flight", "create", "1611", "Planets", "on", "the", "GPU");
        var flight = _node.State.Flights().Single();
        await Ok(_root, "leg", "plan", "TIA1611", "gpu", "--goal", "Draw the planets", "--done-when", "The capture shows them", "--item", "Write the shader", "--item", "Capture it");
        await Ok(_root, "leg", "start", "TIA1611", "gpu");
        await Ok(flight.Worktree, "check", "1");
        File.WriteAllBytes(Path.Combine(flight.Worktree, "capture.png"), [0x89, 0x50, 0x4E, 0x47]);
        await Ok(flight.Worktree, "artifact", "capture.png", "The planets");

        var (_, markdown, _) = await Pilot(_root, "status", "--output-format", "markdown");
        markdown.ShouldContain("## Flights");
        markdown.ShouldContain("### TIA1611 · Planets on the GPU");
        markdown.ShouldContain("| 1 | gpu | flying | 1/2 |");
        // The node's copy is shown, not the plane's file, so it still renders once the plane's file is gone.
        var kept = _node.State.Flights().Single().Code;
        var copy = Directory.GetFiles(Path.Combine(Path.GetFullPath(_node.Options.DataFolder), "attachments", kept)).Single().Replace('\\', '/');
        markdown.ShouldContain($"![The planets]({copy})");
        markdown.ShouldNotContain(Path.Combine(flight.Worktree, "capture.png").Replace('\\', '/'));
        File.Delete(Path.Combine(flight.Worktree, "capture.png"));
        (await Pilot(_root, "status", "--output-format", "markdown")).Output.ShouldContain($"![The planets]({copy})");

        // pilot attachments lists the flight's copies with their paths, and every one with no argument.
        var (_, listed, _) = await Pilot(_root, "attachments", "TIA1611");
        listed.ShouldContain("TIA1611/gpu");
        listed.ShouldContain("The planets");
        listed.Replace('\\', '/').ShouldContain(copy);
        (await Pilot(_root, "attachments")).Output.ShouldBe(listed);
        (await Pilot(_root, "attachments", "TIA9999")).Output.ShouldBe("No attachments for TIA9999.\n");

        // The tests' host has no terminal, as the GUI's command bar and the MCP server have none: auto is plain, and rich the source.
        var (_, auto, _) = await Pilot(_root, "status");
        auto.ShouldContain("TIA1611  Planets on the GPU");
        auto.ShouldNotContain("##");
        (await Pilot(_root, "status", "--output-format", "rich")).Output.ShouldBe(markdown);
        (await Pilot(_root, "status", "--output-format", "plain")).Output.ShouldBe(auto);

        var (exit, _, error) = await Pilot(_root, "status", "--output-format", "html");
        exit.ShouldBe(2);
        error.ShouldContain("'html' not recognized");
    }

    [Fact]
    public async Task StatusHandsAutoAndRichToTheHostsRendererAndWritesPlainTextWhenItSettlesOnPlain()
    {
        await Ok(Repo, "flight", "create", "1611", "Planets", "on", "the", "GPU");
        var asked = new List<string>();
        async Task<string> Run(bool renders, params string[] args)
        {
            var output = new StringWriter();
            await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _root, output, new StringWriter(), Ct, rich: async (format, markdown, writer, _) =>
            {
                asked.Add(format);
                if (renders)
                {
                    await writer.WriteAsync("RENDERED " + markdown.Split('\n')[0].TrimEnd('\r'));
                }
                return renders;
            });
            return output.ToString();
        }

        (await Run(renders: true, "status")).ShouldBe("RENDERED # pilot status");
        (await Run(renders: true, "status", "--output-format", "rich")).ShouldBe("RENDERED # pilot status");
        (await Run(renders: false, "status")).ShouldContain("TIA1611  Planets on the GPU");
        (await Run(renders: true, "status", "--output-format", "plain")).ShouldContain("TIA1611  Planets on the GPU");
        asked.ShouldBe(["auto", "rich", "auto"]);
    }

    [Theory]
    [InlineData("a|b", @"a\|b")]
    [InlineData("snake_case *x* [y] <z> `q`", @"snake\_case \*x\* \[y\] \<z\> \`q\`")]
    [InlineData("issue #12", "issue #12")]
    public void StatusMarkdownEscapesWhatMarkdownWouldActOn(string text, string escaped) =>
        Markdown.Text(text).ShouldBe(escaped);

    [Fact]
    public async Task CardsWriteMarkdownForOutputFormatMarkdownAndPlainTextWhenNoHostCanRenderAuto()
    {
        (await Pilot(_root, "cards", "--output-format", "markdown")).Output.ShouldBe("No open cards.\n");

        var snapshot = new SnapshotEvent { Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [], Cards =
        [
            new CardRow
            {
                Id = "c1", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/X1", Title = "older", At = DateTimeOffset.UnixEpoch,
                State = CardState.Open,
            },
            new CardRow
            {
                Id = "c2", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/X1", Title = "A|B is *ready*", Detail = "first\nsecond",
                Link = "https://example.com/pull/7", At = DateTimeOffset.UnixEpoch.AddHours(1), State = CardState.Open,
                Choices = [new CardChoice { Label = "Merge", Verb = "merge", Target = "flight/X1", Status = "passing" }],
            },
            new CardRow
            {
                Id = "c3", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/X0", Title = "done", At = DateTimeOffset.UnixEpoch,
                State = CardState.Closed, Outcome = "dismissed", ClosedBy = "cli",
            },
        ]};

        var markdown = CardsMarkdown.Build(snapshot);
        markdown.ShouldContain(@"## c2 · RING · A\|B is \*ready\*");
        markdown.ShouldContain("first\\\nsecond");
        markdown.ShouldContain("[https://example.com/pull/7](https://example.com/pull/7)");
        markdown.ShouldContain("1. Merge (checks passing): `pilot answer c2 1`");
        markdown.ShouldContain("or: `pilot dismiss c2`");
        markdown.IndexOf("## c2", StringComparison.Ordinal).ShouldBeLessThan(markdown.IndexOf("## c1", StringComparison.Ordinal));
        markdown.ShouldNotContain("c3");
        CardsMarkdown.Build(snapshot, all: true).ShouldContain("**closed**: dismissed, by cli");
        PilotCommand.Cards(snapshot).ShouldContain("c2  RING  A|B is *ready*");
    }

    [Fact]
    public void ReposWriteMarkdownWithTablesAndLeavePlainAsItWas()
    {
        RepoStatus[] rows =
        [
            new()
            {
                Name = "pilot", Path = "C:/r/pilot", Tracked = true, Flights = 2, DefaultBranch = "main", Branch = "main", Ahead = 1, Behind = 3,
                Worktrees = [new WorktreeStatus { Path = "C:/r/pilot/.claude/worktrees/x", Branch = "flight/X1", Flight = "X1", Dirty = true, Files = 2, Added = 5, Removed = 1 }],
                PullRequests =
                [
                    new PullRequestStatus { Number = 7, Title = "A|B is *ready*", Head = "flight/X1", Url = "https://example.com/pull/7", Flight = "X1", Checks = "passing", Labels = ["pilot node: SE1", "bug"] },
                    new PullRequestStatus { Number = 8, Title = "wip", Head = "wip", Draft = true },
                ],
                Errors = ["gh offline"],
            },
            new() { Name = "other", Path = "C:/r/other", Branch = null },
        ];

        var markdown = ReposMarkdown.Build(rows, all: false);
        markdown.ShouldContain("## pilot");
        markdown.ShouldContain("main, clean, ahead 1, behind 3 of main, 2 flights");
        markdown.ShouldContain("| flight/X1 | X1 | dirty | 2 | +5 -1 |");
        markdown.ShouldContain(@"| [#7 A\|B is \*ready\*](https://example.com/pull/7) | flight/X1 | X1 | ready | passing | pilot node: SE1, bug |");
        markdown.ShouldContain("| #8 wip | wip |  | draft |  |  |");
        markdown.ShouldContain("- gh offline");
        markdown.ShouldNotContain("other");
        ReposMarkdown.Build(rows, all: true).ShouldContain("(detached), clean, 0 flights, untracked");
        ReposMarkdown.Build([], all: false).ShouldBe(PilotCommand.Repos([], all: false));
        PilotCommand.Repos(rows, all: false).ShouldContain("PR #7 A|B is *ready*  flight/X1  X1  ready  checks passing  labels pilot node: SE1, bug");
        PilotCommand.Repos(rows, all: false).ShouldNotContain("draft  labels");
    }

    [Fact]
    public void PullRequestsPrintGroupedByRepoWithTheirLabelsAsPlainTextAndAsMarkdownTables()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        PullRequestRow Pr(string repo, int number, string title, PullRequestKind kind, string label, bool needsMe, int hoursAgo) => new()
        {
            Repo = repo, Number = number, Title = title, Url = $"https://github.com/{repo}/pull/{number}", Kind = kind, Label = label, NeedsMe = needsMe,
            Author = "sebgod", UpdatedAt = now.AddHours(-hoursAgo),
        };
        var row = new PullRequestsRow
        {
            Orgs = ["SharpAstro", "sebgod"], ReadAt = now.AddMinutes(-2),
            PullRequests =
            [
                Pr("SharpAstro/tianwen", 3, "A|B is *wip*", PullRequestKind.ChecksRunning, "Checks running 4/7", false, 30),
                Pr("SharpAstro/pilot", 7, "Ready one", PullRequestKind.Ready, "Ready", true, 3) with { Flight = "PIL7", Review = PullRequestReview.Passed, ReviewUrl = "https://github.com/SharpAstro/pilot/pull/7#issuecomment-1" },
                Pr("sebgod/mercury", 9, "Old", PullRequestKind.Conflicts, "Conflicts", true, 24 * 400),
            ],
            Errors = [new PullRequestsError { Org = "sebgod", Message = "gh offline" }],
        };

        var plain = PullRequestsMarkdown.Plain(row, needsMe: false, now).ReplaceLineEndings("\n");
        plain.ShouldContain("3 open · 1 ready · 1 checks running · 1 conflicts · read 2m ago");
        // An org line with its counts, its repos indented under it, their PRs under those.
        plain.ShouldContain("SharpAstro  2 open · 1 ready · 1 checks running\n  SharpAstro/pilot  1\n      #7 Ready one  Ready  PIL7  sebgod  review passed  updated 3h ago");
        plain.ShouldContain("      #3 A|B is *wip*  Checks running 4/7  sebgod  updated 1d ago");
        plain.ShouldContain("updated 400d ago");
        // The org that could not be read says so under its own line, with its PRs of the last read.
        plain.ShouldContain("sebgod  1 open · 1 conflicts\n  ! gh offline\n  sebgod/mercury  1\n");
        // The orgs, then their repos, by name, so a group never moves when a PR's state changes.
        new[] { "sebgod  1 open", "sebgod/mercury  1", "SharpAstro  2 open", "SharpAstro/pilot  1", "SharpAstro/tianwen  1" }.Select(l => plain.IndexOf(l, StringComparison.Ordinal)).ShouldBeInOrder();

        var needsMe = PullRequestsMarkdown.Plain(row, needsMe: true, now).ReplaceLineEndings("\n");
        needsMe.ShouldNotContain("tianwen");
        needsMe.ShouldContain("#9 Old");
        // The counts stay the whole org's, as the summary line's are the whole row's.
        needsMe.ShouldContain("SharpAstro  2 open · 1 ready · 1 checks running\n  SharpAstro/pilot  1\n");

        var markdown = PullRequestsMarkdown.Build(row, needsMe: false, now).ReplaceLineEndings("\n");
        markdown.ShouldContain("## [SharpAstro](https://github.com/SharpAstro) · 2 open · 1 ready · 1 checks running\n\n### [SharpAstro/pilot](https://github.com/SharpAstro/pilot) (1)");
        markdown.ShouldContain("## [sebgod](https://github.com/sebgod) · 1 open · 1 conflicts\n\n- gh offline\n\n### [sebgod/mercury]");
        markdown.ShouldContain("| Ready | [#7 Ready one](https://github.com/SharpAstro/pilot/pull/7) | PIL7 | sebgod | [passed](https://github.com/SharpAstro/pilot/pull/7#issuecomment-1) | 3h ago |");
        markdown.ShouldContain(@"[#3 A\|B is \*wip\*]");

        // The row's rate-limit note stays at the foot, on its own; an org with nothing to show still prints when its read failed.
        var limited = row with { Note = "Rate-limited by GitHub until 14:05; reading again then", Orgs = ["SharpAstro", "sebgod", "quiet"] };
        PullRequestsMarkdown.Plain(limited, needsMe: false, now).ReplaceLineEndings("\n").ShouldEndWith("quiet  0 open\n! Rate-limited by GitHub until 14:05; reading again then\n");
        PullRequestsMarkdown.Build(limited, needsMe: false, now).ReplaceLineEndings("\n").ShouldEndWith("## [quiet](https://github.com/quiet) · 0 open\n\n- Rate-limited by GitHub until 14:05; reading again then\n");
        // Nothing needs the person in an org that failed: its error is still said, and an org with nothing and no error is not.
        var failed = new PullRequestsRow { Orgs = ["SharpAstro", "sebgod"], ReadAt = now, PullRequests = [row.PullRequests[0]], Errors = row.Errors };
        PullRequestsMarkdown.Plain(failed, needsMe: true, now).ReplaceLineEndings("\n").ShouldBe("No pull requests need you in SharpAstro, sebgod.\n! sebgod: gh offline\n");

        PullRequestsMarkdown.Plain(null, needsMe: false, now).ShouldContain("pilot prs --refresh");
        PullRequestsMarkdown.Plain(new PullRequestsRow { Orgs = ["SharpAstro", "sebgod"], ReadAt = now }, needsMe: false, now).ShouldBe("No open pull requests in SharpAstro, sebgod.\n");
        PullRequestsMarkdown.Plain(new PullRequestsRow { Orgs = ["SharpAstro"], ReadAt = now, PullRequests = [row.PullRequests[0]] }, needsMe: true, now).ShouldBe("No pull requests need you in SharpAstro.\n");
    }

    [Fact]
    public void PullRequestsHintHowToFixAReviewThatAsksForChangesInBothOutputsAndNothingElse()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        PullRequestRow Pr(int number, string? flight, string? review) => new()
        {
            Repo = "SharpAstro/pilot", Number = number, Title = $"T{number}", Url = $"https://github.com/SharpAstro/pilot/pull/{number}", Kind = PullRequestKind.Ready,
            Label = "Ready", Author = "sebgod", UpdatedAt = now, Flight = flight, Review = review,
        };
        var row = new PullRequestsRow
        {
            Orgs = ["SharpAstro"], ReadAt = now,
            PullRequests = [Pr(1, null, PullRequestReview.Changes), Pr(2, "PIL2", PullRequestReview.Changes), Pr(3, null, PullRequestReview.Passed), Pr(4, "PIL4", null)],
        };
        var plain = PullRequestsMarkdown.Plain(row, needsMe: false, now).ReplaceLineEndings("\n");
        var markdown = PullRequestsMarkdown.Build(row, needsMe: false, now).ReplaceLineEndings("\n");
        foreach (var (output, prefix) in new[] { (plain, "          -> "), (markdown, "- #") })
        {
            output.ShouldContain("pilot review fix SharpAstro/pilot#1");
            output.ShouldContain("PIL2's review-changes card with Fix the review's findings");
            // Two hints, for the two rows whose review asks for changes; none for a passed or an unreviewed one.
            output.Split('\n').Count(l => l.Contains("review asks for changes")).ShouldBe(2);
            output.ShouldNotContain("#3: review");
            output.ShouldNotContain("PIL4's");
        }
        plain.ShouldContain("          -> review asks for changes: pilot review fix SharpAstro/pilot#1\n");
        markdown.ShouldContain("- #1: review asks for changes: pilot review fix SharpAstro/pilot#1\n- #2: ");
    }
}
