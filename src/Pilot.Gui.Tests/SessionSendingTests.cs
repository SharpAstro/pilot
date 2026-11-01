using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The say bar shows what was said at once, dimmed and marked "sending…", while the node delivers; the host's words replace
/// it when the send completes, and a second say while one is out shows both.
/// </summary>
public sealed class SessionSendingTests
{
    private static SessionsScreen Screen() => new(SessionLine.Order(
    [
        SessionLine.From(Live.Row("aaaa0001", "w-working", SessionState.Working), Live.At),
        SessionLine.From(Live.Row("aaaa0002", "w-waiting", SessionState.Waiting), Live.At),
    ]), "test", Live.At);

    private static (string Text, RGBAColor32 Color)? Leaf(SessionsView<RgbaImage> view, SessionsScreen screen, string contains) =>
        Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 400, 40), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>()
            .Where(t => t.Value.Contains(contains, StringComparison.Ordinal))
            .Select(t => ((string Text, RGBAColor32 Color)?)(t.Value, t.Color))
            .FirstOrDefault();

    [Fact]
    public async Task ASayShowsDimmedAndSendingAtOnceThenTheHostsWordsReplaceIt()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var screen = Screen();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionsView<RgbaImage>? view = null;
        view = new SessionsView<RgbaImage>(renderer)
        {
            CanSay = true,
            OnSay = async (line, text) =>
            {
                await held.Task;
                view!.SayStatus = "delivered at once as a message";
                return null;
            },
        };
        var settled = Leaf(view, screen, "Ctrl+S").ShouldNotBeNull().Color;

        var send = view.SayInput.OnCommit!("keep going");

        send.IsCompleted.ShouldBeFalse();
        var sending = Leaf(view, screen, "sending…").ShouldNotBeNull();
        sending.Text.ShouldContain("keep going");
        sending.Color.ShouldBe(settled, "the sending line is as dim as the status line");
        view.SayInput.Text.ShouldBe("");

        held.SetResult();
        await send;

        Leaf(view, screen, "sending…").ShouldBeNull();
        Leaf(view, screen, "delivered at once as a message").ShouldNotBeNull();
    }

    [Fact]
    public async Task ASecondSayWhileOneIsSendingShowsBothAndARefusalReplacesTheMark()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var screen = Screen();
        TaskCompletionSource[] all = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        var gates = new Queue<TaskCompletionSource>(all);
        SessionsView<RgbaImage>? view = null;
        view = new SessionsView<RgbaImage>(renderer)
        {
            CanSay = true,
            OnSay = async (line, text) =>
            {
                await gates.Dequeue().Task;
                return $"{text} was not taken";
            },
        };
        Leaf(view, screen, "Ctrl+S").ShouldNotBeNull();

        var first = view.SayInput.OnCommit!("one");
        var second = view.SayInput.OnCommit!("two");

        var both = Leaf(view, screen, "sending…").ShouldNotBeNull().Text;
        both.ShouldContain("one");
        both.ShouldContain("two");
        view.Sending.ShouldBe(["one", "two"]);

        all[0].SetResult();
        await first;
        Leaf(view, screen, "sending…").ShouldNotBeNull().Text.ShouldNotContain("one");

        all[1].SetResult();
        await second;
        Leaf(view, screen, "sending…").ShouldBeNull();
        Leaf(view, screen, "refused: two was not taken").ShouldNotBeNull();
    }
}
