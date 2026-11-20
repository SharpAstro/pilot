using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Xunit;

namespace Pilot.Gui.Tests;

public sealed class ProbeDepthTests
{
    [Theory]
    [InlineData(520, null)]
    [InlineData(520, "right:9999:0")]
    [InlineData(360, "right:9999:0")]
    [InlineData(700, "right:9999:0")]
    public void Probe(int height, string? place)
    {
        var data = PageData.For("sample");
        using var renderer = new RgbaImageRenderer(900, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.Mode = FlightsViewMode.Canvas;
        if (place is not null) chrome.Flights.MapLayers.Restore(new MapLayerSettings { Palette = place });
        chrome.Notifications.State = BarStates.Collapsed;
        chrome.Show(PilotPage.Flights);
        var card = new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/ORBc1", Title = "Card c1", Summary = "A short line.", At = Live.At, State = CardState.Open,
            Choices = [new CardChoice { Label = "Start", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }] };
        for (var i = 0; i < 2; i++) chrome.Render(data.Sessions, data.Flights, CardsScreen.From([card]), data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, 900, height), data.Machines);
        var lines = new List<string> { $"h={height} place={place} body={chrome.Flights.MapBody} palette={chrome.Flights.MapLayers.Palette.PanelRect}" };
        var regs = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(regs);
        foreach (var r in regs) lines.Add($"note {r.Result} {r.X},{r.Y} {r.Width}x{r.Height}");
        regs.Clear();
        chrome.Flights.CollectPaintedRegions(regs);
        foreach (var r in regs) lines.Add($"page {r.Result} {r.X},{r.Y} {r.Width}x{r.Height}");
        File.WriteAllLines($@"C:\Users\SebastianGodelet\.claude\jobs\53289a44\tmp\probe-{height}-{(place is null ? "def" : "low")}.txt", lines);
    }
}
