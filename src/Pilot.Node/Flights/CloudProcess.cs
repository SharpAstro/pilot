using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// One external command of a cloud bench (#823): <c>az</c>, <c>ssh</c>, <c>scp</c>, <c>ssh-keygen</c> or <c>git</c>. <see cref="OnLine"/>, when given,
/// gets every line the program writes (stdout and stderr) as it is written, which is how the bench's own output reaches the job's log while
/// the command still runs.
/// </summary>
public sealed record CloudCommand(
    string Program,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    TimeSpan? Budget = null,
    Action<string>? OnLine = null,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    /// <summary>The command as one line, <c>program arg arg</c>: for a log or a test, never to be run.</summary>
    public string Line => Args.Count == 0 ? Program : $"{Program} {string.Join(' ', Args)}";
}

/// <summary>What a <see cref="CloudCommand"/> did: its exit code and what it wrote.</summary>
/// <remarks>
/// A program that could not start is exit code <see cref="NotStarted"/> and one that ran past its budget is <see cref="OutOfBudget"/>, each
/// with the reason in <see cref="Error"/>, so a caller has one shape to read. A caller's own cancel is never a run: it is the exception.
/// </remarks>
public sealed record CloudProcessRun(int ExitCode, string Output, string Error)
{
    /// <summary>The exit code of a program the machine could not start (not installed, not on <c>PATH</c>).</summary>
    public const int NotStarted = -1;

    /// <summary>The exit code of a program the node killed for running past its budget.</summary>
    public const int OutOfBudget = -2;

    public bool Ok => ExitCode == 0;

    /// <summary>What went wrong, in a line: the first line of stderr, else of stdout, else the exit code.</summary>
    public string Why => (Error.Trim().Length > 0 ? Error : Output).Trim().Split('\n')[0].Trim() is { Length: > 0 } line ? line : $"exit code {ExitCode}";
}

/// <summary>
/// The one seam every external command of a cloud bench goes through, so a test says what <c>az</c>, <c>ssh</c> and <c>scp</c> answer and reads
/// the command lines issued, and no test starts a real process or touches Azure.
/// </summary>
public interface ICloudProcess
{
    /// <summary>Runs <paramref name="command"/> to its end. A cancel of <paramref name="cancellationToken"/> kills it and throws.</summary>
    Task<CloudProcessRun> RunAsync(CloudCommand command, CancellationToken cancellationToken);
}

/// <summary><see cref="ICloudProcess"/> by real processes, started without a window and with their whole tree killed on a cancel.</summary>
public sealed class SystemCloudProcess : ICloudProcess
{
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(15);

    // The tools that Windows ships as OpenSSH: taken from there first, so ssh, scp and ssh-keygen are all one flavour. Git for Windows
    // carries its own, and a key made by the one is refused by the other as "UNPROTECTED PRIVATE KEY FILE".
    private static readonly string[] WindowsSsh = ["ssh", "scp", "ssh-keygen"];

    public async Task<CloudProcessRun> RunAsync(CloudCommand command, CancellationToken cancellationToken)
    {
        var program = Resolve(command.Program);
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (command.WorkingDirectory is { Length: > 0 } cwd)
        {
            start.WorkingDirectory = cwd;
        }
        try
        {
            if (IsBatch(program))
            {
                start.Arguments = JoinForCmd(command.Args, program);
            }
            else
            {
                foreach (var arg in command.Args)
                {
                    start.ArgumentList.Add(arg);
                }
            }
        }
        catch (ArgumentException ex)
        {
            return new CloudProcessRun(CloudProcessRun.NotStarted, "", ex.Message);
        }
        // Never a prompt: the node has no terminal to show one in.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (key, value) in command.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception($"{command.Program} did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new CloudProcessRun(CloudProcessRun.NotStarted, "", $"{command.Program} could not be started (is it installed and on PATH?): {ex.Message}");
        }
        using var _ = process;
        process.StandardInput.Close();

        var budget = command.Budget ?? DefaultBudget;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(budget);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var reads = Task.WhenAll(Pump(process.StandardOutput, output, command.OnLine), Pump(process.StandardError, error, command.OnLine));
        try
        {
            await process.WaitForExitAsync(limit.Token);
            await reads.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone; only a descendant held the pipes.
            }
            await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            cancellationToken.ThrowIfCancellationRequested();
            return new CloudProcessRun(CloudProcessRun.OutOfBudget, Snapshot(output), $"{Snapshot(error)}\n{command.Program} ran past its budget of {budget.TotalSeconds:0.###}s and was killed".Trim());
        }
        return new CloudProcessRun(process.ExitCode, Snapshot(output), Snapshot(error));
    }

    private static async Task Pump(StreamReader reader, StringBuilder into, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            lock (into)
            {
                into.Append(line).Append('\n');
            }
            try
            {
                onLine?.Invoke(line);
            }
            catch (IOException)
            {
                // A log that cannot be written to must not end the bench.
            }
        }
    }

    private static string Snapshot(StringBuilder text)
    {
        lock (text)
        {
            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// The file a bare program name runs as. On Windows a program with no extension is looked up on <c>PATH</c> as <c>.exe</c>, <c>.cmd</c>,
    /// <c>.bat</c> (CreateProcess finds only <c>.exe</c> by itself, and the Azure CLI is <c>az.cmd</c>); elsewhere, and for a path, the name stands.
    /// </summary>
    public static string Resolve(string program, string? path = null, string? systemRoot = null)
    {
        if (!OperatingSystem.IsWindows() || Path.IsPathRooted(program) || Path.HasExtension(program))
        {
            return program;
        }
        if (WindowsSsh.Contains(program, StringComparer.OrdinalIgnoreCase)
            && (systemRoot ?? System.Environment.GetEnvironmentVariable("SystemRoot")) is { Length: > 0 } root
            && Path.Combine(root, "System32", "OpenSSH", program + ".exe") is var shipped
            && File.Exists(shipped))
        {
            return shipped;
        }
        foreach (var dir in (path ?? System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), program + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return program;
    }

    private static bool IsBatch(string program) =>
        OperatingSystem.IsWindows() && (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The arguments of a command line that a <c>.cmd</c> file will receive. cmd.exe acts on <c>&amp; | &lt; &gt;</c> and the like wherever they are
    /// not in double quotes, so each argument that holds anything but plain characters is quoted; one that no quoting makes safe (a double quote,
    /// a <c>%</c>, a line break) is refused with an <see cref="ArgumentException"/> rather than run as something else.
    /// </summary>
    public static string JoinForCmd(IReadOnlyList<string> args, string file = "a .cmd file")
    {
        var line = new StringBuilder();
        foreach (var arg in args)
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }
            if (arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ',' or ':' or '/' or '=' or '@' or '+' or '-' or '\\'))
            {
                line.Append(arg);
                continue;
            }
            if (arg.Any(c => c is '"' or '%' or '\r' or '\n' or '\0'))
            {
                throw new ArgumentException($"an argument holds a double quote, a percent sign or a line break, which {Path.GetFileName(file)} cannot take safely on Windows");
            }
            var trailing = arg.Length - arg.TrimEnd('\\').Length;
            line.Append('"').Append(arg).Append('\\', trailing).Append('"');
        }
        return line.ToString();
    }
}
