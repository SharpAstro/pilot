using Pilot.Contracts;
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
    public void ALoneSurrogateIsDroppedAndAPairKept()
    {
        ToastXml.Cut("a\uD83Db \uDE00c \uD83D\uDE00d", ToastXml.TitleLimit).ShouldBe("ab c \uD83D\uDE00d");
        ToastXml.Cut("end\uD83D", ToastXml.TitleLimit).ShouldBe("end");
        Parse(ToastXml.ForCard("c1", "x\uDE00y", "\uD83Dz", ToastTier.Normal)).Descendants("text").Select(t => t.Value).ShouldBe(["xy", "z"]);
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
        script.ShouldContain($"$toast.Tag = {PowerShellToastRunner.Decoded("card:c1")}");
        script.ShouldContain("$toast.Group = 'pilot'");
        script.ShouldContain(@"CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe').Show($toast)");
        script.ShouldContain(PowerShellToastRunner.Decoded(xml), Case.Sensitive);
        PowerShellToastRunner.Quote("it's").ShouldBe("'it''s'");
        script.ShouldNotContain("while");
    }

    // The four curly quotes PowerShell reads as single quotes, written as escapes so no source line starts with one.
    private static readonly string[] CurlyQuotes = ["\u2018", "\u2019", "\u201A", "\u201B"];

    [Fact]
    public void CardTextNeverReachesTheScriptAsText()
    {
        foreach (var quote in CurlyQuotes)
        {
            var text = $"Don{quote}t'; calc.exe; {quote}'";
            var xml = ToastXml.ForCard("c1", text, text, ToastTier.Normal);
            var script = PowerShellToastRunner.ShowScript(text, xml);
            script.ShouldNotContain(quote, Case.Sensitive, "no card text, so no quote of any kind, is written into the script");
            script.ShouldNotContain("calc.exe");
            PowerShellToastRunner.RemoveScript(text).ShouldNotContain(quote);
            // What the script decodes is exactly the card's own text.
            PowerShellToastRunner.Decoded(text).ShouldContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
        }
    }

    [Fact]
    public void QuoteDoublesEveryCharacterPowerShellTakesForASingleQuote()
    {
        foreach (var quote in CurlyQuotes.Append("'"))
            PowerShellToastRunner.Quote($"a{quote}b").ShouldBe($"'a{quote}{quote}b'");
    }

    [Fact]
    public void ACurlyQuoteInTheTitleBodyAndTagKeepsTheXmlWellFormed()
    {
        foreach (var quote in CurlyQuotes)
        {
            var xml = ToastXml.ForCard("c1", $"Don{quote}t", $"it{quote}s", ToastTier.Normal);
            Parse(xml).Descendants("text").Select(t => t.Value).ShouldBe([$"Don{quote}t", $"it{quote}s"]);
            ToastXml.TagOf($"flight/a{quote}b").ShouldBe($"flight/a{quote}b");
        }
    }

    [Fact]
    public void AControlCharacterInTheTextIsDroppedSoTheXmlLoads()
    {
        var xml = ToastXml.ForCard("c1", "red\u001b[31m alert", "a\u0000b\u0007c", ToastTier.Normal);
        Parse(xml).Descendants("text").Select(t => t.Value).ShouldBe(["red[31m alert", "abc"]);
    }

    [Fact]
    public void ALongSubjectGetsAStableShortTagAndAShortOneKeepsItsOwn()
    {
        var repo = @"repo/C:\Users\SebastianGodelet\source\repos\sharpastro\openphd2-multiscope-with-a-long-name-" + new string('x', 40);
        var tag = ToastXml.TagOf(repo);
        tag.Length.ShouldBeLessThanOrEqualTo(ToastXml.TagLimit);
        tag.ShouldStartWith("s:");
        ToastXml.TagOf(repo).ShouldBe(tag, "the same subject has the same tag, for the show and the withdraw");
        ToastXml.TagOf(repo + "y").ShouldNotBe(tag);
        ToastXml.TagOf(new string('a', 100)).Length.ShouldBeLessThanOrEqualTo(ToastXml.TagLimit);
        ToastXml.TagOf("flight/PIL1356").ShouldBe("flight/PIL1356");
        ToastXml.TagOf(new string('a', ToastXml.TagLimit)).ShouldBe(new string('a', ToastXml.TagLimit));
    }

    [Fact]
    public void BothScriptsCatchTheirErrorAndWriteItAsPlainText()
    {
        foreach (var script in new[] { PowerShellToastRunner.ShowScript("t", "<toast/>"), PowerShellToastRunner.RemoveScript("t") })
        {
            script.ShouldContain("try {");
            script.ShouldEndWith("} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\r\n");
        }
    }

    [Fact]
    public void AFailedScriptSaysItsExitAndItsFirstPlainErrorLine()
    {
        PowerShellToastRunner.FailureOf(0, "anything").ShouldBeNull();
        PowerShellToastRunner.FailureOf(1, "Element not found.\r\nmore\r\n").ShouldBe("the toast script exited 1: Element not found.");
        PowerShellToastRunner.FailureOf(1, "#< CLIXML\r\n<Objs Version=\"1.1.0.1\"><S S=\"Error\">boom</S></Objs>\r\nthe cause\r\n")
            .ShouldBe("the toast script exited 1: the cause");
        PowerShellToastRunner.FailureOf(2, "").ShouldBe("the toast script exited 2: no error text");
    }

    [Fact]
    public void TheWithdrawScriptRemovesTheTagFromTheHistory()
    {
        var script = PowerShellToastRunner.RemoveScript("card:c1");
        script.ShouldContain(@"History.Remove(" + PowerShellToastRunner.Decoded("card:c1") + @", 'pilot', '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe')");
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
