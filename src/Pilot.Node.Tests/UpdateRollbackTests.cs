using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Updates;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An update to a version that cannot start goes back (pilot #830): the install keeps the release it replaces in <c>previous/</c> and a record in the
/// data folder; the new node counts its starts and clears the record once the host has started; the second failed start in a row puts the old files
/// back, starts the restored node and exits; the restored node says so in one card, and auto-update does not install that version again. Every test
/// works in a temp folder with a fake <c>StartSuccessor</c> and a fake host build: no node is started and the installed pilot is never touched.
/// </summary>
public sealed class UpdateRollbackTests : IDisposable
{
    private const string Page = "https://github.com/SharpAstro/pilot/releases/";
    private const string Rid = "test-rid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Node = NodeUpdater.Layout.Required[0];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _install;
    private readonly string _data;
    private readonly List<string> _started = [];
    private readonly List<string> _said = [];

    public UpdateRollbackTests()
    {
        _install = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnInstallKeepsThePreviousReleaseWholeAndTheRecordOfTheUpdate()
    {
        Install("2.0.1", Binaries("old", ["Fonts/a.ttf"]));
        var before = Tree(_install);

        var installed = await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);

        installed.Ok.ShouldBeTrue(installed.Error);
        Read(Node).ShouldBe("new " + Node);
        // What was installed, byte for byte, marker included, and nothing of the new release.
        Tree(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldBe(before);
        ReleaseInstall.ReadMarker(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldNotBeNull().Version.ShouldBe("2.0.1");
        Rollback("2.0.1").Read().ShouldBe(new UpdateRecord("2.0.1", "2.0.2"));
        Directory.EnumerateFiles(_data, "*.tmp").ShouldBeEmpty();
        _started.ShouldBe([Path.Combine(_install, Node)]);
    }

    [Fact]
    public async Task AGoodStartClearsTheRecordAndPreviousStaysForTheNextInstallToReplace()
    {
        Install("2.0.1", Binaries("old"));
        var before = Tree(_install);
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);

        var trial = Rollback("2.0.2").Begin().ShouldNotBeNull();

        trial.Record.FailedStarts.ShouldBe(1);
        Rollback("2.0.2").Read()!.FailedStarts.ShouldBe(1);
        trial.Clear();
        Rollback("2.0.2").Read().ShouldBeNull();
        File.Exists(Path.Combine(_data, UpdateRollback.RecordFile)).ShouldBeFalse();
        Tree(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldBe(before);

        // The next install replaces it with the release it replaces: 2.0.2.
        await Updater(UpdatePolicy.Off, Feed("2.0.3", Binaries("newer")), version: "2.0.2").RunAsync(Command(UpdateArgs.Install), Ct);

        Read(Node).ShouldBe("newer " + Node);
        Read(Path.Combine(ReleaseInstall.PreviousName, Node)).ShouldBe("new " + Node);
        ReleaseInstall.ReadMarker(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldNotBeNull().Version.ShouldBe("2.0.2");
        Rollback("2.0.2").Read().ShouldBe(new UpdateRecord("2.0.2", "2.0.3"));
    }

    [Fact]
    public async Task TwoFailedStartsPutTheOldFilesBackByteForByteStartTheRestoredNodeAndLeaveARecordThatBecomesOneRingCard()
    {
        Install("2.0.1", Binaries("old", ["Fonts/a.ttf"]));
        var before = Tree(_install);
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new", ["Fonts/b.ttf"]))).RunAsync(Command(UpdateArgs.Install), Ct);
        _started.Clear();
        var failing = Rollback("2.0.2");
        using var held = Held();

        // The first failed start counts it and starts the node once more, from its own binary.
        var first = await NodeProgram.RunHostAsync(Options, held, _said.Add, failing, Fails("the stored state is not one 2.0.2 reads"), Ct);

        first.ShouldBe(1);
        _started.ShouldBe([Path.Combine(_install, Node)]);
        Read(Node).ShouldBe("new " + Node);
        failing.Read().ShouldNotBeNull().FailedStarts.ShouldBe(1);
        failing.Read()!.LastError.ShouldNotBeNull().ShouldContain("the stored state is not one 2.0.2 reads");
        _said.ShouldContain(line => line.Contains("starting it once more", StringComparison.Ordinal));

        // The second, in a row, puts the old release back and starts that node instead.
        _started.Clear();
        var second = await NodeProgram.RunHostAsync(Options, held, _said.Add, failing, Fails("the stored state is not one 2.0.2 reads"), Ct);

        second.ShouldBe(NodeExitCodes.Stopped);
        _started.ShouldBe([Path.Combine(_install, Node)]);
        Tree(_install, ignoring: [ReleaseInstall.PreviousName, ReleaseInstall.StagingName]).ShouldBe(before);
        File.Exists(Path.Combine(_install, "Fonts", "b.ttf")).ShouldBeFalse();
        // Nothing was deleted: the files of the release that did not start went aside.
        Read(Path.Combine("Fonts", "b.ttf~old-1")).ShouldBe("new Fonts/b.ttf");
        // The first install renamed the old node aside as ~old-1, so this one is the next free name.
        Read(Node + "~old-2").ShouldBe("new " + Node);
        _said.ShouldContain(line => line.Contains("pilot 2.0.2 did not start twice; pilot 2.0.1 is back", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(_data, NodeProgram.StartLogFile)).ShouldContain("pilot 2.0.2 did not start twice");

        // The record, as the restored node reads it: one Ring card, once, and the next start of that node adds nothing.
        var restored = Rollback("2.0.1");
        restored.Read().ShouldBe(new UpdateRecord("2.0.1", "2.0.2", 2, restored.Read()!.LastError, RolledBack: true));
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);

        restored.Tell(state).ShouldNotBeNull();
        restored.Tell(state).ShouldBeNull();
        new UpdateRollback(_install, _data, "2.0.1").Tell(new NodeState(new InMemoryJournal(), TimeProvider.System)).ShouldBeNull();

        var card = state.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Kind.ShouldBe(NodeUpdater.Kind);
        card.Subject.ShouldBe("rollback/2.0.2");
        card.Title.ShouldStartWith("pilot 2.0.2 did not start twice; back on 2.0.1: ");
        card.Title.ShouldContain("the stored state is not one 2.0.2 reads");
        card.Detail.ShouldNotBeNull().ShouldContain("Auto-update does not install 2.0.2 again");
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.NoteIdea);

        // The restored node's own start sweeps the asides; previous/ is the way back to a release that is gone, and stays.
        ReleaseInstall.CleanAside(_install).ShouldBe(0);
        Directory.EnumerateFiles(_install, "*" + ReleaseInstall.AsideSuffix + "*", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.Exists(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldBeTrue();
    }

    [Fact]
    public async Task UnderSystemdNoSuccessorIsStartedAndTheUnitRestartsTheNodeOnTheFilesInPlace()
    {
        Install("2.0.1", Binaries("old"));
        var before = Tree(_install);
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);
        _started.Clear();
        var failing = Rollback("2.0.2", systemd: true);
        using var held = Held();

        var first = await NodeProgram.RunHostAsync(Options, held, _said.Add, failing, Fails("boom"), Ct);
        var second = await NodeProgram.RunHostAsync(Options, held, _said.Add, failing, Fails("boom"), Ct);

        (first, second).ShouldBe((1, NodeExitCodes.Stopped));
        _started.ShouldBeEmpty();
        Tree(_install, ignoring: [ReleaseInstall.PreviousName, ReleaseInstall.StagingName]).ShouldBe(before);
        Rollback("2.0.1").Read().ShouldNotBeNull().RolledBack.ShouldBeTrue();
    }

    [Fact]
    public async Task AStartThatFindsTwoCountedAfterACrashNothingCaughtGoesBackWithoutTryingAThird()
    {
        Install("2.0.1", Binaries("old"));
        var before = Tree(_install);
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);
        _started.Clear();
        var failing = Rollback("2.0.2");
        // Two starts that were counted and never cleared, as a crash that no catch saw leaves them.
        failing.Begin().ShouldNotBeNull();
        failing.Begin().ShouldNotBeNull();
        using var held = Held();
        var built = false;

        var exit = await NodeProgram.RunHostAsync(Options, held, _said.Add, failing, (_, _) =>
        {
            built = true;
            throw new InvalidOperationException("a third start is not tried");
        }, Ct);

        exit.ShouldBe(NodeExitCodes.Stopped);
        built.ShouldBeFalse();
        _started.ShouldBe([Path.Combine(_install, Node)]);
        Tree(_install, ignoring: [ReleaseInstall.PreviousName, ReleaseInstall.StagingName]).ShouldBe(before);
        var record = Rollback("2.0.1").Read().ShouldNotBeNull();
        (record.RolledBack, record.FailedStarts, record.LastError).ShouldBe((true, 2, null));
        // No error was recorded, and the card says so rather than inventing one.
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        Rollback("2.0.1").Tell(state).ShouldNotBeNull().Title.ShouldContain("no error was recorded");
    }

    [Fact]
    public async Task AFailedStartThatCannotStartTheNodeAgainFailsAsItAlwaysDid()
    {
        Install("2.0.1", Binaries("old"));
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);
        _started.Clear();
        var failing = Rollback("2.0.2", starts: false);
        using var held = Held();

        var crash = await Should.ThrowAsync<InvalidOperationException>(() => NodeProgram.RunHostAsync(Options, held, _said.Add, failing, Fails("boom"), Ct));

        crash.Message.ShouldBe("boom");
        failing.Read().ShouldNotBeNull().FailedStarts.ShouldBe(1);
        Read(Node).ShouldBe("new " + Node);
    }

    [Fact]
    public async Task ABuildOfOnesOwnIsNeverRolledBackWhateverRecordIsInTheDataFolder()
    {
        Install("2.0.1", Binaries("old"));
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);
        _started.Clear();
        // A build of one's own: no VERSION marker for the running version.
        File.Delete(Path.Combine(_install, ReleaseInstall.MarkerName));
        var before = Tree(_install);
        var own = Rollback("2.0.2");
        using var held = Held();

        own.Begin().ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => NodeProgram.RunHostAsync(Options, held, _said.Add, own, Fails("boom"), Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => NodeProgram.RunHostAsync(Options, held, _said.Add, own, Fails("boom"), Ct));

        _started.ShouldBeEmpty();
        Tree(_install).ShouldBe(before);
        own.Read().ShouldNotBeNull().FailedStarts.ShouldBe(0);
    }

    [Fact]
    public async Task AStartOfAnotherVersionOrWithNothingKeptToGoBackToIsNotATrial()
    {
        Install("2.0.1", Binaries("old"));
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);

        // Not the version the record names.
        Rollback("2.0.9").Begin().ShouldBeNull();
        // The record of a rollback already done.
        Rollback("2.0.2").TryWrite(new UpdateRecord("2.0.1", "2.0.2", 2, "boom", RolledBack: true));
        Rollback("2.0.2").Begin().ShouldBeNull();
        // Nothing kept: no start is counted, and there is nothing to restore.
        Rollback("2.0.2").TryWrite(new UpdateRecord("2.0.1", "2.0.2"));
        Directory.Delete(Path.Combine(_install, ReleaseInstall.PreviousName), recursive: true);
        Rollback("2.0.2").Begin().ShouldBeNull();
        Rollback("2.0.2").Read().ShouldNotBeNull().FailedStarts.ShouldBe(0);
        var restore = Rollback("2.0.2").Restore(new UpdateRecord("2.0.1", "2.0.2", 2));
        (restore.Restored, restore.Started).ShouldBe((false, false));
        restore.Message.ShouldContain("no pilot 2.0.1");
        Read(Node).ShouldBe("new " + Node);
        _started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AStoreANewerNodeWroteIsNotTheNewVersionsFaultAndRollsNothingBack()
    {
        Install("2.0.1", Binaries("old"));
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);
        _started.Clear();
        var trial = Rollback("2.0.2");
        using var held = Held();

        var exit = await NodeProgram.RunHostAsync(Options, held, _said.Add, trial, (_, _) =>
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton<NodeState>(_ => throw new StoreTooNewException("9.9.9", 99, 1));
            return builder.Build();
        }, Ct);

        exit.ShouldBe(NodeExitCodes.StoreTooNew);
        _started.ShouldBeEmpty();
        Read(Node).ShouldBe("new " + Node);
        // The start is taken back out of the count, so the next one is the first again.
        trial.Read().ShouldNotBeNull().FailedStarts.ShouldBe(0);
        File.ReadAllText(Path.Combine(_data, NodeProgram.StartLogFile)).ShouldContain("did not start");
    }

    [Fact]
    public async Task AHostThatHasStartedClearsTheRecordWhileThePreviousReleaseStays()
    {
        Install("2.0.1", Binaries("old"));
        var before = Tree(_install);
        await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);
        _started.Clear();
        var running = Rollback("2.0.2");
        using var held = Held();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        int? countedBeforeTheHost = null;

        var exit = await NodeProgram.RunHostAsync(Options, held, _said.Add, running, (_, _) =>
        {
            // The start is counted before the host is built, and the record is still there while it is.
            countedBeforeTheHost = running.Read()?.FailedStarts;
            // A real host on a loopback port of the OS's choosing, with the state the node reads before it starts a thing.
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new NodeState(new InMemoryJournal(), TimeProvider.System));
            var app = builder.Build();
            app.Lifetime.ApplicationStarted.Register(stop.Cancel);
            return app;
        }, stop.Token);

        exit.ShouldBe(NodeExitCodes.Stopped);
        countedBeforeTheHost.ShouldBe(1);
        running.Read().ShouldBeNull();
        _started.ShouldBeEmpty();
        Tree(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldBe(before);
    }

    [Fact]
    public async Task AVersionThatWasRolledBackIsSkippedUnderAutoAndAskButInstalledByUpdateNodeInstall()
    {
        Install("2.0.1", Binaries("old"));
        var feed = Feed("2.0.2", Binaries("new"));
        Rollback("2.0.1").TryWrite(new UpdateRecord("2.0.1", "2.0.2", 2, "boom", RolledBack: true, Told: true)).ShouldBeTrue();

        var auto = Updater(UpdatePolicy.Auto, feed, out var autoLifetime);
        await auto.CheckAsync(Ct);
        var ask = Updater(UpdatePolicy.Ask, feed, out _, state: out var askState);
        await ask.CheckAsync(Ct);

        // Neither installed it nor asked about it.
        Read(Node).ShouldBe("old " + Node);
        autoLifetime.Stopped.ShouldBeFalse();
        _started.ShouldBeEmpty();
        askState.Cards().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldBeFalse();

        // By hand it goes in, and the record is the new update's.
        var byHand = await auto.RunAsync(Command(UpdateArgs.Install), Ct);

        byHand.Ok.ShouldBeTrue(byHand.Error);
        Read(Node).ShouldBe("new " + Node);
        Rollback("2.0.1").Read().ShouldBe(new UpdateRecord("2.0.1", "2.0.2"));
    }

    [Fact]
    public async Task ANewerVersionThanTheOneThatWasRolledBackIsInstalledByAuto()
    {
        Install("2.0.1", Binaries("old"));
        Rollback("2.0.1").TryWrite(new UpdateRecord("2.0.1", "2.0.2", 2, "boom", RolledBack: true, Told: true)).ShouldBeTrue();

        await Updater(UpdatePolicy.Auto, Feed("2.0.3", Binaries("newer"))).CheckAsync(Ct);

        Read(Node).ShouldBe("newer " + Node);
        Rollback("2.0.1").Read().ShouldBe(new UpdateRecord("2.0.1", "2.0.3"));
        _started.ShouldBe([Path.Combine(_install, Node)]);
    }

    [Fact]
    public async Task AnInstallThatCannotKeepThePreviousReleaseIsRefusedWithTheInstallAndNoRecordAsTheyWere()
    {
        Install("2.0.1", Binaries("old"));
        // previous is a file where its folder should be: the release cannot be kept there.
        File.WriteAllText(Path.Combine(_install, ReleaseInstall.PreviousName), "in the way");
        var before = Tree(_install);

        var refused = await Updater(UpdatePolicy.Off, Feed("2.0.2", Binaries("new"))).RunAsync(Command(UpdateArgs.Install), Ct);

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("could not be kept");
        Tree(_install, ignoring: [ReleaseInstall.StagingName]).ShouldBe(before);
        File.Exists(Path.Combine(_data, UpdateRollback.RecordFile)).ShouldBeFalse();
        _started.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInstallOverABuildOfOnesOwnKeepsNothingAndLeavesNoRecord()
    {
        File.WriteAllText(Path.Combine(_install, Node), "my own build");
        Rollback("2.0.0").TryWrite(new UpdateRecord("1.9.0", "2.0.0", 1)).ShouldBeTrue();
        var feed = Feed("2.0.2", Binaries("new"), rid: System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);

        var adopted = await Updater(UpdatePolicy.Off, feed, version: "2.0.0").RunAsync(Command(UpdateArgs.Install, UpdateArgs.Adopt), Ct);

        adopted.Ok.ShouldBeTrue(adopted.Error);
        Read(Node).ShouldBe("new " + Node);
        Directory.Exists(Path.Combine(_install, ReleaseInstall.PreviousName)).ShouldBeFalse();
        File.Exists(Path.Combine(_data, UpdateRollback.RecordFile)).ShouldBeFalse();
    }

    [Fact]
    public void CleanAsideLeavesThePreviousFolderAloneWhateverItHolds()
    {
        Install("2.0.2", Binaries("new"));
        string[] asides = [Node + "~old-1", "Fonts/a.ttf~old-2"];
        string[] kept = [Path.Combine(ReleaseInstall.PreviousName, Node + "~old-1"), Path.Combine(ReleaseInstall.PreviousName, "Fonts", "a.ttf~old")];
        foreach (var name in asides.Concat(kept))
        {
            var path = Path.Combine(_install, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
        }

        ReleaseInstall.CleanAside(_install).ShouldBe(0);

        asides.ShouldAllBe(a => !File.Exists(Path.Combine(_install, a)));
        kept.ShouldAllBe(k => File.Exists(Path.Combine(_install, k)));
    }

    [Fact]
    public void TheRecordIsWrittenWholeAndReadBackAndAnUnreadableOneIsNone()
    {
        var rollback = Rollback("2.0.1");
        var record = new UpdateRecord("2.0.1", "2.0.2", 1, "InvalidOperationException: a \"quoted\" word\tand more", RolledBack: false, Told: false);

        rollback.TryWrite(record).ShouldBeTrue();

        rollback.Read().ShouldBe(record);
        Directory.EnumerateFiles(_data).Select(Path.GetFileName).ShouldBe([UpdateRollback.RecordFile]);
        File.WriteAllText(rollback.RecordPath, "{ \"from\": ");
        rollback.Read().ShouldBeNull();
        File.WriteAllText(rollback.RecordPath, "{}");
        rollback.Read().ShouldBeNull();
        rollback.IsRolledBack("2.0.2").ShouldBeFalse();
    }

    // ---- the fixtures ----

    private NodeOptions Options => new(Path.Combine(_root, "n.sock"), _data);

    private NodeLock Held()
    {
        NodeLock.TryAcquire(Options.SocketPath, out var held, out _).ShouldBeTrue("the lock beside the test's own socket");
        return held!;
    }

    // A host build that dies of the message: what a start of a version that cannot read its store does.
    private static Func<NodeOptions, NodeLock, WebApplication> Fails(string message) => (_, _) => throw new InvalidOperationException(message);

    private UpdateRollback Rollback(string version, bool systemd = false, bool starts = true) => new(_install, _data, version)
    {
        RunningNode = Path.Combine(_install, Node),
        RestartedBySystemd = _ => systemd,
        StartSuccessor = path =>
        {
            _started.Add(path);
            return starts;
        },
    };

    private NodeUpdater Updater(UpdatePolicy policy, IReleaseFeed feed, string version = "2.0.1")
        => Updater(policy, feed, out _, out _, version);

    private NodeUpdater Updater(UpdatePolicy policy, IReleaseFeed feed, out FakeLifetime lifetime, string version = "2.0.1")
        => Updater(policy, feed, out lifetime, out _, version);

    private NodeUpdater Updater(UpdatePolicy policy, IReleaseFeed feed, out FakeLifetime lifetime, out NodeState state, string version = "2.0.1")
    {
        lifetime = new FakeLifetime();
        state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        return new NodeUpdater(state, new NodeOptions(Path.Combine(_root, "node.sock"), _data, UpdatePolicy: policy), feed, TimeProvider.System, lifetime,
            NullLogger<NodeUpdater>.Instance)
        {
            InstallFolder = _install,
            Version = version,
            RestartedBySystemd = _ => false,
            StartSuccessor = path =>
            {
                _started.Add(path);
                return true;
            },
            Smoke = (_, _, _) => Task.FromResult<string?>(null),
        };
    }

    // pilot's four binaries, each saying which it is, and any other files of the release.
    private static Dictionary<string, string> Binaries(string which, string[]? more = null)
        => NodeUpdater.Layout.Required.Concat(more ?? []).ToDictionary(n => n, n => $"{which} {n}");

    private static CommandRequest Command(params string[] args) => new() { Verb = Verbs.Update, Target = Verbs.NodeTarget, Args = args, Who = "cli" };

    private string Read(string file) => File.ReadAllText(Path.Combine(_install, file));

    // Every file under a folder with a hash of its bytes, by path with /; asides and what <ignoring> names are left out.
    private static Dictionary<string, string> Tree(string folder, string[]? ignoring = null, bool asides = false)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => (asides || !f.Contains(ReleaseInstall.AsideSuffix, StringComparison.Ordinal))
                && !(ignoring ?? []).Any(i => f == i || f.StartsWith(i + "/", StringComparison.Ordinal)))
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, f)))));

    // An installed release: its files and the marker that lists them.
    private void Install(string version, Dictionary<string, string> files)
    {
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(_install, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        File.WriteAllText(Path.Combine(_install, ReleaseInstall.MarkerName), Marker(version, Rid, files.Keys));
    }

    private static string Marker(string version, string rid, IEnumerable<string> files)
        => string.Join('\n', [$"{version} {rid}", .. files.Append(ReleaseInstall.MarkerName).Order(StringComparer.Ordinal)]) + "\n";

    // A feed whose latest release has the zip for rid, as the release workflow makes it.
    private ZipFeed Feed(string version, Dictionary<string, string> files, string rid = Rid)
    {
        var name = NodeUpdater.Layout.AssetName(version, rid);
        var zip = Path.Combine(_root, name);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (file, text) in files.Append(new(ReleaseInstall.MarkerName, Marker(version, rid, files.Keys))))
            {
                using var entry = archive.CreateEntry(file).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        var release = new Release(version, "v" + version, $"{Page}tag/v{version}")
        {
            Assets = [new ReleaseAsset(name, $"{Page}download/v{version}/{name}", ReleaseInstall.Sha256Of(zip))],
        };
        return new ZipFeed(release, zip);
    }

    private sealed class ZipFeed(Release release, string zip) : IReleaseFeed
    {
        public Task<Release?> LatestAsync(CancellationToken cancellationToken) => Task.FromResult<Release?>(release);

        public Task DownloadAsync(Release release, ReleaseAsset asset, string file, CancellationToken cancellationToken)
        {
            File.Copy(zip, file, overwrite: true);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }
}
