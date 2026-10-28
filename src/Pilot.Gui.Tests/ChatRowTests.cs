using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// The chat's rows at real pixel widths: a reply whose text wraps beside the fixed label and time takes the height of the
/// lines it is drawn in, so nothing is drawn over the row below, and a long message of the person's wraps instead of ending
/// in an ellipsis. The rows were measured at the row's whole width, fewer lines than the text column takes, and each long
/// reply's last line sat on the next row (SE1, 2026-10-28; DIR.Lib's stack measure).
/// </summary>
public sealed class ChatRowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 0, 37, 18, TimeSpan.Zero);

    private const string Answer = "Merge PIL383's PR #390 once CI is green (my answer to: What should I do with PIL383's PR #390 (c1727)?)";

    private static TranscriptEntry[] Feed() =>
    [
        new() { Index = 0, Kind = TranscriptKind.Assistant, Text = "Tests pass (2891 passed, 14 skipped). Now pushing the branch and opening the draft PR, as you allowed.", At = At },
        new() { Index = 1, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "git push", Description = "Push branch and open draft PR", ToolUseId = "t1", At = At.AddSeconds(1) },
        new() { Index = 2, Kind = TranscriptKind.ToolResult, Text = "ok", ToolUseId = "t1" },
        new() { Index = 3, Kind = TranscriptKind.Assistant, Text = "DIR.Lib's navigation work is now in draft PR #168, and its review has started: https://github.com/SharpAstro/DIR.Lib/pull/168\n\n- **Rebase:** 497a4f8 now sits on `587e701` as 62375c7. DIR164's editor already took 11.38, so I bumped this to 11.39. I fixed the CHANGELOG conflict by putting the navigation entry above the editor's 11.38 entry.\n- **Tests:** the full Release suite passes (2891 passed, 14 skipped).\n- **Branch:** pushed as feat/navigation-strip. Nothing went to main.\n- **Cards:** I dismissed c1550 with the reason. I also nudged the PIL375 overlap-card-connect worker (c1719).\n\nDIR.Lib publishes 11.39 when the PR merges, and the node merges it once the review passes. Then TIA1821's `dir-lib-nav` leg still needs steps 7 and 8, with tianwen's pins moved to 11.39 instead of 11.38. The leg was written for 11.38, so I'll point it at 11.39 when the package is out.", At = At.AddSeconds(55) },
        new() { Index = 4, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "pilot status", Description = "Check PIL375 worker state", ToolUseId = "t2", At = At.AddSeconds(62) },
        new() { Index = 5, Kind = TranscriptKind.ToolResult, Text = "ok", ToolUseId = "t2" },
        new() { Index = 6, Kind = TranscriptKind.User, Text = Answer, At = At.AddMinutes(7) },
        new() { Index = 7, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "gh pr checks 390", Description = "Check CI on PR 390", ToolUseId = "t3", At = At.AddMinutes(7) },
        new() { Index = 8, Kind = TranscriptKind.ToolResult, Text = "ok", ToolUseId = "t3" },
        new() { Index = 9, Kind = TranscriptKind.Assistant, Text = "PIL383's PR #390 is already queued to merge, so I changed nothing. The autopilot closed c1727 itself and will merge once its last check passes. Two of the three checks are done and green. Nothing more is needed from you for it.", At = At.AddMinutes(8) },
    ];

    private static async Task<ChatView<RgbaImage>> Chat(RgbaImageRenderer renderer)
    {
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var feed = Feed();
        chat.Load = (after, _) => Task.FromResult(after < 0 ? new ChatPage { Entries = feed, Total = feed.Length } : new ChatPage { Entries = [], Total = feed.Length });
        chat.OnSend = _ => Task.FromResult<string?>(null);
        chat.Open();
        for (var i = 0; i < 100 && chat.Entries.Count < feed.Length; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        chat.Close();
        chat.Entries.Count.ShouldBe(feed.Length);
        return chat;
    }

    // At DPI 1.25 and 1.5 as well, as the flight page's padded panels are held (DIR.Lib#163): a measure off by a few
    // pixels shows at one scale and not another.
    [Theory]
    [InlineData(700, 900, 1f)]
    [InlineData(900, 700, 1f)]
    [InlineData(1640, 1260, 1f)]
    [InlineData(1640, 1260, 1.25f)]
    [InlineData(1640, 1260, 1.5f)]
    [InlineData(1100, 900, 1.5f)]
    public async Task NoRowsTextIsDrawnOverAnotherRowAndALongMessageWraps(int width, int height, float dpi)
    {
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chat = await Chat(renderer);
        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);
        var arranged = Layout.Engine.Arrange(chat.Tree(), new Rect<float>(0, 0, width, height), ctx);

        // Every text leaf in the page's body (under the header), as drawn: no two of them share a pixel.
        var texts = arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value.Length: > 0 } } && a.Bounds.Y > 70f * dpi)
            .Select(a => (Text: ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, a.Bounds))
            .ToList();
        texts.Count.ShouldBeGreaterThan(50);
        for (var i = 0; i < texts.Count; i++)
        {
            for (var j = i + 1; j < texts.Count; j++)
            {
                var (a, b) = (texts[i].Bounds, texts[j].Bounds);
                var overlap = a.X < b.X + b.Width - 0.5f && b.X < a.X + a.Width - 0.5f && a.Y < b.Y + b.Height - 0.5f && b.Y < a.Y + a.Height - 0.5f;
                overlap.ShouldBeFalse($"'{texts[i].Text}' {a} is drawn over '{texts[j].Text}' {b} at {width} wide, DPI {dpi}");
            }
        }

        // The person's long message is there whole, its last word included, and nothing in the feed ends in an ellipsis.
        texts.ShouldContain(t => t.Text.Trim() == "(c1727)?)");
        texts.ShouldNotContain(t => t.Text.EndsWith('…'));

        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            chat.Render(new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(capture) ?? ".", $"chat-rows-{width}-{dpi}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }
}
