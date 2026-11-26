using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Captures of the Chat page with a picture held in the box and one sent (PIL1366), light and dark, narrow and wide, written to
/// PILOT_JOB_ARTIFACTS for the PR.
/// </summary>
public sealed class ChatPictureFeedCaptureTests
{
    private static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : System.IO.Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData(false, 560, 800)]
    [InlineData(false, 1500, 900)]
    [InlineData(true, 560, 800)]
    [InlineData(true, 1500, 900)]
    public void TheChatShowsAPictureHeldInTheBoxAndOneSent(bool night, int width, int height)
    {
        TranscriptEntry[] feed =
        [
            new() { Index = 0, Kind = TranscriptKind.Assistant, Text = "Ready. What should I look at?", At = Live.At },
            ChatPictureFeedTests.Tell(1, "what is the streak across the lower left of this frame, and should I drop the sub?"),
        ];
        using var held = new ChatPictureFeedTests.Held(width, height, feed);
        held.Chat.Night = new NightSwitch { On = night };
        held.Chat.GetClipboardImage = () => ([1, 2, 3], "png");
        held.Chat.OnSendWithPicture = (_, _) => Task.FromResult<string?>(null);
        held.Chat.Tree();
        held.Chat.Input.OnKeyOverride!(TextInputKey.Paste).ShouldBeTrue();
        held.Chat.Input.InsertText("and this one, the second frame?");

        held.Chat.Render(new RectF32(0, 0, width, height));
        held.Painted.ShouldNotBeEmpty();
        File.WriteAllBytes(System.IO.Path.Combine(ArtifactDir(), $"chat-picture-{(night ? "dark" : "light")}-{width}.png"), PngWriter.Encode(held.Renderer.Surface.Pixels, width, height));
    }
}
