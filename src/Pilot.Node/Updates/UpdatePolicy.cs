// Generic, with ReleaseFeed and ReleaseInstall: no pilot type in it, so the three can move to SharpAstro.AppShell
// together once a second app updates itself the same way (YtPlayer has its own copy, which these began as).
namespace Pilot.Node.Updates;

/// <summary>What an installed release does when a newer one is out.</summary>
public enum UpdatePolicy
{
    /// <summary>Installs it at once and says so; running jobs carry on under the new node.</summary>
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

/// <summary>Which releases an installed one follows.</summary>
public enum UpdateChannel
{
    /// <summary>The tagged releases: native AOT, one per version.</summary>
    Stable,

    /// <summary>
    /// Main as it moves: every push to main replaces the <c>beta</c> prerelease's zips (a Debug build that needs the .NET
    /// runtime), and a stable release newer than it is taken too. Back on stable, a node waits for a release newer than
    /// the beta it runs rather than going back.
    /// </summary>
    Beta,
}

/// <summary>An <see cref="UpdateChannel"/> as a command line and the settings file spell it.</summary>
public static class UpdateChannels
{
    public static IReadOnlyList<string> Names { get; } = ["stable", "beta"];

    public static UpdateChannel? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "stable" => UpdateChannel.Stable,
        "beta" => UpdateChannel.Beta,
        _ => null,
    };

    public static string Name(this UpdateChannel channel) => Names[(int)channel];
}
