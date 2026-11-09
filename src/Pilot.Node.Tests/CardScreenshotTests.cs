using System.Net;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A card can carry one image the node keeps (an overlap's screenshot): raised with it, held on the card row, served by the attachment route.</summary>
public sealed class CardScreenshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static Task<ResponseEnvelope<CommandResult>> RaiseAsync(NodeHarness node, string subject, string? attachment, params string[] args) =>
        node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = subject, Args = args, Who = "tests", Attachment = attachment }, Ct);

    [Fact]
    public async Task ARaiseWithoutAnImageIsTheFormItAlwaysWasAndTheCardHasNone()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        (await RaiseAsync(node, "flight/TIA1", null, "ring", "plain card", "no picture")).Success.ShouldBeTrue();

        var card = node.State.Cards().ShouldHaveSingleItem();
        (card.Title, card.Attachment, card.AttachmentExt).ShouldBe(("plain card", null, null));
    }

    [Fact]
    public async Task ACardRaisedWithAnUploadedImageCarriesItAndTheNodeServesIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var upload = await node.Client.UploadAttachmentAsync(Png, "png", Ct);
        var id = upload.Response.ShouldNotBeNull();

        var sent = await RaiseAsync(node, "flight/TIA1", $"{id}.png", "for-you", "TIA1 overlaps TIA2 on the cards panel", "text over text");

        sent.Response.ShouldNotBeNull().Ok.ShouldBeTrue(sent.Response.Error);
        var card = node.State.Cards().ShouldHaveSingleItem();
        (card.Attachment, card.AttachmentExt).ShouldBe((id, "png"));
        var (status, type, bytes) = await node.Client.GetAttachmentAsync(card.Attachment!, Ct);
        (status, type).ShouldBe((HttpStatusCode.OK, "image/png"));
        bytes.ShouldBe(Png);
    }

    [Fact]
    public async Task ACardRaisedWithALegsArtifactCopyKeepsACopyOfItsOwnThatOutlivesTheLeg()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var plane = TestTemp.NewPath();
        Directory.CreateDirectory(plane);
        try
        {
            var file = Path.Combine(plane, "shot.png");
            File.WriteAllBytes(file, Png);
            node.State.PutFlight(new FlightRow
            {
                Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/tia1", Worktree = plane, Base = "origin/main",
                State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
            });
            node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
            (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.AddArtifact, Target = "flight/TIA1/parser", Args = [file, "The shot"], Who = "tests" }, Ct))
                .Response.ShouldNotBeNull().Ok.ShouldBeTrue();
            var copy = node.State.Legs("TIA1").Single().Artifacts.Single();

            var sent = await RaiseAsync(node, "flight/TIA1", $"{copy.Attachment}.png", "ring", "overlap", "see the shot");

            sent.Response.ShouldNotBeNull().Ok.ShouldBeTrue(sent.Response.Error);
            var card = node.State.Cards().ShouldHaveSingleItem();
            card.Attachment.ShouldNotBeNullOrEmpty();
            card.Attachment.ShouldNotBe(copy.Attachment, "the leg's copy goes when its flight lands, so the card keeps its own");
            (await node.Client.GetAttachmentAsync(card.Attachment!, Ct)).Bytes.ShouldBe(Png);
        }
        finally
        {
            TestTemp.Delete(plane);
        }
    }

    [Theory]
    [InlineData("nosuchcopy.png", "holds no image")]
    [InlineData("notes.txt", "of an image the node holds")]
    [InlineData("noextension", "of an image the node holds")]
    public async Task AnImageTheNodeDoesNotHoldIsRefusedInWordsAndNoCardIsRaised(string attachment, string said)
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var sent = await RaiseAsync(node, "flight/TIA1", attachment, "ring", "overlap");

        sent.Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain(said);
        node.State.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheImageSurvivesTheCardChangingAndGoingThroughTheWire()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var id = (await node.Client.UploadAttachmentAsync(Png, "png", Ct)).Response.ShouldNotBeNull();
        await RaiseAsync(node, "flight/TIA1", $"{id}.png", "ring", "overlap");
        var card = node.State.Cards().Single();

        node.State.UpdateCard(card.Id, "overlap, updated", null, [], null).ShouldBeTrue();

        var again = node.State.Cards().Single();
        (again.Title, again.Attachment, again.AttachmentExt).ShouldBe(("overlap, updated", id, "png"));
        var json = System.Text.Json.JsonSerializer.Serialize(again, PilotJsonContext.Default.CardRow);
        System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.CardRow)!.Attachment.ShouldBe(id);
        // A card without one writes no field at all, so an older client reads it as before.
        System.Text.Json.JsonSerializer.Serialize(again with { Attachment = null, AttachmentExt = null }, PilotJsonContext.Default.CardRow).ShouldNotContain("ttachment");
    }
}
