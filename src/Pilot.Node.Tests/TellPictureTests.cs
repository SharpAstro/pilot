using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1366: a tell carries one picture the node holds; the watchtower is told its path to open with Read.</summary>
public sealed class TellPictureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static async Task Watchtower(NodeHarness node)
    {
        var name = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, name, status: "busy"));
        for (var i = 0; i < 100 && !node.State.Sessions().Any(s => s.Name == name); i++)
        {
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<string> UploadAsync(NodeHarness node) =>
        $"{(await node.Client.UploadAttachmentAsync(Png, "png", Ct)).Response.ShouldNotBeNull()}.png";

    private static string PathIn(string text) =>
        text[(text.IndexOf(TellPictures.Lead, StringComparison.Ordinal) + TellPictures.Lead.Length)..].Split('\n')[0].Trim();

    private static async Task<(int Exit, string Said)> RunAsync(NodeHarness node, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, .. args], node.Folder, output, error, Ct);
        return (exit, output.ToString() + error.ToString());
    }

    private static string AttachmentFiles(NodeHarness node)
    {
        var folder = node.App.Services.GetService(typeof(Attachments)).ShouldBeOfType<Attachments>().IdeaFolder;
        return Directory.Exists(folder) ? string.Join(",", Directory.GetFiles(folder)) : "";
    }

    [Fact]
    public async Task ATellWithAnUploadedPictureRelaysTheWordsAndThePathOfTheHeldFile()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await Watchtower(node);
        var named = await UploadAsync(node);

        var told = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = ["look", "at", "this"], Who = "gui", Attachment = named }, Ct)).Response.ShouldNotBeNull();

        told.Error.ShouldBeNull();
        var text = node.Delivery.Relays.ShouldHaveSingleItem().Text;
        text.ShouldStartWith("look at this");
        var path = PathIn(text);
        Path.IsPathRooted(path).ShouldBeTrue();
        File.ReadAllBytes(path).ShouldBe(Png);
        text.ShouldContain("Read");
    }

    [Fact]
    public async Task ATellAboutACardKeepsTheCardContextAndAddsThePicture()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await Watchtower(node);
        var card = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR is ready", "Checks pass.", [], null);
        var named = await UploadAsync(node);

        var told = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.CardTarget(card.Id), Args = ["is", "this", "right?"], Who = "gui", Attachment = named }, Ct)).Response.ShouldNotBeNull();

        told.Error.ShouldBeNull();
        var text = node.Delivery.Relays.ShouldHaveSingleItem().Text;
        text.ShouldContain($"[pilot card {card.Id}]");
        text.ShouldContain("is this right?");
        text.IndexOf("is this right?", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf(TellPictures.Lead, StringComparison.Ordinal));
        File.ReadAllBytes(PathIn(text)).ShouldBe(Png);
    }

    [Fact]
    public async Task AnUnknownPictureIsRefusedAndNothingIsTold()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await Watchtower(node);

        var told = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = ["hi"], Who = "gui", Attachment = "abc123.png" }, Ct)).Response.ShouldNotBeNull();

        told.Error.ShouldNotBeNull().ShouldContain("pilot tell --attachment");
        node.Delivery.Relays.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCliRefusesANonImageAndAnOverSizeFileBeforeUploadingAnything()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await Watchtower(node);
        await File.WriteAllTextAsync(Path.Combine(node.Folder, "notes.txt"), "not a picture", Ct);
        await using (var big = File.Create(Path.Combine(node.Folder, "big.png")))
        {
            big.SetLength(IdeaImages.MaxBytes + 1);
        }

        var (textExit, textSaid) = await RunAsync(node, "tell", "--attachment", "notes.txt", "look");
        var (bigExit, bigSaid) = await RunAsync(node, "tell", "--attachment", "big.png", "look");

        textExit.ShouldNotBe(0);
        textSaid.ShouldContain("not an image");
        bigExit.ShouldNotBe(0);
        bigSaid.ShouldContain("at most");
        AttachmentFiles(node).ShouldBeEmpty();
        node.Delivery.Relays.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCliTellsAPngWithItsPathLine()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await Watchtower(node);
        await File.WriteAllBytesAsync(Path.Combine(node.Folder, "shot.png"), Png, Ct);

        var (exit, said) = await RunAsync(node, "tell", "--attachment", "shot.png", "see", "this");

        exit.ShouldBe(0, said);
        var text = node.Delivery.Relays.ShouldHaveSingleItem().Text;
        text.ShouldStartWith("see this");
        File.ReadAllBytes(PathIn(text)).ShouldBe(Png);
    }

    [Theory]
    [InlineData("C:\\Users\\me\\AppData\\Local\\pilot2\\attachments\\ideas\\abc123def456.png", "abc123def456", "png")]
    [InlineData("/home/me/.local/share/pilot2/attachments/ideas/abc123def456.jpg", "abc123def456", "jpg")]
    public void SplitReadsBackWhatLineWroteForAWindowsAndAPosixPath(string path, string id, string ext)
    {
        var (words, pictures) = TellPictures.Split("what is this?" + TellPictures.Line(path));

        words.ShouldBe("what is this?");
        pictures.ShouldBe([(id, ext)]);
    }

    [Fact]
    public void SplitLeavesADesignChatsPictureLineAlone()
    {
        var text = "a screen\n\nA picture came with this message: /d/ideas/abc.png";

        var (words, pictures) = TellPictures.Split(text);

        words.ShouldBe(text);
        pictures.ShouldBeEmpty();
    }

    [Fact]
    public void TheHelperRefusesANodeOfWire31AndTakesWire32()
    {
        NodeInfo Node(int wire) => new() { WireVersion = wire, Version = "1.0", MachineName = "m", ProcessId = 1, StartedAt = DateTimeOffset.UnixEpoch, DataFolder = "/d", SocketPath = "/s", Stream = "x" };

        TellPicture.WireProblem(Node(31)).ShouldNotBeNull().ShouldContain("older pilot");
        TellPicture.WireProblem(Node(NodeWire.Version)).ShouldBeNull();
    }
}
