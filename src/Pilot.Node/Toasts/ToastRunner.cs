using System.Runtime.Versioning;
using Pilot.Contracts;
using SharpAstro.AppShell;

namespace Pilot.Node.Toasts;

/// <summary>One desktop toast pilot asks for: the tag it replaces and is withdrawn by, the words, the tier and what a click carries.</summary>
/// <param name="Tag">The card's subject (or the burst or test tag); AppShell shortens one over 64 characters the way pilot always did.</param>
/// <param name="Title">The first line.</param>
/// <param name="Body">The second line, or null.</param>
/// <param name="Tier">Urgent asks for an urgent toast, normal for a normal one.</param>
/// <param name="Payload">What a click opens, a <see cref="ToastLink"/> payload.</param>
public sealed record ToastRequest(string Tag, string Title, string? Body, ToastTier Tier, string Payload);

/// <summary>Shows and withdraws the node's desktop toasts (#1356); a fake records what it is asked, the real one is <see cref="AppShellToastRunner"/>.</summary>
public interface IToastRunner
{
    /// <summary>Shows <paramref name="request"/>, in group <c>pilot</c>; a toast of the same tag is replaced. Null on success, else <c>&lt;Status&gt;: &lt;Reason&gt;</c>.</summary>
    Task<string?> Show(ToastRequest request, CancellationToken cancel = default);

    /// <summary>Withdraws the toast of <paramref name="tag"/>, in group <c>pilot</c>. Null on success (a toast not there is one), else <c>&lt;Status&gt;: &lt;Reason&gt;</c>.</summary>
    Task<string?> Withdraw(string tag, CancellationToken cancel = default);
}

/// <summary>
/// The node's toasts through AppShell's <see cref="DesktopNotification"/> (#1384), under the app id <see cref="AppId"/> that
/// <see cref="AppIdentity.Register"/> writes. The calls are synchronous, so they run off the caller's thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppShellToastRunner : IToastRunner
{
    /// <summary>The group every toast of the node is in, so a tag stands for one card.</summary>
    public const string Group = "pilot";

    /// <summary>The app id the toasts are shown under.</summary>
    public const string AppId = "SharpAstro.Pilot";

    /// <inheritdoc />
    public Task<string?> Show(ToastRequest request, CancellationToken cancel = default) =>
        Task.Run(() => Describe(DesktopNotification.Show(request.Title, request.Body, OptionsOf(request))), cancel);

    /// <inheritdoc />
    public Task<string?> Withdraw(string tag, CancellationToken cancel = default) =>
        Task.Run(() => Describe(DesktopNotification.Withdraw(tag, Group, AppId)), cancel);

    /// <summary>The options a request is shown with.</summary>
    public static DesktopNotificationOptions OptionsOf(ToastRequest request) => new()
    {
        Tag = request.Tag,
        Group = Group,
        Urgency = UrgencyOf(request.Tier),
        AppId = AppId,
        LaunchPayload = request.Payload,
        Scheme = ToastLink.Scheme,
    };

    /// <summary>Urgent for the urgent tier, normal otherwise.</summary>
    public static ToastUrgency UrgencyOf(ToastTier tier) => tier == ToastTier.Urgent ? ToastUrgency.Urgent : ToastUrgency.Normal;

    /// <summary>Null for a success, else <c>&lt;Status&gt;: &lt;Reason&gt;</c>.</summary>
    public static string? Describe(DesktopNotificationResult result) => result.IsSuccess ? null : $"{result.Status}: {result.Reason}";
}
