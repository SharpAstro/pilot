using System.Diagnostics;
using System.Text;

namespace Pilot.Node.Toasts;

/// <summary>Shows and withdraws the node's desktop toasts (#1356); a fake records what it is asked, the real one starts Windows PowerShell.</summary>
public interface IToastRunner
{
    /// <summary>Shows the toast <paramref name="xml"/> under <paramref name="tag"/>, in group <c>pilot</c>; a toast of the same tag is replaced.</summary>
    Task Show(string tag, string xml, CancellationToken cancel = default);

    /// <summary>Withdraws the toast of <paramref name="tag"/>, in group <c>pilot</c>, from the notification centre.</summary>
    Task Remove(string tag, CancellationToken cancel = default);
}

/// <summary>
/// Shows a toast the way <c>pricewatch.py</c> does, proven on this machine (#1356): one windowless Windows PowerShell 5.1 process per call
/// (<c>powershell.exe</c>, never <c>pwsh</c>: only 5.1 has the WinRT projection), under Windows PowerShell's own registered AUMID (an
/// unregistered one is dropped silently). The script goes by <c>-EncodedCommand</c>; the process has a timeout that kills it, and there is
/// no loop. A no-op off Windows.
/// </summary>
public sealed class PowerShellToastRunner : IToastRunner
{
    /// <summary>Windows PowerShell's own AUMID, registered with the shell, so its toasts show.</summary>
    public const string Aumid = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>The group every toast of the node is in, so a tag stands for one card.</summary>
    public const string Group = "pilot";

    private readonly TimeSpan _timeout;

    /// <summary>A runner whose process is killed after <paramref name="timeout"/> (15 s when null).</summary>
    public PowerShellToastRunner(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public Task Show(string tag, string xml, CancellationToken cancel = default) => Run(ShowScript(tag, xml), cancel);

    /// <inheritdoc />
    public Task Remove(string tag, CancellationToken cancel = default) => Run(RemoveScript(tag), cancel);

    private const string Prelude =
        "$ErrorActionPreference = 'Stop'\r\n"
        + "try {\r\n"
        + "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WinRT] | Out-Null\r\n"
        + "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WinRT] | Out-Null\r\n";

    // Windows PowerShell 5.1 under -EncodedCommand writes its error stream as CLIXML, so an uncaught error would reach the log as "#< CLIXML":
    // the script catches it and writes the message itself, as plain text.
    private const string Coda = "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\r\n";

    /// <summary>The PowerShell that shows <paramref name="xml"/> as the toast of <paramref name="tag"/>; pure, so a test reads it.</summary>
    public static string ShowScript(string tag, string xml) =>
        Prelude
        + "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument\r\n"
        + $"$xml.LoadXml({Decoded(xml)})\r\n"
        + "$toast = New-Object Windows.UI.Notifications.ToastNotification $xml\r\n"
        + $"$toast.Tag = {Decoded(tag)}\r\n"
        + $"$toast.Group = {Quote(Group)}\r\n"
        + $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier({Quote(Aumid)}).Show($toast)\r\n"
        + Coda;

    /// <summary>The PowerShell that withdraws the toast of <paramref name="tag"/> (<c>History.Remove</c>); pure, so a test reads it.</summary>
    public static string RemoveScript(string tag) =>
        Prelude
        + $"[Windows.UI.Notifications.ToastNotificationManager]::History.Remove({Decoded(tag)}, {Quote(Group)}, {Quote(Aumid)})\r\n"
        + Coda;

    /// <summary>
    /// A PowerShell expression of <paramref name="text"/> that holds no card text in the script: the UTF-8 bytes go as base64 (only
    /// <c>A-Za-z0-9+/=</c>, nothing to break out of the literal) and are decoded inside the script. Card text is model-written, so it is never
    /// interpolated: PowerShell reads U+2018, U+2019, U+201A and U+201B as quotes too.
    /// </summary>
    public static string Decoded(string text) =>
        $"[System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}'))";

    /// <summary>
    /// A PowerShell single-quoted string literal of <paramref name="text"/>, for the node's own constants only (never card text): each of
    /// the characters PowerShell takes for a single quote (U+0027, U+2018, U+2019, U+201A, U+201B) is doubled.
    /// </summary>
    public static string Quote(string text)
    {
        var quoted = new StringBuilder("'");
        foreach (var c in text)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') quoted.Append(c);
        }
        return quoted.Append('\'').ToString();
    }

    /// <summary>The <c>-EncodedCommand</c> argument of <paramref name="script"/>: base64 of its UTF-16LE bytes.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>The process start of <paramref name="script"/>: windowless Windows PowerShell 5.1, no profile, not interactive.</summary>
    public static ProcessStartInfo StartOf(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Encode(script) })
            start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>
    /// What a toast script's exit says, null for a success: the exit code and the first line of its error text that is not CLIXML (the
    /// <c>#&lt; CLIXML</c> header and the serialized records Windows PowerShell writes for an error the script did not catch).
    /// </summary>
    public static string? FailureOf(int exitCode, string stderr)
    {
        if (exitCode == 0) return null;
        var first = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !line.StartsWith("#<", StringComparison.Ordinal) && !line.StartsWith('<')) ?? "no error text";
        return $"the toast script exited {exitCode}: {first}";
    }

    private async Task Run(string script, CancellationToken cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = StartOf(script);
        ProcessStarts.Observe("toast", start);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return;
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(_timeout);
        try
        {
            // Drain both pipes so a full pipe cannot hold the process, then wait for its exit; the deadline ends both.
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            // A script that failed (parse error, bad AUMID, tag too long) says so, and the Toaster logs it.
            if (FailureOf(process.ExitCode, await error.ConfigureAwait(false)) is { } failure)
            {
                throw new InvalidOperationException(failure);
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancel.ThrowIfCancellationRequested();
        }
    }
}
