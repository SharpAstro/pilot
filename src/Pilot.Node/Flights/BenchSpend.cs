using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What the calendar month's cloud benches have cost (#823), from the job rows: the cap's count, and the spend view's bench line.</summary>
public static class BenchSpend
{
    /// <summary>
    /// The month's bench VMs (UTC, as Azure bills): each ended bench's minutes and cost, and for one still running the hours it has run so far
    /// at its price (<paramref name="fallbackPrice"/> when the price is not known yet). The cap stops a bench only once the month has reached it:
    /// a bench that starts just under it can run past it, and benches handed over together each see about the same spend.
    /// </summary>
    public static SpendBench Month(IEnumerable<JobRow> jobs, DateTimeOffset now, double fallbackPrice, double? cap)
    {
        int runs = 0, evictions = 0;
        double minutes = 0, cost = 0;
        foreach (var job in jobs)
        {
            if (job is not { Kind: JobKind.Bench, BenchVm: { Size: not null } vm })
            {
                continue;
            }
            var when = (job.EndedAt ?? job.StartedAt).ToUniversalTime();
            if (when.Year != now.Year || when.Month != now.Month)
            {
                continue;
            }
            runs++;
            evictions += vm.Evictions;
            var running = job.EndedAt is null;
            minutes += vm.Minutes ?? (running ? Math.Max(0, (now - job.StartedAt).TotalMinutes) : 0);
            cost += vm.Cost ?? (running ? (vm.PricePerHour ?? fallbackPrice) * Math.Max(0, (now - job.StartedAt).TotalHours) : 0);
        }
        return new SpendBench
        {
            Month = now.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Runs = runs,
            Minutes = minutes,
            Cost = cost,
            Evictions = evictions,
            Cap = cap,
        };
    }

    /// <summary>The spend report's bench block: null when cloud benches are not set up (no cap) and none has run this month.</summary>
    public static SpendBench? For(IEnumerable<JobRow> jobs, NodeSettings settings, DateTimeOffset now)
    {
        var (cloud, _) = settings.BenchCloudOrWhy();
        var month = Month(jobs, now, cloud?.SpotPricePerHour ?? settings.BenchSpotPrice ?? NodeSettings.DefaultBenchSpotPrice, cloud?.MonthlyCap);
        return cloud is null && month.Runs == 0 ? null : month;
    }
}
