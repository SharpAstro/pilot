using System.Globalization;
using System.Text;
using Pilot.Contracts;
using Pilot.Node.Designs;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>Where a design's versions are on the node (its designs folder) and the design's terminal sizes; what a brief needs besides the flight's own fields (PIL1153).</summary>
    public sealed record DesignRef(string Root, string[] Terminals);

    /// <summary>The roles that read the design paragraph.</summary>
    public enum DesignRole { Planner, Worker, Reviewer }

    /// <summary>
    /// The paragraph that tells a flight built from a design version (<c>design-build</c>) where the version is: its folder, prototype.json with its SHA-256,
    /// NOTES.md and the renders. One source for the planner, worker and reviewer briefs; empty for a flight with no design.
    /// </summary>
    public static string DesignVersion(FlightRow flight, DesignRole role, DesignRef? design)
    {
        if (flight.FromDesign is not { Length: > 0 } id || flight.FromDesignVersion is not { } version)
        {
            return "";
        }
        var v = version.ToString(CultureInfo.InvariantCulture);
        var folder = design is { Root.Length: > 0 }
            ? Path.Combine(design.Root, id, DesignStore.VersionsFolderName, "v" + v)
            : $"<the node's designs folder>/{id}/{DesignStore.VersionsFolderName}/v{v}";
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine($"## Design {id} version {v}");
        text.AppendLine($"This flight was built from version {v} of design {id}. The version is on the node, in `{folder}`:");
        text.AppendLine($"- `{DesignStore.PrototypeFile}`: the screens as a tree, SHA-256 `{flight.FromDesignSha256 ?? "(not recorded)"}`;");
        text.AppendLine($"- `{DesignStore.NotesFile}`: what the designer wrote about it;");
        text.AppendLine($"- `{DesignChecker.RendersFolderName}/`: one image of each screen, as the version looks.");
        if (role == DesignRole.Reviewer)
        {
            var sizes = string.Join(", ", DesignSizes.All.Select(s => $"{s.Width}x{s.Height}"));
            var terminals = design is { Terminals.Length: > 0 } ? string.Join(", ", design.Terminals) : "(the design names none)";
            text.AppendLine("Hold the build to this version: compare the built screens (the CPU-rendered Gui test captures) with the version's renders at the design's");
            text.AppendLine($"sizes, {sizes}, and at the design's terminal sizes, {terminals}. A difference from the renders is a finding unless the PR says why.");
            text.AppendLine("Check too that `prototype.json` still has the SHA-256 above; if it changed since the flight was made, say so rather than judge by it.");
        }
        else
        {
            text.AppendLine("Build the screens as the prototype tree shows them: its structure, text and order, and the renders for how they look. A deliberate difference is");
            text.AppendLine(role == DesignRole.Planner
                ? "planned as a step of a leg, and said in that leg's goal, so the worker reports it."
                : "said in the leg's report and in the PR, with why; the reviewer treats any other difference as a finding.");
        }
        return text.ToString();
    }
}
