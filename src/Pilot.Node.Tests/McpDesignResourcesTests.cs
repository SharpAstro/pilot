using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The designs as MCP resources (PIL1187): the list, a design's record, its DESIGN.md (current, a version, the draft) with each frame linked to its render, the
/// renders as PNG and the chat, read through <c>pilot mcp</c> against an in-memory node holding a design made by <c>design new</c> and published with a render,
/// and once through the real CLI process.
/// </summary>
public sealed class McpDesignResourcesTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private string DesignFolder() => Path.Combine(_node.Options.DataFolder, "designs", "DSN-1");

    private string Version1() => Path.Combine(DesignFolder(), "versions", "v1");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", name);

    private async Task<(int Exit, string Output)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString() + error);
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

    /// <summary>DSN-1 with a published v1 (rendered) and a draft holding the same document.</summary>
    private async Task Published()
    {
        (await Pilot("design", "new", "--", "A fleet screen")).Exit.ShouldBe(0);
        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);
        var draft = Path.Combine(DesignFolder(), "draft");
        File.Copy(Fixture("sample.prototype.json"), Path.Combine(draft, "prototype.json"), overwrite: true);
        Directory.Delete(Path.Combine(draft, "screens"), recursive: true);
        File.WriteAllText(Path.Combine(draft, "DESIGN.md"), SampleDocument);
        File.WriteAllText(Path.Combine(draft, "NOTES.md"), "The sample.");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
        exit.ShouldBe(0, said);
        // A terminal render the way 'design render' writes one for a design with a terminal target.
        File.WriteAllBytes(Path.Combine(Version1(), "renders", "home.80x24.png"), [1, 2, 3, 4, 5]);
    }

    private McpServer Server() => new(_node.Folder, node: null, socket: _node.SocketPath);

    private static string Read(int id, string uri) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"resources/read","params":{"uri":"{{{uri}}}"}}""";

    private async Task<JsonElement> ReadOne(string uri) =>
        (await McpTests.Serve(Server(), McpTests.Initialize, Read(2, uri)))[1];

    private async Task<string> Refused(string uri)
    {
        var reply = await ReadOne(uri);
        reply.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32002);
        return reply.GetProperty("error").GetProperty("message").GetString()!;
    }

    private static JsonElement Contents(JsonElement reply) => reply.GetProperty("result").GetProperty("contents")[0];

    [Fact]
    public async Task TheListHoldsTheDesignsAndTheTemplatesListHoldsTheirUris()
    {
        await Published();
        var replies = await McpTests.Serve(Server(), McpTests.Initialize,
            """{"jsonrpc":"2.0","id":2,"method":"resources/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"resources/templates/list"}""");

        var uris = replies[1].GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToArray();
        uris.Take(4).ShouldBe(["pilot://flights", "pilot://legs", "pilot://cards", "pilot://sessions"]);
        uris.Skip(4).ShouldBe(["pilot://designs", "pilot://designs/DSN-1", "pilot://designs/DSN-1/document", "pilot://designs/DSN-1/chat"]);

        var templates = replies[2].GetProperty("result").GetProperty("resourceTemplates").EnumerateArray().Select(t => t.GetProperty("uriTemplate").GetString()).ToArray();
        templates.ShouldContain("pilot://designs/{id}");
        templates.ShouldContain("pilot://designs/{id}/document");
        templates.ShouldContain("pilot://designs/{id}/versions/{version}/document");
        templates.ShouldContain("pilot://designs/{id}/draft/document");
        templates.ShouldContain("pilot://designs/{id}/versions/{version}/screens/{screen}{?size}");
        templates.ShouldContain("pilot://designs/{id}/draft/screens/{screen}{?size}");
        templates.ShouldContain("pilot://designs/{id}/chat");
    }

    [Fact]
    public async Task TheListOfDesignsAndADesignsRecordAreJson()
    {
        await Published();
        var list = Contents(await ReadOne("pilot://designs"));
        list.GetProperty("mimeType").GetString().ShouldBe("application/json");
        var rows = JsonDocument.Parse(list.GetProperty("text").GetString()!).RootElement;
        rows.GetArrayLength().ShouldBe(1);
        rows[0].GetProperty("id").GetString().ShouldBe("DSN-1");
        rows[0].GetProperty("current").GetInt32().ShouldBe(1);

        var record = Contents(await ReadOne("pilot://designs/DSN-1"));
        var detail = JsonDocument.Parse(record.GetProperty("text").GetString()!).RootElement;
        detail.GetProperty("row").GetProperty("id").GetString().ShouldBe("DSN-1");
        detail.GetProperty("versions").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task TheDocumentIsMarkdownWithEachFrameLinkedToItsRender()
    {
        await Published();
        var current = Contents(await ReadOne("pilot://designs/DSN-1/document"));
        current.GetProperty("mimeType").GetString().ShouldBe("text/markdown");
        var text = current.GetProperty("text").GetString()!;
        text.ShouldContain("# A fleet screen");
        text.ShouldContain("[render of home](pilot://designs/DSN-1/versions/1/screens/home)");
        text.ShouldContain("[render of settings](pilot://designs/DSN-1/versions/1/screens/settings)");
        text.ShouldContain("[render of confirm](pilot://designs/DSN-1/versions/1/screens/confirm)");

        // A version by its number reads the same; the draft links to the draft's renders.
        Contents(await ReadOne("pilot://designs/DSN-1/versions/1/document")).GetProperty("text").GetString().ShouldBe(text);
        var draft = Contents(await ReadOne("pilot://designs/DSN-1/draft/document")).GetProperty("text").GetString()!;
        draft.ShouldContain("[render of home](pilot://designs/DSN-1/draft/screens/home)");
    }

    [Fact]
    public void AFrameNamingATerminalSizeLinksTheRenderAtThatSizeOnlyWhenItExists()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcpdr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "renders"));
            File.WriteAllBytes(Path.Combine(folder, "renders", "home.80x24.png"), [1]);
            var text = "## Home\n\n```screen id=home size=80x24\ncaption\n```\n\n```screen id=other size=100x30\n```\n\n```text\n```screen id=inside\n```\n";
            var linked = McpDesigns.Linked(text, folder, "pilot://designs/DSN-1/draft");
            linked.ShouldContain("[render of home](pilot://designs/DSN-1/draft/screens/home?size=80x24)");
            linked.ShouldContain("[render of other](pilot://designs/DSN-1/draft/screens/other)");
            linked.ShouldNotContain("render of inside");
            linked.ShouldContain("caption");
        }
        finally
        {
            TestDirs.DeleteTree(folder);
        }
    }

    [Fact]
    public async Task ARenderIsPngBytesEqualToTheFileAndATerminalRenderIsPickedBySize()
    {
        await Published();
        var file = Path.Combine(Version1(), "renders", "home.png");
        File.Exists(file).ShouldBeTrue();

        var png = Contents(await ReadOne("pilot://designs/DSN-1/versions/1/screens/home"));
        png.GetProperty("mimeType").GetString().ShouldBe("image/png");
        Convert.FromBase64String(png.GetProperty("blob").GetString()!).ShouldBe(await File.ReadAllBytesAsync(file, Ct));
        png.TryGetProperty("text", out _).ShouldBeFalse();

        var terminal = Contents(await ReadOne("pilot://designs/DSN-1/versions/1/screens/home?size=80x24"));
        Convert.FromBase64String(terminal.GetProperty("blob").GetString()!).ShouldBe([1, 2, 3, 4, 5]);

        var draftFile = Path.Combine(DesignFolder(), "draft", "renders", "home.png");
        Convert.FromBase64String(Contents(await ReadOne("pilot://designs/DSN-1/draft/screens/home")).GetProperty("blob").GetString()!)
            .ShouldBe(await File.ReadAllBytesAsync(draftFile, Ct));
    }

    [Fact]
    public async Task TheChatIsJson()
    {
        await Published();
        (await Pilot("design", "say", "DSN-1", "--", "wider please")).Exit.ShouldBe(0);
        var chat = Contents(await ReadOne("pilot://designs/DSN-1/chat"));
        chat.GetProperty("mimeType").GetString().ShouldBe("application/json");
        var text = chat.GetProperty("text").GetString()!;
        JsonDocument.Parse(text).RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        text.ShouldContain("wider please");
    }

    [Fact]
    public async Task ADesignVersionScreenOrSizeThatDoesNotExistIsRefusedSayingWhatDoes()
    {
        await Published();
        (await Refused("pilot://designs/DSN-9")).ShouldContain("DSN-1");
        (await Refused("pilot://designs/DSN-9/document")).ShouldContain("no design DSN-9");
        (await Refused("pilot://designs/DSN-1/versions/7/document")).ShouldContain("v1");
        (await Refused("pilot://designs/DSN-1/versions/x/document")).ShouldContain("no version x");
        var screen = await Refused("pilot://designs/DSN-1/versions/1/screens/nope");
        screen.ShouldContain("no screen nope");
        screen.ShouldContain("home");
        var size = await Refused("pilot://designs/DSN-1/versions/1/screens/home?size=90x30");
        size.ShouldContain("90x30");
        size.ShouldContain("80x24");
        (await Refused("pilot://designs/DSN-1/versions/1/screens/home?size=big")).ShouldContain("terminal size");
        (await Refused("pilot://designs/DSN-1/versions/1/screens/..%2F..%2Fdesign.json")).ShouldContain("no screen");
        (await Refused("pilot://designs/DSN-1/nothing")).ShouldContain("resources/templates/list");
    }

    [Fact]
    public async Task ADesignWithNoVersionHasNoCurrentDocument()
    {
        (await Pilot("design", "new", "--", "A fleet screen")).Exit.ShouldBe(0);
        (await Refused("pilot://designs/DSN-1/document")).ShouldContain("no version yet");
        (await Refused("pilot://designs/DSN-1/draft/document")).ShouldContain("no draft");
        var uris = (await McpTests.Serve(Server(), McpTests.Initialize, """{"jsonrpc":"2.0","id":2,"method":"resources/list"}"""))[1]
            .GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToArray();
        uris.ShouldNotContain("pilot://designs/DSN-1/document");
        uris.ShouldContain("pilot://designs/DSN-1/chat");
    }

    [Fact]
    public async Task ADesignThatNamesTargetsServesTheFirstTargetsPngForAScreen()
    {
        await Published();
        var renders = Path.Combine(Version1(), "renders");
        File.Move(Path.Combine(renders, "home.png"), Path.Combine(renders, "home.Phone.png"));
        // A published version's files are read-only.
        File.SetAttributes(DesignPaths.PrototypeFile(Version1()), FileAttributes.Normal);
        File.WriteAllText(DesignPaths.PrototypeFile(Version1()), """{"targets":["Phone","Tablet"]}""");

        var png = Contents(await ReadOne("pilot://designs/DSN-1/versions/1/screens/home"));
        Convert.FromBase64String(png.GetProperty("blob").GetString()!).ShouldBe(await File.ReadAllBytesAsync(Path.Combine(renders, "home.Phone.png"), Ct));
        (await Refused("pilot://designs/DSN-1/versions/1/screens/nope")).ShouldContain("no screen nope");
    }

    [Theory]
    [InlineData("""{"targets":["Phone"]}""", "Phone")]
    [InlineData("""{"targets":["phone"]}""", "Phone")]
    [InlineData("""{"targets":["Nowhere"]}""", null)]
    [InlineData("""{"targets":[{"name":"Wide","width":10,"height":5}]}""", "Wide")]
    [InlineData("""{"targets":[{"width":390,"height":844}]}""", "390x844")]
    [InlineData("""{"targets":[]}""", null)]
    [InlineData("""{"targets":[3]}""", null)]
    [InlineData("""{"other":1}""", null)]
    [InlineData("[1]", null)]
    [InlineData("{not json", null)]
    public void TheFirstTargetIsNamedFromThePrototype(string json, string? expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcpdt-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(DesignPaths.PrototypeFile(folder), json);
            DesignPaths.FirstTarget(folder).ShouldBe(expected);
            DesignPaths.FirstTarget(Path.Combine(folder, "absent")).ShouldBeNull();
        }
        finally
        {
            TestDirs.DeleteTree(folder);
        }
    }

    [Theory]
    [InlineData("home", true)]
    [InlineData("home.Phone", true)]
    [InlineData("C:home", false)]
    [InlineData("C:", false)]
    [InlineData("home:stream", false)]
    [InlineData("..\\home", false)]
    [InlineData("a/b", false)]
    [InlineData("a\0b", false)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    public void AScreenNameIsOnePlainFileNameOrNone(string name, bool plain) => McpDesigns.IsPlainFileName(name).ShouldBe(plain);

    [Fact]
    public async Task AScreenNameThatIsNotOnePlainFileNameIsRefused()
    {
        await Published();
        foreach (var name in new[] { "C%3Ahome", "C%3A", "home%3Astream", "..%5Chome", "a%00b" })
        {
            (await Refused($"pilot://designs/DSN-1/versions/1/screens/{name}")).ShouldContain("no screen");
        }
    }

    [Fact]
    public async Task ARenderThatCannotBeReadIsRefusedNamingTheFileNotTakenForANodeThatIsAway()
    {
        await Published();
        var file = Path.Combine(Version1(), "renders", "home.png");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var why = await Refused("pilot://designs/DSN-1/versions/1/screens/home");
        why.ShouldContain("home.png");
        why.ShouldContain("cannot be read");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePilotCliServesTheDesignsOverStdio()
    {
        await Published();
        var folder = Path.Combine(Path.GetTempPath(), "pilot-mcpdesigns-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }
        var info = new ProcessStartInfo(Path.Combine(folder, OperatingSystem.IsWindows() ? "pilot.exe" : "pilot"))
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        info.Environment[LanTransports.OffVariable] = "1";
        foreach (var arg in new[] { "--node-socket", _node.SocketPath, "mcp" })
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new IOException("no process started");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        try
        {
            async Task<JsonElement> Ask(string line)
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), Ct);
                await process.StandardInput.FlushAsync(Ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var reply = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("the server ended");
                return JsonDocument.Parse(reply).RootElement.Clone();
            }

            (await Ask(McpTests.Initialize)).GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("pilot");
            var list = await Ask("""{"jsonrpc":"2.0","id":2,"method":"resources/list"}""");
            list.GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ShouldContain("pilot://designs/DSN-1");
            var templates = await Ask("""{"jsonrpc":"2.0","id":3,"method":"resources/templates/list"}""");
            templates.GetProperty("result").GetProperty("resourceTemplates").EnumerateArray().Select(t => t.GetProperty("uriTemplate").GetString())
                .ShouldContain("pilot://designs/{id}/versions/{version}/screens/{screen}{?size}");

            var record = Contents(await Ask(Read(4, "pilot://designs/DSN-1")));
            JsonDocument.Parse(record.GetProperty("text").GetString()!).RootElement.GetProperty("row").GetProperty("id").GetString().ShouldBe("DSN-1");
            var document = Contents(await Ask(Read(5, "pilot://designs/DSN-1/document")));
            document.GetProperty("mimeType").GetString().ShouldBe("text/markdown");
            document.GetProperty("text").GetString()!.ShouldContain("[render of home](pilot://designs/DSN-1/versions/1/screens/home)");
            var png = Contents(await Ask(Read(6, "pilot://designs/DSN-1/versions/1/screens/home")));
            Convert.FromBase64String(png.GetProperty("blob").GetString()!).ShouldBe(await File.ReadAllBytesAsync(Path.Combine(Version1(), "renders", "home.png"), Ct));
            Contents(await Ask(Read(7, "pilot://designs/DSN-1/draft/document"))).GetProperty("text").GetString()!.ShouldContain("draft/screens/home");
            Contents(await Ask(Read(9, "pilot://designs/DSN-1/versions/1/document"))).GetProperty("text").GetString()!.ShouldContain("versions/1/screens/home");
            var terminal = Contents(await Ask(Read(10, "pilot://designs/DSN-1/versions/1/screens/home?size=80x24")));
            Convert.FromBase64String(terminal.GetProperty("blob").GetString()!).ShouldBe([1, 2, 3, 4, 5]);
            Contents(await Ask(Read(11, "pilot://designs/DSN-1/draft/screens/home"))).GetProperty("mimeType").GetString().ShouldBe("image/png");
            JsonDocument.Parse(Contents(await Ask(Read(12, "pilot://designs/DSN-1/chat"))).GetProperty("text").GetString()!).RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
            var unknown = (await Ask(Read(14, "pilot://designs/DSN-9"))).GetProperty("error");
            unknown.GetProperty("code").GetInt32().ShouldBe(-32002);
            unknown.GetProperty("message").GetString()!.ShouldContain("no design DSN-9");
            var noScreen = (await Ask(Read(15, "pilot://designs/DSN-1/versions/1/screens/nope"))).GetProperty("error");
            noScreen.GetProperty("code").GetInt32().ShouldBe(-32002);
            noScreen.GetProperty("message").GetString()!.ShouldContain("no screen nope");
            var noSize = (await Ask(Read(16, "pilot://designs/DSN-1/versions/1/screens/home?size=5x5"))).GetProperty("error");
            noSize.GetProperty("code").GetInt32().ShouldBe(-32002);
            noSize.GetProperty("message").GetString()!.ShouldContain("5x5");
            var badSize = (await Ask(Read(17, "pilot://designs/DSN-1/versions/1/screens/home?size=huge"))).GetProperty("error");
            badSize.GetProperty("code").GetInt32().ShouldBe(-32002);
            badSize.GetProperty("message").GetString()!.ShouldContain("terminal size");
            var refused = await Ask(Read(8, "pilot://designs/DSN-1/versions/9/document"));
            refused.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32002);
            refused.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("v1");
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            await process.WaitForExitAsync(Ct);
            try
            {
                TestDirs.DeleteTree(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
