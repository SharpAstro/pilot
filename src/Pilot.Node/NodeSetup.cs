using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// What it takes for this machine's node to be part of the fleet, as a checklist, and the fixes the node can make
/// itself: started at logon, pilot's hooks in Claude Code's settings, on the LAN, let through the firewall, its
/// watchtower's folder trusted, gh signed in, and usage figures to show. The Settings page shows it (<see cref="Checks"/>),
/// its buttons are <c>setup node &lt;key&gt;</c> (<see cref="FixAsync"/>), and <c>pilot-node setup</c> makes every fix a
/// command line can.
/// </summary>
/// <remarks>
/// Two fixes need a person, and say so rather than going around them: the firewall rule asks for elevation (one prompt,
/// on this machine's screen), and the trust prompt is Claude Code's own, so the fix opens <c>claude</c> in the folder in a
/// window that says what it is for. gh's login is a person's alone.
/// </remarks>
public sealed class NodeSetup(NodeOptions options, string nodePath, string profileRoot)
{
    public const string AutostartKey = "autostart";
    public const string NoAutostartKey = "no-autostart";
    public const string HooksKey = "hooks";
    public const string LanKey = "lan";
    public const string FirewallKey = "firewall";
    public const string TrustKey = "trust";
    public const string GhKey = "gh";
    public const string UsageKey = "usage";
    public const string GuardHooksKey = "guard-hooks";
    public const string BuildRspKey = "build-rsp";

    /// <summary>The response file that turns MSBuild node reuse off, as runaway-agent's <c>machine/</c> folder and install.sh name it.</summary>
    public const string BuildRsp = "Directory.Build.rsp";

    /// <summary>The firewall rule's name, which the check looks for and the fix replaces: one rule of it per binary.</summary>
    public const string FirewallRule = "pilot-node";

    /// <summary>
    /// The binaries beside the node that listen for the LAN's beacons, each let in on the discovery port alone: the
    /// Fleet page and <c>pilot nodes</c> hear other nodes through them. Without a rule of its own, a binary is let in on
    /// whatever its first firewall prompt was answered for, often Public only, so on a Private network it heard this
    /// machine's own beacons and no other node's (SE1 and DE1, 2026-10-24).
    /// </summary>
    public static readonly string[] LanListeners = ["pilot", "pilot-gui", "pilot-tui"];

    /// <summary>The Claude Code settings file the hook checks read and the fixes write; a test points it elsewhere.</summary>
    public string SettingsFile { get; init; } = HookCommands.DefaultSettings();

    /// <summary>A runaway-agent clone to take the guard scripts from, or null when there is none; a test replaces it.</summary>
    public Func<string?> FindRunawayAgent { get; init; } = RunawayAgentClone;

    /// <summary>The folder the response file goes in: the user's home, where install.sh puts it; a test points it elsewhere.</summary>
    public string MachineFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Runs a program and gives its exit code and output; a test replaces it.</summary>
    public Func<string, IReadOnlyList<string>, TimeSpan, (int Exit, string Output)> Run { get; init; } = RunProcess;

    /// <summary>Starts a program elevated (Windows' UAC prompt) and waits for it; false when the prompt was refused. A test replaces it.</summary>
    public Func<string, string, bool> RunElevated { get; init; } = RunElevatedProcess;

    /// <summary>Opens a window for a person (the trust prompt); false when it did not open. A test replaces it.</summary>
    public Func<string, string, bool> OpenWindow { get; init; } = OpenConsoleWindow;

    /// <summary>The checklist as it stands now.</summary>
    public IReadOnlyList<SetupCheck> Checks()
    {
        var checks = new List<SetupCheck>();
        var entry = Autostart.Entry();
        checks.Add(entry is null
            ? new SetupCheck { Key = AutostartKey, Title = "Starts at logon", Ok = false, Line = "nothing starts the node at logon: the fleet loses this machine until someone opens pilot", Fix = "Start at logon" }
            : new SetupCheck { Key = AutostartKey, Title = "Starts at logon", Ok = true, Line = entry, Fix = null });

        var hooks = HooksInstalled();
        checks.Add(new SetupCheck
        {
            Key = HooksKey,
            Title = "Claude Code hooks",
            Ok = hooks,
            Line = hooks ? $"in {SettingsFile}" : $"pilot's hooks are not in {SettingsFile} for {nodePath}: sessions are seen late and without their state",
            Fix = hooks ? null : "Install hooks",
        });
        checks.Add(GuardHooksCheck());
        checks.Add(BuildRspCheck());

        var lanWanted = options.Saved?.Lan ?? options.Lan;
        checks.Add(options.Lan
            ? new SetupCheck { Key = LanKey, Title = "On the LAN", Ok = true, Line = $"listening on port {options.LanPort} and announcing itself as {options.Machine}" }
            : options.LocalOnly
                ? new SetupCheck { Key = LanKey, Title = "On the LAN", Ok = false, Line = "this node was started local-only (by a GUI, or for an agent): the node that starts at logon is the one on the LAN" }
                : new SetupCheck { Key = LanKey, Title = "On the LAN", Ok = false, Line = lanWanted ? "saved as on; it applies when the node restarts" : "off: other machines cannot see this node", Fix = lanWanted ? null : "Turn on" });

        if (OperatingSystem.IsWindows())
        {
            var (rule, why) = FirewallAllows();
            checks.Add(new SetupCheck
            {
                Key = FirewallKey,
                Title = "Firewall",
                Ok = rule,
                Line = why,
                Fix = rule ? null : "Allow (asks for elevation)",
                Optional = !lanWanted,
            });
        }

        var trusted = Trusted(options.WatchtowerFolder);
        checks.Add(new SetupCheck
        {
            Key = TrustKey,
            Title = "Watchtower folder trusted",
            Ok = trusted is not null,
            Line = trusted is { } by ? (by == options.WatchtowerFolder ? options.WatchtowerFolder : $"{options.WatchtowerFolder}, through {by}") : $"Claude Code starts no background session in {options.WatchtowerFolder} until someone accepts its trust prompt there once",
            Fix = trusted is null ? "Trust it" : null,
        });

        var (gh, ghLine) = GhSignedIn();
        checks.Add(new SetupCheck { Key = GhKey, Title = "GitHub CLI signed in", Ok = gh, Line = gh ? ghLine : $"{ghLine}: run gh auth login (updates, PRs and issues go through it)" });

        var usage = Path.Combine(profileRoot, "rate-limits.json");
        var hasUsage = File.Exists(usage);
        checks.Add(new SetupCheck
        {
            Key = UsageKey,
            Title = "Usage figures",
            Ok = hasUsage,
            Line = hasUsage ? $"from {usage}, as of {File.GetLastWriteTime(usage).ToString("g", CultureInfo.CurrentCulture)}" : $"no {usage}: a status line that writes it gives the fleet this machine's 5-hour and 7-day usage",
            Optional = true,
        });
        return checks;
    }

    /// <summary>Makes the fix of the check <paramref name="key"/>; null when it is done, else why not.</summary>
    public async Task<string?> FixAsync(string key, CancellationToken cancellationToken)
    {
        switch (key)
        {
            case AutostartKey:
            {
                // The node's options are the settings file's now, so the entry carries none but a socket that is not the default.
                var args = options.SocketPath == NodeSocket.Resolve(null) ? Array.Empty<string>() : ["--node-socket", options.SocketPath];
                var output = new StringWriter();
                var exit = await Autostart.InstallAsync(nodePath, args, options.SocketPath, output, output, cancellationToken);
                return exit == 0 ? null : output.ToString().Trim();
            }
            case NoAutostartKey:
                return await Autostart.RemoveAsync(TextWriter.Null, cancellationToken) == 0 ? null : "the logon entry could not be taken out";
            case HooksKey:
            {
                var output = new StringWriter();
                return HookCommands.Hooks(install: true, SettingsFile, HookSocket(), nodePath, output, output) == 0 ? null : output.ToString().Trim();
            }
            case GuardHooksKey:
                return InstallGuardHooks();
            case BuildRspKey:
                return InstallBuildRsp();
            case FirewallKey:
                if (!OperatingSystem.IsWindows())
                {
                    return "the node makes a firewall rule on Windows only";
                }
                // One elevated cmd for all, so one prompt: the old rules out (an earlier install's paths) and these in. The
                // node takes anything in; a listener only the discovery port's beacons.
                var add = $"netsh advfirewall firewall add rule name={FirewallRule} dir=in action=allow profile=private enable=yes";
                var netsh = string.Join(" & ", FirewallPrograms().Select((program, i) => i == 0
                        ? $"{add} program=\"{program}\""
                        : $"{add} program=\"{program}\" protocol=udp localport={LAN.Lib.LanProtocol.DiscoveryPort}")
                    .Prepend($"netsh advfirewall firewall delete rule name={FirewallRule} >nul"));
                // A command line by hand: cmd reads its own quoting, which ArgumentList's escaped quotes would break.
                if (!RunElevated(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe"), $"--headless cmd.exe /d /c \"{netsh}\""))
                {
                    return "the elevation prompt was refused or did not open";
                }
                return FirewallAllows() is (true, _) ? null : "the rule is still not there";
            case TrustKey:
                // Recorded in Claude Code's file, carefully (ClaudeTrust); the prompt in a window only when that cannot be.
                Directory.CreateDirectory(options.WatchtowerFolder);
                if (Trust.Ensure(options.WatchtowerFolder) is null)
                {
                    return null;
                }
                return OpenWindow(options.WatchtowerFolder, "pilot: trust the watchtower folder")
                    ? null
                    : $"no window opened: run claude in {options.WatchtowerFolder} and accept its trust prompt";
            default:
                return $"setup takes {string.Join(", ", new[] { AutostartKey, NoAutostartKey, HooksKey, GuardHooksKey, BuildRspKey, LanKey, FirewallKey, TrustKey })}, not {key}";
        }
    }

    private string? HookSocket() => options.SocketPath == NodeSocket.Resolve(null) ? null : options.SocketPath;

    /// <summary>Whether Claude Code's settings hold pilot's hook on every event, for this node's binary.</summary>
    public bool HooksInstalled()
    {
        try
        {
            var text = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "";
            return text.Length > 0 && ReferenceEquals(HookCommands.Install(text, nodePath, HookSocket()), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether runaway-agent's three guards (<see cref="GuardHooks"/>) are in Claude Code's settings. Optional: the fleet
    /// runs without them, and they are another project's. When one is missing the line says where the scripts come from:
    /// a runaway-agent clone when there is one (the fix copies them, as its install.sh does), else where to get it. With a
    /// clone found, a script beside the settings file that differs from the clone's also fails it, so the fix refreshes it.
    /// </summary>
    private SetupCheck GuardHooksCheck()
    {
        const string Title = "Runaway-agent guard hooks";
        try
        {
            var text = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "";
            var missing = GuardHooks.Missing(text);
            var clone = FindRunawayAgent();
            if (missing.Count == 0)
            {
                // Wired, but the scripts are copies: a clone that has moved on is not picked up until the fix copies again.
                var older = clone is null ? [] : StaleGuardScripts(clone, GuardHooks.HooksFolder(SettingsFile));
                return older.Count == 0
                    ? new SetupCheck { Key = GuardHooksKey, Title = Title, Ok = true, Line = $"{string.Join(", ", GuardHooks.Guards.Select(g => g.Script))} in {SettingsFile}", Optional = true }
                    : new SetupCheck { Key = GuardHooksKey, Title = Title, Ok = false, Line = $"{string.Join(", ", older)} in {GuardHooks.HooksFolder(SettingsFile)} older than the copy in {clone}", Fix = "Install guards", Optional = true };
            }

            var names = string.Join(", ", missing.Select(g => g.Script));
            return clone is null
                ? new SetupCheck { Key = GuardHooksKey, Title = Title, Ok = false, Line = $"{names} not in {SettingsFile}, and no runaway-agent clone found under the repo folders: get it from {GuardHooks.Home} and clone it beside the others", Optional = true }
                : new SetupCheck { Key = GuardHooksKey, Title = Title, Ok = false, Line = $"{names} not in {SettingsFile}: they refuse a whole-file read and an inline pgrep -f, and flag a fat tool result; taken from {clone}", Fix = "Install guards", Optional = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SetupCheck { Key = GuardHooksKey, Title = Title, Ok = false, Line = $"{SettingsFile}: {ex.Message}", Optional = true };
        }
    }

    /// <summary>
    /// Copies the guard scripts from a runaway-agent clone to <c>hooks/</c> beside the settings file and wires them in it;
    /// null when done, else why not. A script already there with the same content is left alone, and so is a settings file
    /// that wires them all, so a second run changes nothing.
    /// </summary>
    private string? InstallGuardHooks()
    {
        try
        {
            var clone = FindRunawayAgent();
            if (clone is null)
            {
                return $"no runaway-agent clone found under the repo folders: get it from {GuardHooks.Home} and clone it beside the others";
            }

            var folder = GuardHooks.HooksFolder(SettingsFile);
            Directory.CreateDirectory(folder);
            foreach (var guard in GuardHooks.Guards)
            {
                var to = Path.Combine(folder, guard.Script);
                if (GuardScriptDiffers(clone, folder, guard.Script))
                {
                    File.Copy(Path.Combine(clone, guard.Script), to, overwrite: true);
                }
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(to, File.GetUnixFileMode(to) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }

            var text = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "";
            var updated = GuardHooks.Install(text, folder);
            if (updated != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                var temp = SettingsFile + ".pilot.tmp";
                File.WriteAllText(temp, updated, new System.Text.UTF8Encoding(false));
                File.Move(temp, SettingsFile, overwrite: true);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"{SettingsFile}: {ex.Message}";
        }
    }

    /// <summary>The guard scripts that are not in <paramref name="folder"/> or differ from the clone's copy, by name.</summary>
    private static List<string> StaleGuardScripts(string clone, string folder) =>
        GuardHooks.Guards.Where(g => GuardScriptDiffers(clone, folder, g.Script)).Select(g => g.Script).ToList();

    private static bool GuardScriptDiffers(string clone, string folder, string script)
    {
        var to = Path.Combine(folder, script);
        return !File.Exists(to) || !File.ReadAllBytes(Path.Combine(clone, script)).AsSpan().SequenceEqual(File.ReadAllBytes(to));
    }

    /// <summary>
    /// Whether runaway-agent's <c>machine/Directory.Build.rsp</c> is in the user's home folder as its install.sh installs
    /// it. MSBuild reads one found by walking up from the project, so every build under home, a flight's included, gets
    /// <c>-nodeReuse:false</c> and its worker nodes exit with the build: idle nodes held worktree folders and gigabytes.
    /// Optional, as the guard hooks are. The copy is compared with the clone's; with no clone a file that is there passes,
    /// since there is nothing to compare it with, and a missing one says where to get runaway-agent.
    /// </summary>
    private SetupCheck BuildRspCheck()
    {
        const string Title = "MSBuild node reuse off";
        var target = Path.Combine(MachineFolder, BuildRsp);
        try
        {
            var clone = FindRunawayAgent();
            var source = clone is null ? null : Path.Combine(clone, "machine", BuildRsp);
            if (source is not null && !File.Exists(source))
            {
                // A clone older than the file: the guard hooks may still come from it, this cannot.
                return new SetupCheck { Key = BuildRspKey, Title = Title, Ok = false, Line = $"{target} not checked: {clone} has no machine/{BuildRsp}, so pull it, or get runaway-agent from {GuardHooks.Home}", Optional = true };
            }
            if (!File.Exists(target))
            {
                return source is null
                    ? new SetupCheck { Key = BuildRspKey, Title = Title, Ok = false, Line = $"no {target}, and no runaway-agent clone found under the repo folders: get it from {GuardHooks.Home} and clone it beside the others", Optional = true }
                    : new SetupCheck { Key = BuildRspKey, Title = Title, Ok = false, Line = $"no {target}: builds leave idle MSBuild nodes behind that hold folders and memory; taken from {source}", Fix = "Install response file", Optional = true };
            }
            if (source is not null && BuildRspDiffers(source, target))
            {
                return new SetupCheck { Key = BuildRspKey, Title = Title, Ok = false, Line = $"{target} differs from {source}; the fix keeps it as a .bak copy beside it", Fix = "Install response file", Optional = true };
            }
            return new SetupCheck { Key = BuildRspKey, Title = Title, Ok = true, Line = target, Optional = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupCheck { Key = BuildRspKey, Title = Title, Ok = false, Line = $"{target}: {ex.Message}", Optional = true };
        }
    }

    /// <summary>
    /// Copies the response file from the runaway-agent clone to the home folder; null when done, else why not. A file that
    /// differs is copied to <c>Directory.Build.rsp.bak-&lt;UTC timestamp&gt;</c> first, as install.sh does, and one that is
    /// the same is left alone, so a second run changes nothing and makes no backup.
    /// </summary>
    private string? InstallBuildRsp()
    {
        try
        {
            var clone = FindRunawayAgent();
            if (clone is null)
            {
                return $"no runaway-agent clone found under the repo folders: get it from {GuardHooks.Home} and clone it beside the others";
            }
            var source = Path.Combine(clone, "machine", BuildRsp);
            if (!File.Exists(source))
            {
                return $"{clone} has no machine/{BuildRsp}: pull runaway-agent";
            }

            var target = Path.Combine(MachineFolder, BuildRsp);
            if (File.Exists(target))
            {
                if (!BuildRspDiffers(source, target))
                {
                    return null;
                }
                var backup = $"{target}.bak-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
                File.Copy(target, backup, overwrite: false);
                File.SetLastWriteTimeUtc(backup, File.GetLastWriteTimeUtc(target));
            }
            Directory.CreateDirectory(MachineFolder);
            File.Copy(source, target, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{Path.Combine(MachineFolder, BuildRsp)}: {ex.Message}";
        }
    }

    private static bool BuildRspDiffers(string source, string target) =>
        !File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target));

    /// <summary>The first <c>runaway-agent</c> clone under the user's <c>source/repos/*</c> that has all three guard scripts, or null.</summary>
    private static string? RunawayAgentClone()
    {
        try
        {
            var repos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
            return Directory.Exists(repos)
                ? Directory.EnumerateDirectories(repos).Select(org => Path.Combine(org, "runaway-agent"))
                    .FirstOrDefault(dir => GuardHooks.Guards.All(g => File.Exists(Path.Combine(dir, g.Script))))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The node's binary, then each <see cref="LanListeners"/> binary that is installed beside it.</summary>
    private IEnumerable<string> FirewallPrograms()
    {
        yield return nodePath;
        var folder = Path.GetDirectoryName(Path.GetFullPath(nodePath)) ?? "";
        foreach (var name in LanListeners)
        {
            var program = Path.Combine(folder, name + Path.GetExtension(nodePath));
            if (File.Exists(program))
            {
                yield return program;
            }
        }
    }

    /// <summary>Whether a rule lets this node's binary in, and each LAN listener beside it, and how it stands, in a line.</summary>
    public (bool Allows, string Line) FirewallAllows()
    {
        var (exit, output) = Run("netsh", ["advfirewall", "firewall", "show", "rule", $"name={FirewallRule}", "verbose"], TimeSpan.FromSeconds(15));
        if (exit != 0)
        {
            return (false, $"no rule named {FirewallRule}: other machines' connections are dropped before they reach the node");
        }
        // A rule is a block of "Name: value" lines; one for a binary, enabled and allowing, is the one that counts.
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in output.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = rule.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
            if (fields.TryGetValue("Program", out var program)
                && fields.TryGetValue("Enabled", out var enabled) && enabled.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                && fields.TryGetValue("Action", out var action) && action.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                allowed.TryAdd(Path.GetFullPath(program), fields.TryGetValue("Profiles", out var profiles) ? profiles : "its");
            }
        }
        if (!allowed.TryGetValue(Path.GetFullPath(nodePath), out var on))
        {
            return (false, $"rule {FirewallRule} is there but not for {nodePath}, or not enabled: an earlier install's");
        }
        var missing = FirewallPrograms().Skip(1).Where(p => !allowed.ContainsKey(Path.GetFullPath(p))).Select(Path.GetFileName).ToList();
        return missing.Count > 0
            ? (false, $"rule {FirewallRule} lets {Path.GetFileName(nodePath)} in, but not {string.Join(", ", missing)}: the Fleet page and pilot nodes hear no other node")
            : (true, $"rule {FirewallRule} allows {Path.GetFileName(nodePath)} in, on {on} networks");
    }

    /// <summary>Claude Code's folder trust, in this profile's file; a test points it elsewhere.</summary>
    public ClaudeTrust Trust { get; init; } = new(ClaudeAccount.FileFor(profileRoot));

    /// <summary>The folder whose trust covers <paramref name="folder"/> (itself or one above it), or null when none does.</summary>
    public string? Trusted(string folder) => Trust.Trusted(folder);

    private (bool, string) GhSignedIn()
    {
        var (exit, output) = Run("gh", ["auth", "status", "--hostname", "github.com"], TimeSpan.FromSeconds(15));
        var account = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Logged in to", StringComparison.Ordinal));
        return exit == 0 ? (true, account?.TrimStart('✓', ' ') ?? "signed in") : (false, exit == -1 ? "gh is not on PATH" : "gh is not signed in");
    }

    private static (int, string) RunProcess(string program, IReadOnlyList<string> args, TimeSpan budget)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            start.Environment["GH_PROMPT_DISABLED"] = "1";
            start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(budget))
            {
                process.Kill(entireProcessTree: true);
                return (-2, "");
            }
            return (process.ExitCode, output.Result + error.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    // Through the shell's runas verb: Windows asks the person on this machine's screen, and a refusal throws.
    private static bool RunElevatedProcess(string program, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            process.WaitForExit(TimeSpan.FromMinutes(2));
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // A console window of its own, titled, saying what it is for before claude takes it over: a window a person has to
    // answer shows who opened it and why.
    private static bool OpenConsoleWindow(string folder, string title)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = folder };
            start.ArgumentList.Add("/k");
            start.ArgumentList.Add($"title {title} & echo pilot's node opened this so its watchtower can run here. & echo Accept Claude Code's trust prompt, then type /exit and close this window. & claude");
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
