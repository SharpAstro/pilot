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

    /// <summary>The firewall rule's name, which the check looks for and the fix replaces.</summary>
    public const string FirewallRule = "pilot-node";

    /// <summary>The Claude Code settings file the hook checks read and the fixes write; a test points it elsewhere.</summary>
    public string SettingsFile { get; init; } = HookCommands.DefaultSettings();

    /// <summary>A runaway-agent clone to take the guard scripts from, or null when there is none; a test replaces it.</summary>
    public Func<string?> FindRunawayAgent { get; init; } = RunawayAgentClone;

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
            Fix = trusted is null && OperatingSystem.IsWindows() ? "Open the trust prompt" : null,
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
            case FirewallKey:
                if (!OperatingSystem.IsWindows())
                {
                    return "the node makes a firewall rule on Windows only";
                }
                // One elevated cmd for both, so one prompt: the old rule out (an earlier install's path) and this one in.
                var netsh = $"netsh advfirewall firewall delete rule name={FirewallRule} >nul & netsh advfirewall firewall add rule name={FirewallRule} dir=in action=allow profile=private enable=yes program=\"{nodePath}\"";
                // A command line by hand: cmd reads its own quoting, which ArgumentList's escaped quotes would break.
                if (!RunElevated(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe"), $"--headless cmd.exe /d /c \"{netsh}\""))
                {
                    return "the elevation prompt was refused or did not open";
                }
                return FirewallAllows() is (true, _) ? null : "the rule is still not there";
            case TrustKey:
                Directory.CreateDirectory(options.WatchtowerFolder);
                return OpenWindow(options.WatchtowerFolder, "pilot: trust the watchtower folder")
                    ? null
                    : $"no window opened: run claude in {options.WatchtowerFolder} and accept its trust prompt";
            default:
                return $"setup takes {string.Join(", ", new[] { AutostartKey, NoAutostartKey, HooksKey, GuardHooksKey, LanKey, FirewallKey, TrustKey })}, not {key}";
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
    /// a runaway-agent clone when there is one (the fix copies them, as its install.sh does), else where to get it.
    /// </summary>
    private SetupCheck GuardHooksCheck()
    {
        const string Title = "Runaway-agent guard hooks";
        try
        {
            var text = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "";
            var missing = GuardHooks.Missing(text);
            if (missing.Count == 0)
            {
                return new SetupCheck { Key = GuardHooksKey, Title = Title, Ok = true, Line = $"{string.Join(", ", GuardHooks.Guards.Select(g => g.Script))} in {SettingsFile}", Optional = true };
            }

            var names = string.Join(", ", missing.Select(g => g.Script));
            var clone = FindRunawayAgent();
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
                var from = Path.Combine(clone, guard.Script);
                var to = Path.Combine(folder, guard.Script);
                if (!File.Exists(to) || !File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to)))
                {
                    File.Copy(from, to, overwrite: true);
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

    /// <summary>Whether a rule lets this node's binary in, and how it stands, in a line.</summary>
    public (bool Allows, string Line) FirewallAllows()
    {
        var (exit, output) = Run("netsh", ["advfirewall", "firewall", "show", "rule", $"name={FirewallRule}", "verbose"], TimeSpan.FromSeconds(15));
        if (exit != 0)
        {
            return (false, $"no rule named {FirewallRule}: other machines' connections are dropped before they reach the node");
        }
        // A rule is a block of "Name: value" lines; one for this binary, enabled and allowing, is the one that counts.
        var rules = output.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in rules)
        {
            var fields = rule.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
            if (fields.TryGetValue("Program", out var program) && string.Equals(Path.GetFullPath(program), Path.GetFullPath(nodePath), StringComparison.OrdinalIgnoreCase)
                && fields.TryGetValue("Enabled", out var enabled) && enabled.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                && fields.TryGetValue("Action", out var action) && action.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"rule {FirewallRule} allows {Path.GetFileName(nodePath)} in, on {(fields.TryGetValue("Profiles", out var profiles) ? profiles : "its")} networks");
            }
        }
        return (false, $"rule {FirewallRule} is there but not for {nodePath}, or not enabled: an earlier install's");
    }

    /// <summary>The folder whose trust covers <paramref name="folder"/> (itself or one above it), or null when none does.</summary>
    public string? Trusted(string folder)
    {
        try
        {
            var file = ClaudeAccount.FileFor(profileRoot);
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root || root["projects"] is not JsonObject projects)
            {
                return null;
            }
            // Claude Code keys a project by its path with forward slashes; trust in a folder covers those under it.
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                var key = Path.TrimEndingDirectorySeparator(dir.FullName).Replace('\\', '/');
                key = key.EndsWith(':') ? key + "/" : key;
                var project = projects.FirstOrDefault(p => string.Equals(p.Key, key, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;
                if (project?["hasTrustDialogAccepted"] is JsonValue accepted && accepted.TryGetValue<bool>(out var yes) && yes)
                {
                    return dir.FullName;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

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
