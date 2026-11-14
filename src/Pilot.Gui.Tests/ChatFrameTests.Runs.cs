using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1036: a run of identical consecutive error entries (the watchtower's spend-limit error printed fifteen times) is one row with a count
/// and its first and last times. CPU-rendered; the capture is the arranged tree's text leaves.
/// </summary>
public sealed partial class ChatFrameTests
{
    private const string SpendLimit = "API Error: you have hit your spend limit";

    private static DateTimeOffset Local(int hour, int minute) => new(new DateTime(2026, 11, 9, hour, minute, 0, DateTimeKind.Local));

    private static TranscriptEntry Err(string text, DateTimeOffset at) => new() { Index = 0, Kind = TranscriptKind.Error, At = at, Text = text };

    // Fifteen identical errors, 11:38 to 12:17, between two other entries.
    private static List<TranscriptEntry> SpendLimitFeed()
    {
        var feed = new List<TranscriptEntry> { new() { Index = 0, Kind = TranscriptKind.Assistant, At = Local(11, 30), Text = "Starting." } };
        for (var i = 0; i < 15; i++)
        {
            feed.Add(Err(SpendLimit, Local(11, 38).AddMinutes(i * 39 / 14.0)) with { Index = feed.Count });
        }
        feed.Add(new TranscriptEntry { Index = feed.Count, Kind = TranscriptKind.Assistant, At = Local(12, 20), Text = "Back again." });
        return feed;
    }

    [Fact]
    public async Task FifteenIdenticalErrorsAreOneRowWithTheCountAndBothTimes()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, SpendLimitFeed());
        await held.OpenAsync();

        var frame = Draw(renderer, held.Chat);
        frame.Texts.Count(t => t.Text.Contains("spend limit", StringComparison.Ordinal)).ShouldBe(1);
        var count = frame.Texts.Where(t => t.Text.Contains("x15", StringComparison.Ordinal)).ToArray();
        count.Length.ShouldBe(1);
        count[0].Text.ShouldContain("11:38-12:17");
        frame.Has("Starting.").ShouldBeTrue();
        frame.Has("Back again.").ShouldBeTrue();
    }

    [Fact]
    public async Task ADifferentErrorOrTheSameTextAfterAnotherEntryStartsANewRow()
    {
        var feed = new List<TranscriptEntry>
        {
            Err(SpendLimit, Local(11, 38)), Err(SpendLimit, Local(11, 39)),
            Err("API Error: overloaded", Local(11, 40)),
            Err(SpendLimit, Local(11, 41)),
            new() { Index = 0, Kind = TranscriptKind.Assistant, At = Local(11, 42), Text = "Hello." },
            Err(SpendLimit, Local(11, 43)),
        };
        for (var i = 0; i < feed.Count; i++)
        {
            feed[i] = feed[i] with { Index = i };
        }
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, feed);
        await held.OpenAsync();

        var frame = Draw(renderer, held.Chat);
        frame.Texts.Count(t => t.Text.Contains("spend limit", StringComparison.Ordinal)).ShouldBe(3);
        frame.Texts.Count(t => t.Text.Contains("overloaded", StringComparison.Ordinal)).ShouldBe(1);
        var counts = frame.Texts.Where(t => t.Text.Contains("x2,", StringComparison.Ordinal)).ToArray();
        counts.Length.ShouldBe(1);
        counts[0].Text.ShouldContain("11:38-11:39");
        frame.Texts.Any(t => t.Text.Contains("x1,", StringComparison.Ordinal)).ShouldBeFalse("a single error has no count");
    }

    [Fact]
    public async Task ASixteenthErrorLaterUpdatesTheSameRowAndBuildsOnlyIt()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, SpendLimitFeed());
        await held.OpenAsync();
        Draw(renderer, held.Chat);

        await held.AppendAsync(Err(SpendLimit, Local(12, 30)));
        held.Chat.Render(Bounds);
        var frame = Draw(renderer, held.Chat);
        frame.Texts.Count(t => t.Text.Contains("spend limit", StringComparison.Ordinal)).ShouldBe(1);
        var count = frame.Texts.Single(t => t.Text.Contains("x16", StringComparison.Ordinal));
        count.Text.ShouldContain("11:38-12:30");
        // The fold replaced the run's entry: its row is the one built, the rest came from the cache.
        held.Chat.Tree();
        held.Chat.RowsBuilt.ShouldBe(0);
    }

    [Fact]
    public async Task TheRunsRowIsTheOnlyOneBuiltWhenTheSixteenthArrives()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, SpendLimitFeed());
        await held.OpenAsync();
        held.Chat.Tree();

        await held.AppendAsync(Err(SpendLimit, Local(12, 30)));
        held.Chat.Tree();
        held.Chat.RowsBuilt.ShouldBe(1);
    }
}
