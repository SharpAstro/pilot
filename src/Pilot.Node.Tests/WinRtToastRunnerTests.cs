using Pilot.Contracts;
using Pilot.Node.Toasts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The in-process toast runner and pilot's AUMID registration (#1376); no process, no toast shown, no real registry.</summary>
public sealed class WinRtToastRunnerTests
{
    private sealed class FakeRegistry : ISchemeRegistry
    {
        public Dictionary<(string, string), string> Values { get; } = [];
        public int Writes { get; private set; }

        public string? Read(string subKey, string name) => Values.GetValueOrDefault((subKey, name));

        public void Write(string subKey, string name, string value)
        {
            Writes++;
            Values[(subKey, name)] = value;
        }
    }

    [Fact]
    public void TheAumidKeyHoldsTheDisplayNameAndTheIconWhenThereIsOne()
    {
        PilotAumid.Key.ShouldBe(@"Software\Classes\AppUserModelId\SharpAstro.Pilot");
        PilotAumid.Values(null).ShouldBe([(PilotAumid.Key, "DisplayName", "pilot")]);
        PilotAumid.Values(@"C:\bin\Resources\pilot-256.png").ShouldBe(
            [(PilotAumid.Key, "DisplayName", "pilot"), (PilotAumid.Key, "IconUri", @"C:\bin\Resources\pilot-256.png")]);
    }

    [Fact]
    public void TheIconIsTheGuisPngBesideTheNodeNeverAnExecutable()
    {
        var icon = PilotAumid.IconPathBeside(Path.Combine("bin", "pilot-node.exe"));
        icon.ShouldBe(Path.Combine("bin", "Resources", "pilot-256.png"));
        Path.GetExtension(icon).ShouldBe(".png");
    }

    [Fact]
    public void RegisteringTheAumidTwiceWritesOnce()
    {
        var registry = new FakeRegistry();
        PilotAumid.Register(registry, @"C:\bin\pilot-gui.exe").ShouldBeTrue();
        registry.Writes.ShouldBe(2);
        PilotAumid.Register(registry, @"C:\bin\pilot-gui.exe").ShouldBeFalse();
        registry.Writes.ShouldBe(2);
        PilotAumid.Register(registry, @"C:\other\pilot-gui.exe").ShouldBeTrue();
        registry.Writes.ShouldBe(3);
    }

    [Fact]
    public async Task TheRegistrarWritesTheAumidWithoutAnIconAndWithTheGuisPngWhenOneSitsBesideTheNode()
    {
        var registry = new FakeRegistry();
        await new AumidRegistrar(registry, @"C:\bin\pilot-node.exe", _ => false, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .StartAsync(TestContext.Current.CancellationToken);
        registry.Writes.ShouldBe(1);

        await new AumidRegistrar(registry, @"C:\bin\pilot-node.exe", _ => true, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .StartAsync(TestContext.Current.CancellationToken);
        registry.Writes.ShouldBe(2);
        registry.Values[(PilotAumid.Key, "IconUri")].ShouldEndWith("pilot-256.png");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TheNotificationIsBuiltFromTheCardXmlWithItsTagAndGroupAndNothingIsShown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinRT is Windows only");
        var xml = ToastXml.ForCard("c1", "it's \u2019 a \"title\"", "body", ToastTier.Urgent);
        WinRtToastRunner.BuildAndReadBack("card:c1", xml).ShouldBe(("card:c1", "pilot"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ABadXmlThrowsAnInvalidOperationNamingLoadXml()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinRT is Windows only");
        var failure = Should.Throw<InvalidOperationException>(() => WinRtToastRunner.BuildAndReadBack("t", "<toast><unclosed>"));
        failure.Message.ShouldContain("LoadXml");
        failure.Message.ShouldContain("0x");
    }
}
