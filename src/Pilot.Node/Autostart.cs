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
/// <para><b>Windows:</b> a value in the user's <c>Run</c> key that starts the node through <c>conhost --headless</c>, so
/// no console window opens and none is left to close by mistake. A self-update's successor starts windowless already.</para>
/// <para><b>Linux:</b> a systemd user unit, <c>Restart=always</c>. Jobs run in transient scopes of their own, outside the
/// unit's cgroup, so they outlive the node. A self-update or a settings restart starts no successor: the node exits and
/// the unit starts it again on the binaries in place. Exit code 3 (another node holds the socket) is not retried. A unit
/// that differs from <see cref="SystemdUnit"/> is out of date, and the setup check has it rewritten.</para>
/// </remarks>
public static class Autostart
{
    /// <summary>The Run value's name, and the unit's.</summary>
    public const string Name = "pilot-node";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The command line the Run value holds: the node, windowless, with its options.</summary>
    public static string WindowsCommand(string nodePath, IReadOnlyList<string> nodeArgs, string? systemRoot = null)
    {
        // Joined by hand: a Run value is a Windows path, whatever the platform this is built on (Path.Combine joins with /).
        var conhost = (systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.System)).TrimEnd('\\') + @"\conhost.exe";
        return string.Join(' ', new[] { Quote(conhost), "--headless", Quote(nodePath) }.Concat(nodeArgs.Select(Quote)));
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

    /// <summary>Whether the entry in place is current: always on Windows, else the unit file against <see cref="SystemdUnit"/>.</summary>
    public static bool EntryIsCurrent() => !OperatingSystem.IsLinux() || !File.Exists(UnitPath()) || UnitIsCurrent(File.ReadAllText(UnitPath()));

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

    /// <summary>Installs the entry for <paramref name="nodePath"/> with <paramref name="nodeArgs"/>, and starts the node now when none answers.</summary>
    public static async Task<int> InstallAsync(string nodePath, IReadOnlyList<string> nodeArgs, string socketPath, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        string where;
        if (OperatingSystem.IsWindows())
        {
            var command = WindowsCommand(nodePath, nodeArgs);
            WriteRun(command);
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

    /// <summary>Takes the entry out; the node running now runs on.</summary>
    public static async Task<int> RemoveAsync(TextWriter output, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await output.WriteLineAsync(DeleteRun() ? $@"autostart: removed HKCU\{RunKey}\{Name}" : "autostart: there was none");
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
    public static string? Entry() => OperatingSystem.IsWindows() ? ReadRun()
        : OperatingSystem.IsLinux() && File.Exists(UnitPath()) ? $"{UnitPath()}: {File.ReadAllLines(UnitPath()).FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))?["ExecStart=".Length..]}"
        : null;

    /// <summary>Says what starts the node at logon, if anything.</summary>
    public static async Task<int> ShowAsync(TextWriter output)
    {
        var entry = Entry();
        await output.WriteLineAsync(entry is null ? "autostart: none; pilot-node autostart install -- --lan starts the node at logon" : $"autostart: {entry}");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteRun(string command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(Name, command, Microsoft.Win32.RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static bool DeleteRun()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(Name) is null)
        {
            return false;
        }
        key.DeleteValue(Name);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRun()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) as string;
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
            foreach (var arg in nodeArgs)
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
