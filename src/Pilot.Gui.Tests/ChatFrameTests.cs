using System.Diagnostics;
using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL907: the Chat page lays out only the rows in view, so a frame's cost does not grow with the watchtower's transcript. The page keeps
/// one row node an entry across frames (DIR.Lib's measure memo and scroll cull key on the node's reference), so a transcript of two
/// thousand entries is drawn as fast as one of twenty. The frames are CPU-rendered at 1600x900, the best of several after a warm-up as the
/// flight pages' budget test takes them; nothing here waits on the clock (the feed is read on a nudge and awaited by the page's own change
/// signal) and no process runs.
/// </summary>
public sealed partial class ChatFrameTests
{
    /// <summary>The longest a frame of the Chat page may take (the best of the frames), in milliseconds: the GUI frame budget of the admission views' test.</summary>
    private const double FrameBudgetMs = 250;

    private static readonly DateTimeOffset At = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private const string LongLine = "C:\\Users\\SebastianGodelet\\source\\repos\\sharpastro\\pilot\\.claude\\worktrees\\pil907\\src\\Pilot.Ui\\TranscriptEntryView.cs:228 and a path that is far past a column";

    // A transcript of `count` entries in a repeating run of eight: the person's prompt, Claude's Markdown (a list, a code span, a word with no
    // break in it), two tool calls with what they gave back (one of them thirty lines of output), a short reply and a long one. The feed's
    // indices are the positions, as the node numbers them.
    private static List<TranscriptEntry> Transcript(int count)
    {
        var feed = new List<TranscriptEntry>(count);
        for (var i = 0; feed.Count < count; i++)
        {
            var at = At.AddSeconds(feed.Count * 7);
            TranscriptEntry[] run =
            [
                new() { Index = 0, Kind = TranscriptKind.User, At = at, Text = $"Please look at flight {i}: why did its last leg fail on the node, and what would you change? {LongLine}" },
                new() { Index = 0, Kind = TranscriptKind.Assistant, At = at, Text = $"The leg {i} failed on a restore.\n\n- **Cause:** the package pin is `DIR.Lib 11.46.*`, not 11.54.\n- **Where:** {LongLine}\n- **Next:** pin it and run the check again.\n\nI will start with the pin." },
                new() { Index = 0, Kind = TranscriptKind.ToolUse, At = at, Tool = "Bash", Text = "dotnet build", Description = $"Build the solution ({i})", ToolUseId = $"call-{i}-a" },
                new() { Index = 0, Kind = TranscriptKind.ToolResult, At = at, Text = string.Join('\n', Enumerable.Range(0, 30).Select(l => $"  line {l} of the build output for {i}: {LongLine}")), ToolUseId = $"call-{i}-a" },
                new() { Index = 0, Kind = TranscriptKind.ToolUse, At = at, Tool = "Read", Text = "src/Pilot.Ui/ChatView.cs", ToolUseId = $"call-{i}-b" },
                new() { Index = 0, Kind = TranscriptKind.ToolResult, At = at, Text = "namespace Pilot.Ui;\n\ninternal sealed class ChatView", ToolUseId = $"call-{i}-b" },
                new() { Index = 0, Kind = TranscriptKind.Assistant, At = at, Text = "Pinned." },
                new() { Index = 0, Kind = TranscriptKind.Assistant, At = at, Text = string.Join("\n\n", Enumerable.Range(0, 6).Select(p => $"Paragraph {p} of the report on flight {i}: the build is green, the tests pass and nothing is left on the plane. {LongLine}")) },
            ];
            foreach (var entry in run)
            {
                if (feed.Count < count)
                {
                    feed.Add(entry with { Index = feed.Count });
                }
            }
        }
        return feed;
    }

    // A Chat page holding the feed, read through its own follow: the feed's reads are awaited on the page's change signal (and a deadline), never on a clock.
    // Tail is what the node says beside the entries (the reply in progress, the lines queued behind it); a test sets it and reads again.
    private sealed class Held(RgbaImageRenderer renderer, List<TranscriptEntry> feed)
    {
        public ChatView<RgbaImage> Chat { get; } = new(renderer) { FontPath = SessionsCapture.FontPath };
        public ChatPage Tail { get; set; } = new() { Entries = [], Total = 0 };
        public List<TranscriptEntry> Feed => feed;
        private TaskCompletionSource _changed = NewSignal();
        private Func<bool> _arrived = () => true;

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OpenAsync()
        {
            Chat.Load = (after, _) => Task.FromResult(Tail with { Entries = [.. feed.Where(e => e.Index > after)], Total = feed.Count });
            Chat.OnSend = _ => Task.FromResult<string?>(null);
            Chat.OnAnswerNow = _ => Task.FromResult<string?>(null);
            Chat.Changed = () =>
            {
                if (_arrived())
                {
                    _changed.TrySetResult();
                }
            };
            var want = feed.Count;
            await ReadAsync(() => Chat.Entries.Count >= want);
        }

        // Has the page read the node again, and waits for the change signal that says `arrived`.
        public async Task ReadAsync(Func<bool> arrived)
        {
            _changed = NewSignal();
            _arrived = arrived;
            Chat.Open();
            await _changed.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Chat.Close();
        }

        // Adds an entry to the feed and has the page read it now.
        public Task AppendAsync(TranscriptEntry entry)
        {
            feed.Add(entry with { Index = feed.Count });
            var want = feed.Count;
            return ReadAsync(() => Chat.Entries.Count >= want);
        }
    }

    private static readonly RectF32 Bounds = new(0, 0, 1600, 900);

    private static double BestFrameMs(ChatView<RgbaImage> chat)
    {
        // The first frames load the font and fill the caches; the page's steady state is what a person waits on.
        chat.Render(Bounds);
        chat.Render(Bounds);
        var times = new List<double>();
        // The best frame, not the median: a loaded machine only ever adds to a frame's time (as AdmissionViewsTests takes it).
        for (var i = 0; i < 9; i++)
        {
            var watch = Stopwatch.StartNew();
            chat.Render(Bounds);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        return times.Min();
    }

    [Fact]
    public async Task AFrameOfTwoThousandEntriesIsWithinTheBudgetAndNearOneOfTwenty()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var small = new Held(renderer, Transcript(20));
        await small.OpenAsync();
        var big = new Held(renderer, Transcript(2000));
        await big.OpenAsync();

        var few = BestFrameMs(small.Chat);
        var many = BestFrameMs(big.Chat);
        TestContext.Current.SendDiagnosticMessage($"chat frame at 1600x900: 20 entries {few:F1} ms, 2000 entries {many:F1} ms");

        many.ShouldBeLessThan(FrameBudgetMs);
        // The rows in view are drawn either way; what the long transcript adds is the stack's walk of its row references, not their layout.
        many.ShouldBeLessThan(few * Multiple + Floor);
    }

    // Chosen from the measured frames (see the commit): well above what a loaded CI runner adds to the two frames, far below the 100x of laying every row out.
    private const double Multiple = 4;
    private const double Floor = 25;

    [Fact]
    public async Task AnUnchangedEntrysRowIsTheSameNodeAcrossFramesAndAnAppendBuildsOnlyItsOwnRow()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var held = new Held(renderer, Transcript(2000));
        await held.OpenAsync();
        var chat = held.Chat;

        var first = Rows(chat.Tree());
        chat.RowsBuilt.ShouldBeGreaterThan(1000, "the first frame builds the rows");
        var second = Rows(chat.Tree());
        chat.RowsBuilt.ShouldBe(0, "an unchanged transcript builds nothing");
        second.Length.ShouldBe(first.Length);
        for (var i = 0; i < first.Length; i++)
        {
            second[i].ShouldBeSameAs(first[i], $"row {i}");
        }

        await held.AppendAsync(new TranscriptEntry { Index = 0, Kind = TranscriptKind.Assistant, At = At, Text = "One more thing: the flight landed." });
        var third = Rows(chat.Tree());
        chat.RowsBuilt.ShouldBe(1, "the appended entry's row is the only one built");
        third.Length.ShouldBe(first.Length + 1);
        for (var i = 0; i < first.Length; i++)
        {
            third[i].ShouldBeSameAs(first[i], $"row {i} after the append");
        }
    }

    // The page's feed: the stack with the most children, which is the list of rows (the head and foot stacks hold a handful).
    private static Layout.Node[] Rows(Layout.Node page)
    {
        Layout.Node.Stack? widest = null;
        void Walk(Layout.Node node)
        {
            switch (node)
            {
                case Layout.Node.Stack stack:
                    if (widest is null || stack.Children.Length > widest.Children.Length)
                    {
                        widest = stack;
                    }
                    foreach (var child in stack.Children)
                    {
                        Walk(child);
                    }
                    break;
                case Layout.Node.Overlay overlay:
                    Walk(overlay.Base);
                    Walk(overlay.Top);
                    break;
                case Layout.Node.Anchored anchored:
                    Walk(anchored.Child);
                    break;
            }
        }
        Walk(page);
        return [.. widest.ShouldNotBeNull().Children];
    }

    private static TranscriptEntry Call(int index, string id, string tool = "Bash") =>
        new() { Index = index, Kind = TranscriptKind.ToolUse, Tool = tool, Text = "run it", ToolUseId = id, At = At };

    private static TranscriptEntry Result(int index, string id, TranscriptKind kind = TranscriptKind.ToolResult) =>
        new() { Index = index, Kind = kind, Text = "done", ToolUseId = id, At = At };

    [Fact]
    public void ARowIsBuiltAgainOnlyForWhatChangedItAndResetDropsThemAll()
    {
        var view = new TranscriptEntryView();
        List<TranscriptEntry> entries =
        [
            new() { Index = 0, Kind = TranscriptKind.User, Text = "go", At = At },
            Call(1, "a"),
            new() { Index = 2, Kind = TranscriptKind.Assistant, Text = "Working on **it**.", At = At },
            Result(3, "a"),
            Call(4, "b"),
        ];
        var rows = view.Rows(entries, live: false);
        rows.Count.ShouldBe(4, "the first call's result is shown under it");
        view.RowsBuilt.ShouldBe(4);

        // Nothing changed: every row is the node it was.
        var again = view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(0);
        for (var i = 0; i < rows.Count; i++)
        {
            again[i].ShouldBeSameAs(rows[i]);
        }

        // The result of the open call arrives: its call's row is built again, and the result takes no row of its own.
        entries.Add(Result(5, "b"));
        var arrived = view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(1);
        arrived.Count.ShouldBe(4);
        arrived.Where((n, i) => !ReferenceEquals(n, rows[i])).Count().ShouldBe(1);
        arrived[3].ShouldNotBeSameAs(rows[3]);

        // A failed result is another row for its call, then the same again.
        entries[5] = Result(5, "b", TranscriptKind.Error);
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(1);

        // A call's fold: toggled, its row alone changes; toggled back it is built again (the cache holds the one state).
        var before = view.Rows(entries, live: false);
        view.Toggle(1);
        var opened = view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(1);
        opened[1].ShouldNotBeSameAs(before[1]);
        view.Toggle(1);
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(1);

        // Live or finished counts for a call with nothing back yet only: here none is open, so no row changes.
        view.Rows(entries, live: true);
        view.RowsBuilt.ShouldBe(0);
        entries.Add(Call(6, "c"));
        view.Rows(entries, live: true);
        view.RowsBuilt.ShouldBe(1);
        view.Rows(entries, live: true);
        view.RowsBuilt.ShouldBe(0);
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(1, "the running call stops running");

        // A subagent the call started: its row changes, the others do not.
        view.Subagents = [new SubagentRow { Id = "x", Type = "Explore", ToolUseId = "c" }];
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(1);
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(0);

        // The list ends sooner: the rows past its end go, and come back built when the entries do.
        entries.RemoveAt(entries.Count - 1);
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(0);
        entries.Add(Call(6, "c"));
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(1);

        // Reset (a new epoch) drops every row.
        view.Reset();
        view.Rows(entries, live: false);
        view.RowsBuilt.ShouldBe(5);
    }

    [Fact]
    public void APaletteSwitchBuildsEveryRowAgain()
    {
        var view = new TranscriptEntryView();
        List<TranscriptEntry> entries = [new() { Index = 0, Kind = TranscriptKind.User, Text = "go", At = At }, Call(1, "a")];
        var was = Palette.Current;
        try
        {
            Palette.Current = Palette.Standard;
            var day = view.Rows(entries, live: false);
            Palette.Current = Palette.Night;
            var night = view.Rows(entries, live: false);
            view.RowsBuilt.ShouldBe(2);
            night[0].ShouldNotBeSameAs(day[0]);
            view.Rows(entries, live: false);
            view.RowsBuilt.ShouldBe(0);
        }
        finally
        {
            Palette.Current = was;
        }
    }
}
