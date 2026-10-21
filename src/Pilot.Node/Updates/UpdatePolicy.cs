// Generic, with ReleaseFeed and ReleaseInstall: no pilot type in it, so the three can move to SharpAstro.AppShell
// together once a second app updates itself the same way (YtPlayer has its own copy, which these began as).
namespace Pilot.Node.Updates;

/// <summary>What an installed release does when a newer one is out.</summary>
public enum UpdatePolicy
{
    /// <summary>Installs it, once nothing it would cut short is running, and says so.</summary>
    Auto,

    /// <summary>Says it is out, with a way to install it; nothing installs until someone takes it.</summary>
    Ask,

    /// <summary>Does not look; installing it by hand still works.</summary>
    Off,
}

/// <summary>An <see cref="UpdatePolicy"/> as a command line spells it.</summary>
public static class UpdatePolicies
{
    public static IReadOnlyList<string> Names { get; } = ["auto", "ask", "off"];

    public static UpdatePolicy? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "auto" => UpdatePolicy.Auto,
        "ask" => UpdatePolicy.Ask,
        "off" => UpdatePolicy.Off,
        _ => null,
    };

    public static string Name(this UpdatePolicy policy) => Names[(int)policy];
}
