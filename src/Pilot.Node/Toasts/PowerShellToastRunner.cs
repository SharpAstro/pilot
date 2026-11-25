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
        + "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WinRT] | Out-Null\r\n"
        + "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WinRT] | Out-Null\r\n";

    /// <summary>The PowerShell that shows <paramref name="xml"/> as the toast of <paramref name="tag"/>; pure, so a test reads it.</summary>
    public static string ShowScript(string tag, string xml) =>
        Prelude
        + "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument\r\n"
        + $"$xml.LoadXml({Quote(xml)})\r\n"
        + "$toast = New-Object Windows.UI.Notifications.ToastNotification $xml\r\n"
        + $"$toast.Tag = {Quote(tag)}\r\n"
        + $"$toast.Group = {Quote(Group)}\r\n"
        + $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier({Quote(Aumid)}).Show($toast)\r\n";

    /// <summary>The PowerShell that withdraws the toast of <paramref name="tag"/> (<c>History.Remove</c>); pure, so a test reads it.</summary>
    public static string RemoveScript(string tag) =>
        Prelude
        + $"[Windows.UI.Notifications.ToastNotificationManager]::History.Remove({Quote(tag)}, {Quote(Group)}, {Quote(Aumid)})\r\n";

    /// <summary>A PowerShell single-quoted string literal of <paramref name="text"/> (an apostrophe is doubled; nothing else is special).</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

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
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancel.ThrowIfCancellationRequested();
        }
    }
}
