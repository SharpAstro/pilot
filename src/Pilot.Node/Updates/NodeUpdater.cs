using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Pilot.Contracts;

namespace Pilot.Node.Updates;

/// <summary>
/// Keeps an installed release of pilot (the node, the CLI, the GUI and the TUI, one folder) on the latest release of
/// SharpAstro/pilot, by its <see cref="UpdatePolicy"/>: <c>auto</c> installs it at once, jobs running or not (a job runs
/// detached from the node and the next node follows it again), and says so on a Handled card; <c>ask</c> raises a For-you
/// card with <b>Update</b>; <c>off</c> does not look. <c>pilot update</c> installs it now whatever the policy.
/// </summary>
/// <remarks>
/// The node installs the release into the folder it runs from (<see cref="ReleaseInstall"/>), then starts the new
/// <c>pilot-node</c> with <c>--after &lt;its pid&gt;</c> and its own arguments, and stops: the new one waits for it to
/// exit and takes the socket. The flights, legs and sessions are the store's, so they carry on; a client resumes its
/// stream; workers and the watchtower are processes of their own and keep running. A GUI or TUI open at the time runs
/// on from its renamed-aside binary until it is reopened.
/// </remarks>
public sealed class NodeUpdater(NodeState state, NodeOptions options, IReleaseFeed feed, TimeProvider time,
    IHostApplicationLifetime lifetime, ILogger<NodeUpdater> logger) : BackgroundService
{
    public const string Kind = "update";
    public const string Repo = "SharpAstro/pilot";

    /// <summary>The release zips: <c>pilot-&lt;version&gt;-&lt;rid&gt;.zip</c>, each with the four binaries.</summary>
    public static ReleaseLayout Layout { get; } = new("pilot",
        [.. new[] { "pilot-node", "pilot", "pilot-gui", "pilot-tui" }.Select(Executable)]);

    /// <summary>This build's version, without the commit (<c>2.0.12</c>); a local build is <c>2.0.0</c>.</summary>
    public static string CurrentVersion { get; } = ReleaseVersions.Plain(
        typeof(NodeUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>How long after the start it first looks, and how often after that.</summary>
    public TimeSpan FirstCheckAfter { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How often a node on the beta channel looks: main moves far more often than releases are tagged.</summary>
    public TimeSpan BetaCheckEvery { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The folder the node runs from, which a release replaces.</summary>
    public string InstallFolder { get; init; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public string Version { get; init; } = CurrentVersion;

    /// <summary>Starts the installed node to take over from this one; false if it did not start. A test replaces it.</summary>
    public Func<string, bool> StartSuccessor { get; init; } = NodeRestarter.Start;

    /// <summary>Whether the node's systemd unit restarts it, so no successor is started. A test replaces it.</summary>
    public Func<string, bool> RestartedBySystemd { get; init; } = NodeRestarter.UnderSystemdUnit;

    /// <summary>Runs the staged <c>pilot-node --version</c> before it goes in: a release that does not start here is not
    /// installed. A test replaces it.</summary>
    public Func<string, string, CancellationToken, Task<string?>> Smoke { get; init; } = SmokeAsync;

    private readonly SemaphoreSlim _installing = new(1, 1);
    // The version a card was last raised for, and the one an install last failed for.
    private string? _offered;
    private string? _failed;

    /// <summary>The release this folder is, when it is one; null for a build of one's own.</summary>
    public ReleaseBuild? Installed => ReleaseInstall.Installed(InstallFolder, Version);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        if (Installed is not null && ReleaseInstall.CleanAside(InstallFolder) is > 0 and var left)
        {
            logger.LogInformation("update: {Left} file(s) a former release left aside are still in use; they go at a later start", left);
        }
        if (options.UpdatePolicy == UpdatePolicy.Off || Installed is null)
        {
            // A build of one's own never looks: pilot update installs a release over it, by hand.
            return;
        }
        var next = time.GetUtcNow() + FirstCheckAfter;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var now = time.GetUtcNow();
                var wait = next - now;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, stopping);
                }
                if (time.GetUtcNow() >= next)
                {
                    next = time.GetUtcNow() + (options.UpdateChannel == UpdateChannel.Beta ? BetaCheckEvery : CheckEvery);
                    await CheckAsync(stopping);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Looks for a newer release and does what the policy says with it.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (await feed.LatestAsync(cancellationToken) is not { } latest || !ReleaseVersions.IsNewer(Version, latest.Version))
        {
            return;
        }
        if (options.UpdatePolicy == UpdatePolicy.Auto)
        {
            await InstallAsync(latest, "node", cancellationToken);
        }
        else if (options.UpdatePolicy == UpdatePolicy.Ask && _offered != latest.Version)
        {
            _offered = latest.Version;
            state.CloseCardsAbout(Verbs.NodeTarget, $"pilot {latest.Version} is out", Kind);
            state.RaiseCard(CardBin.ForYou, Kind, Verbs.NodeTarget, $"pilot {latest.Version} is out (this is {Version})",
                $"The node installs it and restarts; flights, sessions and jobs carry on.",
                [new CardChoice { Label = "Update", Verb = Verbs.Update, Target = Verbs.NodeTarget, Args = [UpdateArgs.Install] }],
                latest.Url);
        }
    }

    /// <summary><c>update node [check|install] [--adopt]</c>: says whether a newer release is out, or installs it now.</summary>
    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var args = command.Args ?? [];
        var check = args.Contains(UpdateArgs.Check);
        var adopt = args.Contains(UpdateArgs.Adopt);
        if (Installed is null && !adopt)
        {
            return state.Complete(command,
                $"this node runs from a build of its own ({InstallFolder} has no {ReleaseInstall.MarkerName} for {Version}); "
                + $"pilot update --adopt replaces it with the latest release");
        }
        if (await feed.LatestAsync(cancellationToken) is not { } latest)
        {
            return state.Complete(command, $"no answer from {Repo}'s releases (gh api repos/{Repo}/releases/latest)");
        }
        if (!ReleaseVersions.IsNewer(Version, latest.Version) && !(adopt && Installed is null))
        {
            return state.Complete(command, null, way: $"{Version} is current");
        }
        if (check)
        {
            return state.Complete(command, null, way: $"{latest.Version} is out; this is {Version}");
        }
        var carrying = Carrying();
        var error = await InstallAsync(latest, command.Who, cancellationToken);
        return state.Complete(command, error, way: error is null ? $"{Version} -> {latest.Version}; the node restarts{carrying}" : null);
    }

    /// <summary>Installs <paramref name="release"/> and hands over to it at once, jobs running or waiting or not. Returns
    /// why not, or null once the node is stopping for its successor.</summary>
    private async Task<string?> InstallAsync(Release release, string who, CancellationToken cancellationToken)
    {
        if (!await _installing.WaitAsync(0, cancellationToken))
        {
            return "an update is being installed already";
        }
        try
        {
            var old = Installed;
            var rid = old?.Rid ?? RuntimeInformation.RuntimeIdentifier;
            var (staging, staged) = await ReleaseInstall.StageAsync(feed, release, InstallFolder, rid, Layout, cancellationToken);
            if (await Smoke(Path.Combine(staging, Executable("pilot-node")), release.Version, cancellationToken) is { } broken)
            {
                throw new ReleaseInstallException($"the staged pilot-node did not start: {broken}");
            }
            if (ReleaseInstall.Swap(staging, staged, InstallFolder, old) is { } failed)
            {
                throw new ReleaseInstallException($"it could not go in: {failed}");
            }
            var node = Path.Combine(InstallFolder, Executable("pilot-node"));
            var line = $"pilot {Version} -> {release.Version}, installed by {who}; the node restarts{Carrying()}";
            if (!RestartedBySystemd(node) && !StartSuccessor(node))
            {
                // Installed but not running: the next start of the node (the GUI starts one) is the new one.
                state.RaiseCard(CardBin.Ring, Kind, Verbs.NodeTarget, $"pilot {release.Version} is installed but the node did not restart",
                    $"{node} did not start; this node ({Version}) runs on until it is stopped. Start pilot-node, or open pilot-gui.");
                return $"{release.Version} is installed, but {node} did not start";
            }
            state.CloseCardsAbout(Verbs.NodeTarget, $"pilot {release.Version} installed", Kind);
            state.RaiseCard(CardBin.Handled, Kind, Verbs.NodeTarget, line, null, null, release.Url);
            logger.LogInformation("update: {Line}", line);
            lifetime.StopApplication();
            return null;
        }
        catch (ReleaseInstallException ex)
        {
            logger.LogWarning("update: {Version} not installed: {Why}", release.Version, ex.Message);
            if (_failed != release.Version)
            {
                _failed = release.Version;
                state.RaiseCard(CardBin.ForYou, Kind, Verbs.NodeTarget, $"pilot {release.Version} could not be installed", ex.Message,
                    [new CardChoice { Label = "Try again", Verb = Verbs.Update, Target = Verbs.NodeTarget, Args = [UpdateArgs.Install] }], release.Url);
            }
            return ex.Message;
        }
        finally
        {
            _installing.Release();
        }
    }

    // The jobs running or waiting, said as what they do through the restart: ", 2 jobs carry on", or nothing.
    private string Carrying() => NodeRestarter.JobsCarryOn(state) is { } jobs ? $"; {jobs}" : "";

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static async Task<string?> SmokeAsync(string node, string version, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(node, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start) ?? throw new IOException("it did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode != 0 ? $"--version exited with {process.ExitCode}"
                : ReleaseVersions.Plain(output.Trim()) != version ? $"it says it is {output.Trim()}, not {version}"
                : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ex.Message;
        }
    }
}

/// <summary>The arguments of <c>update node</c>.</summary>
public static class UpdateArgs
{
    /// <summary>Say whether a newer release is out, and install nothing.</summary>
    public const string Check = "check";

    /// <summary>Install it now (what the cards' buttons send; also what no argument does).</summary>
    public const string Install = "install";

    /// <summary>Install the latest release over a build of one's own, which then becomes an installed release.</summary>
    public const string Adopt = "--adopt";
}
