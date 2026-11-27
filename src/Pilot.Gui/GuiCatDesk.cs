using Node.Lib.Processes;
using SharpAstro.AppShell;

namespace Pilot.Gui;

/// <summary>
/// The real <see cref="Pilot.Ui.ICatDesk"/>: the cat is running when something holds its InstanceGate channel (the one pilot-buddy claims), asked
/// without claiming it, and it is started from the pilot-buddy beside pilot-gui. One cat per desktop is
/// the cat's own rule: a second start finds the gate held and exits.
/// </summary>
internal sealed class GuiCatDesk : Pilot.Ui.ICatDesk
{
    private static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(200);

    private static string Channel => InstanceGate.ChannelFor("pilot-v2-buddy", string.Empty);

    // "Not certainly absent" is "held, or cannot tell": the page says running then, and the cat's own gate refuses a second one either way.
    // AppShell's IsKnownAbsent is not public in the pinned package (follow-up filed), so this is the same check by hand: Windows publishes the
    // pipe namespace as a filesystem, so the gate's pipe exists exactly while the cat holds it. Elsewhere the gate is a lock file and is not probed here.
    public Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperatingSystem.IsWindows() && File.Exists(@"\\.\pipe\" + Channel));

    public async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        var binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "pilot-buddy.exe" : "pilot-buddy");
        if (!File.Exists(binary))
        {
            return "pilot-buddy is not beside pilot-gui";
        }
        if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            using var cat = DetachedProcess.Start(binary, [], AppContext.BaseDirectory, new Dictionary<string, string?>());
            // Started is not running: given a moment to take its gate, on a deadline, so the page's next probe has the answer.
            var until = DateTimeOffset.UtcNow + ReadyBudget;
            while (DateTimeOffset.UtcNow < until && !cancellationToken.IsCancellationRequested)
            {
                if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
                if (cat.TryGetExitCode(out var exit))
                {
                    return $"pilot-buddy exited with {exit} before it took its place";
                }
                await Task.Delay(Step, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
    }
}
