namespace Pilot.Node.Flights;

/// <summary>What a refused <c>git push</c> comes to, read from git's own text (PIL836).</summary>
public static class PushRefusals
{
    /// <summary>
    /// Whether <paramref name="text"/> is GitHub's refusal of a push to a protected branch, as the remote prints it:
    /// <c>remote: error: GH006: Protected branch update failed for refs/heads/flight/pil819.</c> A branch whose PR sits in the merge
    /// queue is locked the same way, and so is one a ruleset protects, so the text alone does not say which.
    /// </summary>
    public static bool IsProtectedBranch(string? text) =>
        text is { Length: > 0 }
        && (text.Contains("GH006", StringComparison.Ordinal) || text.Contains("Protected branch update failed", StringComparison.OrdinalIgnoreCase));
}
