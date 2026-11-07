using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// The names a cloud bench gives the Azure resources it makes (#823). Every name starts with <see cref="Prefix"/>, which holds the job's id and
/// ends in a dash, so the sweep after a bench finds what is left by the prefix alone and never takes another job's (<c>j7-</c> is not <c>j71-</c>).
/// </summary>
internal sealed record CloudBenchNames(string Id, string Prefix, string Vm, string Nic, string PublicIp, string Nsg, string Vnet, string Disk)
{
    public static CloudBenchNames For(string jobId)
    {
        var id = new string([.. jobId.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(20)]);
        id = id.Length > 0 ? id : "x";
        var prefix = $"pilot-bench-{id}-";
        var vm = prefix + "vm";
        // az vm create names the NIC after the VM; everything else is named here, so a delete never has to guess.
        return new CloudBenchNames(id, prefix, vm, vm + "VMNic", prefix + "ip", prefix + "nsg", prefix + "vnet", prefix + "disk");
    }

    /// <summary>The ref (inside the repo, under its own namespace) that holds a commit while the bundle is made, since a bundle needs refs and not bare commits.</summary>
    public string TempRef(string label) => $"refs/pilot-bench/{Id}/{label}";
}

/// <summary>
/// The script that runs on the VM (<c>bash</c>, written with LF line endings whatever the node's OS): the same per-ref sections as
/// <see cref="BenchRun"/>'s script, with the clone from the git bundle, the build and the copying of results added. One ref failing does not
/// skip the next, and the script exits with the last failing code (255, which is ssh's own, is 254).
/// </summary>
internal static class CloudBenchScript
{
    /// <summary>The SDK the repos target (<c>net10.0</c>); installed when the image has none.</summary>
    public const string DotnetChannel = "10.0";

    public const string BundleFile = "bench.bundle";

    public const string ScriptFile = "bench.sh";

    /// <summary>The folder (in the VM user's home) each ref's results are gathered into, one folder per label, copied back by scp.</summary>
    public const string OutFolder = "out";

    public const string SdkInstallFailed = "=== the .NET SDK could not be installed, no benchmark was run ===";

    public const string CloneFailed = "=== the ref could not be cloned from the bundle, the benchmark was not run ===";

    public const string BuildFailed = "=== the build failed, the benchmark was not run ===";

    // Builds the one solution at the root or under src, in Release; with none, the benchmark command builds what it needs.
    private const string AutoBuild =
        "sln=$(ls -1 ./*.slnx ./*.sln src/*.slnx src/*.sln 2>/dev/null | head -n 1); if [ -n \"$sln\" ]; then dotnet build \"$sln\" -c Release --nologo -v:m; else true; fi";

    public static string For(CloudBenchRequest request, CloudBenchNames names)
    {
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append('\n');

        Line("#!/usr/bin/env bash");
        Line($"# The pilot node's cloud bench {request.JobId}: runs on the spot VM, one section per ref.");
        Line("set -u");
        Line("cd \"$HOME\" || exit 1");
        Line("export DEBIAN_FRONTEND=noninteractive DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_ROOT=\"$HOME/.dotnet\"");
        Line("export PATH=\"$HOME/.dotnet:$PATH\"");
        // The tools the clone and the SDK need, when the image has not got them.
        Line("if ! command -v git >/dev/null || ! command -v curl >/dev/null || ! ldconfig -p | grep -q libicuuc; then");
        Line("  sudo apt-get update -qq && sudo apt-get install -y -qq git curl ca-certificates libicu-dev");
        Line("fi");
        Line($"if ! dotnet --list-sdks 2>/dev/null | grep -q '^{DotnetChannel.Split('.')[0]}\\.'; then");
        Line("  echo '=== installing the .NET SDK ==='");
        Line($"  if ! {{ curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && bash dotnet-install.sh --channel {DotnetChannel} --install-dir \"$HOME/.dotnet\" --no-path; }}; then");
        Line($"    echo {Sh(SdkInstallFailed)}");
        Line("    exit 1");
        Line("  fi");
        Line("fi");
        Line("bad=0");

        var command = ShellLine.Join(request.Command, windows: false);
        var build = request.BuildCommand switch
        {
            null => AutoBuild,
            { Length: 0 } => "true",
            var given => given,
        };
        foreach (var leg in request.Refs)
        {
            var dir = $"$HOME/bench/{leg.Label}";
            var gathered = $"$HOME/{OutFolder}/{leg.Label}";
            Line(string.Empty);
            Line($"echo {Sh(BenchMarkers.Start(leg.Label, leg.Ref, leg.Sha))}");
            Line($"rm -rf \"{dir}\" \"{gathered}\"; mkdir -p \"{dir}\" \"{gathered}\"");
            Line($"if git init -q \"{dir}\" && git -C \"{dir}\" fetch -q \"$HOME/{BundleFile}\" {Sh(names.TempRef(leg.Label))} && git -C \"{dir}\" checkout -q --detach {Sh(leg.Sha)}; then");
            Line($"  cd \"{dir}\"");
            Line("  if {");
            Line(build);
            Line("  }; then");
            Line("    if {");
            Line(request.Fetch is { Length: > 0 } ? request.Fetch : "true");
            Line("    }; then");
            // A subshell, so an exit in the command ends that ref only.
            Line("      (");
            Line(command);
            Line("      )");
            Line("      code=$?; [ $code -ne 0 ] && bad=$code");
            Line($"    else echo {Sh(BenchMarkers.FetchFailed)}; bad=1; fi");
            Line($"  else echo {Sh(BuildFailed)}; bad=1; fi");
            Line($"else echo {Sh(CloneFailed)}; bad=1; fi");
            Line("cd \"$HOME\"");
            // Every BenchmarkDotNet results folder, wherever in the clone the command left it, and the paths the bench named.
            Line($"( cd \"{dir}\" && find . -path ./.git -prune -o -type d -path '*/BenchmarkDotNet.Artifacts/results' -print0 | xargs -0 -r -I{{}} cp -r --parents {{}} \"{gathered}/\" ) 2>/dev/null");
            foreach (var path in request.ResultPaths)
            {
                Line($"( cd \"{dir}\" && [ -e {Sh(path)} ] && cp -r --parents {Sh(path)} \"{gathered}/\" ) 2>/dev/null");
            }
        }
        Line(string.Empty);
        Line("[ \"$bad\" -gt 254 ] && bad=254");
        Line("exit \"$bad\"");
        return text.ToString();
    }

    /// <summary>
    /// A path a bench may name to be copied back: relative, inside the clone, with nothing that could be read as an option, a glob or a
    /// quote. Null when it is fine, else why not.
    /// </summary>
    public static string? WhyNotAResultPath(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.StartsWith('-') || path.StartsWith('~') || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
        {
            return $"{path}: a result path is relative to the clone's root";
        }
        if (path.Split('/', '\\').Contains(".."))
        {
            return $"{path}: a result path stays inside the clone";
        }
        if (path.Any(c => char.IsControl(c) || c is '\'' or '"' or '`' or '$' or '*' or '?' or '\\'))
        {
            return $"{path}: a result path is plain text, with no quote, glob or backslash";
        }
        return null;
    }

    /// <summary>Text as one single-quoted word of <c>sh</c>.</summary>
    private static string Sh(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
