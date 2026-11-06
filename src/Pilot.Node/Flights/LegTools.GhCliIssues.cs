using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

public sealed partial class GhCli
{
    public async Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "list", "--state", "open", "--limit", "200", "--json", "number,title,labels,updatedAt"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the issues: {error.Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray()
            .Select(i => new IssueInfo
            {
                Number = i.GetProperty("number").GetInt32(),
                Title = i.GetProperty("title").GetString() ?? "",
                Labels = i.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? [.. labels.EnumerateArray().Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>()]
                    : [],
                UpdatedAt = i.TryGetProperty("updatedAt", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null,
            })
            .OrderByDescending(i => i.UpdatedAt)];
    }

    /// <summary>A search that runs to more pages than this is cut short: 40 pages are 1200 open PRs.</summary>
    private const int MostPages = 40;

    public async Task CloseIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "close", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reason", "completed", "--comment", comment], repoPath, cancellationToken);
        // gh says so, and exits 0, for an issue closed already; anything else that failed is said.
        if (exit != 0)
        {
            throw new FlightException($"gh could not close issue #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<IssueInfo> CreateIssueAsync(string repoPath, string title, string? body, CancellationToken cancellationToken)
    {
        // An empty body is given explicitly: without one gh would ask for it, and the node has no terminal.
        var (exit, output, error) = await RunAsync(["issue", "create", "--title", title, "--body", body ?? ""], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not create the issue: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        // gh prints the new issue's URL, .../issues/<n>, as its last line.
        var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return int.TryParse(url[(url.LastIndexOf('/') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? new IssueInfo { Number = number, Title = title, UpdatedAt = DateTimeOffset.UtcNow }
            : throw new FlightException($"gh created the issue but did not say which: {output.Trim()}");
    }

    public async Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["issue", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "title"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }

    public async Task<bool?> IssueClosedAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["issue", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "state"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.TryGetProperty("state", out var state) && state.GetString() is { } text ? string.Equals(text, "CLOSED", StringComparison.OrdinalIgnoreCase) : null;
    }

    public async Task SetPilotLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        var (color, description) = PilotMarks.LabelStyle(label);
        // Made when missing: a repo that has it already answers "already exists", which is the state wanted.
        var (made, madeOutput, madeError) = await RunAsync(["label", "create", label, "--color", color, "--description", description], repoPath, cancellationToken);
        if (made != 0 && !(madeError + madeOutput).Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"GitHub refused the label {label} on the repo: {(madeError.Trim().Length > 0 ? madeError : madeOutput).Trim()}");
        }
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (read, readOutput, readError) = await RunAsync(["pr", "view", text, "--json", "labels", "-q", ".labels[].name"], repoPath, cancellationToken);
        if (read != 0)
        {
            throw new FlightException($"gh could not read #{number}'s labels: {(readError.Trim().Length > 0 ? readError : readOutput).Trim()}");
        }
        var args = new List<string> { "pr", "edit", text, "--add-label", label };
        foreach (var other in readOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(l => l.StartsWith(PilotMarks.LabelPrefix, StringComparison.Ordinal) && l != label))
        {
            args.AddRange(["--remove-label", other]);
        }
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the label {label} on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task RemoveLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "edit", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--remove-label", label], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused to take the label {label} off #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<string[]> IssueLabelsAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["issue", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "labels", "-q", ".labels[].name"], repoPath, cancellationToken);
        return exit != 0 ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task EnsureLabelAsync(string repoPath, string label, CancellationToken cancellationToken)
    {
        var (color, description) = PilotMarks.LabelStyle(label);
        var (made, madeOutput, madeError) = await RunAsync(["label", "create", label, "--color", color, "--description", description], repoPath, cancellationToken);
        if (made != 0 && !(madeError + madeOutput).Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"GitHub refused the label {label} on the repo: {(madeError.Trim().Length > 0 ? madeError : madeOutput).Trim()}");
        }
    }

    public async Task AddIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        await EnsureLabelAsync(repoPath, label, cancellationToken);
        await EditIssueAsync(repoPath, number, ["--add-label", label], label, cancellationToken);
    }

    public Task RemoveIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken) =>
        EditIssueAsync(repoPath, number, ["--remove-label", label], label, cancellationToken);

    private static async Task EditIssueAsync(string repoPath, int number, string[] change, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "edit", number.ToString(System.Globalization.CultureInfo.InvariantCulture), .. change], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the label {label} on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task CommentIssueAsync(string repoPath, int number, string body, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "comment", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--body", body], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the comment on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }
}
