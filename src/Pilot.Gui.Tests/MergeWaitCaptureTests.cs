using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A held merge's button says what it waits for (#918): the legs not done, a leg whose check reruns, CI running, a check failed;
/// a plain Merge only when a press would go through. The words are the node's (<c>LegCommander.LegWaitWords</c>, the checks'
/// progress); this checks what the card shows of them, and writes captures for the PR when a person asks.
/// </summary>
public sealed class MergeWaitCaptureTests
{
    private static CardRow Card(string id, string title, CardChoice merge) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "pr-ready", Subject = "flight/PIL909", Title = title,
        Summary = "The reviewer passed the PR; the node says below what the merge waits for.",
        Choices = [merge],
        At = Live.At, State = CardState.Open,
    };

    private static CardChoice Merge(string status, string? note) => new() { Label = "Merge", Verb = Verbs.Merge, Target = "flight/PIL909", Args = ["4f2c9a01"], Status = status, Note = note };

    /// <summary>One card per held state of the issue, and the ready one.</summary>
    internal static CardRow[] States() =>
    [
        Card("w1", "PIL909's PR #910 merges once review-notes-3 is done (legs 5/6 done)", Merge(ChoiceStatus.WaitsForLegs, "review-notes-3 (check rerunning)")),
        Card("w2", "PIL909's PR #910 merges once review-notes-3, review-notes-4 are done (legs 4/6 done)", Merge(ChoiceStatus.WaitsForLegs, "review-notes-3, review-notes-4")),
        Card("w3", "PIL909's PR #910 merges once its checks pass", Merge(ChoiceStatus.Armed, ArmedProgress.Say(3, 6))),
        Card("w4", "GitHub blocks the merge of PIL909: the check build (windows-latest) failed", Merge(ChoiceStatus.Blocked, "build (windows-latest) failed")),
        Card("w5", "PIL909 is reviewed and its PR is ready", Merge(ChoiceStatus.Passing, null)),
    ];

    [Fact]
    public void EachHeldStateSaysWhatItWaitsForAndOnlyTheReadyOneIsAPlainMerge()
    {
        var labels = States().Select(c => CardsView<object>.StatusLabel(c.Choices[0])).ToList();

        labels[0].ShouldStartWith("Waits for review-notes-3");
        labels[1].ShouldStartWith("Waits for review-notes-3");
        labels.Take(4).ShouldAllBe(l => l != "Merge", "a held merge never reads as a bare Merge");
        labels[4].ShouldBe("Merge");

        // A card's button wraps, so it carries the whole wait; the PR row's one-line chip keeps the short one, the rest on hover.
        CardsView<object>.StatusLabel(States()[0].Choices[0], fits: CardsView<object>.WrappedNoteFits).ShouldBe("Waits for review-notes-3 (check rerunning)");
    }

    [Fact]
    public void CapturesOfEachStateAtTheNarrowestAndAWidePanel()
    {
        // Written only when a person asks (PILOT_CARD_CAPTURES names the folder): docs/captures, for the PR.
        if (Environment.GetEnvironmentVariable("PILOT_CARD_CAPTURES") is not { Length: > 0 } folder)
        {
            return;
        }
        var tag = Environment.GetEnvironmentVariable("PILOT_CARD_CAPTURE_TAG") is { Length: > 0 } t ? $"-{t}" : "";
        Directory.CreateDirectory(folder);
        foreach (var width in new[] { (int)CardsView<RgbaImage>.MinWidth, 420 })
        {
            const int height = 1200;
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
            view.Render(CardsScreen.From(States()), new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(folder, $"merge-wait-{width}{tag}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }
}
