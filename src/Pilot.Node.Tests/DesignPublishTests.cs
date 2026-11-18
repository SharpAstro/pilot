using System.Security.Cryptography;
using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>pilot design check</c>, <c>render</c> and <c>publish</c> (PIL1096) through the CLI against the in-memory node: the node loads, checks and draws the draft
/// itself with DIR.Lib's Prototype, and makes a read-only version only from a clean draft whose renders are of it as it stands.
/// </summary>
public sealed class DesignPublishTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private string DesignFolder(string id = "DSN-1") => Path.Combine(_node.Options.DataFolder, "designs", id);

    private string Draft(string file) => Path.Combine(DesignFolder(), "draft", file);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", name);

    private async Task<(int Exit, string Output)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString() + error);
    }

    /// <summary>A design with a draft holding <paramref name="fixture"/> and notes, not yet checked or rendered.</summary>
    private async Task DraftOf(string fixture, string notes = "The sample: a home screen, settings and a confirm modal.")
    {
        (await Pilot("design", "new", "--", "A fleet screen")).Exit.ShouldBe(0);
        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);
        File.Copy(Fixture(fixture), Draft("prototype.json"), overwrite: true);
        // The fixture carries its screens (the older shape), so the starter's screens/ folder goes.
        Directory.Delete(Draft("screens"), recursive: true);
        File.WriteAllText(Draft("DESIGN.md"), SampleDocument);
        File.WriteAllText(Draft("NOTES.md"), notes);
    }

    private const string SampleDocument = """
        # A fleet screen

        **Objective:** Shows the sample: a home screen, its settings and a confirm modal.

        ## Home

        **Objective:** Shows the fleet at a glance.

        ```screen id=home
        ```

        ## Settings

        **Objective:** Shows what can be changed.

        ```screen id=settings size=Phone
        ```

        ## Confirm

        **Objective:** Shows the question asked before a change.

        ```screen id=confirm
        ```

        """;

    private async Task<string> PublishedV1()
    {
        await DraftOf("sample.prototype.json");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
        exit.ShouldBe(0, said);
        return Path.Combine(DesignFolder(), "versions", "v1");
    }

    /// <summary>One hash over every file of a folder, by relative path, so any write, add or removal shows.</summary>
    private static string HashOf(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/') + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [Fact]
    public async Task CheckRenderAndPublishMakeV1FromTheSample()
    {
        await DraftOf("sample.prototype.json");

        var (checkExit, checkSaid) = await Pilot("design", "check", "DSN-1");
        checkExit.ShouldBe(0, checkSaid);
        checkSaid.Trim().ShouldEndWith("clean");

        var (renderExit, rendered) = await Pilot("design", "render", "DSN-1");
        renderExit.ShouldBe(0, rendered);
        rendered.ShouldContain("3 renders");
        foreach (var screen in new[] { "home", "settings", "confirm" })
        {
            var png = File.ReadAllBytes(Draft(Path.Combine("renders", screen + ".png")));
            png.Take(4).ToArray().ShouldBe([0x89, 0x50, 0x4E, 0x47]);
        }
        File.Exists(Draft(Path.Combine("renders", "render.json"))).ShouldBeTrue();

        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
        exit.ShouldBe(0, said);
        said.ShouldContain("DSN-1 v1");

        var v1 = Path.Combine(DesignFolder(), "versions", "v1");
        File.ReadAllBytes(Path.Combine(v1, "prototype.json")).ShouldBe(File.ReadAllBytes(Draft("prototype.json")));
        File.ReadAllText(Path.Combine(v1, "NOTES.md")).ShouldBe(File.ReadAllText(Draft("NOTES.md")));
        Directory.EnumerateFiles(Path.Combine(v1, "renders"), "*.png").Select(Path.GetFileName).Order().ShouldBe(["confirm.png", "home.png", "settings.png"]);
        foreach (var file in Directory.EnumerateFiles(v1, "*", SearchOption.AllDirectories))
        {
            new FileInfo(file).IsReadOnly.ShouldBeTrue(file);
        }

        var record = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(DesignFolder(), "design.json")), PilotJsonContext.Default.DesignRow)!;
        record.Current.ShouldBe(1);
        record.Versions.ShouldBe(1);
        record.Why.ShouldBe("brief");
        record.PrototypeSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Draft("prototype.json")))));
        record.History.Select(v => (v.Number, v.Why)).ShouldBe([(1, "brief")]);
        record.History[0].At.ShouldNotBeNull();
        record.History[0].PrototypeSha256.ShouldBe(record.PrototypeSha256);

        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("current version: v1 of 1");
    }

    [Fact]
    public async Task AFreeTextWhyIsStoredAndReadOnTheVersionLine()
    {
        await DraftOf("sample.prototype.json");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        const string why = "added the bind rows to the settings screen";
        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", why);
        exit.ShouldBe(0, said);

        var record = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(DesignFolder(), "design.json")), PilotJsonContext.Default.DesignRow)!;
        record.Why.ShouldBe(why);
        record.History.Select(v => v.Why).ShouldBe([why]);
    }

    [Fact]
    public async Task AStrayFileInTheDraftIsNotPublished()
    {
        await DraftOf("sample.prototype.json");
        File.WriteAllText(Draft("scratch.txt"), "notes to self");
        Directory.CreateDirectory(Draft("junk"));
        File.WriteAllText(Path.Combine(Draft("junk"), "a.txt"), "x");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "first");
        exit.ShouldBe(0, said);

        var v1 = Path.Combine(DesignFolder(), "versions", "v1");
        Directory.EnumerateFileSystemEntries(v1).Select(Path.GetFileName).Order().ShouldBe(["DESIGN.md", "NOTES.md", "prototype.json", "renders"]);
        Directory.EnumerateFiles(Path.Combine(v1, "renders")).Select(Path.GetFileName).ShouldNotContain("render.json");
    }

    [Fact]
    public async Task ASecondPublishMakesV2AndLeavesV1AsItWas()
    {
        var v1 = await PublishedV1();
        var before = HashOf(v1);

        // The draft of a design with a version starts from it, writable.
        (await Pilot("design", "draft", "DSN-1", "--replace")).Exit.ShouldBe(0);
        new FileInfo(Draft("prototype.json")).IsReadOnly.ShouldBeFalse();
        File.WriteAllText(Draft("prototype.json"), File.ReadAllText(Draft("prototype.json")).Replace("Fleet", "Fleets"));
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);

        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "message");
        exit.ShouldBe(0, said);
        said.ShouldContain("DSN-1 v2");
        File.ReadAllText(Path.Combine(DesignFolder(), "versions", "v2", "prototype.json")).ShouldContain("Fleets");
        HashOf(v1).ShouldBe(before);
        File.ReadAllText(Path.Combine(v1, "prototype.json")).ShouldNotContain("Fleets");

        var record = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(DesignFolder(), "design.json")), PilotJsonContext.Default.DesignRow)!;
        record.Current.ShouldBe(2);
        record.History.Select(v => (v.Number, v.Why)).ShouldBe([(1, "brief"), (2, "message")]);
        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("current version: v2 of 2");
    }

    [Fact]
    public async Task PublishRefusesADraftWithAFindingAndCheckSaysWhereOnALine()
    {
        await DraftOf("overlap.prototype.json");

        var (checkExit, checkSaid) = await Pilot("design", "check", "DSN-1");
        checkExit.ShouldBe(1);
        var lines = checkSaid.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("home |", StringComparison.Ordinal)).ToArray();
        lines.ShouldNotBeEmpty(checkSaid);
        lines.ShouldAllBe(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^home \| \d+x\d+ \| \w+: .+$"));
        // One line for each size it is found at, and every size is one of the design sizes.
        var sizes = lines.Select(l => l.Split(" | ")[1]).ToArray();
        sizes.ShouldAllBe(s => DesignSizes.All.Any(d => s == $"{d.Width}x{d.Height}"));

        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "fix");
        exit.ShouldBe(1);
        said.ShouldContain("refused");
        said.ShouldContain("not clean");
        said.ShouldContain("home |");
        Directory.Exists(Path.Combine(DesignFolder(), "versions", "v1")).ShouldBeFalse();
    }

    [Fact]
    public async Task PublishRefusesADraftEditedAfterItsRender()
    {
        await DraftOf("sample.prototype.json");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        File.WriteAllText(Draft("prototype.json"), File.ReadAllText(Draft("prototype.json")).Replace("Three flights", "Four flights"));

        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
        exit.ShouldBe(1);
        said.ShouldContain("renders are not of the draft as it stands");
        Directory.Exists(Path.Combine(DesignFolder(), "versions", "v1")).ShouldBeFalse();

        // Rendered again, it publishes.
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        (await Pilot("design", "publish", "DSN-1", "--why", "brief")).Exit.ShouldBe(0);
    }

    [Fact]
    public async Task PublishRefusesADraftNeverRendered()
    {
        await DraftOf("sample.prototype.json");

        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
        exit.ShouldBe(1);
        said.ShouldContain("renders are not of the draft as it stands");
    }

    [Fact]
    public async Task CheckPrintsALoaderProblemWithItsJsonPath()
    {
        await DraftOf("sample.prototype.json");
        File.WriteAllText(Draft("prototype.json"), File.ReadAllText(Draft("prototype.json")).Replace("\"kind\": \"textInput\"", "\"kind\": \"textInpt\""));

        var (exit, said) = await Pilot("design", "check", "DSN-1");
        exit.ShouldBe(1);
        said.ShouldContain("$.screens[1].root.children[1].kind");

        File.WriteAllText(Draft("prototype.json"), "{ not json");
        var (badExit, bad) = await Pilot("design", "check", "DSN-1");
        badExit.ShouldBe(1);
        bad.ShouldContain("$");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(1);
    }

    [Fact]
    public async Task TheStarterDraftLoadsAndChecksClean()
    {
        (await Pilot("design", "new", "--", "brief")).Exit.ShouldBe(0);
        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);

        var (exit, said) = await Pilot("design", "check", "DSN-1");
        exit.ShouldBe(0, said);
        said.Trim().ShouldEndWith("clean");
    }

    [Fact]
    public async Task PublishRefusesWithoutADraftWithoutADocumentAndWithAnUnknownWhy()
    {
        (await Pilot("design", "new", "--", "brief")).Exit.ShouldBe(0);

        var (noDraft, noDraftSaid) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
        noDraft.ShouldBe(1);
        noDraftSaid.ShouldContain("no draft");
        (await Pilot("design", "check", "DSN-1")).Output.ShouldContain("no draft");

        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        // The starter's notes are empty.
        var (noNotes, noNotesSaid) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
        noNotes.ShouldBe(1);
        noNotesSaid.ShouldContain("NOTES.md");

        File.WriteAllText(Draft("NOTES.md"), "  \n");
        (await Pilot("design", "publish", "DSN-1", "--why", "brief")).Output.ShouldContain("NOTES.md");

        File.WriteAllText(Draft("NOTES.md"), "What this is.");
        foreach (var (bad, reason) in new[] { ("", "not empty"), ("  ", "not empty"), ("two\nlines", "one line"), (new string('x', DesignWhys.MaxLength + 1), "at most 120") })
        {
            var (badWhy, badWhySaid) = await Pilot("design", "publish", "DSN-1", "--why", bad);
            badWhy.ShouldBe(1);
            badWhySaid.ShouldContain(reason);
        }

        (await Pilot("design", "publish", "DSN-5", "--why", "brief")).Output.ShouldContain("There is no design DSN-5");
        Directory.Exists(Path.Combine(DesignFolder(), "versions", "v1")).ShouldBeFalse();
    }

    [Fact]
    public async Task PublishFailsRatherThanWriteIntoAVersionFolderThatExists()
    {
        var v1 = await PublishedV1();
        // The record says v1 is current, but a v2 is on disk already: it is never written again.
        var v2 = Path.Combine(DesignFolder(), "versions", "v2");
        Directory.CreateDirectory(v2);
        File.WriteAllText(Path.Combine(v2, "mine.txt"), "kept");
        var v2Before = HashOf(v2);

        (await Pilot("design", "draft", "DSN-1", "--replace")).Exit.ShouldBe(0);
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "fix");

        exit.ShouldBe(1);
        said.ShouldContain("exists already");
        HashOf(v2).ShouldBe(v2Before);
        HashOf(v1).ShouldNotBeNullOrEmpty();
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(DesignFolder(), "design.json")), PilotJsonContext.Default.DesignRow)!.Current.ShouldBe(1);
    }

    [Fact]
    public async Task NoCommandWritesUnderAnExistingVersion()
    {
        var v1 = await PublishedV1();
        var before = HashOf(v1);

        string[][] commands =
        [
            ["design", "brief", "DSN-1", "--", "more words"],
            ["design", "show", "DSN-1"],
            ["designs"],
            ["design", "draft", "DSN-1"],
            ["design", "draft", "DSN-1", "--replace"],
            ["design", "check", "DSN-1"],
            ["design", "render", "DSN-1"],
            ["design", "publish", "DSN-1", "--why", "bogus"],
            ["design", "publish", "DSN-1", "--why", "message"],
        ];
        foreach (var command in commands)
        {
            await Pilot(command);
            HashOf(v1).ShouldBe(before, string.Join(' ', command));
        }
    }

    [Fact]
    public void TheDesignSizesAreTheSweepsDistinctSizes()
    {
        DesignSizes.All.Select(s => (s.Width, s.Height)).ShouldBe(
        [
            (1000u, 900u), (1280u, 900u), (1400u, 900u), (1920u, 900u), (2560u, 900u),
            (1280u, 1440u), (2560u, 1440u),
        ]);
    }
}
