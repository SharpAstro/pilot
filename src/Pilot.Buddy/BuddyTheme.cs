using DIR.Lib;
using Pilot.Ui;

namespace Pilot.Buddy;

/// <summary>
/// Whether the desktop is dark and what its accent colour is, read from the registry as Pilot.Gui reads the accent (<c>SystemAccent</c>, which is the GUI's
/// own and not shared, so this is a small copy that goes through the same <see cref="DesktopAccent"/> parse). A reason, never an exception, where there is
/// none (not Windows, no value), so the cat falls back to the bright palette and the built-in blue.
/// </summary>
public static class BuddyTheme
{
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>What the desktop says, or why it says nothing.</summary>
    public sealed record Reading(bool Dark, RGBAColor32? Accent, string? Reason);

    /// <summary>Reads the desktop's theme and accent now.</summary>
    public static Reading Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Reading(false, null, "the desktop publishes no theme or accent here (not Windows)");
        }
        try
        {
            return ReadWindows();
        }
        catch (Exception ex)
        {
            return new Reading(false, null, $"the theme could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>The palette for a reading: dark or bright, the accent the desktop picks for this chrome.</summary>
    public static BuddyPalette PaletteFor(Reading reading) => BuddyPalette.For(reading.Dark, reading.Accent);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Reading ReadWindows()
    {
        using var personalize = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        // AppsUseLightTheme 0 is dark; a missing value is bright.
        var dark = personalize?.GetValue("AppsUseLightTheme") is int light && light == 0;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AccentKey);
        if (key?.GetValue("AccentPalette") is not byte[] bytes)
        {
            return new Reading(dark, null, @"HKCU Explorer\Accent\AccentPalette has no value");
        }
        return DesktopAccent.Parse(bytes) is { } accent
            ? new Reading(dark, accent.Pick(dark), null)
            : new Reading(dark, null, $"the accent value is {bytes.Length} bytes, expected {DesktopAccent.PaletteLength}");
    }
}
