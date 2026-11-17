using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// Which runner <c>dotnet test</c> uses in a folder: Microsoft.Testing.Platform when the nearest <c>global.json</c>
/// (<c>test.runner</c>) or <c>dotnet.config</c> (<c>[dotnet.test.runner] name</c>) up the tree says so, VSTest otherwise.
/// Read from files only. <c>leg plan</c> refuses a check whose switches belong to the other runner (pilot #1132), since the
/// SDK only fails it at the recheck, long after the plan.
/// </summary>
public static partial class TestRunner
{
    /// <summary>The name a config file gives the Microsoft.Testing.Platform runner.</summary>
    public const string PlatformName = "Microsoft.Testing.Platform";

    /// <summary>Whether <paramref name="folder"/> runs Microsoft.Testing.Platform (nearest selecting file wins; VSTest otherwise).</summary>
    public static bool IsPlatform(string folder)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(folder)); dir is not null; dir = dir.Parent)
        {
            var global = Path.Combine(dir.FullName, "global.json");
            if (File.Exists(global) && FromGlobalJson(global) is { } byGlobal)
            {
                return byGlobal;
            }
            var config = Path.Combine(dir.FullName, "dotnet.config");
            if (File.Exists(config) && FromDotnetConfig(config) is { } byConfig)
            {
                return byConfig;
            }
        }
        return false;
    }

    /// <summary>The runner's name for <paramref name="folder"/>, as the refusal says it.</summary>
    public static string Of(string folder) => IsPlatform(folder) ? PlatformName : "VSTest";

    // null: the file says nothing about the runner (or is malformed), so the search goes on up.
    private static bool? FromGlobalJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String)
            {
                return string.Equals(runner.GetString(), PlatformName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static bool? FromDotnetConfig(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.TrimEnd().Equals("[dotnet.test.runner]", StringComparison.OrdinalIgnoreCase);
            }
            else if (inSection && NameLine().Match(line) is { Success: true } m)
            {
                return string.Equals(m.Groups[1].Value.Trim(), PlatformName, StringComparison.OrdinalIgnoreCase);
            }
        }
        return null;
    }

    [GeneratedRegex("""^name\s*=\s*"?([^"#;]*)"?\s*(?:[#;].*)?$""", RegexOptions.IgnoreCase)]
    private static partial Regex NameLine();

    /// <summary>
    /// The refusal for the first <c>dotnet test</c> segment of <paramref name="check"/> whose switches belong to the other runner than
    /// the one its folder (the plane, after any leading <c>cd</c>) selects: the switch, the runner and the form that works. Null when none.
    /// </summary>
    public static string? Refusal(string check, string plane)
    {
        if (!Directory.Exists(plane))
        {
            return null;
        }
        string? here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plane));
        foreach (var segment in CheckPaths.Segments(check))
        {
            var tokens = CheckPaths.Tokenize(segment);
            if (tokens.Count == 0 || here is null)
            {
                continue;
            }
            if (tokens[0].Value.ToLowerInvariant() is "cd" or "chdir" or "pushd" or "set-location" or "sl")
            {
                var target = tokens.Skip(1).FirstOrDefault(t => !(t.Value.StartsWith('-') && t.Value.Length > 1) && !t.Value.Equals("/d", StringComparison.OrdinalIgnoreCase));
                if (target.Value is null)
                {
                    continue;
                }
                here = target.Value.IndexOfAny(['$', '*', '?', '%', '`', '{', '~', '<', '>', '@']) >= 0 || target.Value == "-"
                    ? null
                    : Path.GetFullPath(target.Value, here);
                continue;
            }
            var start = tokens[0].Value == "&" ? 1 : 0;
            if (tokens.Count < start + 2
                || !Path.GetFileNameWithoutExtension(tokens[start].Value).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || !tokens[start + 1].Value.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var platform = IsPlatform(here);
            if (TestFilters.WrongRunnerSwitch(tokens.Skip(start + 2).Select(t => t.Value), platform) is not { } wrong)
            {
                continue;
            }
            return platform
                ? $"the check `{segment.Trim()}` uses {wrong}, which is VSTest's, but the folder it runs in selects {PlatformName} (global.json or dotnet.config): " +
                  $"there use {TestFilters.Class} <Class> (or {TestFilters.Method}, {TestFilters.Namespace}, {TestFilters.Query}) and --project <proj>"
                : $"the check `{segment.Trim()}` uses {wrong}, which is a {PlatformName} switch, but the folder it runs in runs VSTest (no global.json test.runner or dotnet.config selects {PlatformName}): " +
                  $"there use dotnet test <proj> --filter 'FullyQualifiedName~...'";
        }
        return null;
    }
}
