using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pilot.Node.Toasts;

/// <summary>Where the scheme's values are written: the registry on Windows, a fake in a test (a unit test touches no registry).</summary>
public interface ISchemeRegistry
{
    /// <summary>The string value <paramref name="name"/> (<c>""</c> is the key's default) of <c>HKCU\<paramref name="subKey"/></c>, or null when missing.</summary>
    string? Read(string subKey, string name);

    /// <summary>Sets the string value, creating the key.</summary>
    void Write(string subKey, string name, string value);
}

/// <summary>
/// The per-user registration of the <c>pilot:</c> URL scheme (#1356), so a click on a toast starts <c>pilot-gui</c> with the link:
/// <c>HKCU\Software\Classes\pilot</c> with <c>URL Protocol</c> and <c>shell\open\command</c> running the installed GUI with <c>"%1"</c>.
/// <see cref="Values"/> is the pure builder; <see cref="Register"/> writes what differs, so it is idempotent.
/// </summary>
public static class PilotScheme
{
    /// <summary>The scheme's key under HKCU.</summary>
    public const string Key = @"Software\Classes\pilot";

    /// <summary>The command key under HKCU.</summary>
    public const string CommandKey = Key + @"\shell\open\command";

    /// <summary>The GUI's binary beside the node's.</summary>
    public static string GuiPathBeside(string nodePath) =>
        Path.Combine(Path.GetDirectoryName(nodePath) ?? "", OperatingSystem.IsWindows() ? "pilot-gui.exe" : "pilot-gui");

    /// <summary>What the scheme holds for the GUI at <paramref name="guiPath"/>: (key, value name, value); an empty name is the key's default.</summary>
    public static IReadOnlyList<(string SubKey, string Name, string Value)> Values(string guiPath) =>
    [
        (Key, "", "URL:Pilot"),
        (Key, "URL Protocol", ""),
        (CommandKey, "", $"{Autostart.Quote(guiPath)} \"%1\""),
    ];

    /// <summary>Writes what is missing or differs; true when anything was written.</summary>
    public static bool Register(ISchemeRegistry registry, string guiPath)
    {
        var wrote = false;
        foreach (var (subKey, name, value) in Values(guiPath))
        {
            if (registry.Read(subKey, name) != value)
            {
                registry.Write(subKey, name, value);
                wrote = true;
            }
        }
        return wrote;
    }
}

/// <summary>The registry of the current user (Windows only).</summary>
public sealed class UserSchemeRegistry : ISchemeRegistry
{
    /// <inheritdoc />
    public string? Read(string subKey, string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    /// <inheritdoc />
    public void Write(string subKey, string name, string value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
    }
}

/// <summary>Registers the scheme once when the node starts, when the GUI binary sits beside the node's; a failure is logged, never fatal.</summary>
public sealed class SchemeRegistrar(ISchemeRegistry registry, string nodePath, Func<string, bool> exists, ILogger logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var gui = PilotScheme.GuiPathBeside(nodePath);
            if (exists(gui) && PilotScheme.Register(registry, gui))
            {
                logger.LogInformation("registered the pilot: URL scheme for {Gui}", gui);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogWarning(e, "could not register the pilot: URL scheme");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
