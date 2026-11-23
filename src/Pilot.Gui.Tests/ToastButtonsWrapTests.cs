using System.Collections.Immutable;
using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// A staying card's toast shows every choice's whole label (the owner, 2026-11-23: the 12-character cut hid "Nudge the worker" and
/// "Restart the worker"): the library's button row wraps when the buttons do not fit, so the buttons sit on two rows inside the toast.
/// </summary>
public sealed class ToastButtonsWrapTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    private static CardRow Stuck() => new()
    {
        Id = "c28040", Bin = CardBin.Ring, Kind = "worker-stopped", Subject = "flight/PIL1293/gh-call-counter",
        Title = "PIL1293 is stuck", Summary = "Its worker stopped with the leg unfinished.", At = T0.AddSeconds(0.5), State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = "Nudge the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" },
            new CardChoice { Label = "Restart the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" },
        ],
    };

    private static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Squeezed(string text) => string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)));

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void ThreeButtonsThatDoNotFitOneRowWrapInsideTheToastWithEveryLabelWhole(float dpi)
    {
        var (width, height) = ((int)(1920 * dpi), (int)(1080 * dpi));
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        var seen = ImmutableArray<Layout.ArrangedNode<float>>.Empty;
        chrome.NotificationsLayer.Arranged = nodes => seen = nodes;
        chrome.Notifications.Open = false;
        chrome.Show(PilotPage.Jobs);
        var data = PageData.For("sample");
        void Frame(CardsScreen cards, int seconds) =>
            chrome.Render(data.Sessions with { AsOf = T0.AddSeconds(seconds) }, data.Flights, cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines);
        Frame(CardsScreen.Empty, 0);
        Frame(CardsScreen.From([Stuck()]), 1);
        Frame(CardsScreen.From([Stuck()]), 2);

        var text = ArrangedText.Concat(seen);
        text.ShouldNotContain("…");
        Squeezed(text).ShouldContain(Squeezed("Nudge the worker"));
        Squeezed(text).ShouldContain(Squeezed("Restart the worker"));
        text.ShouldContain("Dismiss");

        var buttons = seen.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var act } && act.StartsWith("toast:c28040:") && !act.EndsWith(":whole")).ToArray();
        buttons.Length.ShouldBeGreaterThanOrEqualTo(2);
        var dismiss = seen.Where(a => a.Node.Hit is DIR.Lib.Notifications.NotificationHit { Part: DIR.Lib.Notifications.NotificationPart.Dismiss }).ToArray();
        dismiss.Length.ShouldBe(1, "the toast's one Dismiss, found by its hit node");
        // Each button is held by its whole hit node, padding included.
        var held = buttons.Concat(dismiss).ToArray();
        var title = seen.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.Contains("PIL1293")).Bounds;
        var toast = seen.Where(a => a.Bounds.X <= title.X && a.Bounds.Y <= title.Y && a.Bounds.X + a.Bounds.Width >= title.X + title.Width
                && held.All(b => b.Bounds.X >= a.Bounds.X - 0.5f && b.Bounds.Y >= a.Bounds.Y - 0.5f
                    && b.Bounds.X + b.Bounds.Width <= a.Bounds.X + a.Bounds.Width + 0.5f && b.Bounds.Y + b.Bounds.Height <= a.Bounds.Y + a.Bounds.Height + 0.5f))
            .OrderBy(a => a.Bounds.Width * a.Bounds.Height).First().Bounds;
        // The smallest node holding the title, both choices and Dismiss in full is the toast's own frame; the layer's root (the whole window) would pass any
        // check, so the frame must be no wider than a toast (its width plus the frame's own margin).
        toast.Width.ShouldBeLessThanOrEqualTo((NotificationsHost.Style.ToastWidth + 2f * NotificationsHost.Style.Padding) * dpi + 1f,
            "the frame holding the buttons is the toast, not the layer over the window");
        foreach (var b in held)
        {
            var name = b.Node.Hit?.ToString() ?? "Dismiss";
            (b.Bounds.X + b.Bounds.Width).ShouldBeLessThanOrEqualTo(toast.X + toast.Width + 0.5f, $"{name} inside the toast on the right");
            (b.Bounds.Y + b.Bounds.Height).ShouldBeLessThanOrEqualTo(toast.Y + toast.Height + 0.5f, $"{name} inside the toast at the bottom");
        }
        buttons.Select(b => MathF.Round(b.Bounds.Y)).Distinct().Count().ShouldBeGreaterThan(1, "the buttons wrap onto more than one row");
        TextOverlap.Faults(seen).Select(f => f.Line).ShouldBeEmpty();

        File.WriteAllBytes(Path.Combine(ArtifactDir(), $"toast-buttons-wrap-dpi{dpi:0.0}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
    }
}
