using System.Diagnostics;
using System.Text;

namespace Pilot.Node.Flights;

/// <summary>GhCli's one way to run gh for text, with GitHub's budget asked first and told after (#1403).</summary>
public sealed partial class GhCli
{
    private static async Task<(int Exit, string Output, string Error)> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken, TimeSpan? timeout = null, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        var start = new ProcessStartInfo("gh")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        // While GitHub refuses, a call that spends points is answered as refused here, with no gh started (#1403).
        if (NodeTraffic.Shared.GhBudget.Holds(args) is { } held)
        {
            return (1, "", held);
        }
        var (process, counted) = await NodeTraffic.Shared.StartAsync("gh", start, cancellationToken: cancellationToken, caller: $"GhCli.{caller}");
        using var _ = process;
        using var __ = counted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout ?? Timeout);
        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        await process.WaitForExitAsync(budget.Token);
        var said = await error;
        if (NodeTraffic.Shared.GhBudget.Saw(args, process.ExitCode, said))
        {
            // When the points come back, from GitHub: the refusal then holds until then, not a guess.
            await ReadBudgetAsync(cancellationToken);
        }
        return (process.ExitCode, await output, said);
    }

    /// <summary>
    /// Asks GitHub what is left of the GraphQL budget and when it resets (<c>gh api rate_limit</c>, which costs no point) and writes it to
    /// <see cref="NodeTraffic.GhBudget"/>; false when gh could not say, a gh that timed out included (#1403).
    /// </summary>
    internal static async Task<bool> ReadBudgetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exit, output, _) = await RunAsync(["api", "rate_limit", "--jq", ".resources.graphql | [.remaining, .limit, .reset] | @tsv"], AppContext.BaseDirectory, cancellationToken);
            if (exit == 0 && output.Trim().Split('\t') is [var left, var limit, var reset]
                && int.TryParse(left, out var remaining) && int.TryParse(limit, out var of) && long.TryParse(reset, out var epoch))
            {
                NodeTraffic.Shared.GhBudget.Observe(remaining, of, DateTimeOffset.FromUnixTimeSeconds(epoch));
                return true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // RunAsync's own time budget ran out: gh hung, which says nothing of the budget.
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        return false;
    }
}
