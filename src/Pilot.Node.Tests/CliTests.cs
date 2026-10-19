using System.Diagnostics;
using Pilot.Cli;
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

    public async ValueTask InitializeAsync()
    {
        // A repo with a bare origin, so a flight's push is a real one.
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(Repo);
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        Git(Repo, "remote", "add", "origin", origin);
        Git(Repo, "push", "-q", "-u", "origin", "main");
        Git(Repo, "remote", "set-head", "origin", "main");
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

        await Ok(_root, "flight", "merge", "TIA1611", "cafe0000", "--rebase");
        _node.GitHub.Merged.ShouldBe([(71, "cafe0000cafe0000", "rebase")]);
        _node.State.Flights().Single().State.ShouldBe(FlightState.Landed);

        var (_, status, _) = await Pilot(_root, "status");
        status.ShouldContain("TIA1611  Planets on the GPU  (tianwen #1611, landed)");
        status.ShouldContain("1. gpu: done, 2/2 steps");
        status.ShouldContain("artifact The planets:");
    }

    [Fact]
    public async Task OutsideAPlaneALegMustBeNamedAndAPlaneWithoutAFlyingLegSaysSo()
    {
        await Ok(Repo, "flight", "create", "7");
        var plane = _node.State.Flights().Single().Worktree;

        var outside = await Pilot(_root, "check", "1");
        (outside.Exit, outside.Error).ShouldBe((2, "pilot: this folder is in no flight's plane; name the leg with --leg <code>/<leg>" + Environment.NewLine));

        var idle = await Pilot(plane, "leg", "finish", "done");
        idle.Error.ShouldContain("TIA7 has no leg flying");

        await Ok(_root, "leg", "plan", "TIA7", "one", "--goal", "g", "--done-when", "d", "--item", "a step");
        (await Pilot(_root, "check", "1", "--leg", "TIA7/one")).Exit.ShouldBe(0);
        _node.State.Legs("TIA7").Single().Checklist.Single().Done.ShouldBeTrue();
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
}
