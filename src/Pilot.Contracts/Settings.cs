namespace Pilot.Contracts;

/// <summary>
/// The Settings page (<c>GET /api/v1/settings</c>, this machine only): the node's settings, each with where its value
/// comes from. It is read from <c>settings.json</c> alone, so it answers at once; the setup checklist is apart, in
/// <see cref="SetupChecks"/> (<c>GET /api/v1/settings/checks</c>), as it runs programs.
/// </summary>
public sealed record SettingsPage
{
    public required SettingRow[] Settings { get; init; }

    /// <summary>
    /// The setup checklist. The node does not send it with the page: a host that shows both fills it from
    /// <see cref="SetupChecks"/>.
    /// </summary>
    public SetupCheck[] Checks { get; init => field = value ?? []; } = [];

    /// <summary>Why the node has not yet restarted for a saved change, or null when none waits. A restart no longer waits for jobs (they run detached and the new node picks them up), so a current node sends null; an older one may still say.</summary>
    public string? Restart { get; init; }

    /// <summary>The settings file the page edits.</summary>
    public required string File { get; init; }
}

/// <summary>One setting: what it is now, what the settings file says, and where the running value comes from.</summary>
public sealed record SettingRow
{
    /// <summary>Its name in the settings file and to <c>set node</c>: <c>lan</c>, <c>update-channel</c>.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>What it does, in a line.</summary>
    public required string Detail { get; init; }

    /// <summary>The value the node runs with, as <c>set node</c> spells it; a list's items a line each.</summary>
    public required string Value { get; init; }

    /// <summary>What the settings file says; null when it says nothing and the default stands.</summary>
    public string? Saved { get; init; }

    /// <summary>Where <see cref="Value"/> comes from (<see cref="SettingSources"/>).</summary>
    public required string Source { get; init; }

    /// <summary>The values it takes, for a setting that takes one of a few; empty for a free one (a prefix, folders).</summary>
    public string[] Choices { get; init => field = value ?? []; } = [];

    /// <summary>Whether it is a list (repo roots): <c>set node &lt;key&gt;</c> takes every value at once.</summary>
    public bool Many { get; init; }

    /// <summary>Whether the file holds a value the node does not run with yet: it applies at the node's next start.</summary>
    public bool Pending { get; init; }
}

/// <summary>Where a setting's running value comes from.</summary>
public static class SettingSources
{
    public const string Default = "default";

    public const string File = "settings";

    /// <summary>The node's command line (or its environment) gave it, and wins over the file.</summary>
    public const string CommandLine = "command line";
}

/// <summary>
/// The setup checklist (<c>GET /api/v1/settings/checks</c>, this machine only): the node's cache of the checks, with
/// when they were measured; <c>?refresh=true</c> runs them again.
/// </summary>
public sealed record SetupChecks
{
    public required SetupCheck[] Checks { get; init; }

    /// <summary>When the checks were measured.</summary>
    public required DateTimeOffset CheckedAt { get; init; }
}

/// <summary>One item of the setup checklist: whether it is done, in a line, and the fix the node can make, if any.</summary>
public sealed record SetupCheck
{
    /// <summary>Its name to <c>setup node</c>: <c>autostart</c>, <c>firewall</c>.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required bool Ok { get; init; }

    /// <summary>How it stands, or what a person does about it.</summary>
    public required string Line { get; init; }

    /// <summary>The button that fixes it (<c>setup node &lt;key&gt;</c>), or null when it is done or only a person can do it.</summary>
    public string? Fix { get; init; }

    /// <summary>Whether it is nice to have rather than needed.</summary>
    public bool Optional { get; init; }

    /// <summary>What reading it runs or asks (<c>runs netsh</c>, <c>asks GitHub</c>), for a page's "checking…" line; null when it only reads files.</summary>
    public string? Runs { get; init; }
}
