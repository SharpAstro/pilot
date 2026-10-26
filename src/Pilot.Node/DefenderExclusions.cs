using System.Text;
using System.Text.Json;

namespace Pilot.Node;

/// <summary>
/// What Windows Defender's exclusions for a build machine are, and the pure parts of reading and setting them: the state
/// as one pwsh call reports it (<see cref="Parse"/>), the folders and processes still missing from it
/// (<see cref="MissingPaths"/>, <see cref="MissingProcesses"/>) and the elevated script that adds them (<see cref="Script"/>).
/// <see cref="NodeSetup"/> runs the calls; nothing here touches the machine, so a test feeds it text.
/// </summary>
public static class DefenderExclusions
{
    /// <summary>The processes <c>--processes</c> excludes: the node itself and what a build and its tests start.</summary>
    public static readonly string[] Processes = ["pilot-node.exe", "dotnet.exe", "MSBuild.exe", "VBCSCompiler.exe", "testhost.exe"];

    /// <summary>What Get-MpPreference gives a process that is not elevated, in place of the list: Defender hides its exclusions from it.</summary>
    private const string Hidden = "N/A";

    /// <summary>
    /// Defender as one pwsh call reports it. <paramref name="Active"/>: it is the antivirus in force (running normally with real-time
    /// protection on, not passive beside another one). <paramref name="Readable"/>: the exclusions could be read, which Defender
    /// allows an elevated process only. <paramref name="DevDrives"/>: the drive letters among those asked about that are Dev Drives,
    /// which Defender's performance mode already treats.
    /// </summary>
    public sealed record State(bool Active, bool Readable, IReadOnlyList<string> Paths, IReadOnlyList<string> Processes, IReadOnlyList<string> DevDrives);

    /// <summary>
    /// The pwsh script that reports <see cref="State"/> as one JSON line, for the drive letters <paramref name="drives"/> to be asked
    /// about. A cmdlet that fails (no Defender, or another antivirus) leaves its fields empty, which reads as not active.
    /// </summary>
    public static string Query(IEnumerable<char> drives)
    {
        var letters = string.Join(',', drives.Select(d => $"'{char.ToUpperInvariant(d)}'"));
        return string.Join('\n',
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$p = Get-MpPreference",
            "$s = Get-MpComputerStatus",
            $"$dev = @(@({letters}) | Where-Object {{ $q = (fsutil devdrv query \"$($_):\") -join ' '; $q -match 'developer volume' -and $q -notmatch 'not a developer' }})",
            "[pscustomobject]@{ Paths = @($p.ExclusionPath); Processes = @($p.ExclusionProcess); Mode = [string]$s.AMRunningMode; Realtime = [bool]$s.RealTimeProtectionEnabled; DevDrives = $dev } | ConvertTo-Json -Compress");
    }

    /// <summary>The state in <see cref="Query"/>'s output, or null when it is not that JSON.</summary>
    public static State? Parse(string output)
    {
        try
        {
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                return null;
            }
            using var json = JsonDocument.Parse(output[start..(end + 1)]);
            var root = json.RootElement;
            var paths = Strings(root, "Paths");
            var processes = Strings(root, "Processes");
            var mode = root.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "";
            var realtime = root.TryGetProperty("Realtime", out var r) && r.ValueKind == JsonValueKind.True;
            return new State(
                Active: string.Equals(mode, "Normal", StringComparison.OrdinalIgnoreCase) && realtime,
                Readable: !paths.Concat(processes).Any(p => p.StartsWith(Hidden, StringComparison.OrdinalIgnoreCase)),
                Paths: paths,
                Processes: processes,
                DevDrives: Strings(root, "DevDrives"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A property that is one string, an array of them, or null (ConvertTo-Json writes a one-item array either way, but be kind).</summary>
    private static List<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return [];
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
            _ => [],
        };
    }

    /// <summary>A Windows path as it is compared: one separator, none at the end, case dropped.</summary>
    public static string Normalise(string path) => path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    /// <summary>The drive letter of a Windows path, or null for one without a drive.</summary>
    public static char? Drive(string path) => path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) : null;

    private static bool Under(string path, string folder) =>
        path == folder || path.StartsWith(folder + "\\", StringComparison.Ordinal);

    /// <summary>
    /// The folders of <paramref name="wanted"/> that <paramref name="state"/> still has to exclude: those on a Dev Drive left out, those
    /// at or under a folder already excluded left out, and those under another wanted folder left out (it covers them), each once.
    /// </summary>
    public static List<string> MissingPaths(IEnumerable<string> wanted, State state)
    {
        var excluded = state.Paths.Select(Normalise).ToList();
        var distinct = wanted.GroupBy(Normalise).Select(g => g.First()).ToList();
        return [.. distinct.Where(w =>
        {
            var n = Normalise(w);
            return !(Drive(w) is { } d && state.DevDrives.Contains(d.ToString(), StringComparer.OrdinalIgnoreCase))
                && !excluded.Any(e => Under(n, e))
                && !distinct.Any(o => !ReferenceEquals(o, w) && Normalise(o) != n && Under(n, Normalise(o)));
        })];
    }

    /// <summary>The wanted folders that sit on a Dev Drive, which the check leaves to Defender's performance mode and says so.</summary>
    public static List<string> OnDevDrive(IEnumerable<string> wanted, State state) =>
        [.. wanted.Where(w => Drive(w) is { } d && state.DevDrives.Contains(d.ToString(), StringComparer.OrdinalIgnoreCase))];

    /// <summary>The processes of <paramref name="wanted"/> not excluded yet, by name without regard to case.</summary>
    public static List<string> MissingProcesses(IEnumerable<string> wanted, State state) =>
        [.. wanted.Where(w => !state.Processes.Contains(w, StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// The elevated script: one <c>Add-MpPreference</c> line for each folder and each process, each in single quotes, which
    /// pwsh takes literally, with a quote in a name doubled. Adding one that is there changes nothing.
    /// </summary>
    public static string Script(IEnumerable<string> paths, IEnumerable<string> processes)
    {
        var script = new StringBuilder();
        foreach (var path in paths)
        {
            script.Append("Add-MpPreference -ExclusionPath ").Append(Quote(path)).Append('\n');
        }
        foreach (var process in processes)
        {
            script.Append("Add-MpPreference -ExclusionProcess ").Append(Quote(process)).Append('\n');
        }
        return script.ToString().TrimEnd('\n');
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>The dotnet root (the folder that holds <c>sdk</c>) from <c>dotnet --list-sdks</c>: <c>10.0.100 [C:\Program Files\dotnet\sdk]</c>; null when it lists none.</summary>
    public static string? SdkRoot(string listSdks)
    {
        foreach (var line in listSdks.Replace("\r", "").Split('\n'))
        {
            var open = line.IndexOf('[');
            var close = line.LastIndexOf(']');
            if (open >= 0 && close > open)
            {
                var sdk = line[(open + 1)..close].Trim().Replace('/', '\\').TrimEnd('\\');
                var cut = sdk.LastIndexOf('\\');
                if (cut > 0)
                {
                    return sdk[..cut];
                }
            }
        }
        return null;
    }
}
