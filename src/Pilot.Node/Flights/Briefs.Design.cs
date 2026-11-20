using System.Globalization;
using System.Text;
using Pilot.Contracts;
using Pilot.Node.Designs;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// Where a design's versions are on the node (its designs folder) and the design's terminal sizes; what a brief needs besides the flight's own fields (PIL1153).
    /// <paramref name="TerminalsOf"/> holds the sizes of each design by id (PIL1185); <paramref name="Terminals"/> is the fallback for one it does not list.
    /// </summary>
    public sealed record DesignRef(string Root, string[] Terminals, IReadOnlyDictionary<string, string[]>? TerminalsOf = null)
    {
        /// <summary>The terminal sizes of design <paramref name="id"/>.</summary>
        public string[] TerminalsFor(string id) => TerminalsOf is not null && TerminalsOf.TryGetValue(id, out var own) ? own : Terminals;
    }

    /// <summary>The roles that read the design paragraph.</summary>
    public enum DesignRole { Planner, Worker, Reviewer }

    /// <summary>
    /// The paragraphs that tell a flight built to designs (<c>design-build</c>, <c>plan-idea</c>) where each version is: its folder, prototype.json with its SHA-256,
    /// the document and the renders, one section per design. One source for the planner, worker and reviewer briefs; empty for a flight with no design.
    /// </summary>
    public static string DesignVersion(FlightRow flight, DesignRole role, DesignRef? design)
    {
        var designs = flight.Designs;
        if (designs.Length == 0)
        {
            return "";
        }
        var text = new StringBuilder();
        foreach (var d in designs)
        {
            var v = d.Version.ToString(CultureInfo.InvariantCulture);
            var folder = design is { Root.Length: > 0 }
                ? Path.Combine(design.Root, d.Id, DesignStore.VersionsFolderName, "v" + v)
                : $"<the node's designs folder>/{d.Id}/{DesignStore.VersionsFolderName}/v{v}";
            text.AppendLine();
            text.AppendLine($"## Design {d.Id} version {v}");
            // Designs are node-local: a flight moved to a peer names a folder that is on the node that built it, not on this one (the review of #1179).
            text.AppendLine($"This flight is built to version {v} of design {d.Id}. The version is on the node that built it, in `{folder}`; a flight moved to another node finds it there, not on its own node:");
            text.AppendLine($"- `{DesignStore.DocumentFile}`: the design as a document, its objective and one cell per screen (an older version has `{DesignStore.NotesFile}` instead);");
            text.AppendLine($"- `{DesignStore.PrototypeFile}` and `{DesignStore.ScreensFolderName}/<id>.json`: the screens as trees, SHA-256 `{d.Sha256 ?? "(not recorded)"}` (an older version holds its screens in `{DesignStore.PrototypeFile}`);");
            text.AppendLine($"- `{DesignChecker.RendersFolderName}/`: one image of each screen, as the version looks.");
            if (role != DesignRole.Worker)
            {
                AppendObjectives(text, folder);
            }
            if (role == DesignRole.Reviewer)
            {
                var sizes = string.Join(", ", DesignSizes.All.Select(s => $"{s.Width}x{s.Height}"));
                var own = design?.TerminalsFor(d.Id) ?? [];
                var terminals = own.Length > 0 ? string.Join(", ", own) : "(the design names none)";
                text.AppendLine($"Hold the build to {d.Id} at version {v}: compare the built screens (the CPU-rendered Gui test captures) with the version's renders at the design's");
                text.AppendLine($"sizes, {sizes}, and at the design's terminal sizes, {terminals}. A difference from the renders is a finding unless the PR says why.");
                text.AppendLine("A screen the design shows that the build leaves out, or builds differently, is a finding too: go through the design's screens one by one.");
                text.AppendLine("Check too that `prototype.json` still has the SHA-256 above; if it changed since the flight was made, say so rather than judge by it.");
            }
            else if (role == DesignRole.Worker)
            {
                text.AppendLine($"Read the screens of {d.Id} that your leg's goal names (its `{DesignStore.DocumentFile}` cell and `{DesignStore.ScreensFolderName}/<id>.json` for each), and build those.");
            }
        }
        if (role == DesignRole.Planner)
        {
            text.AppendLine();
            text.AppendLine("Name in each leg's goal the screens of the design(s) it builds (by design id and screen id), so the worker reads those and the reviewer can tell a screen left out.");
            text.AppendLine("Build the screens as the prototype tree shows them: its structure, text and order, and the renders for how they look. A deliberate difference is");
            text.AppendLine("planned as a step of a leg, and said in that leg's goal, so the worker reports it.");
        }
        else if (role == DesignRole.Worker)
        {
            text.AppendLine("Build the screens as the prototype tree shows them: its structure, text and order, and the renders for how they look. A deliberate difference is");
            text.AppendLine("said in the leg's report and in the PR, with why; the reviewer treats any other difference as a finding.");
        }
        return text.ToString();
    }

    // The design's objective and each screen's objective, read from the version's document when it is on this node.
    private static void AppendObjectives(StringBuilder text, string folder)
    {
        var file = Path.Combine(folder, DesignStore.DocumentFile);
        if (!File.Exists(file))
        {
            return;
        }
        DesignDocument document;
        try
        {
            document = DesignDocument.Parse(File.ReadAllText(file));
        }
        catch (IOException)
        {
            return;
        }
        if (document.Objective is { Length: > 0 } objective)
        {
            text.AppendLine($"Its objective: {objective.Trim()}");
        }
        foreach (var cell in document.Cells)
        {
            var screens = string.Join(", ", cell.Frames.Select(f => f.Id));
            text.AppendLine($"- Screen {cell.Heading}{(screens.Length > 0 ? $" ({screens})" : "")}: {(cell.Objective is { Length: > 0 } o ? o.Trim() : "(no objective written)")}");
        }
    }
}
