using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// <c>pilot-node autostart install|remove|show</c>: the node started when this user logs in, with the options it was
/// installed with (<c>--lan</c> above all, which the node a GUI starts never has), so a machine's node is up and on the
/// LAN for the rest of the fleet without anyone starting it. Per user, never per machine: the node runs as its person,
/// on their Claude profile, and needs no administrator.
/// </summary>
/// <remarks>
/// <para><b>Windows:</b> a value in the user's <c>Run</c> key that starts the watchdog (<c>pilot-node watchdog -- ...</c>, see
/// <see cref="NodeWatchdog"/>) through <c>conhost --headless</c>, so no console window opens and none is left to close by mistake;
/// the watchdog starts the node and starts it again when it dies. A self-update's successor starts windowless already.</para>
/// <para><b>Linux:</b> a systemd user unit, <c>Restart=always</c>. Jobs run in transient scopes of their own, outside the
/// unit's cgroup, so they outlive the node. A self-update or a settings restart starts no successor: the node exits and
/// the unit starts it again on the binaries in place. Exit code 3 (another node holds the socket) is not retried. A unit
/// that differs from <see cref="SystemdUnit"/> is out of date, and the setup check has it rewritten.</para>
/// </remarks>
public static class Autostart
{
    /// <summary>The Run value's name, and the unit's.</summary>
    public const string Name = "pilot-node";

    /// <summary>The desktop cat's Run value (Windows): pilot-buddy, started at logon when it sits beside the node.</summary>
    public const string BuddyName = "pilot-buddy";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The user's Run values by name: the registry on Windows. A seam, so a test proves the node's and the cat's entries on a fake and writes no registry.</summary>
    public interface IRunValues
    {
        /// <summary>The command of the value <paramref name="name"/>, or null when there is none.</summary>
        string? Read(string name);

        /// <summary>Sets the value <paramref name="name"/> to <paramref name="command"/>.</summary>
        void Write(string name, string command);

        /// <summary>Removes the value <paramref name="name"/>; false when there was none.</summary>
        bool Delete(string name);
    }

    /// <summary>The real Run key, HKCU. Windows only; elsewhere it reads nothing and writes nothing.</summary>
    public sealed class RegistryRunValues : IRunValues
    {
        public string? Read(string name)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(name) as string;
        }

        public void Write(string name, string command)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(name, command, Microsoft.Win32.RegistryValueKind.String);
        }

        public bool Delete(string name)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(name) is null)
            {
                return false;
            }
            key.DeleteValue(name);
            return true;
        }
    }

    /// <summary>The real Run values (HKCU). Read only: a caller that wants another (a test's fake) passes its own, as NodeSetup.Runs does.</summary>
    public static IRunValues RealRuns { get; } = new RegistryRunValues();

    /// <summary>Makes the cat's Run value for the binary beside <paramref name="nodePath"/>; false (and says why in <paramref name="line"/>) when there is none. Its own entry: the node's is not touched.</summary>
    public static bool InstallBuddy(string nodePath, IRunValues runs, out string line) => InstallBuddyAt(BuddyBeside(nodePath), runs, out line);

    /// <summary>Makes the cat's Run value for <paramref name="buddy"/>, the cat's binary as the caller found it; false (and says why in <paramref name="line"/>) when it is null.</summary>
    public static bool InstallBuddyAt(string? buddy, IRunValues runs, out string line)
    {
        if (buddy is null)
        {
            line = "autostart: no pilot-buddy beside the node, so no desktop cat is started at logon";
            return false;
        }
        runs.Write(BuddyName, BuddyCommand(buddy));
        line = $@"autostart: HKCU\{RunKey}\{BuddyName} = {BuddyCommand(buddy)} (the desktop cat, at logon)";
        return true;
    }

    /// <summary>
    /// A fresh setup (no node entry before this install) starts the cat at logon too, when pilot-buddy sits beside the node: the
    /// cat is on by default. A node already set up keeps whatever the person chose for the cat, so a rewrite never turns it back on.
    /// </summary>
    /// <param name="nodeEntryExisted">Whether the node's entry was there before this install.</param>
    /// <param name="buddy">The cat's binary beside the node, or null when there is none.</param>
    /// <param name="runs">The Run values written.</param>
    /// <param name="output">Where the line saying what was done goes.</param>
    /// <param name="runKey">Whether the Run key applies here (Windows); null asks the system.</param>
    public static async Task InstallFreshBuddyAsync(bool nodeEntryExisted, string? buddy, IRunValues runs, TextWriter output, bool? runKey = null)
    {
        if (nodeEntryExisted || !(runKey ?? OperatingSystem.IsWindows()))
        {
            return;
        }
        InstallBuddyAt(buddy, runs, out var line);
        await output.WriteLineAsync(line);
    }

    /// <summary>Takes the cat's Run value out and leaves the node's; false when there was none.</summary>
    public static bool RemoveBuddy(IRunValues runs) => runs.Delete(BuddyName);

    /// <summary>The cat's Run command, or null when the cat does not start at logon.</summary>
    public static string? BuddyEntry(IRunValues runs) => runs.Read(BuddyName);

    /// <summary>The desktop cat's binary beside <paramref name="nodePath"/>, or null when there is none (the cat ships with the other binaries, so a node installed alone has no cat).</summary>
    public static string? BuddyBeside(string nodePath)
    {
        // One rule for where the cat lives: the watchtower's MCP config finds it the same way.
        var folder = Path.GetDirectoryName(nodePath);
        return string.IsNullOrEmpty(folder) ? null : Flights.SessionMcpConfig.BuddyBeside(folder);
    }

    /// <summary>The Run value's command for the cat: its path, quoted. A WinExe has no console, so nothing wraps it.</summary>
    public static string BuddyCommand(string buddyPath) => Quote(buddyPath);

    /// <summary>The argument that makes <c>pilot-node</c> the watchdog (#1377): the Run entry starts <c>pilot-node watchdog -- &lt;node options&gt;</c>.</summary>
    public const string WatchdogArgument = "watchdog";

    /// <summary>The command line the Run value holds: the watchdog, windowless, which starts the node with its options and again when it dies.</summary>
    public static string WindowsCommand(string nodePath, IReadOnlyList<string> nodeArgs, string? systemRoot = null)
    {
        // Joined by hand: a Run value is a Windows path, whatever the platform this is built on (Path.Combine joins with /).
        var conhost = (systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.System)).TrimEnd('\\') + @"\conhost.exe";
        return string.Join(' ', new[] { Quote(conhost), "--headless", Quote(nodePath), WatchdogArgument, "--" }.Concat(nodeArgs.Select(Quote)));
    }

    /// <summary>
    /// The node path and options a Run value holds, whether it is the older one (the node itself) or the watchdog's; null when it does not read
    /// as <c>conhost --headless &lt;node&gt; ...</c>. The inverse of <see cref="WindowsCommand"/>.
    /// </summary>
    public static (string Conhost, string NodePath, IReadOnlyList<string> Args)? ParseWindowsCommand(string command)
    {
        var words = SplitWindowsCommand(command);
        if (words.Count < 3 || !words[1].Equals("--headless", StringComparison.Ordinal))
        {
            return null;
        }
        var rest = words.Skip(3).ToList();
        if (rest.Count >= 2 && rest[0] == WatchdogArgument && rest[1] == "--")
        {
            rest = rest.Skip(2).ToList();
        }
        return (words[0], words[2], rest);
    }

    /// <summary>
    /// Whether a Run value is the one <see cref="WindowsCommand"/> writes for its own node path and options: an older entry (the node started
    /// directly, with no watchdog) is not, and the setup check has it rewritten. Compared the way Windows compares paths.
    /// </summary>
    public static bool RunEntryIsCurrent(string command)
    {
        if (ParseWindowsCommand(command) is not { } parsed)
        {
            return false;
        }
        // The folder may be empty: off Windows the system folder is "", so WindowsCommand writes \conhost.exe (a setup test's rewrite on Linux).
        var slash = parsed.Conhost.LastIndexOf('\\');
        return slash >= 0 && string.Equals(command, WindowsCommand(parsed.NodePath, parsed.Args, parsed.Conhost[..slash]), StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitWindowsCommand(string command)
    {
        // CommandLineToArgvW's rules, the inverse of Quote.
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var inQuote = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < command.Length && command[i] == '\\')
                {
                    slashes++;
                    i++;
                }
                inWord = true;
                if (i < command.Length && command[i] == '"')
                {
                    word.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        word.Append('"');
                    }
                    else
                    {
                        inQuote = !inQuote;
                    }
                }
                else
                {
                    word.Append('\\', slashes);
                    i--;
                }
            }
            else if (c == '"')
            {
                inWord = true;
                inQuote = !inQuote;
            }
            else if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
            }
            else
            {
                inWord = true;
                word.Append(c);
            }
        }
        if (inWord)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>The systemd user unit that runs the node with its options.</summary>
    public static string SystemdUnit(string nodePath, IReadOnlyList<string> nodeArgs)
    {
        var exec = string.Join(' ', new[] { nodePath }.Concat(nodeArgs).Select(SystemdQuote));
        return $"""
            [Unit]
            Description=pilot v2's node
            After=network-online.target

            [Service]
            ExecStart={exec}
            WorkingDirectory={Path.GetDirectoryName(nodePath)}
            Restart=always
            RestartSec=5
            RestartPreventExitStatus={NodeExitCodes.AlreadyRunning} {NodeExitCodes.StoreTooNew}

            [Install]
            WantedBy=default.target

            """;
    }

    /// <summary>
    /// The path and arguments an <c>ExecStart=</c> value holds: the inverse of the quoting <see cref="SystemdUnit"/> does.
    /// Null when it does not read back (an unclosed quote, an empty line).
    /// </summary>
    public static (string Path, IReadOnlyList<string> Args)? ParseExecStart(string exec)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (c == '"')
            {
                inWord = true;
                for (i++; ; i++)
                {
                    if (i >= exec.Length)
                    {
                        return null;
                    }
                    if (exec[i] == '"')
                    {
                        break;
                    }
                    if (exec[i] == '\\' && i + 1 < exec.Length)
                    {
                        i++;
                    }
                    else if (exec[i] is '%' or '$' && i + 1 < exec.Length && exec[i + 1] == exec[i])
                    {
                        i++;
                    }
                    word.Append(exec[i]);
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
            }
            else
            {
                inWord = true;
                word.Append(c);
            }
        }
        if (inWord)
        {
            words.Add(word.ToString());
        }
        return words.Count == 0 ? null : (words[0], words.Skip(1).ToList());
    }

    /// <summary>
    /// Whether <paramref name="unitText"/> is the unit <see cref="SystemdUnit"/> writes for its own <c>ExecStart</c>; false
    /// for one that differs in anything else (a <c>KillMode</c> an older node wrote, say) or whose ExecStart does not read.
    /// </summary>
    public static bool UnitIsCurrent(string unitText)
    {
        var text = unitText.Replace("\r\n", "\n");
        var exec = text.Split('\n').FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal));
        return exec is not null && ParseExecStart(exec["ExecStart=".Length..]) is { } parsed
            && string.Equals(text, SystemdUnit(parsed.Path, parsed.Args), StringComparison.Ordinal);
    }

    /// <summary>The unit file's text on Linux, null elsewhere or when there is none.</summary>
    public static string? UnitText() => OperatingSystem.IsLinux() && File.Exists(UnitPath()) ? File.ReadAllText(UnitPath()) : null;

    /// <summary>The path and arguments the <c>ExecStart=</c> of <paramref name="unitText"/> holds; null when it has none or does not read.</summary>
    public static (string Path, IReadOnlyList<string> Args)? ExecOf(string unitText) =>
        unitText.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal)) is { } exec
            ? ParseExecStart(exec["ExecStart=".Length..])
            : null;

    /// <summary>Whether the entry in place is current: on Windows the Run value against <see cref="WindowsCommand"/>, on Linux the unit file against <see cref="SystemdUnit"/>.</summary>
    public static bool EntryIsCurrent() => EntryIsCurrent(RealRuns, OperatingSystem.IsWindows());

    /// <summary>As <see cref="EntryIsCurrent()"/>, reading the Run value from <paramref name="runs"/> where the Run key applies (<paramref name="runKey"/>). No entry is not out of date: it is missing.</summary>
    public static bool EntryIsCurrent(IRunValues runs, bool runKey) => runKey
        ? runs.Read(Name) is not { } command || RunEntryIsCurrent(command)
        : !OperatingSystem.IsLinux() || !File.Exists(UnitPath()) || UnitIsCurrent(File.ReadAllText(UnitPath()));

    /// <summary>Where the unit goes: <c>~/.config/systemd/user</c>, or under <c>XDG_CONFIG_HOME</c>.</summary>
    public static string UnitPath() =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "systemd", "user", Name + ".service");

    /// <summary>One argument as the Windows command line reads it back (CommandLineToArgvW's rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"'))
        {
            return arg;
        }
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            // Backslashes before a quote are doubled, and the quote escaped; elsewhere they stand as they are.
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string SystemdQuote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\' or '%' or '$') ? arg : "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\"";

    /// <summary>
    /// Installs the entry for <paramref name="nodePath"/> with <paramref name="nodeArgs"/>, and starts the node now when none answers. Only the
    /// node's own entry: the cat's Run value is its own setup key's (<see cref="InstallBuddy"/>), so a rewrite never turns the cat back on.
    /// <paramref name="runs"/> are the Run values written on Windows (null is the real registry; NodeSetup passes its own, a test's fake included),
    /// <paramref name="runKey"/> whether the entry is a Run value (null asks the system), and <paramref name="startNow"/> whether the node is
    /// started now when none answers (a test that proves only the entry passes false).
    /// </summary>
    public static async Task<int> InstallAsync(string nodePath, IReadOnlyList<string> nodeArgs, string socketPath, TextWriter output, TextWriter error, CancellationToken cancellationToken,
        IRunValues? runs = null, bool? runKey = null, bool startNow = true)
    {
        string where;
        if (runKey ?? OperatingSystem.IsWindows())
        {
            var command = WindowsCommand(nodePath, nodeArgs);
            (runs ?? RealRuns).Write(Name, command);
            where = $@"HKCU\{RunKey}\{Name}";
            await output.WriteLineAsync($"autostart: {where} = {command}");
        }
        else if (OperatingSystem.IsLinux())
        {
            var unit = UnitPath();
            Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
            await File.WriteAllTextAsync(unit, SystemdUnit(nodePath, nodeArgs), cancellationToken);
            where = unit;
            await output.WriteLineAsync($"autostart: wrote {unit}");
            if ((await SystemctlAsync(["daemon-reload"], cancellationToken) ?? await SystemctlAsync(["enable", Name + ".service"], cancellationToken)) is { } problem)
            {
                await error.WriteLineAsync($"autostart: systemctl --user did not take it ({problem}); run `systemctl --user enable --now {Name}` yourself");
                return 1;
            }
        }
        else
        {
            await error.WriteLineAsync("autostart: only Windows (a Run entry) and Linux (a systemd user unit) for now; start pilot-node from a LaunchAgent on macOS");
            return NodeExitCodes.InvalidArguments;
        }

        if (!startNow)
        {
            return 0;
        }

        // Started now too, so the machine joins the fleet without a logon: unless a node already answers, which this
        // one would only find holding the socket.
        if (await NodeSocket.IsListeningAsync(socketPath, cancellationToken))
        {
            await output.WriteLineAsync($"autostart: a node already answers on {socketPath}, so none was started now; this entry starts it at the next logon (or stop that node and log on again)");
            return 0;
        }
        var started = OperatingSystem.IsWindows() ? StartWindows(nodePath, nodeArgs) : await SystemctlAsync(["start", Name + ".service"], cancellationToken) is null;
        // Started is not running: said only once it answers on its socket.
        var answers = started && await AnswersWithinAsync(socketPath, TimeSpan.FromSeconds(15), cancellationToken);
        await output.WriteLineAsync(answers ? "autostart: started the node now; it answers on its socket"
            : started ? $"autostart: the entry is in place and the node was started, but it did not answer on {socketPath} within 15 s; run pilot-node by hand to see why"
            : "autostart: the entry is in place, but the node could not be started now; it starts at the next logon");
        return answers || !started ? 0 : 1;
    }

    private static async Task<bool> AnswersWithinAsync(string socketPath, TimeSpan budget, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < until)
        {
            if (await NodeSocket.IsListeningAsync(socketPath, cancellationToken))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        return false;
    }

    /// <summary>
    /// Takes the node's entry out, and only it (the cat's Run value stays its own setup key's); the node running now runs on.
    /// <paramref name="runs"/> are the Run values on Windows (null is the real registry), <paramref name="runKey"/> whether the entry is one (null asks the system).
    /// </summary>
    public static async Task<int> RemoveAsync(TextWriter output, CancellationToken cancellationToken, IRunValues? runs = null, bool? runKey = null)
    {
        if (runKey ?? OperatingSystem.IsWindows())
        {
            await output.WriteLineAsync((runs ?? RealRuns).Delete(Name) ? $@"autostart: removed HKCU\{RunKey}\{Name}" : "autostart: there was none");
            return 0;
        }
        if (OperatingSystem.IsLinux())
        {
            var unit = UnitPath();
            if (!File.Exists(unit))
            {
                await output.WriteLineAsync("autostart: there was none");
                return 0;
            }
            await SystemctlAsync(["disable", Name + ".service"], cancellationToken);
            File.Delete(unit);
            await SystemctlAsync(["daemon-reload"], cancellationToken);
            await output.WriteLineAsync($"autostart: removed {unit}; the node running now runs on until it stops");
            return 0;
        }
        await output.WriteLineAsync("autostart: there is none on this system");
        return 0;
    }

    /// <summary>What starts the node at logon, in a line, or null when nothing does.</summary>
    public static string? Entry() => Entry(RealRuns, OperatingSystem.IsWindows());

    /// <summary>What starts the node at logon, read from <paramref name="runs"/> where the Run key applies (<paramref name="runKey"/>), else from the unit.</summary>
    public static string? Entry(IRunValues runs, bool runKey) => runKey ? runs.Read(Name)
        : OperatingSystem.IsLinux() && File.Exists(UnitPath()) ? $"{UnitPath()}: {File.ReadAllLines(UnitPath()).FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))?["ExecStart=".Length..]}"
        : null;

    /// <summary>Says what starts the node at logon, if anything.</summary>
    public static async Task<int> ShowAsync(TextWriter output)
    {
        var entry = Entry();
        await output.WriteLineAsync(entry is null ? "autostart: none; pilot-node autostart install -- --lan starts the node at logon" : $"autostart: {entry}");
        return 0;
    }

    // Started directly, windowless (CreateNoWindow gives it a hidden console of its own), on standard handles that are
    // pipes closed at once rather than the caller's: a node started from a shell whose output is captured (an agent's,
    // a script's) would otherwise hold that pipe open for as long as it runs, and the install that started it would
    // never seem to end. Not through conhost --headless, as the Run entry is: conhost exits when its stdin ends, and a
    // closed pipe ends at once, taking the node with it. A node writing to a closed pipe loses the line, nothing more.
    private static bool StartWindows(string nodePath, IReadOnlyList<string> nodeArgs)
    {
        try
        {
            var start = new ProcessStartInfo(nodePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(nodePath)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // The watchdog starts the node (and starts it again), as the Run entry does at logon.
            foreach (var arg in new[] { WatchdogArgument, "--" }.Concat(nodeArgs))
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Runs <c>systemctl --user</c>; null when it did it, else what it said.</summary>
    private static async Task<string?> SystemctlAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo("systemctl") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--user");
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            var said = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? null : said.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
