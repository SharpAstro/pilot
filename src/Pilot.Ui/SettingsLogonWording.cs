namespace Pilot.Ui;

/// <summary>The words of the Desktop cat section's Start at logon row, in one place so the page and its test read the same (#1381 section 7).</summary>
public static class SettingsLogonWording
{
    /// <summary>The row's description: the cat's own start at logon, and (while "Show in remote desktop sessions" is off) that it stays hidden in remote sessions.</summary>
    public static string Hint(BuddySettings cat) =>
        "the cat starts when you log in, apart from the node's own start at logon" + (cat.ShowsInRemoteSessions ? string.Empty : "; it stays hidden in remote desktop sessions");
}
