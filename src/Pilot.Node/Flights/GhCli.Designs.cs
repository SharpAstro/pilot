using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pilot.Node.Flights;

/// <summary>What a design's sync (PIL1171) asks of GitHub: a branch written as one commit, a branch read, labelled issues listed and made.</summary>
public sealed partial class GhCli
{
    public async Task<string> CommitFilesAsync(string repoPath, string branch, string message, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken)
    {
        // The tip the new commit continues from; none for a branch that is not there yet, whose first commit is an orphan.
        string? tip = null;
        var (tipExit, tipOutput, tipError) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/git/ref/heads/{branch}", "--jq", ".object.sha"], repoPath, cancellationToken);
        var found = ParseFound(tipExit, tipError) ?? throw new FlightException($"gh could not ask GitHub for the branch {branch}: {tipError.Trim()}");
        if (found)
        {
            tip = tipOutput.Trim();
        }

        var entries = new List<(string Path, string Sha)>();
        foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var sha = await ApiShaAsync("git/blobs", repoPath, w => { w.WriteString("content", Convert.ToBase64String(bytes)); w.WriteString("encoding", "base64"); }, cancellationToken);
            entries.Add((path, sha));
        }
        // No base tree: the commit holds exactly these files.
        var tree = await ApiShaAsync("git/trees", repoPath, w =>
        {
            w.WriteStartArray("tree");
            foreach (var (path, sha) in entries)
            {
                w.WriteStartObject();
                w.WriteString("path", path);
                w.WriteString("mode", "100644");
                w.WriteString("type", "blob");
                w.WriteString("sha", sha);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, cancellationToken);
        var commit = await ApiShaAsync("git/commits", repoPath, w =>
        {
            w.WriteString("message", message);
            w.WriteString("tree", tree);
            w.WriteStartArray("parents");
            if (tip is { Length: > 0 })
            {
                w.WriteStringValue(tip);
            }
            w.WriteEndArray();
        }, cancellationToken);
        if (tip is null)
        {
            await ApiShaAsync("git/refs", repoPath, w => { w.WriteString("ref", $"refs/heads/{branch}"); w.WriteString("sha", commit); }, cancellationToken);
        }
        else
        {
            var (exit, output, error) = await RunBytesAsync(["api", "-X", "PATCH", $"repos/{{owner}}/{{repo}}/git/refs/heads/{branch}", "--input", "-"], repoPath,
                Json(w => { w.WriteString("sha", commit); w.WriteBoolean("force", false); }), cancellationToken);
            if (exit != 0)
            {
                throw new FlightException($"gh could not move {branch} to the new commit: {(error.Trim().Length > 0 ? error : Encoding.UTF8.GetString(output)).Trim()}");
            }
        }
        return commit;
    }

    public async Task<IReadOnlyDictionary<string, byte[]>?> BranchFilesAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/git/trees/{branch}?recursive=1", "--jq", ".tree[] | select(.type == \"blob\") | .path"], repoPath, cancellationToken);
        if (ParseFound(exit, error) is false)
        {
            return null;
        }
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the files of {branch}: {error.Trim()}");
        }
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (readExit, bytes, readError) = await RunBytesAsync(
                ["api", "-H", "Accept: application/vnd.github.raw", $"repos/{{owner}}/{{repo}}/contents/{path}?ref={branch}"], repoPath, null, cancellationToken);
            files[path] = readExit == 0 ? bytes : throw new FlightException($"gh could not read {path} on {branch}: {readError.Trim()}");
        }
        return files;
    }

    public async Task<MilestoneIssue[]> LabelledIssuesAsync(string repoPath, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "list", "--label", label, "--state", "open", "--limit", "200", "--json", IssueFields], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the open issues labelled {label}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray().Select(ParseIssue).OrderByDescending(i => i.Number)];
    }

    public async Task<MilestoneIssue> CreateLabelledIssueAsync(string repoPath, string title, string body, string label, CancellationToken cancellationToken)
    {
        await EnsureLabelAsync(repoPath, label, cancellationToken);
        var (exit, output, error) = await RunAsync(["issue", "create", "--title", title, "--body", body, "--label", label], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not create the issue: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (!int.TryParse(url[(url.LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new FlightException($"gh created the issue but did not say which: {output.Trim()}");
        }
        return await IssueNowAsync(repoPath, number, cancellationToken)
            ?? new MilestoneIssue(number, title, body, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "", url, [label]);
    }
}
