using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// The Claude account a profile is logged in as, from Claude Code's <c>.claude.json</c>: its <c>oauthAccount</c> (email,
/// display name, organisation and its type). Read only, and only those fields: the tokens are not in that record, and
/// nothing here would read them if they were.
/// </summary>
public static class ClaudeAccount
{
    /// <summary>
    /// Where Claude Code keeps <c>.claude.json</c> for the profile folder <paramref name="profileRoot"/>: inside it when
    /// <c>CLAUDE_CONFIG_DIR</c> names it, else beside the default <c>~/.claude</c>, in the home folder.
    /// </summary>
    public static string FileFor(string profileRoot)
    {
        var inside = Path.Combine(profileRoot, ".claude.json");
        if (File.Exists(inside))
        {
            return inside;
        }
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profileRoot));
        return parent is null ? inside : Path.Combine(parent, ".claude.json");
    }

    /// <summary>The account in <paramref name="path"/>, or null when there is no file, no login on record, or another shape.</summary>
    public static AccountRow? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object
                || Text(account, "emailAddress") is not { } email)
            {
                return null;
            }
            return new AccountRow
            {
                Email = email,
                Name = Text(account, "displayName"),
                Organization = Text(account, "organizationName"),
                Plan = Text(account, "organizationType") is { } type ? (type.StartsWith("claude_", StringComparison.Ordinal) ? type["claude_".Length..] : type) : null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
