using Pilot.Ui;

namespace Pilot.Gui;

/// <summary>Where the raw AccentPalette bytes come from: the bytes, or the reason there are none. A test fakes it.</summary>
internal interface IAccentSource
{
    (byte[]? Bytes, string? Reason) Read();
}

/// <summary>The Windows registry's AccentPalette; a reason, never an exception, elsewhere or when it cannot be read.</summary>
internal sealed class RegistryAccentSource : IAccentSource
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    public (byte[]? Bytes, string? Reason) Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "the desktop publishes no accent colour here (not Windows)");
        }
        try
        {
            return ReadWindows();
        }
        catch (Exception ex)
        {
            return (null, $"the accent could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (byte[]? Bytes, string? Reason) ReadWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("AccentPalette") is byte[] bytes
            ? (bytes, null)
            : (null, @"HKCU Explorer\Accent\AccentPalette has no value");
    }
}

/// <summary>
/// The desktop's accent, read once in the static initialiser (never on the render thread). <see cref="Current"/> is null
/// where none is published and <see cref="Reason"/> says why; <see cref="LogOnce"/> writes it to gui.log at startup.
/// </summary>
internal static class SystemAccent
{
    public static DesktopAccent? Current { get; }

    public static string? Reason { get; }

    static SystemAccent() => (Current, Reason) = Resolve(new RegistryAccentSource());

    /// <summary>Parses what a source gives; null with a reason when it gives none, a short value, or throws.</summary>
    internal static (DesktopAccent? Accent, string? Reason) Resolve(IAccentSource source)
    {
        try
        {
            var (bytes, reason) = source.Read();
            if (bytes is null)
            {
                return (null, reason ?? "no accent value");
            }
            return DesktopAccent.Parse(bytes) is { } accent
                ? (accent, null)
                : (null, $"the accent value is {bytes.Length} bytes, expected {DesktopAccent.PaletteLength}");
        }
        catch (Exception ex)
        {
            return (null, $"the accent could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static int _logged;

    /// <summary>Says once, at startup, why there is no desktop accent; nothing when there is one.</summary>
    public static void LogOnce(Action<string> log)
    {
        if (Current is null && Interlocked.Exchange(ref _logged, 1) == 0)
        {
            log($"pilot-gui: no desktop accent colour: {Reason}");
        }
    }
}
