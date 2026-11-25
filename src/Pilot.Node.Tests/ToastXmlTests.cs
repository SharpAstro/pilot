using System.Text;
using System.Xml.Linq;
using Pilot.Node.Toasts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The toast XML of a card, the burst toast and the scripts that show and withdraw one (#1356); a fake runner, no process.</summary>
public sealed class ToastXmlTests
{
    private sealed class FakeToastRunner : IToastRunner
    {
        public List<(string Op, string Tag, string? Xml)> Calls { get; } = [];

        public Task Show(string tag, string xml, CancellationToken cancel = default)
        {
            Calls.Add(("show", tag, xml));
            return Task.CompletedTask;
        }

        public Task Remove(string tag, CancellationToken cancel = default)
        {
            Calls.Add(("remove", tag, null));
            return Task.CompletedTask;
        }
    }

    private static XElement Parse(string xml) => XDocument.Parse(xml).Root!;

    [Fact]
    public void TextIsEscapedAndTheXmlStaysWellFormed()
    {
        var xml = ToastXml.ForCard("c1", "A <b> & \"c\" 'd'", "x < y > z & \"q\" 'r'", ToastTier.Normal);
        xml.ShouldContain("A &lt;b&gt; &amp; &quot;c&quot; &apos;d&apos;");
        xml.ShouldContain("x &lt; y &gt; z &amp; &quot;q&quot; &apos;r&apos;");
        var texts = Parse(xml).Descendants("text").Select(t => t.Value).ToArray();
        texts.ShouldBe(["A <b> & \"c\" 'd'", "x < y > z & \"q\" 'r'"]);
    }

    [Fact]
    public void ALongTitleAndBodyAreCutWithAnEllipsis()
    {
        var xml = ToastXml.ForCard("c1", new string('t', 200), new string('b', 900), ToastTier.Normal);
        var texts = Parse(xml).Descendants("text").Select(t => t.Value).ToArray();
        texts[0].Length.ShouldBe(ToastXml.TitleLimit);
        texts[0].ShouldEndWith("…");
        texts[1].Length.ShouldBe(ToastXml.BodyLimit);
        texts[1].ShouldEndWith("…");
    }

    [Fact]
    public void TextWithinTheLimitIsKeptWhole()
    {
        var texts = Parse(ToastXml.ForCard("c1", "short title", "one\ntwo   three", ToastTier.Normal)).Descendants("text").Select(t => t.Value).ToArray();
        texts.ShouldBe(["short title", "one two three"]);
    }

    [Fact]
    public void TheCutNeverSplitsASurrogatePair()
    {
        var text = new string('a', ToastXml.TitleLimit - 2) + "😀😀";
        var cut = ToastXml.Cut(text, ToastXml.TitleLimit);
        cut.ShouldEndWith("…");
        cut.Length.ShouldBeLessThanOrEqualTo(ToastXml.TitleLimit);
        cut[..^1].ToCharArray().Count(char.IsHighSurrogate).ShouldBe(cut[..^1].ToCharArray().Count(char.IsLowSurrogate));
    }

    [Fact]
    public void ANoBodyCardHasOnlyATitleLine()
    {
        Parse(ToastXml.ForCard("c1", "title", null, ToastTier.Normal)).Descendants("text").Count().ShouldBe(1);
    }

    [Fact]
    public void OnlyAnUrgentToastCarriesTheUrgentScenario()
    {
        Parse(ToastXml.ForCard("c1", "t", "b", ToastTier.Urgent)).Attribute("scenario")?.Value.ShouldBe("urgent");
        Parse(ToastXml.ForCard("c1", "t", "b", ToastTier.Normal)).Attribute("scenario").ShouldBeNull();
        Parse(ToastXml.Burst(4)).Attribute("scenario").ShouldBeNull();
    }

    [Fact]
    public void AClickLaunchesTheCardByProtocol()
    {
        var root = Parse(ToastXml.ForCard("card 7/x", "t", "b", ToastTier.Normal));
        root.Attribute("activationType")?.Value.ShouldBe("protocol");
        root.Attribute("launch")?.Value.ShouldBe("pilot://card/card%207%2Fx");
        Parse(ToastXml.ForCard("abc", "t", "b", ToastTier.Urgent)).Attribute("launch")?.Value.ShouldBe("pilot://card/abc");
    }

    [Fact]
    public void EveryToastHasADismissAction()
    {
        foreach (var xml in new[] { ToastXml.ForCard("c", "t", "b", ToastTier.Urgent), ToastXml.ForCard("c", "t", "b", ToastTier.Normal), ToastXml.Burst(5) })
        {
            var action = Parse(xml).Descendants("action").Single();
            action.Attribute("activationType")?.Value.ShouldBe("system");
            action.Attribute("arguments")?.Value.ShouldBe("dismiss");
            action.Attribute("content")?.Value.ShouldBe("Dismiss");
        }
    }

    [Fact]
    public void TheBurstToastSaysHowManyCardsNeedYou()
    {
        Parse(ToastXml.Burst(4)).Descendants("text").Select(t => t.Value).ShouldBe(["4 cards need you"]);
        ToastXml.BurstTag.ShouldBe("pilot-burst");
    }

    [Fact]
    public void TheShowScriptCarriesTheTagGroupAumidAndTheXml()
    {
        var xml = ToastXml.ForCard("c1", "it's", "b", ToastTier.Urgent);
        var script = PowerShellToastRunner.ShowScript("card:c1", xml);
        script.ShouldContain("Windows.UI.Notifications.ToastNotificationManager");
        script.ShouldContain("Windows.Data.Xml.Dom.XmlDocument");
        script.ShouldContain("$toast.Tag = 'card:c1'");
        script.ShouldContain("$toast.Group = 'pilot'");
        script.ShouldContain(@"CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe').Show($toast)");
        script.ShouldContain("it''s");
        script.ShouldNotContain("while");
    }

    [Fact]
    public void TheWithdrawScriptRemovesTheTagFromTheHistory()
    {
        var script = PowerShellToastRunner.RemoveScript("card:c1");
        script.ShouldContain(@"History.Remove('card:c1', 'pilot', '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe')");
        script.ShouldNotContain("Show(");
    }

    [Fact]
    public void TheProcessIsWindowlessWindowsPowerShellWithTheScriptEncoded()
    {
        var script = PowerShellToastRunner.RemoveScript("t");
        var start = PowerShellToastRunner.StartOf(script);
        start.FileName.ShouldBe("powershell.exe");
        start.CreateNoWindow.ShouldBeTrue();
        start.ArgumentList.ShouldBe(["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", PowerShellToastRunner.Encode(script)]);
        Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[^1])).ShouldBe(script);
    }

    [Fact]
    public async Task AFakeRunnerRecordsAShowAndAWithdrawWithNoProcess()
    {
        var runner = new FakeToastRunner();
        var xml = ToastXml.ForCard("c1", "t", "b", ToastTier.Normal);
        await runner.Show("c1", xml, TestContext.Current.CancellationToken);
        await runner.Remove("c1", TestContext.Current.CancellationToken);
        runner.Calls.ShouldBe([("show", "c1", xml), ("remove", "c1", null)]);
    }
}
