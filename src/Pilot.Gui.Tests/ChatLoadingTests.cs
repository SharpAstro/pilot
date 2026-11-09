using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The chat says it is loading until its first page is read, and "Nothing said yet" only after a read that found nothing
/// (owner, 2026-11-09: opening the watchtower chat showed "nothing there" instead of loading the previous chat).
/// </summary>
public sealed class ChatLoadingTests
{
    private static string Described(ChatView<RgbaImage> chat) =>
        string.Join(' ', Layout.Engine.Arrange(chat.Tree(), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()));

    [Fact]
    public async Task TheChatSaysLoadingUntilItsFirstReadAndNothingSaidOnlyAfterOneThatFoundNothing()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ChatPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.Load = (_, _) =>
        {
            asked.TrySetResult();
            return answer.Task;
        };
        chat.Changed = () => read.TrySetResult();
        chat.Open();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Described(chat).ShouldContain("Loading the conversation");
        Described(chat).ShouldNotContain("Nothing said yet");

        answer.SetResult(new ChatPage { Entries = [], Total = 0 });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        chat.Close();

        Described(chat).ShouldContain("Nothing said yet");
        Described(chat).ShouldNotContain("Loading the conversation");
    }
}
