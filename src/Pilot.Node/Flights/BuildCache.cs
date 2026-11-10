using System.Diagnostics;

namespace Pilot.Node.Flights;

/// <summary>
/// A job in a plane builds through a content-keyed build cache while <c>job-build-cache</c> is on (PIL860, a trial): Microsoft.MSBuildCache.Local
/// keys each project's outputs on what its build read, so a fresh plane gets the unchanged projects of another plane or of main from
/// the cache. <c>docs/build-cache.md</c> has the trial: the cache does not run under <c>dotnet build</c> (it needs
/// <c>-reportfileaccesses</c>, which only Visual Studio's x64 <c>MSBuild.exe</c> has), so each <c>dotnet build</c> segment of a
/// job's command becomes a <c>dotnet restore</c> and that MSBuild with the trial's flags (<see cref="With"/>). Pure: the MSBuild path and the
/// cache root are given, so a test reads the result without a machine that has Visual Studio. Off by default, and a no-op outside a plane.
/// </summary>
public static class BuildCache
{
    public const string EnabledProperty = "MSBuildCacheEnabled";
    public const string RootProperty = "MSBuildCacheLocalCacheRootPath";

    /// <summary>The folder of the node's data folder the cache lives in.</summary>
    public const string FolderName = "build-cache";

    /// <summary>The cache root under the node's data folder.</summary>
    public static string RootIn(string dataFolder) => Path.Combine(dataFolder, FolderName);

    /// <summary>The variables a job run in <paramref name="cwd"/> gets when the setting is on: none for a job outside a plane, or with the setting off.</summary>
    public static IReadOnlyDictionary<string, string> Variables(bool enabled, string cwd, string cacheRoot) =>
        enabled && PlaneOutput.PlaneOf(cwd) is not null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EnabledProperty] = "true",
                [RootProperty] = cacheRoot,
            }
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="command"/> with each <c>dotnet build [project] [-c config]</c> segment rewritten to the trial's restore and cached
    /// MSBuild build. A segment is left alone when it already carries the cache flag, when it says <c>--no-build</c>, or is no
    /// <c>dotnet build</c> (a <c>dotnet test</c> too: the trial found no cached form of it). The whole command is returned as it is
    /// when <paramref name="msBuild"/> is null (no Visual Studio here), outside a plane, or with the setting off.
    /// </summary>
    public static string With(string command, bool enabled, string cwd, string cacheRoot, string? msBuild)
    {
        if (!enabled || msBuild is null || PlaneOutput.PlaneOf(cwd) is null)
        {
            return command;
        }
        var result = command;
        var cursor = 0;
        foreach (var segment in CheckPaths.Segments(command))
        {
            var at = result.IndexOf(segment, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            var rewritten = Rewrite(segment, cacheRoot, msBuild);
            result = string.Concat(result.AsSpan(0, at), rewritten, result.AsSpan(at + segment.Length));
            cursor = at + rewritten.Length;
        }
        return result;
    }

    private static string Rewrite(string segment, string cacheRoot, string msBuild)
    {
        var tokens = CheckPaths.Tokenize(segment);
        var start = tokens.Count > 0 && tokens[0].Value == "&" ? 1 : 0;
        if (tokens.Count < start + 2
            || !Path.GetFileNameWithoutExtension(tokens[start].Value).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || !tokens[start + 1].Value.Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            return segment;
        }
        string? project = null;
        string? configuration = null;
        var noRestore = false;
        var extra = new List<string>();
        for (var i = start + 2; i < tokens.Count; i++)
        {
            var value = tokens[i].Value;
            if (value.Equals("--no-build", StringComparison.OrdinalIgnoreCase) || value.Contains(EnabledProperty, StringComparison.OrdinalIgnoreCase))
            {
                return segment;
            }
            if (value.Equals("-c", StringComparison.OrdinalIgnoreCase) || value.Equals("--configuration", StringComparison.OrdinalIgnoreCase))
            {
                configuration = i + 1 < tokens.Count ? tokens[++i].Value : null;
            }
            else if (value.StartsWith("--configuration=", StringComparison.OrdinalIgnoreCase))
            {
                configuration = value["--configuration=".Length..];
            }
            else if (value.Equals("--no-restore", StringComparison.OrdinalIgnoreCase))
            {
                noRestore = true;
            }
            else if (value.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("-property:", StringComparison.OrdinalIgnoreCase))
            {
                extra.Add(tokens[i].Raw);
            }
            else if (!value.StartsWith('-') && project is null)
            {
                project = tokens[i].Raw;
            }
        }
        if (configuration is not null && (configuration.Contains('\'') || configuration.Any(char.IsWhiteSpace)))
        {
            return segment;
        }
        var cacheOn = $"-p:{EnabledProperty}=true";
        var config = $"-p:Configuration={configuration ?? "Debug"}";
        var shared = string.Join(' ', new[] { config, cacheOn }.Concat(extra));
        var build = string.Join(' ', new[]
        {
            "&", Quote(msBuild), project, "-graph -reportfileaccesses -m -nologo -v:m -clp:Summary", shared,
            "-p:MSBuildCacheAsyncCacheMaterialization=false",
            @"'-p:MSBuildCacheIdenticalDuplicateOutputPatterns=**\*'",
            Quote($"-p:{RootProperty}={cacheRoot}"),
            Quote($"-p:MSBuildCacheLogDirectory={Path.Combine(cacheRoot, "logs")}"),
        }.Where(s => !string.IsNullOrEmpty(s)));
        if (noRestore)
        {
            return build;
        }
        // The package is in a project's assets only if the restore saw the cache on, so it restores first, as its own step.
        var restore = string.Join(' ', new[] { "dotnet restore", project, shared }.Where(s => !string.IsNullOrEmpty(s)));
        return $"{restore} && {build}";
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    private static string? _located;
    private static bool _looked;

    /// <summary>
    /// Visual Studio's x64 <c>MSBuild.exe</c> through vswhere, or null off Windows or with none installed; asked once. Starts a real
    /// process, so a test gives <see cref="JobRunner.MsBuild"/> its own answer.
    /// </summary>
    public static string? LocateMsBuild()
    {
        if (_looked)
        {
            return _located;
        }
        _looked = true;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere))
            {
                return null;
            }
            var start = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\amd64\MSBuild.exe" })
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            _located = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _located = null;
        }
        return _located;
    }
}
