using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The files a job leaves in its artifacts directory (pilot #1082): attached to the ended row, served by the node's route, held to a
/// per-job limit with a message, swept with the job's log, and listed and fetched by <c>pilot job</c>. PILOT.md says how a leg asks for
/// the GUI lane. All on fakes: the fake launcher stands in for the process, and a file written by the test for what it leaves.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class GuiArtifactsTests : JobTestsBase
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public GuiArtifactsTests() : base(fakeLauncher: true)
    {
    }

    private JobRow EndedJob(NodeHarness node, string id, JobArtifact[]? artifacts = null, string? note = null, DateTimeOffset? endedAt = null)
    {
        var jobs = Path.Combine(_root, "node-jobs");
        Directory.CreateDirectory(jobs);
        var row = new JobRow
        {
            Id = id, Command = "tianwen-gui", Cwd = _root, State = JobState.Succeeded, LogPath = Path.Combine(jobs, $"{id}.log"), Gui = true,
            Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-9), EndedAt = endedAt ?? DateTimeOffset.UtcNow, ExitCode = 0,
            Artifacts = artifacts, ArtifactsNote = note,
        };
        File.WriteAllText(row.LogPath, "done\n");
        node.State.PutJob(row);
        return row;
    }

    [Fact]
    public async Task APngWrittenToTheArtifactsDirectoryShowsOnTheEndedRowWithItsNameAndSize()
    {
        var jobs = Idle().Jobs;
        jobs.MaxRunning = 2;
        jobs.SiblingsOf = _ => [];
        jobs.GuiSlots = 1;
        jobs.DesktopProbe = () => null;
        var cwd = Path.Combine(_root, "tianwen");
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [Verbs.GuiArg, cwd, "0", Sleep(30)], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        await Until(id, j => j.State == JobState.Running);
        var dir = Fake!.Job(id).Launch.Environment.ShouldNotBeNull()[JobRunner.ArtifactsVariable];
        await File.WriteAllBytesAsync(Path.Combine(dir, "main.png"), Png, Ct);

        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
        var ended = await Until(id, j => j.EndedAt is not null);

        var artifact = ended.Artifacts.ShouldNotBeNull().ShouldHaveSingleItem();
        artifact.Name.ShouldBe("main.png");
        artifact.Bytes.ShouldBe(Png.Length);
        ended.ArtifactsNote.ShouldBeNull();
    }

    [Fact]
    public async Task ArtifactsOverTheRunnersLimitAreRemovedAndTheRowSaysSoFromTheRunner()
    {
        // #1085: the over-limit note comes from the runner's own collect at the job's end, not a hand-written row: a small limit set here.
        var jobs = Idle().Jobs;
        jobs.MaxRunning = 2;
        jobs.SiblingsOf = _ => [];
        jobs.GuiSlots = 1;
        jobs.DesktopProbe = () => null;
        jobs.ArtifactByteLimit = 1;
        var cwd = Path.Combine(_root, "tianwen");
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [Verbs.GuiArg, cwd, "0", Sleep(30)], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        await Until(id, j => j.State == JobState.Running);
        var dir = Fake!.Job(id).Launch.Environment.ShouldNotBeNull()[JobRunner.ArtifactsVariable];
        await File.WriteAllBytesAsync(Path.Combine(dir, "main.png"), Png, Ct);
        Png.Length.ShouldBeGreaterThan(1);

        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
        var ended = await Until(id, j => j.EndedAt is not null);

        ended.Artifacts.ShouldBeNull();
        ended.ArtifactsNote.ShouldNotBeNull().ShouldContain("over the per-job limit of 1 B");
        File.Exists(Path.Combine(dir, "main.png")).ShouldBeFalse("they were removed, none is kept");
    }

    [Fact]
    public async Task TheRouteReturnsAnArtifactsBytesAsImagePngAndRefusesNamesThatEscape()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var row = EndedJob(node, "j21", [new JobArtifact { Name = "main.png", Bytes = Png.Length }]);
        var dir = JobRunner.ArtifactsOf(row);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "main.png"), Png, Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "node-jobs", "secret.txt"), "no", Ct);

        var found = await node.Client.GetJobArtifactAsync("j21", "main.png", Ct);
        var up = await node.Client.GetJobArtifactAsync("j21", "../secret.txt", Ct);
        var slash = await node.Client.GetJobArtifactAsync("j21", "sub/main.png", Ct);
        var back = await node.Client.GetJobArtifactAsync("j21", "..\\secret.txt", Ct);
        var dots = await node.Client.GetJobArtifactAsync("j21", "..", Ct);
        var absent = await node.Client.GetJobArtifactAsync("j21", "other.png", Ct);
        var nojob = await node.Client.GetJobArtifactAsync("j99", "main.png", Ct);

        found.Status.ShouldBe(System.Net.HttpStatusCode.OK);
        found.ContentType.ShouldBe("image/png");
        found.Bytes.ShouldBe(Png);
        foreach (var refused in new[] { up, slash, back, dots, absent })
        {
            refused.Status.ShouldNotBe(System.Net.HttpStatusCode.OK);
            refused.Bytes.ShouldBeEmpty();
        }
        nojob.Status.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public void ResolveRefusesPathSeparatorsAndDotDotAndFindsAPlainFile()
    {
        var dir = Path.Combine(_root, "a");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.txt"), "x");

        JobArtifacts.Resolve(dir, "x.txt").ShouldNotBeNull();
        JobArtifacts.Resolve(dir, "..").ShouldBeNull();
        JobArtifacts.Resolve(dir, "../x.txt").ShouldBeNull();
        JobArtifacts.Resolve(dir, "sub\\x.txt").ShouldBeNull();
        JobArtifacts.Resolve(dir, "").ShouldBeNull();
    }

    [Fact]
    public void ADirectoryOverTheLimitRecordsAMessageNamingTheLimitAndTheTotal()
    {
        var dir = Path.Combine(_root, "big");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[2000]);

        var (files, note) = JobArtifacts.Collect(dir, limit: 4096);

        files.ShouldBeNull();
        note.ShouldNotBeNull().ShouldContain("4.9 KB");
        note.ShouldContain("per-job limit of 4.0 KB");
        Directory.Exists(dir).ShouldBeFalse();
    }

    [Fact]
    public async Task TheDefaultLimitIsHeldByTheRunnerAndShowsOnTheRow()
    {
        // The runner reads the same Collect with the contract's limit: a row's note carries the message through the node's own row.
        await using var node = await NodeHarness.StartAsync(Ct);
        EndedJob(node, "j22", note: "the job's artifacts total 300.0 MB, over the per-job limit of 256.0 MB; they were removed, none is kept");

        var output = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "job", "j22"], _root, output, new StringWriter(), Ct);

        exit.ShouldBe(0);
        output.ToString().ShouldContain("artifacts: the job's artifacts total 300.0 MB, over the per-job limit of 256.0 MB");
        JobArtifact.ByteLimit.ShouldBe(256L * 1024 * 1024);
    }

    [Fact]
    public async Task PilotJobListsTheArtifactsAndFetchesOneToAPath()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var row = EndedJob(node, "j23", [new JobArtifact { Name = "main.png", Bytes = Png.Length }]);
        var dir = JobRunner.ArtifactsOf(row);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "main.png"), Png, Ct);
        var target = Path.Combine(_root, "fetched.png");

        var list = new StringWriter();
        var listExit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "job", "j23"], _root, list, new StringWriter(), Ct);
        var fetch = new StringWriter();
        var fetchExit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "job", "j23", "artifact", "main.png", "--out", target], _root, fetch, new StringWriter(), Ct);
        var refused = new StringWriter();
        var refusedExit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "job", "j23", "artifact", "../j23.log"], _root, refused, new StringWriter(), Ct);

        listExit.ShouldBe(0);
        list.ToString().ShouldContain($"artifact main.png ({Png.Length} bytes)");
        fetchExit.ShouldBe(0);
        (await File.ReadAllBytesAsync(target, Ct)).ShouldBe(Png);
        refusedExit.ShouldNotBe(0);
    }

    // The node has no separate job-log sweep: DiskSweep removes <id>.artifacts on the same scratch-days cutoff as the job's
    // log, once the job ended longer ago than that. "Swept with the log" means that shared cutoff.
    [Fact]
    public async Task TheArtifactsGoWhenTheJobsLogIsSwept()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var old = EndedJob(node, "j24", [new JobArtifact { Name = "main.png", Bytes = Png.Length }], endedAt: DateTimeOffset.UtcNow.AddDays(-400));
        var fresh = EndedJob(node, "j25", [new JobArtifact { Name = "main.png", Bytes = Png.Length }]);
        foreach (var row in new[] { old, fresh })
        {
            var dir = JobRunner.ArtifactsOf(row);
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "main.png"), Png, Ct);
        }

        var sweep = new DiskSweep(Path.Combine(_root, "claude-jobs"), Path.Combine(_root, "tmp"), node.State, TimeProvider.System);
        await sweep.SweepAsync(Ct);

        Directory.Exists(JobRunner.ArtifactsOf(old)).ShouldBeFalse();
        Directory.Exists(JobRunner.ArtifactsOf(fresh)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void PilotMdSaysHowALegAsksForTheGuiLaneAndHasNoEmDash()
    {
        var folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "src", "Pilot.slnx")))
        {
            folder = Path.GetDirectoryName(folder);
        }
        folder.ShouldNotBeNull();
        var text = File.ReadAllText(Path.Combine(folder, "PILOT.md"));

        text.ShouldContain("pilot run --gui -- <plain command>");
        text.ShouldContain("DIR_INSPECTOR");
        text.ShouldContain("Prove what a test can headless");
        text.ShouldContain("pilot job <id> artifact");
        text.ShouldNotContain("nothing here may measure through it");
        text.ShouldNotContain("Debug-only");
        text.ShouldContain("The inspector is in Release builds but off");
        text.ShouldContain("`DIR_INSPECTOR=1` and `DIR_INSPECTOR_TOKEN`");
        text.ShouldNotContain("—");
    }
}
