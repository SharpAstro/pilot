using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// How often the node's pull request watch reads (#436): every minute only while some client says it shows the Pull requests
/// page, else every pr-watch-idle (15 minutes by default), plus once at once when a client opens the page. The clock is the test's,
/// GitHub is the fake, and the clients are the node's <see cref="PageViews"/> (or a real stream, for the one test that closes it).
/// </summary>
public sealed class PullRequestWatchPaceTests : IDisposable
{
    private const string Prs = ClientPages.PullRequests;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    // What a test needs of the watch: it, its clock, GitHub (3 points a search), its log and the pages its clients show.
    private sealed record Rig(PullRequestWatch Watch, PaceTime Time, FakeGitHub GitHub, StringWriter Log, PageViews Views, IdleBox Idle)
    {
        public void SetIdle(TimeSpan idle) => Idle.Value = idle;

        public int Reads => GitHub.Searches.Count;

        public int Points => Log.ToString().Split('\n').Count(l => l.Contains("GraphQL points")) * GitHub.SearchCost;

        public string Lines => Log.ToString();

        public async Task SettledAsync(int reads) => await Until(() => Reads == reads && Time.Waiting == 1);

        // A minute at a time, each settled: a read the minute brings is done and the next wait set before the next minute.
        public async Task PassAsync(int minutes)
        {
            for (var i = 0; i < minutes; i++)
            {
                Time.Advance(TimeSpan.FromMinutes(1));
                await Until(() => Time.Waiting == 1);
            }
        }
    }

    private sealed class IdleBox { public TimeSpan Value { get; set; } }

    private Rig NewRig(TimeSpan? idle = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "pilot", ".git"));
        File.WriteAllText(Path.Combine(_root, "pilot", ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/pilot.git\n");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var github = new FakeGitHub();
        github.PullRequestsOnGitHub.Add(PullRequestSearch.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PullRequests", "ready.json"))).PullRequests.Single() with { Repo = "SharpAstro/pilot" });
        var time = new PaceTime(Noon);
        var views = new PageViews();
        var log = new StringWriter();
        var box = new IdleBox { Value = idle ?? NodeSettings.DefaultPrWatchIdle };
        var watch = new PullRequestWatch(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), github, time)
        {
            Views = views,
            Idle = () => box.Value,
        };
        watch.Error = TextWriter.Synchronized(log);
        return new Rig(watch, time, github, log, views, box);
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the watch did not get there");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task WithNoOneLookingAnHourCostsFourReadsWhereEveryMinuteCostSixty()
    {
        var idle = NewRig();
        await idle.Watch.StartAsync(Ct);
        try
        {
            await idle.SettledAsync(1);
            await idle.PassAsync(59);

            // At the start and every 15 minutes on: minutes 0, 15, 30 and 45.
            idle.Reads.ShouldBe(4);
            idle.Points.ShouldBe(4 * 3);
        }
        finally
        {
            await idle.Watch.StopAsync(CancellationToken.None);
        }

        // What it was: the same hour read every minute, which is what a page that is open costs now.
        var open = NewRig();
        var client = new object();
        open.Views.Say(client, Prs);
        await open.Watch.StartAsync(Ct);
        try
        {
            await open.SettledAsync(1);
            await open.PassAsync(59);

            open.Reads.ShouldBe(60);
            open.Points.ShouldBe(60 * 3);
        }
        finally
        {
            await open.Watch.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AClientSayingPageIsPrsGivesAReadAtOnceThenOneAMinute()
    {
        var rig = NewRig();
        await rig.Watch.StartAsync(Ct);
        try
        {
            await rig.SettledAsync(1);
            rig.Time.Advance(TimeSpan.FromMinutes(2));
            await rig.SettledAsync(1);

            // Opened two minutes into the idle wait: read now, no time passed.
            rig.Views.Say(new object(), Prs);
            await rig.SettledAsync(2);

            rig.Time.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(50, Ct);
            rig.Reads.ShouldBe(2);
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            await rig.SettledAsync(3);
            rig.Time.Advance(TimeSpan.FromSeconds(60));
            await rig.SettledAsync(4);

            // Each line says why, and what it cost.
            var lines = rig.Lines.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(4);
            lines[0].ShouldEndWith("3 GraphQL points, 4900 left (node started)");
            lines[1].ShouldEndWith("3 GraphQL points, 4900 left (page opened)");
            lines[2].ShouldEndWith("3 GraphQL points, 4900 left (page open)");
            lines[3].ShouldEndWith("3 GraphQL points, 4900 left (page open)");
        }
        finally
        {
            await rig.Watch.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task APageOpenedWhileAReadRunsJoinsItAndAskedForARefreshTheLineSaysSo()
    {
        var rig = NewRig();
        var held = rig.GitHub.HoldSearches();
        await rig.Watch.StartAsync(Ct);
        try
        {
            await Until(() => rig.Reads == 1);
            rig.Views.Say(new object(), Prs);
            held.SetResult();
            await rig.SettledAsync(1);

            // The read it joined was the one: nothing more is read at once.
            await Task.Delay(50, Ct);
            rig.Reads.ShouldBe(1);

            (await rig.Watch.RefreshAsync(Ct)).ShouldNotBeNull();
            rig.Lines.ShouldContain("(refresh asked)");
        }
        finally
        {
            await rig.Watch.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LeavingThePageOrClosingItsStreamGoesBackToTheIdleInterval()
    {
        var rig = NewRig();
        var first = new object();
        var second = new object();
        rig.Views.Say(first, Prs);
        await rig.Watch.StartAsync(Ct);
        try
        {
            await rig.SettledAsync(1);
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            await rig.SettledAsync(2);

            // A second client on the page, the first leaves it: still a viewer, still a minute.
            rig.Views.Say(second, Prs);
            await Task.Delay(50, Ct);
            rig.Reads.ShouldBe(2);
            rig.Views.Say(first, ClientPages.PullRequests + "-not");
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            await rig.SettledAsync(3);

            // The second leaves by closing its stream: nothing is read for the next 14 minutes, and again at the 15th.
            rig.Views.Forget(second);
            await Task.Delay(50, Ct);
            await rig.PassAsync(14);
            rig.Reads.ShouldBe(3);
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            await rig.SettledAsync(4);
            rig.Lines.Split('\n').Last(l => l.Contains("GraphQL")).ShouldEndWith("(idle interval)");

            // And one that says another page, then none, is no viewer either.
            var third = new object();
            rig.Views.Say(third, Prs);
            await rig.SettledAsync(5);
            rig.Views.Say(third, null);
            await Task.Delay(50, Ct);
            await rig.PassAsync(14);
            rig.Reads.ShouldBe(5);
        }
        finally
        {
            await rig.Watch.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PrWatchIdleChangesTheIdleIntervalAlsoForAWaitAlreadyRunning()
    {
        var idle = TimeSpan.FromMinutes(15);
        var rig = NewRig(idle);
        await rig.Watch.StartAsync(Ct);
        try
        {
            await rig.SettledAsync(1);
            await rig.PassAsync(2);

            // Saved as 5m: a read at the fifth minute since the last, not the fifteenth.
            rig.SetIdle(TimeSpan.FromMinutes(5));
            rig.Watch.PaceChanged();
            await Task.Delay(50, Ct);
            await rig.PassAsync(2);
            rig.Reads.ShouldBe(1);
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            await rig.SettledAsync(2);

            // And the settings file says it as 5m, reads it back, and takes default, a bare number as minutes, and no less than a minute.
            var saved = NodeSettings.With(new NodeSettings(), NodeSettings.PrWatchIdleKey, ["5m"]).Settings!;
            saved.PrWatchIdle.ShouldBe(TimeSpan.FromMinutes(5));
            saved.Get(NodeSettings.PrWatchIdleKey).ShouldBe(["5m"]);
            NodeSettings.With(saved, NodeSettings.PrWatchIdleKey, ["default"]).Settings!.PrWatchIdle.ShouldBeNull();
            NodeSettings.With(saved, NodeSettings.PrWatchIdleKey, ["20"]).Settings!.PrWatchIdle.ShouldBe(TimeSpan.FromMinutes(20));
            NodeSettings.With(saved, NodeSettings.PrWatchIdleKey, ["1h"]).Settings!.Get(NodeSettings.PrWatchIdleKey).ShouldBe(["1h"]);
            NodeSettings.With(saved, NodeSettings.PrWatchIdleKey, ["30s"]).Error.ShouldNotBeNull();
            NodeSettings.With(saved, NodeSettings.PrWatchIdleKey, ["soon"]).Error.ShouldNotBeNull();
            new RoleModels(null).PrWatchIdle.ShouldBe(TimeSpan.FromMinutes(15));
            new RoleModels(saved).PrWatchIdle.ShouldBe(TimeSpan.FromMinutes(5));
        }
        finally
        {
            await rig.Watch.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AStreamThatClosesDropsItsClientsPageAndOneThatSaidNothingShowsNone()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var views = node.App.Services.GetRequiredService<PageViews>();
        await using (var silent = await node.Client.OpenEventsAsync(since: null, stream: null, Ct))
        {
            await silent.ReadAsync(Ct);
            views.Count(Prs).ShouldBe(0);
        }

        var shown = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        await shown.ReadAsync(Ct);
        await shown.SayPageAsync(Prs, Ct);
        await Until(() => views.Count(Prs) == 1);

        await shown.SayPageAsync(null, Ct);
        await Until(() => views.Count(Prs) == 0);

        await shown.SayPageAsync(Prs, Ct);
        await Until(() => views.Count(Prs) == 1);
        await shown.DisposeAsync();
        await Until(() => views.Count(Prs) == 0);
    }

    /// <summary>A clock the test moves: <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on it ends when <see cref="Advance"/> passes its time.</summary>
    private sealed class PaceTime(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<PaceTimer> _timers = [];
        private DateTimeOffset _now = start;

        /// <summary>The timers set and not yet fired or disposed.</summary>
        public int Waiting
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count(t => t.Due is not null);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PaceTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<PaceTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due <= _now)];
            }
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class PaceTimer(PaceTime owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                }
                return true;
            }

            public void Fire()
            {
                lock (owner._gate)
                {
                    Due = null;
                }
                callback(state);
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
