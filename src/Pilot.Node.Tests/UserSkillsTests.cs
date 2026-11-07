using System.Text;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The Claude Code skills the node carries (<see cref="UserSkills"/>): written where missing or outdated, a hand
/// edit or a folder pilot did not write kept, a second install changing nothing, off taking out only pilot's own; the
/// setup check and fix over them, the <c>user-skills</c> setting, and the merge-queue skill itself in the binary.</summary>
public sealed class UserSkillsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Skills => Path.Combine(_root, ".claude", "skills");

    private static UserSkills.Skill Skill(string body, string script = "echo hi\n") => new("demo", new Dictionary<string, byte[]>
    {
        ["SKILL.md"] = Encoding.UTF8.GetBytes(body),
        ["bin/run.sh"] = Encoding.UTF8.GetBytes(script),
    });

    private string FileOf(string path) => File.ReadAllText(Path.Combine(Skills, "demo", path));

    [Fact]
    public void AMissingSkillIsWrittenWithItsMarkerAndASecondInstallSaysNothing()
    {
        var v1 = Skill("v1");
        UserSkills.StateOf(Skills, v1).ShouldBe(UserSkills.State.Missing);

        UserSkills.Install(Skills, [v1]).ShouldHaveSingleItem().ShouldContain("demo: installed");

        (FileOf("SKILL.md"), FileOf("bin/run.sh")).ShouldBe(("v1", "echo hi\n"));
        File.ReadAllText(Path.Combine(Skills, "demo", UserSkills.Marker)).Trim().ShouldBe(v1.Hash);
        UserSkills.StateOf(Skills, v1).ShouldBe(UserSkills.State.Current);
        UserSkills.Install(Skills, [v1]).ShouldBeEmpty();
        Directory.EnumerateDirectories(Skills).ShouldHaveSingleItem();
    }

    [Fact]
    public void AnUnchangedOlderSkillIsReplacedByTheNewerOne()
    {
        UserSkills.Install(Skills, [Skill("v1", "echo old\n")]);
        var v2 = Skill("v2");

        UserSkills.StateOf(Skills, v2).ShouldBe(UserSkills.State.Outdated);
        UserSkills.Install(Skills, [v2]).ShouldHaveSingleItem().ShouldContain("demo: updated");

        (FileOf("SKILL.md"), FileOf("bin/run.sh")).ShouldBe(("v2", "echo hi\n"));
        UserSkills.StateOf(Skills, v2).ShouldBe(UserSkills.State.Current);
    }

    [Fact]
    public void ASkillChangedByHandIsKeptAndSaidSo()
    {
        UserSkills.Install(Skills, [Skill("v1")]);
        File.WriteAllText(Path.Combine(Skills, "demo", "SKILL.md"), "mine");
        var v2 = Skill("v2");

        UserSkills.StateOf(Skills, v2).ShouldBe(UserSkills.State.Edited);
        UserSkills.Install(Skills, [v2]).ShouldHaveSingleItem().ShouldContain("changed by hand");
        FileOf("SKILL.md").ShouldBe("mine");

        // A file added by hand is a change too.
        UserSkills.Install(Skills, [Skill("v1")]);
        File.WriteAllText(Path.Combine(Skills, "demo", "notes.md"), "x");
        UserSkills.StateOf(Skills, Skill("v1")).ShouldBe(UserSkills.State.Edited);
    }

    [Fact]
    public void AFolderPilotDidNotWriteIsKept()
    {
        Directory.CreateDirectory(Path.Combine(Skills, "demo"));
        File.WriteAllText(Path.Combine(Skills, "demo", "SKILL.md"), "someone else's");

        UserSkills.StateOf(Skills, Skill("v1")).ShouldBe(UserSkills.State.NotOurs);
        UserSkills.Install(Skills, [Skill("v1")]).ShouldHaveSingleItem().ShouldContain("not pilot's");
        UserSkills.Remove(Skills, [Skill("v1")]).ShouldBeEmpty();
        FileOf("SKILL.md").ShouldBe("someone else's");
    }

    [Fact]
    public void RemoveTakesOutOnlyPilotsOwnUnchangedSkill()
    {
        var v1 = Skill("v1");
        UserSkills.Install(Skills, [v1]);
        UserSkills.Remove(Skills, [v1]).ShouldHaveSingleItem().ShouldBe("demo: removed");
        Directory.Exists(Path.Combine(Skills, "demo")).ShouldBeFalse();

        UserSkills.Install(Skills, [v1]);
        File.WriteAllText(Path.Combine(Skills, "demo", "SKILL.md"), "mine");
        UserSkills.Remove(Skills, [v1]).ShouldHaveSingleItem().ShouldContain("kept");
        FileOf("SKILL.md").ShouldBe("mine");
    }

    [Fact]
    public void TheBinaryCarriesTheMergeQueueSkillWithLfOnly()
    {
        var skill = UserSkills.Carried().Single(s => s.Name == "merge-queue");

        skill.Files.Keys.Order(StringComparer.Ordinal).ShouldBe(["SKILL.md", "mq.sh"]);
        Encoding.UTF8.GetString(skill.Files["SKILL.md"]).ShouldStartWith("---\nname: merge-queue\n");
        Encoding.UTF8.GetString(skill.Files["mq.sh"]).ShouldStartWith("#!/usr/bin/env bash\n");
        // Git Bash fails on a CR in a script, and the frontmatter is read line by line.
        skill.Files.Values.ShouldAllBe(bytes => !bytes.Contains((byte)'\r'));
    }

    /// <summary>
    /// Every carried skill's description is one double-quoted YAML scalar read to its end: unquoted, a ' #' starts a comment,
    /// and Claude Code cut merge-queue's at 'Use when the user asks "is' (the review of #849), losing the triggers it exists for.
    /// </summary>
    [Fact]
    public void EverySkillsDescriptionIsQuotedAndWhole()
    {
        foreach (var skill in UserSkills.Carried())
        {
            var lines = Encoding.UTF8.GetString(skill.Files["SKILL.md"]).Split('\n');
            lines[0].ShouldBe("---", skill.Name);
            var end = Array.IndexOf(lines, "---", 1);
            end.ShouldBeGreaterThan(1, skill.Name);
            var description = lines[1..end].Where(l => l.StartsWith("description: ", StringComparison.Ordinal)).ShouldHaveSingleItem(skill.Name);
            var value = description["description: ".Length..];
            value.ShouldStartWith("\"", customMessage: skill.Name);
            value.ShouldEndWith("\"", customMessage: skill.Name);
            // One scalar: no inner double quote, escaped or not, so nothing ends it early.
            value[1..^1].ShouldNotContain('"', customMessage: skill.Name);
        }
        var mergeQueue = Encoding.UTF8.GetString(UserSkills.Carried().Single(s => s.Name == "merge-queue").Files["SKILL.md"]);
        mergeQueue.Split('\n').Single(l => l.StartsWith("description: ", StringComparison.Ordinal)).ShouldContain("GH013");
    }

    /// <summary>
    /// A replace that cannot happen (a file of the old skill held open, as a running mq.sh watch holds its script on Windows)
    /// leaves the old skill whole, marker and all, so it still reads Outdated and the next install finishes it (the review of #849:
    /// a delete in place took the marker and left the rest, which read NotOurs for ever).
    /// </summary>
    [Fact]
    public void AReplaceBlockedByAnOpenFileLeavesTheOldSkillWholeAndTheNextInstallFinishesIt()
    {
        UserSkills.Install(Skills, [Skill("v1", "echo old\n")]);
        var v2 = Skill("v2");
        var script = Path.Combine(Skills, "demo", "bin", "run.sh");

        using (new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                UserSkills.Install(Skills, [v2]);
            }
            catch (IOException)
            {
                // Windows refuses to move a folder holding a file open without delete sharing: nothing may have changed.
                UserSkills.StateOf(Skills, v2).ShouldBe(UserSkills.State.Outdated);
                (FileOf("SKILL.md"), FileOf("bin/run.sh")).ShouldBe(("v1", "echo old\n"));
            }
            catch (UnauthorizedAccessException)
            {
                UserSkills.StateOf(Skills, v2).ShouldBe(UserSkills.State.Outdated);
                (FileOf("SKILL.md"), FileOf("bin/run.sh")).ShouldBe(("v1", "echo old\n"));
            }
        }

        UserSkills.Install(Skills, [v2]);
        UserSkills.StateOf(Skills, v2).ShouldBe(UserSkills.State.Current);
        (FileOf("SKILL.md"), FileOf("bin/run.sh")).ShouldBe(("v2", "echo hi\n"));
        // Nothing left beside it once the files are let go: no temp folder, no folder moved aside.
        Directory.EnumerateDirectories(Skills).Select(Path.GetFileName).ShouldBe(["demo"]);
    }

    [Fact]
    public async Task TheSetupCheckOffersTheInstallAndPassesAfterIt()
    {
        var setup = Setup(on: null);
        var before = Check(setup);
        (before.Ok, before.Optional, before.Fix).ShouldBe((false, true, "Install skills"));
        before.Line.ShouldContain("demo");

        (await setup.FixAsync(NodeSetup.UserSkillsKey, Ct)).ShouldBeNull();

        var after = Check(setup);
        (after.Ok, after.Fix).ShouldBe((true, null));
        FileOf("SKILL.md").ShouldBe("v1");
    }

    [Fact]
    public void WithTheSettingOffTheCheckSaysSoAndOffTakesThemOut()
    {
        Setup(on: null).ApplyUserSkills(on: true).Problem.ShouldBeNull();

        var off = Setup(on: false);
        var check = Check(off);
        (check.Ok, check.Optional, check.Fix).ShouldBe((false, true, null));
        check.Line.ShouldContain("off");

        off.ApplyUserSkills(on: false).Lines.ShouldHaveSingleItem().ShouldBe("demo: removed");
        Directory.Exists(Path.Combine(Skills, "demo")).ShouldBeFalse();
    }

    [Fact]
    public void TheSettingIsOnOffAndClears()
    {
        var off = NodeSettings.With(new NodeSettings(), NodeSettings.UserSkillsKey, ["off"]).Settings.ShouldNotBeNull();
        off.UserSkills.ShouldBe(false);
        off.Get(NodeSettings.UserSkillsKey).ShouldBe(["off"]);
        NodeSettings.With(off, NodeSettings.UserSkillsKey, ["maybe"]).Error.ShouldBe("user-skills takes on or off");
        NodeSettings.With(off, NodeSettings.UserSkillsKey, []).Settings.ShouldNotBeNull().UserSkills.ShouldBeNull();
        NodeSettings.Keys.ShouldContain(NodeSettings.UserSkillsKey);

        var file = Path.Combine(_root, "settings.json");
        off.Write(file);
        NodeSettings.Read(file).UserSkills.ShouldBe(false);
    }

    // The check reads the settings file (the setting is applied live), so the setting goes there.
    private NodeSetup Setup(bool? on)
    {
        var options = new NodeOptions("/tmp/n.sock", _root);
        new NodeSettings { UserSkills = on }.Write(options.SettingsFile);
        return new NodeSetup(options, "pilot-node", Path.Combine(_root, ".claude"))
        {
            SettingsFile = Path.Combine(_root, ".claude", "settings.json"),
            MachineFolder = Path.Combine(_root, "home"),
            FindRunawayAgent = () => null,
            Run = (_, _, _) => (1, ""),
            CarriedSkills = () => [Skill("v1")],
        };
    }

    private static SetupCheck Check(NodeSetup setup) => setup.Checks().Single(c => c.Key == NodeSetup.UserSkillsKey);
}
