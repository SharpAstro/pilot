using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// How the node reaches a session it runs itself as a <see cref="StreamSession"/> (the watchtower): a say is written to its standard
/// input and a stop ends its process, with no resume and no intercom. <see cref="SessionMessenger"/> asks it before any other way.
/// </summary>
public interface IStreamRoute
{
    /// <summary>Whether <paramref name="row"/> is a stream session of the node's.</summary>
    bool Handles(SessionRow row);

    /// <summary>
    /// Writes <paramref name="text"/> to the session's standard input at once; one sent while it works is read at its next step. A stream session
    /// that has ended is started again on <c>--resume</c> of the same session first. A <paramref name="compact"/> text is <c>/compact</c>,
    /// which only an idle session takes. Throws <see cref="SessionDeliveryException"/> when it cannot be reached.
    /// </summary>
    Task SendAsync(SessionRow row, string text, bool compact, CancellationToken cancellationToken);

    /// <summary>Ends the session's process for <paramref name="reason"/> (<see cref="SessionEndReasons"/>); nothing happens when it has gone already.</summary>
    Task StopAsync(SessionRow row, string reason, CancellationToken cancellationToken);
}
