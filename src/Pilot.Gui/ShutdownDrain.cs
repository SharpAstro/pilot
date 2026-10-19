using SdlVulkan.Renderer;

namespace Pilot.Gui;

/// <summary>
/// Draining background work at shutdown without letting the window stop responding. A copy of tianwen's
/// <c>TianWen.UI.Shared.ShutdownDrain</c> (tianwen 9f25eb3d), until it moves into SdlVulkan.Renderer for every app on
/// it (SharpAstro/SdlVulkan.Renderer#<see cref="UpstreamIssue"/>); delete this then and take that one.
/// </summary>
/// <remarks>
/// <para><b>The window and the Vulkan context can only be destroyed on the thread that created them</b>, the one that
/// ran the event loop, and both obvious spellings of a drain break that:</para>
/// <list type="bullet">
/// <item><description><c>await drain;</c> then dispose resumes the teardown on a thread-pool thread, and the destroy
/// wedges there: pilot-gui hung on close this way (its Main is async for --say and --capture), as tianwen's GUI did
/// after a long AI enhance.</description></item>
/// <item><description><c>drain.Wait();</c> then dispose keeps the thread but stops the loop, so the window is
/// Not Responding for as long as the drain takes.</description></item>
/// </list>
/// <para>So the loop keeps pumping while the drain runs, and the teardown after this returns stays on the loop's
/// thread.</para>
/// </remarks>
internal static class ShutdownDrain
{
    /// <summary>The issue that moves this into SdlVulkan.Renderer.</summary>
    public const int UpstreamIssue = 140;

    /// <summary>How long to keep pumping before abandoning the work: a task that will not cancel costs a slow exit, not a kill.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="loop"/>, painting only its background, until <paramref name="drain"/> completes, the
    /// timeout passes, or the user closes again. Returns whether the drain completed.
    /// </summary>
    public static bool PumpUntilComplete(SdlEventLoop loop, Task drain, TextWriter? log = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(drain);

        if (drain.IsCompleted)
        {
            return true;
        }

        var budget = timeout ?? DefaultTimeout;

        // Nothing of the app's own state is safe to draw from here: the shutdown is disposing what the render
        // callback reads. Paint the background only.
        loop.OnRender = null;
        loop.CheckNeedsRedraw = () =>
        {
            // Polled once per loop iteration, on this thread, so the loop stops itself rather than another thread
            // writing its running flag.
            if (drain.IsCompleted)
            {
                loop.Stop();
            }
            return false;
        };

        // A second close request during the drain means the user has waited long enough.
        loop.OnQuit = () =>
        {
            loop.Stop();
            return true;
        };

        using var deadline = new CancellationTokenSource(budget);
        loop.Run(deadline.Token);

        if (drain.IsCompleted)
        {
            if (drain.IsFaulted)
            {
                log?.WriteLine($"pilot-gui: shutdown: background work faulted while draining: {drain.Exception?.GetBaseException().Message}");
            }
            return true;
        }

        log?.WriteLine($"pilot-gui: shutdown: background work did not finish within {budget.TotalSeconds:0}s; abandoning it.");
        return false;
    }
}
