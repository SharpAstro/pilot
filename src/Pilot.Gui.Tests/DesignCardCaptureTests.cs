using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Captures of a design card as the panel draws it, before (the question whole, so the panel cuts it at 240 with no mark; options of about
/// 120 characters, cut at two lines with no mark) and after (the summary bounded to 240 with an ellipsis, Recommended dropped when it does
/// not fit; option buttons ending in an ellipsis) at a narrow and a wide window. Only with PILOT_TEST_CAPTURE set (ui-changes.md); the
/// words are those <c>PrReviews.DesignSummary</c> and <c>DesignChoices</c> make of the same finding (pinned in the node's DesignCardWordsTests).
/// </summary>
public sealed class DesignCardCaptureTests
{
    private static CardChoice Choice(string label) => new() { Label = label, Verb = Verbs.FixPr, Target = "pr/SharpAstro/pilot#888", Args = [label] };

    private static readonly CardChoice Answer = new() { Label = "Answer in your own words", Verb = Verbs.FixPr, Target = "pr/SharpAstro/pilot#888", AsksFor = "Your answer" };

    private static CardRow Card(string id, string summary, params CardChoice[] choices) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "design", Subject = "pr/SharpAstro/pilot#888",
        Title = "SharpAstro/pilot#888: the review asks a question of design: SiblingSource(job) exports UseLocalSiblings=true for a plane job of any repo",
        Summary = summary, Choices = [.. choices, Answer], At = Live.At, State = CardState.Open,
    };

    [Fact]
    public void CapturesOfADesignCardBeforeAndAfterAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        const string question = "SiblingSource(job) exports UseLocalSiblings=true for a plane job of any repo, so a job in a repo with no sibling clones restores against folders that do not exist: should the variable be exported only when SiblingWatch.ReachedClones has the job's repo, or always?";
        const string recommended = "export the variable only when SiblingWatch.ReachedClones(job.Repo) is true, and say so on the job's line in pilot status (jobs in repos with no clones restore packages)";
        const string other = "same, and also ring a card on the flight when the label is on a repo that has no sibling clones on this node at all (one more card kind)";
        var before = Card("before", question + " Recommended: " + recommended[..80] + ". It rings because no option reaches the criteria.", Choice(recommended), Choice(other));
        var afterQuestion = question[..(240 - " It rings because no option reaches the criteria.".Length - 3)].TrimEnd() + "...";
        var after = Card("after", afterQuestion + " It rings because no option reaches the criteria.", Choice(recommended[..41] + "..."), Choice(other[..41] + "..."));
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var (width, height) in new[] { (700, 600), (1600, 600) })
        {
            foreach (var (name, card) in new[] { ("before", before), ("after", after) })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([card]), new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"design-card-{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }
}
