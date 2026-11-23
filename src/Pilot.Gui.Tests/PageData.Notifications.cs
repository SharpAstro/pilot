using DIR.Lib.Notifications;
using Pilot.Ui;

namespace Pilot.Gui.Tests;

internal sealed partial class PageData
{
    private static readonly PilotEventKind[] EventKinds = [PilotEventKind.JobEnded, PilotEventKind.FlightLanded, PilotEventKind.DesignPublished];

    private static readonly NotificationSeverity[] Severities = [NotificationSeverity.Info, NotificationSeverity.Success, NotificationSeverity.Warn, NotificationSeverity.Error];

    /// <summary>A job's command whose one filter token is 300 characters with no space (pilot #1286, DIR.Lib #275).</summary>
    internal static readonly string LongFilterCommand = "dotnet test Pilot.Gui.Tests -c Release --filter '" + WrappedProseBreakTests.Filter() + "'";

    /// <summary>
    /// What the Notifications page's history holds in a state (#1184), as the window derives the events: a few of each kind in <b>sample</b>,
    /// titles, texts and a node name far past a row in <b>long</b>, sixty in <b>many</b> (enough to scroll), none in <b>empty</b>. The page's
    /// Needs you is the state's open cards, which the chrome's frame syncs itself.
    /// </summary>
    internal static PilotEvent[] EventsFor(string state) => state switch
    {
        "sample" =>
        [
            new(PilotEventKind.JobEnded, "j7543", "Job j7543 finished", "dotnet test Pilot.Gui.Tests, 1m 40s", NotificationSeverity.Success, "DE1"),
            new(PilotEventKind.FlightLanded, "PIL1184", "PIL1184 landed", "The For you bar collapses to the right, with a Notifications page", NotificationSeverity.Success, "DE1"),
            new(PilotEventKind.DesignPublished, "DSN-1", "DSN-1 v2 published", "A settings page for the dome", NotificationSeverity.Info, "SE1"),
            new(PilotEventKind.JobEnded, "j7548", "Job j7548 failed", "1 failed, 437 passed", NotificationSeverity.Error, "DE1"),
        ],
        "long" =>
        [
            .. Enumerable.Range(0, 6).Select(i => new PilotEvent(EventKinds[i % EventKinds.Length], $"{LongWord}{i}", $"{LongTitle} {i}",
                i % 2 == 0 ? LongReport : LongPath, Severities[i % Severities.Length], LongWord)),
            new PilotEvent(PilotEventKind.JobEnded, "j1286", "Job j1286 finished", LongFilterCommand, NotificationSeverity.Success, "DE1"),
        ],
        "many" =>
        [
            .. Enumerable.Range(0, 60).Select(i => new PilotEvent(PilotEventKind.JobEnded, $"j{7000 + i}", $"Job j{7000 + i} {(i % 5 == 0 ? "failed" : "finished")}",
                "dotnet test Pilot.Gui.Tests -c Release", i % 5 == 0 ? NotificationSeverity.Error : NotificationSeverity.Success, i % 2 == 0 ? "DE1" : "SE1")),
        ],
        _ => [],
    };

    /// <summary>
    /// Posts <see cref="EventsFor"/> the state into <paramref name="host"/>'s history, a minute apart and ending a minute before <see cref="Now"/>:
    /// posted while the bar is open, an event goes straight to the history, as the window has it. The bar is left as it was.
    /// </summary>
    internal static void PostEvents(NotificationsHost host, string state)
    {
        var events = EventsFor(state);
        var open = host.Open;
        host.Open = true;
        for (var i = 0; i < events.Length; i++)
        {
            host.PostEvent(events[i], null, Now.AddMinutes(i - events.Length));
        }
        host.Open = open;
    }
}
