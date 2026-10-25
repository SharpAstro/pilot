using System.Text.Json.Nodes;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node trusts a folder in Claude Code's file before a background session starts there, carefully.</summary>
public sealed class ClaudeTrustTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private string File => Path.Combine(_root, ".claude.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AFolderIsTrustedAndEverythingElseInTheFileIsKept()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repos", "tianwen")).FullName;
        System.IO.File.WriteAllText(File, """{ "oauthAccount": { "emailAddress": "a@b.c" }, "projects": { "C:/elsewhere": { "allowedTools": ["x"], "hasTrustDialogAccepted": false } }, "numStartups": 7 }""");
        var trust = new ClaudeTrust(File);

        trust.Trusted(repo).ShouldBeNull();
        trust.Ensure(repo).ShouldBeNull();

        trust.Trusted(repo).ShouldBe(repo);
        var root = JsonNode.Parse(System.IO.File.ReadAllText(File))!.AsObject();
        root["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe("a@b.c");
        root["numStartups"]!.GetValue<int>().ShouldBe(7);
        root["projects"]!["C:/elsewhere"]!["allowedTools"]!.AsArray().Count.ShouldBe(1);
        root["projects"]![ClaudeTrust.Key(repo)]!["hasTrustDialogAccepted"]!.GetValue<bool>().ShouldBeTrue();
        System.IO.File.Exists(File + ".pilot-bak").ShouldBeTrue("the file it replaced is kept");
        System.IO.File.Exists(File + ".pilot-tmp").ShouldBeFalse();
    }

    [Fact]
    public void AFolderUnderATrustedOneIsTrustedItselfSinceClaudeCodeAsksPerFolder()
    {
        // The org root trusted, a repo under it not: Claude Code refused the repo's background session all the same.
        var org = Directory.CreateDirectory(Path.Combine(_root, "repos")).FullName;
        var repo = Directory.CreateDirectory(Path.Combine(org, "tianwen")).FullName;
        System.IO.File.WriteAllText(File, $$"""{ "projects": { "{{ClaudeTrust.Key(org)}}": { "hasTrustDialogAccepted": true } } }""");
        var trust = new ClaudeTrust(File);
        trust.Trusted(repo).ShouldBeNull();

        trust.Ensure(repo).ShouldBeNull();

        trust.Trusted(repo).ShouldBe(repo);
        trust.Trusted(org).ShouldBe(org);
    }

    [Fact]
    public void AFolderTrustedAlreadyIsNotWrittenAgain()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "pilot")).FullName;
        System.IO.File.WriteAllText(File, $$"""{ "projects": { "{{ClaudeTrust.Key(repo)}}": { "hasTrustDialogAccepted": true } } }""");
        var before = System.IO.File.ReadAllText(File);

        new ClaudeTrust(File).Ensure(repo).ShouldBeNull();

        System.IO.File.ReadAllText(File).ShouldBe(before, "nothing was written");
    }

    [Fact]
    public void AFileWithNoProjectsGetsThemAndNoFileIsSaid()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "r")).FullName;
        new ClaudeTrust(File).Ensure(repo).ShouldNotBeNull().ShouldContain("no ");

        System.IO.File.WriteAllText(File, "{}");
        new ClaudeTrust(File).Ensure(repo).ShouldBeNull();
        new ClaudeTrust(File).Trusted(repo).ShouldBe(repo);
    }

    [Fact]
    public void AKeyIsThePathWithForwardSlashes()
    {
        ClaudeTrust.Key(OperatingSystem.IsWindows() ? @"C:\Users\x\repo\" : "/home/x/repo/").ShouldBe(OperatingSystem.IsWindows() ? "C:/Users/x/repo" : "/home/x/repo");
        if (OperatingSystem.IsWindows())
        {
            ClaudeTrust.Key(@"C:\").ShouldBe("C:/");
        }
    }
}
