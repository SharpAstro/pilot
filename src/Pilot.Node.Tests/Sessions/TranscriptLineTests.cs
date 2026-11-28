using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// The transcript reader parses each line from the bytes it read (pilot #1443): a line a read holds whole where it lies, and one the
/// read cut short (by the end of the read, or by a write still in progress) once its end comes. A line used to be gathered byte by
/// byte into a list that doubled as it grew and kept its size for the reader's life.
/// </summary>
public sealed class TranscriptLineTests : IDisposable
{
    private const string Session = "5e55a0a1-0000-4000-8000-000000000001";
    private readonly TempProfile _profile = new();

    public void Dispose() => _profile.Dispose();

    private static string Said(string who, string text)
    {
        var content = who == "user" ? $"\"{text}\"" : "[{\"type\":\"text\",\"text\":\"" + text + "\"}]";
        return $$"""{"type":"{{who}}","timestamp":"2026-10-20T09:00:00Z","message":{"role":"{{who}}","content":""" + content + "}}";
    }

    [Fact]
    public void ALineLongerThanManyReadsIsOneEntryAndTheLinesAroundItStayInOrder()
    {
        var path = _profile.TranscriptPath(Session);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // About 300 KB of words in one reply: several of the reader's 64 KB reads.
        var long_ = string.Join(' ', Enumerable.Repeat("words", 60_000));
        _profile.Append(path, Said("user", "Write a lot"), Said("assistant", long_), Said("user", "Thanks"));

        var window = new TranscriptBook(_profile.Profile).Window(Session, 0, int.MaxValue).ShouldNotBeNull();

        window.Entries.Select(e => e.Kind).ShouldBe([TranscriptKind.User, TranscriptKind.Assistant, TranscriptKind.User]);
        window.Entries[0].Text.ShouldBe("Write a lot");
        window.Entries[1].Text.ShouldStartWith("words words");
        window.Entries[2].Text.ShouldBe("Thanks");
    }

    [Fact]
    public void ALineCutShortByAWriteInProgressIsReadWholeOnceItsEndIsWritten()
    {
        var path = _profile.TranscriptPath(Session);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var book = new TranscriptBook(_profile.Profile);
        var line = Said("assistant", "Half now and half later");
        _profile.Append(path, Said("user", "Go"));
        File.AppendAllText(path, line[..20]);

        book.Window(Session, 0, int.MaxValue).ShouldNotBeNull().Entries.Select(e => e.Text).ShouldBe(["Go"]);

        File.AppendAllText(path, line[20..] + "\n");
        book.Window(Session, 0, int.MaxValue).ShouldNotBeNull().Entries.Select(e => e.Text).ShouldBe(["Go", "Half now and half later"]);
    }
}
