namespace Pilot.Ui;

/// <summary>
/// The desktop cat as the Settings page sees it: whether it runs on this desktop now, and a way to start it. Behind an
/// interface so the page's tests use a fake (no process, no pipe); pilot-gui gives the real one, which asks the cat's InstanceGate.
/// </summary>
public interface ICatDesk
{
    /// <summary>Whether the cat runs on this desktop now; null when this host cannot tell (the page says so and offers no Start). Read on the page's own loop, never in a spin.</summary>
    Task<bool?> IsRunningAsync(CancellationToken cancellationToken);

    /// <summary>Starts the cat beside pilot-gui; null when it was started, else why not. The page shows the next probe's answer, not this one's.</summary>
    Task<string?> StartAsync(CancellationToken cancellationToken);
}
