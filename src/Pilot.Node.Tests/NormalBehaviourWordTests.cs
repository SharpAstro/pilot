using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1271: "autopilot" stays free for a real autopilot feature, so nothing a person reads (docs, briefs, card text, UI and
/// command strings) calls the node's normal behaviour that. The docs, the skills, the brief files, the card summaries and the card
/// catalog are read whole, comments included; the other sources of Pilot.Ui, Pilot.Commands and Pilot.Node by their string literals
/// only, so an identifier kept for old stores does not count. File reads only; runs in milliseconds.
/// </summary>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed partial class NormalBehaviourWordTests
{
    /// <summary>
    /// Stored values and the refused key, each kept because an older store or settings file holds it: removing one would
    /// strand old rows. Everything else must not say the word.
    /// </summary>
    private static readonly string[] Allowed =
    [
        // PIL1285: the word is a real feature again, the switch whose meaning is "the watchtower runs with a standing goal". These
        // are the switch's own names and sentences; they come first, so a longer phrase is taken out before a shorter one inside it.
        // PIL1285, the standing goal: the switch's CI-grant bound, its refusal and the goal section of the watchtower's rules.
        "autopilot-ci-grants", "AutopilotCiGrants", "Autopilot CI grants", "while autopilot is on", "With autopilot on", "on autopilot needs",
        "has autopilot on", "(autopilot)", "bool autopilot", "if (autopilot)", "autopilot: ", "autopilot))", "autopilot,", "autopilot ="  ,
        "autopilot  on: the watchtower runs with a standing goal", // the status line
        "autopilot  off (only the person",                         // the status line
        "**autopilot** on: the watchtower",                        // the status line, as markdown
        "**autopilot** off (only the person",                      // the status line, as markdown
        "autopilot takes on or off",                               // the settings value's refusal
        "autopilot is the person's to change",                     // the person-only refusal
        "Autopilot is on: the watchtower runs with a standing goal", // the switch's tooltip (AutopilotSwitch)
        "Autopilot is off: the watchtower runs with no standing goal", // ditto
        "Autopilot stays {(",                                        // the switch's refusal line
        "autopilot {(",                                              // the switch's asking, refusal and reply lines
        "\"autopilot:toggle\"",                                      // the switch's press action
        "\"autopilot on\"", "\"autopilot off\"",                    // a node's switch on the Fleet line (FleetView)
        "autopilotGoal",                                           // the file member (an old "autopilot" member is ignored)
        "Autopilot switch",                                        // README: the top bar's switch
        "AutopilotKey",                                            // identifier of the setting's key
        "AutopilotFileMember",                                     // identifier of the file member
        "\"autopilot\"",                     // MergeBy / DecisionByKinds value and the refused settings key, stored by older releases
        "autopilot-stall-minutes", "AutopilotStallMinutesKey", "DefaultAutopilotStallMinutes", "Autopilot stall minutes", // PIL1285: the stall bound
        "autopilot-repeat",                  // card kind of the find-the-cause task
        "autopilot-investigations", "AutopilotInvestigationsKey", "DefaultAutopilotInvestigations", "AutopilotInvestigations", "Autopilot investigations", // PIL1285: the investigator bound
        "Autopilot v2 has two tiers", "one autopilot per node", "has an Autopilot part", "\"Autopilot:\"", // PIL1285: README and the digest's part
        "autopilot-ran",                   // card kind of the Handled line, stored in old journals
        "autopilot-merge-refused",           // card kind of the merge refusal, stored in old journals
        "autopilot is gone",                 // the message of a refused `set autopilot`
        "`autopilot` setting is gone",       // README: says the setting is gone
        "pilot settings set autopilot",      // README: the refused command
        "GoneAutopilotKey",                  // identifier of the refused key's constant
        "`autopilot`",                       // README: the stored word of MergeBy / the refused key
        "<c>autopilot</c>",                  // doc comment on the stored word
    ];

    /// <summary>
    /// Names the card catalog holds and only it: the file and test classes a kind's pin points at. They are taken out of the
    /// catalog's text alone, so they cannot hide a real hit in another file.
    /// </summary>
    private static readonly string[] AllowedInCatalog =
    [
        "Autopilot.cs",                      // file name of the assistant commands, which the catalog names as a kind's source
        "AutopilotTests.",                   // test classes are not renamed (PIL1271), and the catalog names their tests
        "AutopilotMergeRefusalTests.",       // ditto
    ];

    [Fact]
    public void NormalBehaviourIsNotCalledAutopilot()
    {
        var root = FindRoot();
        var hits = new List<string>();

        foreach (var file in DocFiles(root))
            Scan(root, file, File.ReadAllText(file), hits);

        var catalog = Path.Combine(root, "src", "Pilot.Node.Tests", "CardKindCatalog.cs");
        foreach (var file in CodeFiles(root).Append(catalog))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (name == "CardKindCatalog.cs")
                Scan(root, file, Without(text, AllowedInCatalog), hits);
            else if (name.StartsWith("Briefs", StringComparison.Ordinal) || name is "CardSummaries.cs")
                Scan(root, file, text, hits);
            else
                Scan(root, file, string.Join('\n', LiteralPattern().Matches(text).Select(m => m.Value)), hits);
        }

        hits.ShouldBeEmpty("the word autopilot is reserved for a real autopilot feature; say the node did it");
    }

    private static string Without(string text, IEnumerable<string> allowed)
    {
        foreach (var word in allowed)
            text = text.Replace(word, "", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static void Scan(string root, string file, string text, List<string> hits)
    {
        text = Without(text, Allowed);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("autopilot", StringComparison.OrdinalIgnoreCase))
                hits.Add($"{Path.GetRelativePath(root, file)}: {trimmed[..Math.Min(trimmed.Length, 120)]}");
        }
    }

    [GeneratedRegex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex LiteralPattern();

    private static IEnumerable<string> DocFiles(string root)
    {
        yield return Path.Combine(root, "PILOT.md");
        yield return Path.Combine(root, "src", "README.md");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src", "Pilot.Node", "UserSkills"), "*", SearchOption.AllDirectories))
            yield return f;
    }

    private static IEnumerable<string> CodeFiles(string root)
    {
        var sep = Path.DirectorySeparatorChar;
        foreach (var project in new[] { "Pilot.Ui", "Pilot.Commands", "Pilot.Node" })
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                if (!f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
                    yield return f;
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("no folder holding src/Pilot.slnx above the test's directory");
    }
}
