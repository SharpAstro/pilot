using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Node.Toasts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The per-user <c>pilot:</c> URL scheme a toast's click opens (#1356): the values the node writes, idempotently, behind a seam; no registry.</summary>
public sealed class PilotSchemeTests
{
    private const string Gui = @"C:\Users\ada\AppData\Local\pilot2\bin\pilot-gui.exe";

    private sealed class FakeRegistry : ISchemeRegistry
    {
        public Dictionary<(string, string), string> Values { get; } = [];
        public int Writes { get; private set; }
        public string? Read(string subKey, string name) => Values.GetValueOrDefault((subKey, name));
        public void Write(string subKey, string name, string value) { Values[(subKey, name)] = value; Writes++; }
    }

    [Fact]
    public void TheValuesAreAUrlProtocolWhoseOpenCommandStartsTheGuiWithTheLink()
    {
        PilotScheme.Values(Gui).ShouldBe(
        [
            (@"Software\Classes\pilot", "", "URL:Pilot"),
            (@"Software\Classes\pilot", "URL Protocol", ""),
            (@"Software\Classes\pilot\shell\open\command", "", @"C:\Users\ada\AppData\Local\pilot2\bin\pilot-gui.exe ""%1"""),
        ]);
    }

    [Fact]
    public void ThePathWithASpaceIsQuoted() =>
        PilotScheme.Values(@"C:\Program Files\pilot\pilot-gui.exe")[2].Value.ShouldBe(@"""C:\Program Files\pilot\pilot-gui.exe"" ""%1""");

    [Fact]
    public void RegisteringWritesOnceAndAgainOnlyWhatChanged()
    {
        var registry = new FakeRegistry();
        PilotScheme.Register(registry, Gui).ShouldBeTrue();
        registry.Writes.ShouldBe(3);

        PilotScheme.Register(registry, Gui).ShouldBeFalse();
        registry.Writes.ShouldBe(3);

        PilotScheme.Register(registry, @"D:\new\pilot-gui.exe").ShouldBeTrue();
        registry.Writes.ShouldBe(4, "only the command moved");
    }

    // The identity seam: like AppShell's AppIdentity.Register, it writes only what differs, so a second start writes nothing.
    private sealed class FakeIdentity : IToastIdentity
    {
        private (string, string?)? _last;
        public int Writes { get; private set; }
        public (string AppId, string Name, string? Icon)? Last { get; private set; }
        public string? Register(string appId, string displayName, string? iconPath)
        {
            Last = (appId, displayName, iconPath);
            if (_last != (displayName, iconPath)) { _last = (displayName, iconPath); Writes++; }
            return null;
        }
    }

    [Fact]
    public async Task TheIdentityCarriesThePngBesideTheNodeOnlyWhenItIsThere()
    {
        var icon = ToastIdentityRegistrar.IconPathBeside(Path.Combine("bin", "pilot-node.exe"));
        icon.ShouldEndWith("pilot-256.png");
        Path.GetExtension(icon).ShouldBe(".png", "an image, never an executable");

        var without = new FakeIdentity();
        await new ToastIdentityRegistrar(without, @"C:\bin\pilot-node.exe", _ => false, NullLogger.Instance).StartAsync(TestContext.Current.CancellationToken);
        without.Last.ShouldBe(("SharpAstro.Pilot", "pilot", null));

        var with = new FakeIdentity();
        await new ToastIdentityRegistrar(with, @"C:\bin\pilot-node.exe", _ => true, NullLogger.Instance).StartAsync(TestContext.Current.CancellationToken);
        with.Last!.Value.Icon.ShouldEndWith("pilot-256.png");
    }

    [Fact]
    public async Task ASecondStartWritesNothingAndAFailureIsNeverFatal()
    {
        var identity = new FakeIdentity();
        var registrar = new ToastIdentityRegistrar(identity, @"C:\bin\pilot-node.exe", _ => true, NullLogger.Instance);
        await registrar.StartAsync(TestContext.Current.CancellationToken);
        await registrar.StartAsync(TestContext.Current.CancellationToken);
        identity.Writes.ShouldBe(1);

        var failing = new RecordingToastIdentity { Failure = "the registry refused the app id key" };
        await new ToastIdentityRegistrar(failing, @"C:\bin\pilot-node.exe", _ => false, NullLogger.Instance).StartAsync(TestContext.Current.CancellationToken);
        failing.Registered.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheRegistrarWritesOnlyWhenTheGuiSitsBesideTheNode()
    {
        var registry = new FakeRegistry();
        await new SchemeRegistrar(registry, @"C:\bin\pilot-node.exe", _ => false, NullLogger.Instance).StartAsync(TestContext.Current.CancellationToken);
        registry.Writes.ShouldBe(0);

        await new SchemeRegistrar(registry, @"C:\bin\pilot-node.exe", _ => true, NullLogger.Instance).StartAsync(TestContext.Current.CancellationToken);
        registry.Writes.ShouldBe(3);
    }
}
