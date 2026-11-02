using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// The break-up the person was last shown for an issue, kept so that <c>break-up --save</c> accepts what was seen, from the
/// <c>updated_at</c> it was made from. A file per issue in the temp folder.
/// </summary>
public static class BreakUpCache
{
    private static string FileOf(string target)
    {
        var name = new string([.. target.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);
        return Path.Combine(Path.GetTempPath(), "pilot-breakup", name + ".json");
    }

    public static void Write(string target, BreakUpProposal proposal)
    {
        var file = FileOf(target);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(proposal, PilotJsonContext.Default.BreakUpProposal));
    }

    public static BreakUpProposal? Read(string target)
    {
        try
        {
            return File.Exists(FileOf(target)) ? JsonSerializer.Deserialize(File.ReadAllText(FileOf(target)), PilotJsonContext.Default.BreakUpProposal) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Forgets the proposal; whether there was one.</summary>
    public static bool Discard(string target)
    {
        var file = FileOf(target);
        if (!File.Exists(file))
        {
            return false;
        }
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        return true;
    }
}
