using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An image recorded as an artifact is kept by the node: copied under its data folder, served by its route, there when the
/// plane is gone, a grant's on the LAN, and removed when the flight lands or the session is archived.
/// </summary>
public sealed class AttachmentTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly string _plane = Path.Combine(Path.GetTempPath(), "p2a-" + Guid.NewGuid().ToString("N")[..8]);
    private NodeHarness? _node;

    public AttachmentTests() => Directory.CreateDirectory(_plane);

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
        }
        if (Directory.Exists(_plane))
        {
            Directory.Delete(_plane, recursive: true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private string Shot(string name = "shot.png", byte[]? bytes = null)
    {
        var path = Path.Combine(_plane, name);
        File.WriteAllBytes(path, bytes ?? Png);
        return path;
    }

    private async Task<NodeHarness> NodeWithLegAsync(int? lanPort = null)
    {
        _node = await NodeHarness.StartAsync(Ct, lanPort: lanPort);
        _node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/tia1", Worktree = _plane, Base = "origin/main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        _node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
        return _node;
    }

    private static Task<CommandResult> AddAsync(NodeHarness node, string target, string location, params string[] title) =>
        node.Client.SendAsync(new CommandRequest { Verb = Verbs.AddArtifact, Target = target, Args = [location, .. title], Who = "tests" }, Ct)
            .ContinueWith(t => t.Result.Response ?? new CommandResult { Seq = 0, Verb = Verbs.AddArtifact, Target = target, Who = "tests", Ok = false, Error = t.Result.Error }, Ct);

    private string AttachmentsFolder(NodeHarness node) => Path.Combine(node.Options.DataFolder, "attachments");

    [Fact]
    public async Task AnImageOnALegIsCopiedIntoTheDataFolderAndItsArtifactNamesTheCopy()
    {
        var node = await NodeWithLegAsync();

        var result = await AddAsync(node, "flight/TIA1/parser", Shot(), "The shot");

        result.Ok.ShouldBeTrue(result.Error);
        var artifact = node.State.Legs("TIA1").Single().Artifacts.ShouldHaveSingleItem();
        (artifact.Title, artifact.AttachmentExt).ShouldBe(("The shot", "png"));
        artifact.Attachment.ShouldNotBeNullOrEmpty();
        var copy = Path.Combine(AttachmentsFolder(node), "TIA1", artifact.Attachment + ".png");
        File.ReadAllBytes(copy).ShouldBe(Png);
    }

    [Fact]
    public async Task TheCopyIsServedByTheRouteAndOutlivesTheFileItCameFrom()
    {
        var node = await NodeWithLegAsync();
        var shot = Shot();
        await AddAsync(node, "flight/TIA1/parser", shot, "The shot");
        var id = node.State.Legs("TIA1").Single().Artifacts.Single().Attachment!;

        var served = await node.Client.GetAttachmentAsync(id, Ct);
        (served.Status, served.ContentType).ShouldBe((HttpStatusCode.OK, "image/png"));
        served.Bytes.ShouldBe(Png);

        // The plane goes; the node's copy does not.
        File.Delete(shot);
        Directory.Delete(_plane, recursive: true);
        (await node.Client.GetAttachmentAsync(id, Ct)).Bytes.ShouldBe(Png);
        (await node.Client.GetAttachmentAsync("nosuchid", Ct)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AUrlOrAFileThatIsNotAnImageIsNotCopied()
    {
        var node = await NodeWithLegAsync();
        var notes = Path.Combine(_plane, "notes.txt");
        File.WriteAllText(notes, "hello");

        (await AddAsync(node, "flight/TIA1/parser", "https://example.com/page.png", "Page")).Ok.ShouldBeTrue();
        (await AddAsync(node, "flight/TIA1/parser", notes, "Notes")).Ok.ShouldBeTrue();

        node.State.Legs("TIA1").Single().Artifacts.ShouldAllBe(a => a.Attachment == null && a.AttachmentExt == null);
        Directory.Exists(AttachmentsFolder(node)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnImageThatIsNotThereIsAnErrorThatSaysSo()
    {
        var node = await NodeWithLegAsync();

        var result = await AddAsync(node, "flight/TIA1/parser", Path.Combine(_plane, "missing.png"), "Gone");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("there is no file");
        node.State.Legs("TIA1").Single().Artifacts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARecordedTitleAgainReplacesItsCopy()
    {
        var node = await NodeWithLegAsync();
        await AddAsync(node, "flight/TIA1/parser", Shot(), "The shot");
        var first = node.State.Legs("TIA1").Single().Artifacts.Single().Attachment!;

        await AddAsync(node, "flight/TIA1/parser", Shot(bytes: [9, 9]), "The shot");

        var second = node.State.Legs("TIA1").Single().Artifacts.Single().Attachment!;
        second.ShouldNotBe(first);
        Directory.EnumerateFiles(Path.Combine(AttachmentsFolder(node), "TIA1")).Select(Path.GetFileNameWithoutExtension).ShouldBe([second]);
    }

    [Fact]
    public async Task ALanRequestWithoutAGrantIsRefusedAndOneWithAGrantIsServed()
    {
        var port = FreePort();
        var node = await NodeWithLegAsync(port);
        await AddAsync(node, "flight/TIA1/parser", Shot(), "The shot");
        var id = node.State.Legs("TIA1").Single().Artifacts.Single().Attachment!;

        using var remote = NodeClient.Remote($"127.0.0.1:{port}");
        (await remote.GetAttachmentAsync(id, Ct)).Status.ShouldBe(HttpStatusCode.Forbidden);

        var ticket = (await remote.AskGrantAsync("se2-gui", Ct)).Response.ShouldNotBeNull();
        var card = node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [card.Choices[0].Label], Who = "controller" }, Ct)).Success.ShouldBeTrue();
        var allowed = (await remote.GrantTicketAsync(ticket.Id, Ct)).Response.ShouldNotBeNull();

        using var granted = NodeClient.Remote($"127.0.0.1:{port}", allowed.Token);
        var served = await granted.GetAttachmentAsync(id, Ct);
        served.Status.ShouldBe(HttpStatusCode.OK);
        served.Bytes.ShouldBe(Png);
    }

    [Fact]
    public async Task ASessionInNoFlightKeepsItsImageOnItsRowAndArchivingRemovesTheCopiesOfThatSessionOnly()
    {
        _node = await NodeHarness.StartAsync(Ct);
        var node = _node;
        long recent = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        node.Agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "one", "done", startedAt: recent),
            AgentRows.Background(AgentRows.SessionB, "two", "done", startedAt: recent));
        await Until(() => node.State.Sessions().Length == 2);

        (await AddAsync(node, Verbs.SessionTarget("a1b2c3d4"), Shot(), "One's shot")).Ok.ShouldBeTrue();
        (await AddAsync(node, Verbs.SessionTarget("b2c3d4e5"), Shot("other.png"), "Two's shot")).Ok.ShouldBeTrue();
        var one = node.State.Sessions().Single(s => s.Id == "a1b2c3d4").Artifacts.ShouldNotBeNull().Single();
        one.Attachment.ShouldNotBeNullOrEmpty();
        File.Exists(Path.Combine(AttachmentsFolder(node), "a1b2c3d4", one.Attachment + ".png")).ShouldBeTrue();
        (await node.Client.GetAttachmentAsync(one.Attachment!, Ct)).Bytes.ShouldBe(Png);
        (await AddAsync(node, Verbs.SessionTarget("nosuch"), Shot(), "x")).Error.ShouldNotBeNull().ShouldContain("no session");

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Archive, Target = Verbs.SessionTarget("a1b2c3d4"), Who = "tests" }, Ct)).Success.ShouldBeTrue();

        Directory.Exists(Path.Combine(AttachmentsFolder(node), "a1b2c3d4")).ShouldBeFalse();
        Directory.EnumerateFiles(Path.Combine(AttachmentsFolder(node), "b2c3d4e5")).ShouldHaveSingleItem();
        (await node.Client.GetAttachmentAsync(one.Attachment!, Ct)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LandingAFlightRemovesItsCopiesAndLeavesAnotherFlightsAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2a-" + Guid.NewGuid().ToString("N")[..8]);
        var repo = Path.Combine(root, "tianwen");
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
            Git(repo, "add", ".");
            Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
            var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
            var data = Path.Combine(root, "data");
            var attachments = new Attachments(data);
            var git = new GitCli();
            var flights = new FlightCommander(state, new Planes(git), TimeProvider.System) { Attachments = attachments };
            var options = new NodeOptions(Path.Combine(root, "node.sock"), data, MachinePrefix: "DU1");
            var legs = new LegCommander(state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, TimeProvider.System) { Attachments = attachments };
            (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["2", "Moons"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            foreach (var code in new[] { "TIA1", "TIA2" })
            {
                (await legs.RunAsync(new CommandRequest { Verb = Verbs.PlanLeg, Target = $"flight/{code}", Args = ["name=parser", "goal=Do the work", "done-when=Its check passes"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
                (await legs.RunAsync(new CommandRequest { Verb = Verbs.AddArtifact, Target = $"flight/{code}/parser", Args = [Shot(), "shot"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            }
            Directory.Exists(Path.Combine(data, "attachments", "TIA1")).ShouldBeTrue();

            (await flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = "flight/TIA1", Who = "tests" }, Ct)).Ok.ShouldBeTrue();

            Directory.Exists(Path.Combine(data, "attachments", "TIA1")).ShouldBeFalse();
            // Its artifacts stay listed without the copy, so nothing names a file that is gone.
            state.Legs("TIA1").Single().Artifacts.ShouldHaveSingleItem().Attachment.ShouldBeNull();
            state.Legs("TIA2").Single().Artifacts.ShouldHaveSingleItem().Attachment.ShouldNotBeNull();
            Directory.EnumerateFiles(Path.Combine(data, "attachments", "TIA2")).ShouldHaveSingleItem();
        }
        finally
        {
            try
            {
                Git(repo, "worktree", "prune");
            }
            catch (InvalidOperationException)
            {
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnArtifactRecordedBeforeCopiesReadsAsItDid()
    {
        var json = """{"title":"old","location":"C:\\x.png","at":"2026-01-01T00:00:00+00:00"}""";

        var artifact = System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.Artifact).ShouldNotBeNull();

        (artifact.Attachment, artifact.AttachmentExt).ShouldBe((null, null));
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            (DateTime.UtcNow < until).ShouldBeTrue("the node never showed what the test waits for");
            await Task.Delay(50, Ct);
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
}
