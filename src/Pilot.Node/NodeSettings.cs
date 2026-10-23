using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// The node's settings file, <c>settings.json</c> in its data folder: what the Settings page and <c>set node</c> save,
/// read at every start. The command line (and the environment it stands for) wins over it, so an autostart entry or a
/// test can still pin a value; a key the file leaves out takes the default. A value the file holds that does not read
/// is passed over, never fatal: a node that would not start over a typo in a settings file is worse than one that
/// ignores the typo.
/// </summary>
public sealed record NodeSettings
{
    public const string FileName = "settings.json";

    public const string LanKey = "lan";
    public const string LanPortKey = "lan-port";
    public const string MachineKey = "machine";
    public const string RepoRootsKey = "repo-roots";
    public const string UpdatePolicyKey = "update-policy";
    public const string UpdateChannelKey = "update-channel";
    public const string MaxJobsKey = "max-jobs";
    public const string JobMemoryKey = "job-memory";
    public const string MergeMethodKey = "merge-method";

    /// <summary>How a flight's PR is merged when the merge names no method: rebase keeps every commit's subject, which a
    /// repo's docs may cite (tianwen's rule); squash makes one commit of them; merge adds a merge commit.</summary>
    public static IReadOnlyList<string> MergeMethods { get; } = ["rebase", "squash", "merge"];

    /// <summary>Every key, in the order the Settings page shows them: the node's own, then a model and an effort per role.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [LanKey, MachineKey, RepoRootsKey, UpdateChannelKey, UpdatePolicyKey, MaxJobsKey, JobMemoryKey, LanPortKey, MergeMethodKey,
         .. RoleModels.Roles.Select(RoleModels.ModelKey), .. RoleModels.Roles.Select(RoleModels.EffortKey)];

    /// <summary>Whether <paramref name="key"/> is a role's model or effort (<see cref="RoleModels"/>).</summary>
    public static bool IsRoleKey(string key) => RoleModels.Roles.Any(r => key == RoleModels.ModelKey(r) || key == RoleModels.EffortKey(r));

    public bool? Lan { get; init; }

    public int? LanPort { get; init; }

    public string? Machine { get; init; }

    public IReadOnlyList<string>? RepoRoots { get; init; }

    public UpdatePolicy? UpdatePolicy { get; init; }

    public UpdateChannel? UpdateChannel { get; init; }

    public int? MaxJobs { get; init; }

    public double? JobMemoryGigabytes { get; init; }

    public string? MergeMethod { get; init; }

    /// <summary>The roles' models and efforts by key (<c>worker-model</c>: <c>opus</c>); a key left out takes the role's default.</summary>
    public IReadOnlyDictionary<string, string>? Roles { get; init; }

    public static string PathIn(string dataFolder) => Path.Combine(dataFolder, FileName);

    /// <summary>The file at <paramref name="file"/>; none, or one that is not a JSON object, is no settings at all.</summary>
    public static NodeSettings Read(string file)
    {
        JsonObject? root;
        try
        {
            root = File.Exists(file)
                ? JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            root = null;
        }
        if (root is null)
        {
            return new NodeSettings();
        }
        var settings = new NodeSettings();
        foreach (var key in Keys)
        {
            if (root[key] is { } value && Values(value) is { } values && With(settings, key, values) is { Settings: { } read })
            {
                settings = read;
            }
        }
        return settings;
    }

    /// <summary>Writes the file whole, through a temporary file, so a reader never sees half of it.</summary>
    public void Write(string file)
    {
        var root = new JsonObject();
        foreach (var key in Keys)
        {
            if (Get(key) is { } values)
            {
                root[key] = key switch
                {
                    LanKey => JsonValue.Create(Lan),
                    LanPortKey => JsonValue.Create(LanPort),
                    MaxJobsKey => JsonValue.Create(MaxJobs),
                    JobMemoryKey => JsonValue.Create(JobMemoryGigabytes),
                    RepoRootsKey => new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]),
                    _ => JsonValue.Create(values[0]),
                };
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = file + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>The value the file holds for <paramref name="key"/>, as <c>set node</c> spells it; null when it holds none.</summary>
    public IReadOnlyList<string>? Get(string key) => key switch
    {
        LanKey => Lan is { } lan ? [lan ? "on" : "off"] : null,
        LanPortKey => LanPort is { } port ? [port.ToString(CultureInfo.InvariantCulture)] : null,
        MachineKey => Machine is { } machine ? [machine] : null,
        RepoRootsKey => RepoRoots,
        UpdatePolicyKey => UpdatePolicy is { } policy ? [policy.Name()] : null,
        UpdateChannelKey => UpdateChannel is { } channel ? [channel.Name()] : null,
        MaxJobsKey => MaxJobs is { } jobs ? [jobs.ToString(CultureInfo.InvariantCulture)] : null,
        JobMemoryKey => JobMemoryGigabytes is { } memory ? [memory.ToString(CultureInfo.InvariantCulture)] : null,
        MergeMethodKey => MergeMethod is { } merge ? [merge] : null,
        _ when IsRoleKey(key) && Roles?.GetValueOrDefault(key) is { } role => [role],
        _ => null,
    };

    /// <summary>
    /// These settings with <paramref name="key"/> set to <paramref name="values"/> (none, or <c>default</c>, takes it
    /// out), or why not: an unknown key, or a value it does not take.
    /// </summary>
    public static (NodeSettings? Settings, string? Error) With(NodeSettings settings, string key, IReadOnlyList<string> values)
    {
        var given = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
        var clear = given.Length == 0 || given is ["default"];
        string? One() => given.Length == 1 ? given[0] : null;
        switch (key)
        {
            case LanKey:
                if (clear)
                {
                    return (settings with { Lan = null }, null);
                }
                return One()?.ToLowerInvariant() switch
                {
                    "on" or "true" or "yes" => (settings with { Lan = true }, null),
                    "off" or "false" or "no" => (settings with { Lan = false }, null),
                    _ => (null, "lan takes on or off"),
                };
            case LanPortKey:
                if (clear)
                {
                    return (settings with { LanPort = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
                    ? (settings with { LanPort = port }, null)
                    : (null, "lan-port takes a TCP port, 1 to 65535");
            case MachineKey:
                if (clear)
                {
                    return (settings with { Machine = null }, null);
                }
                return MachinePrefixes.Normalise(One()) is { } prefix
                    ? (settings with { Machine = prefix }, null)
                    : (null, "machine takes two letters and a number, as in DE1");
            case RepoRootsKey:
                if (clear)
                {
                    return (settings with { RepoRoots = null }, null);
                }
                var roots = given.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return roots.FirstOrDefault(r => !Directory.Exists(r)) is { } missing
                    ? (null, $"{missing} is not a folder on this machine")
                    : (settings with { RepoRoots = roots }, null);
            case UpdatePolicyKey:
                if (clear)
                {
                    return (settings with { UpdatePolicy = null }, null);
                }
                return UpdatePolicies.Parse(One()) is { } policy
                    ? (settings with { UpdatePolicy = policy }, null)
                    : (null, $"update-policy takes {string.Join(", ", UpdatePolicies.Names)}");
            case UpdateChannelKey:
                if (clear)
                {
                    return (settings with { UpdateChannel = null }, null);
                }
                return UpdateChannels.Parse(One()) is { } channel
                    ? (settings with { UpdateChannel = channel }, null)
                    : (null, $"update-channel takes {string.Join(" or ", UpdateChannels.Names)}");
            case MaxJobsKey:
                if (clear)
                {
                    return (settings with { MaxJobs = null }, null);
                }
                return int.TryParse(One(), NumberStyles.None, CultureInfo.InvariantCulture, out var jobs) && jobs is >= 1 and <= 64
                    ? (settings with { MaxJobs = jobs }, null)
                    : (null, "max-jobs takes a number, 1 to 64");
            case JobMemoryKey:
                if (clear)
                {
                    return (settings with { JobMemoryGigabytes = null }, null);
                }
                return double.TryParse(One(), NumberStyles.Float, CultureInfo.InvariantCulture, out var memory) && memory is >= 0 and <= 1024
                    ? (settings with { JobMemoryGigabytes = memory }, null)
                    : (null, "job-memory takes the free memory in GB a job waits for, 0 to not wait");
            case MergeMethodKey:
                if (clear)
                {
                    return (settings with { MergeMethod = null }, null);
                }
                return One()?.ToLowerInvariant() is { } merge && MergeMethods.Contains(merge)
                    ? (settings with { MergeMethod = merge }, null)
                    : (null, $"merge-method takes {string.Join(", ", MergeMethods)}");
            case var role when IsRoleKey(role):
            {
                var roles = new Dictionary<string, string>(settings.Roles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                if (clear)
                {
                    roles.Remove(role);
                    return (settings with { Roles = roles.Count == 0 ? null : roles }, null);
                }
                var value = One()?.ToLowerInvariant();
                var effort = role.EndsWith("-effort", StringComparison.Ordinal);
                if (value is null || (value != RoleModels.NoneValue && (effort ? !RoleModels.Efforts.Contains(value) : !RoleModels.IsModel(value))))
                {
                    return (null, effort
                        ? $"{role} takes {string.Join(", ", RoleModels.Efforts)} or none"
                        : $"{role} takes a model (sonnet, opus, haiku, or a full id as claude-opus-5-5) or none");
                }
                roles[role] = value;
                return (settings with { Roles = roles }, null);
            }
            default:
                return (null, $"there is no setting {key}; there are {string.Join(", ", Keys)}");
        }
    }

    // A file's value as set node's words: a list's items, or the one value as text.
    private static string[]? Values(JsonNode value) => value switch
    {
        JsonArray array => [.. array.Select(v => v is JsonValue item && item.TryGetValue<string>(out var text) ? text : "").Where(v => v.Length > 0)],
        JsonValue v when v.TryGetValue<bool>(out var flag) => [flag ? "on" : "off"],
        JsonValue v when v.TryGetValue<string>(out var text) => [text],
        JsonValue v => [v.ToJsonString()],
        _ => null,
    };
}

/// <summary>The settings file laid under a node's command line.</summary>
public static class NodeSettingsMerge
{
    /// <summary>
    /// <paramref name="options"/> (the command line's, its defaults filled in) with every setting the command line did
    /// not give (<paramref name="given"/>) taken from <paramref name="saved"/> where it holds one.
    /// </summary>
    public static NodeOptions Over(this NodeSettings saved, NodeOptions options, IReadOnlySet<string> given)
    {
        bool Free(string key) => !given.Contains(key);
        return options with
        {
            Lan = Free(NodeSettings.LanKey) && saved.Lan is { } lan ? lan : options.Lan,
            LanPort = Free(NodeSettings.LanPortKey) && saved.LanPort is { } port ? port : options.LanPort,
            MachinePrefix = Free(NodeSettings.MachineKey) && saved.Machine is { } machine ? machine : options.MachinePrefix,
            RepoRootsGiven = Free(NodeSettings.RepoRootsKey) && saved.RepoRoots is { Count: > 0 } roots ? roots : options.RepoRootsGiven,
            UpdatePolicy = Free(NodeSettings.UpdatePolicyKey) && saved.UpdatePolicy is { } policy ? policy : options.UpdatePolicy,
            UpdateChannel = Free(NodeSettings.UpdateChannelKey) && saved.UpdateChannel is { } channel ? channel : options.UpdateChannel,
            MaxJobs = Free(NodeSettings.MaxJobsKey) && saved.MaxJobs is { } jobs ? jobs : options.MaxJobs,
            JobMemoryGigabytes = Free(NodeSettings.JobMemoryKey) && saved.JobMemoryGigabytes is { } memory ? memory : options.JobMemoryGigabytes,
            Saved = saved,
            CommandLineKeys = given,
        };
    }
}
