using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>A job in a line: <c>j3  running  dotnet test  (se1-tia1-parser, 4m)</c>, its step or how it ended after.</summary>
    internal static string JobLine(JobRow job)
    {
        var state = job.State.ToString().ToLowerInvariant();
        if (job.State == JobState.Running && job.PausedAt is { } pausedAt)
        {
            state = $"paused since {pausedAt.ToLocalTime():HH:mm}";
        }
        var end = job.Error is { } error ? $": {error}" : "";
        var took = (job.EndedAt ?? DateTimeOffset.UtcNow) - job.StartedAt;
        var told = job.EndedAt is not null && job.Owner is not null && !job.Told ? ", owner not told" : "";
        return $"{job.Id}  {state}{end}  {job.Command}  ({job.OwnerName ?? "terminal"}, {(int)took.TotalMinutes}m{told}){(job.Step is { } step ? $"  · {step}" : "")}";
    }

    /// <summary>A merge queue entry in a line: <c>TIA61  #63  tianwen  1  abc1234  checks  · merging when checks pass</c>: flight, PR, repo, place, head, state, then why.</summary>
    internal static string MergeLine(MergeRow merge) =>
        $"{merge.Flight}  {(merge.Pr is { } pr ? $"#{pr}" : "no PR")}  {merge.Repo}  {(merge.Place is { } place ? place.ToString(CultureInfo.InvariantCulture) : "-")}  {merge.Head[..Math.Min(8, merge.Head.Length)]}  {merge.State.ToString().ToLowerInvariant()}{(merge.Why is { } why ? $"  · {why}" : "")}";

    /// <summary>A duration as people write it: <c>90m</c>, <c>2h</c>, <c>45s</c>.</summary>
    internal static bool TryDuration(string text, out TimeSpan span)
    {
        span = default;
        if (text.Length < 2 || !long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return false;
        }
        span = text[^1] switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => default };
        return span > TimeSpan.Zero;
    }
}
