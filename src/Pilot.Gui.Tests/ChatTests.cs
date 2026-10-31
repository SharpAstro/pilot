using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Chat page (#207): the conversation with the node's watchtower, read from the chat feed. The person's messages and
/// the replies in Markdown, each tool call folded to a line, a working… line, a divider where a fresh watchtower began, the
/// messages not yet taken, and a pending question with its choices as buttons; a box at the foot that sends (Enter) or starts
/// a new line (Shift+Enter), a refusal said in the chat.
/// </summary>
public sealed class ChatTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 9, 40, 12, TimeSpan.Zero);

    private static TranscriptEntry[] Conversation() =>
    [
        new() { Index = 0, Kind = TranscriptKind.User, Text = "let's launch a flight for issue 12", At = At },
        new() { Index = 1, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "pilot flight new", Description = "Start the flight", ToolUseId = "t1", At = At.AddSeconds(3) },
        new() { Index = 2, Kind = TranscriptKind.ToolResult, Text = "result-line-1\nresult-line-2", ToolUseId = "t1" },
        new() { Index = 3, Kind = TranscriptKind.Assistant, Text = "Started **PIL12** for issue 12.\n\n- the flight is flying\n- its planner is working", At = At.AddSeconds(9) },
        new() { Index = 4, Kind = TranscriptKind.Divider, Text = "A fresh watchtower started: its brief changed", At = At.AddMinutes(5) },
        new() { Index = 5, Kind = TranscriptKind.User, Text = "and the next one?", At = At.AddMinutes(6) },
    ];

    private static ChatPage Page(TranscriptEntry[]? entries = null, bool working = false, string? question = null, string[]? choices = null, string[]? queued = null) => new()
    {
        Entries = entries ?? Conversation(), Total = (entries ?? Conversation()).Length, Working = working, PendingQuestion = question, Choices = choices, Queued = queued,
    };

    private static ChatView<RgbaImage> NewChat(RgbaImageRenderer renderer, ChatPage? page = null, Func<string, Task<string?>>? send = null)
    {
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (after, _) => Task.FromResult(after < 0 ? page ?? Page() : new ChatPage { Entries = [], Total = (page ?? Page()).Total });
        chat.OnSend = send ?? (_ => Task.FromResult<string?>(null));
        return chat;
    }

    /// <summary>Opens the chat and waits for the follow's first read to land.</summary>
    private static async Task Read(ChatView<RgbaImage> chat, int entries)
    {
        chat.Open();
        for (var i = 0; i < 100 && chat.Entries.Count < entries; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        chat.Close();
        chat.Entries.Count.ShouldBe(entries);
    }

    private static ImmutableArray<Layout.ArrangedNode<int>> Arranged(ChatView<RgbaImage> chat, int rows = 80) =>
        Layout.Engine.Arrange(chat.Tree(), new Rect<int>(0, 0, 160, rows), CellMeasureContext.PixelAuthored);

    // What the page says, in reading order, one space between the texts: wrapped prose and Markdown are a leaf a word.
    private static string Described(ChatView<RgbaImage> chat, int rows = 80) =>
        string.Join(' ', Arranged(chat, rows).Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()));

    private static void Click(ChatView<RgbaImage> chat, string action)
    {
        chat.Render(new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
    }

    private static readonly NodeMark Own = new("here", "/sock", "DE1", Here: true);
    private static readonly NodeMark Other = new("se1", "http://se1", "SE1", Here: false);

    private static ChatView<RgbaImage> TwoNodes(RgbaImageRenderer renderer, NodeFilter nodes, List<string> read)
    {
        var chat = NewChat(renderer);
        chat.Nodes = nodes;
        nodes.Choices = [Own, Other];
        chat.LoadOn = (node, after, _) =>
        {
            read.Add($"{node.Name}@{after}");
            return Task.FromResult(new ChatPage { Entries = after < 0 ? [new TranscriptEntry { Index = 0, Kind = TranscriptKind.Assistant, Text = "from SE1", At = At }] : [], Total = 1 });
        };
        return chat;
    }

    [Fact]
    public async Task TheHeaderNamesWhoseWatchtowerItIsOwnNodeAndOtherAndSwitchingReadsTheOthersFeedFromItsStart()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var nodes = new NodeFilter();
        var read = new List<string>();
        var chat = TwoNodes(renderer, nodes, read);
        await Read(chat, 6);

        Described(chat).ShouldContain("fleet chat");
        Described(chat).ShouldContain("held by DE1's watchtower");
        chat.Input.Placeholder.ShouldBe("Message the fleet chat");

        chat.Open();
        Click(chat, "chat:node:se1");
        for (var i = 0; i < 100 && chat.Entries.Count != 1; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        chat.Close();

        chat.Node.ShouldBe(Other);
        chat.Entries.Single().Text.ShouldBe("from SE1");
        read[0].ShouldBe("SE1@-1");
        Described(chat).ShouldContain("watchtower · SE1");
        Described(chat).ShouldNotContain("DE1's watchtower");
        chat.Tree();
        chat.Input.Placeholder.ShouldBe("Message SE1's watchtower");
        nodes.Picked.ShouldBeEmpty();
    }

    [Fact]
    public void TheTopBarOfTheChatPageHasOneChosenNodePillWhateverTheWindowFilterPicks()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var nodes = new NodeFilter();
        var chat = TwoNodes(renderer, nodes, []);
        nodes.Restore(["here", "se1"]);

        var text = Described(chat);

        text.ShouldNotContain("All nodes");
        text.ShouldContain("DE1 (this)");
        text.ShouldContain("SE1");
        chat.Render(new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith("chat:node:")).ShouldBe(["chat:node:here", "chat:node:se1"]);
        chat.Node.ShouldBeNull();
        nodes.Picked.Count.ShouldBe(2);
    }

    [Fact]
    public void ANodeWhoseChatCannotBeReadSaysSoOnItsPill()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var nodes = new NodeFilter();
        var chat = TwoNodes(renderer, nodes, []);
        chat.CanRead = _ => false;

        Described(chat).ShouldContain("SE1 · chat not readable");
    }

    [Fact]
    public async Task NewWatchtowerAsksFirstThenRestartsItAndKeepItLeavesIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer);
        var restarts = 0;
        chat.OnRestart = () =>
        {
            restarts++;
            return Task.FromResult<string?>(null);
        };
        await Read(chat, 6);

        // One press only asks: the restart stops what the watchtower is doing.
        Click(chat, "chat:restart");
        restarts.ShouldBe(0);
        Described(chat).ShouldContain("Stop it and start fresh");
        Click(chat, "chat:restart-no");
        restarts.ShouldBe(0);
        Described(chat).ShouldContain("New watchtower");

        await Capture("chat-restart");
        Click(chat, "chat:restart");
        await Capture("chat-restart-confirm", confirming: true);
        Click(chat, "chat:restart-yes");
        restarts.ShouldBe(1);
        Described(chat).ShouldContain("New watchtower");

        // A page with no restart to call shows no button.
        chat.OnRestart = null;
        Described(chat).ShouldNotContain("New watchtower");
    }

    private static async Task Capture(string name, bool confirming = false)
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var (width, height) in new[] { (700, 500), (1600, 600) })
        {
            using var shot = new RgbaImageRenderer((uint)width, (uint)height);
            var view = NewChat(shot);
            view.OnRestart = () => Task.FromResult<string?>(null);
            await Read(view, 6);
            if (confirming)
            {
                Click(view, "chat:restart");
            }
            view.Render(new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(dir, $"{name}-{width}.png"), SharpAstro.Png.PngWriter.Encode(shot.Surface.Pixels, width, height));
        }
    }

    [Fact]
    public async Task AMessageRelayedThroughTheNodeIsShownAsFromItsSenderWithoutItsEnvelope()
    {
        // The owner (2026-10-29): "why is it still this weird cross xml thing?" The node relays words wrapped in Claude Code's envelope.
        const string fromYou = """
            <cross-session-message from="uds:\\.\pipe\LOCAL\cc-msg-4aca" from-name="controller" from-mode="prompting">
            https://github.com/SharpAstro/pilot/pull/457 this one has priority.

            (Sent through the pilot node. The sender cannot be messaged back: answer in your own reply, not with SendMessage.)
            </cross-session-message>
            """;
        const string fromWorker = """
            <cross-session-message from="uds:x" from-name="de1-tia1910-measure-spread" from-mode="prompting">
            measured: 1.6 frames a second

            (Sent through the pilot node. To answer, run: pilot say 2ca9 "<your answer>". Not SendMessage: it cannot reach the sender.)
            </cross-session-message>
            """;
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, Page([
            new() { Index = 0, Kind = TranscriptKind.User, Text = fromYou, At = At },
            new() { Index = 1, Kind = TranscriptKind.User, Text = fromWorker, At = At.AddMinutes(1) },
        ]));
        await Read(chat, 2);

        var text = Described(chat);

        // The person's words as theirs (a long link is drawn in pieces, so it is checked by its ends).
        text.ShouldContain("you https:");
        text.ShouldContain("457 this one has priority.");
        text.ShouldContain("de1-tia1910-measure-spread measured: 1.6 frames a second");
        text.ShouldNotContain("cross-session-message");
        text.ShouldNotContain("Sent through the pilot node");
        CrossSessionMessage.Parse("just words").ShouldBeNull();
    }

    [Fact]
    public async Task TheChatShowsAPersonsMessageAReplyInMarkdownAFoldedCallAWorkingLineAndTheDivider()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, Page(working: true));
        await Read(chat, 6);

        var text = Described(chat);

        // The person's words, labelled as theirs.
        text.ShouldContain("you let's launch a flight for issue 12");
        // The reply's Markdown is drawn, not shown as its source: the bold's words without the stars, the list's items apart.
        text.ShouldContain("watchtower Started PIL12 for issue 12.");
        text.ShouldNotContain("**");
        text.ShouldContain("the flight is flying");
        text.ShouldContain("its planner is working");
        // The tool call is one folded line: its state, its tool, what it says it does; the output waits behind a click.
        text.ShouldContain("Start the flight");
        text.ShouldContain("✓");
        text.ShouldContain("Bash");
        text.ShouldNotContain("result-line-1");
        // Where a fresh watchtower began, and that it is at work.
        text.ShouldContain("A fresh watchtower started: its brief changed");
        text.ShouldContain("● working…");
        // In their order, the working line last.
        var order = new[] { "let's launch", "Start the flight", "PIL12", "A fresh watchtower started", "and the next one?", "● working…" }
            .Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToArray();
        order.ShouldAllBe(i => i >= 0);
        order.ShouldBe([.. order.OrderBy(i => i)]);
    }

    [Fact]
    public async Task ACallUnfoldsToItsOutputOnAClick()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer);
        await Read(chat, 6);

        Click(chat, "transcript:call:1");

        Described(chat).ShouldContain("result-line-2");
        Click(chat, "transcript:call:1");
        Described(chat).ShouldNotContain("result-line-2");
    }

    [Fact]
    public async Task AReplyIsShownWholeNotFoldedAtFortyLines()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var reply = string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"paragraph-{i}"));
        var chat = NewChat(renderer, Page([new TranscriptEntry { Index = 0, Kind = TranscriptKind.Assistant, Text = reply }]));
        await Read(chat, 1);

        var text = Described(chat, 400);

        text.ShouldContain("paragraph-60");
        text.ShouldNotContain("click to expand");
    }

    [Fact]
    public async Task NoWorkingLineWhileTheWatchtowerIsIdle()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, Page(working: false));
        await Read(chat, 6);

        Described(chat).ShouldNotContain("working…");
    }

    [Fact]
    public async Task APendingQuestionHasItsChoicesAsButtonsAndAClickSendsTheChoice()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sent = new List<string>();
        var chat = NewChat(renderer, Page(question: "Which repo should the flight fly in?", choices: ["tianwen", "pilot"]), send: text =>
        {
            sent.Add(text);
            return Task.FromResult<string?>(null);
        });
        await Read(chat, 6);

        var text = Described(chat);
        text.ShouldContain("Which repo should the flight fly in?");
        text.ShouldContain("tianwen");
        text.ShouldContain("pilot");

        // A button each, in the order the question gave them; the second one answers with its label.
        var arranged = Arranged(chat);
        arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith("chat:choice:", StringComparison.Ordinal))
            .ShouldBe(["chat:choice:1", "chat:choice:2"]);
        Click(chat, "chat:choice:2");
        for (var i = 0; i < 100 && sent.Count == 0; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // With the question it answers, so a reply like "Leave it" is never read without it.
        sent.ShouldBe(["pilot (my answer to: Which repo should the flight fly in?)"]);
    }

    [Fact]
    public async Task AChoiceIsRunThroughTheHostsTracker()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sent = new List<string>();
        var chat = NewChat(renderer, Page(question: "Which?", choices: ["a", "b"]), send: text =>
        {
            sent.Add(text);
            return Task.FromResult<string?>(null);
        });
        var tracked = new List<string>();
        chat.Track = (run, what) =>
        {
            tracked.Add(what);
            _ = run();
        };
        await Read(chat, 6);

        Click(chat, "chat:choice:1");

        tracked.ShouldBe(["chat choice a"]);
        for (var i = 0; i < 100 && sent.Count == 0; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        sent.ShouldBe(["a (my answer to: Which?)"]);
    }

    [Fact]
    public async Task AQuestionTheLastReplyAlreadySaysIsNotSaidAgainOverItsButtons()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        TranscriptEntry[] entries = [new() { Index = 0, Kind = TranscriptKind.Assistant, Text = "Two repos fit.\nWhich repo should the flight fly in?", At = At }];
        var chat = NewChat(renderer, Page(entries, question: "Which repo should the flight fly in?", choices: ["tianwen", "pilot"]));
        await Read(chat, 1);

        var text = Described(chat);

        text.Split("Which repo should the flight fly in?").Length.ShouldBe(2, "the reply says it; the card holds only the choices");
        text.ShouldNotContain("The watchtower asks");
        text.ShouldContain("tianwen");
    }

    [Fact]
    public async Task NoQuestionNoButtons()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer);
        await Read(chat, 6);

        Arranged(chat).Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().ShouldNotContain(b => b.Action.StartsWith("chat:choice:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatWasSaidAndNotYetTakenWaitsDimmedAtTheEnd()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, Page(queued: ["one more thing"]));
        await Read(chat, 6);

        var text = Described(chat);

        text.ShouldContain("queued");
        text.ShouldContain("not read yet");
        text.IndexOf("one more thing", StringComparison.Ordinal).ShouldBeGreaterThan(text.IndexOf("and the next one?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFollowReadsOnlyWhatCameAfterWhatItHas()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var afters = new List<int>();
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (after, _) =>
        {
            afters.Add(after);
            return Task.FromResult(after < 0
                ? Page(Conversation()[..3])
                : new ChatPage { Entries = [.. Conversation().Where(e => e.Index > after)], Total = 6 });
        };

        chat.Open();
        for (var i = 0; i < 100 && chat.Entries.Count < 3; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        // A message just sent asks for a read at once, not at the next look two seconds on.
        chat.Refresh();
        for (var i = 0; i < 100 && chat.Entries.Count < 6; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        chat.Close();

        afters.Take(2).ShouldBe([-1, 2]);
        chat.Entries.Select(e => e.Index).ShouldBe([0, 1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task AFeedThatRenumberedUnderTheHeldIndexIsReadAgainFromTheStart()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var afters = new List<int>();
        var shrunk = false;
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        // An earlier watchtower's transcript goes (Claude Code deletes old sessions): the feed is the last three entries, renumbered from 0.
        var remaining = Conversation()[3..].Select((e, i) => e with { Index = i, Text = "after the shrink " + i }).ToArray();
        chat.Load = (after, _) =>
        {
            afters.Add(after);
            return Task.FromResult(!shrunk
                ? (after < 0 ? Page() : new ChatPage { Entries = [], Total = 6 })
                : after < 0 ? Page(remaining) : new ChatPage { Entries = [.. remaining.Where(e => e.Index > after)], Total = remaining.Length });
        };

        await Read(chat, 6);
        shrunk = true;
        chat.Open();
        for (var i = 0; i < 100 && chat.Entries.Count != 3; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        chat.Close();

        chat.Entries.Select(e => e.Text).ShouldBe(["after the shrink 0", "after the shrink 1", "after the shrink 2"]);
        // The first read, then after the held last index (5), which finds the feed shorter: read from the start.
        afters.Take(3).ShouldBe([-1, 5, -1]);
    }

    [Fact]
    public async Task ClosingStopsTheFollowAndReopeningReadsOnFromWhatItHolds()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var afters = new List<int>();
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (after, _) =>
        {
            afters.Add(after);
            return Task.FromResult(after < 0 ? Page(Conversation()[..2]) : new ChatPage { Entries = [], Total = 2 });
        };

        await Read(chat, 2);
        chat.IsOpen.ShouldBeFalse();
        chat.Open();
        for (var i = 0; i < 100 && afters.Count < 2; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        chat.Close();

        afters.Take(2).ShouldBe([-1, 1]);
    }

    [Fact]
    public async Task AFeedThatCannotBeReadSaysSoInTheHeader()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (_, _) => throw new HttpRequestException("the node is not answering");

        chat.Open();
        string text = "";
        for (var i = 0; i < 100 && !text.Contains("the node is not answering", StringComparison.Ordinal); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            text = Described(chat);
        }
        chat.Close();

        text.ShouldContain("the node is not answering");
    }

    [Fact]
    public async Task TheKeysHintIsSaidOnceUnderTheBoxAndNotAgainInItsPlaceholder()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer);
        await Read(chat, Conversation().Length);

        Described(chat).Split("Enter sends").Length.ShouldBe(2, "the hint under the box is the one that says it");
        chat.Input.Placeholder.ShouldNotBeNull().ShouldNotContain("Enter");
    }

    private static async Task<string> TypeAndEnter(ChatView<RgbaImage> chat, params string[] lines)
    {
        var tracker = new BackgroundTaskTracker();
        var focus = chat.Ui.Focus;
        chat.Tree();
        focus.Focus(chat.Input);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                chat.HandleNewLine(new InputEvent.KeyDown(InputKey.Enter, InputModifier.Shift)).ShouldBeTrue();
            }
            TextInputInteraction.HandleText(chat.Input, lines[i]);
        }
        TextInputInteraction.HandleKey(InputKey.Enter, InputModifier.None, new TextInputInteraction.KeyContext(tracker, focus, () => { }));
        await tracker.DrainAsync();
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task EnterSendsWhatWasTypedThroughTheNodesTellAndEmptiesTheBox()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sent = new List<string>();
        var chat = NewChat(renderer, send: text =>
        {
            sent.Add(text);
            return Task.FromResult<string?>(null);
        });

        await TypeAndEnter(chat, "launch a flight for pilot#7");

        sent.ShouldBe(["launch a flight for pilot#7"]);
        chat.Input.Text.ShouldBe("");
        chat.Notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task ShiftEnterStartsANewLineAndEnterSendsAllOfThem()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sent = new List<string>();
        var chat = NewChat(renderer, send: text =>
        {
            sent.Add(text);
            return Task.FromResult<string?>(null);
        });
        var tracker = new BackgroundTaskTracker();
        var focus = chat.Ui.Focus;
        chat.Tree();
        focus.Focus(chat.Input);

        TextInputInteraction.HandleText(chat.Input, "first line");
        chat.HandleNewLine(new InputEvent.KeyDown(InputKey.Enter, InputModifier.Shift)).ShouldBeTrue();
        // The line break is in the box's text, the caret on the second line, and nothing was sent.
        chat.Input.Text.ShouldBe("first line\n");
        chat.Input.LineCount.ShouldBe(2);
        chat.Input.Line.ShouldBe(1);
        sent.ShouldBeEmpty();
        TextInputInteraction.HandleText(chat.Input, "second line");
        TextInputInteraction.HandleKey(InputKey.Enter, InputModifier.None, new TextInputInteraction.KeyContext(tracker, focus, () => { }));
        await tracker.DrainAsync();

        sent.ShouldBe(["first line\nsecond line"]);
        chat.Input.Text.ShouldBe("");
    }

    [Fact]
    public void ShiftEnterIsOnlyANewLineWhileTheBoxHasTheKeyboard()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer);
        chat.Tree();

        chat.HandleNewLine(new InputEvent.KeyDown(InputKey.Enter, InputModifier.Shift)).ShouldBeFalse();
        chat.Ui.Focus.Focus(chat.Input);
        // A plain Enter is a send, never a new line.
        chat.HandleNewLine(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeFalse();
        chat.HandleNewLine(new InputEvent.KeyDown(InputKey.Enter, InputModifier.Shift)).ShouldBeTrue();
        // Alt+Enter, for a terminal that cannot tell Shift.
        chat.HandleNewLine(new InputEvent.KeyDown(InputKey.Enter, InputModifier.Alt)).ShouldBeTrue();
        chat.Input.LineCount.ShouldBe(3);
    }

    [Fact]
    public void BackspaceOnAnEmptyLineJoinsTheLineBefore()
    {
        var composer = new ChatComposer();
        composer.Input.InsertText("one\n");

        composer.Input.HandleKey(TextInputKey.Backspace);

        composer.Input.Text.ShouldBe("one");
        composer.Input.CursorPos.ShouldBe(3);
    }

    [Fact]
    public void APasteOfSeveralLinesIsOneMessageWithItsLineBreaks()
    {
        var composer = new ChatComposer();

        composer.Input.InsertText("a\nb\r\nc");

        composer.Input.LineCount.ShouldBe(3);
        composer.Message.ShouldBe("a\nb\nc");
    }

    [Fact]
    public void TheCaretMovesAcrossLinesAndALongMessageKeepsTheCap()
    {
        var composer = new ChatComposer();
        composer.Input.InsertText(string.Join('\n', Enumerable.Range(1, ChatComposer.MaxLines + 4).Select(i => $"line {i}")));

        composer.Input.Line.ShouldBe(ChatComposer.MaxLines + 3);
        composer.Input.HandleKey(TextInputKey.Up);
        composer.Input.Line.ShouldBe(ChatComposer.MaxLines + 2);
        composer.Input.MaxLines.ShouldBe(ChatComposer.MaxLines);
    }

    [Fact]
    public async Task ARefusalIsSaidInTheChatAndTheNextSendClearsIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var refuse = true;
        var chat = NewChat(renderer, send: _ => Task.FromResult(refuse ? "refused: the node is in maintenance" : (string?)null));
        await Read(chat, 6);

        await chat.SendAsync("hello?");

        chat.Notices.ShouldBe(["refused: the node is in maintenance · hello?"]);
        var text = Described(chat);
        text.ShouldContain("refused:");
        text.ShouldContain("the node is in maintenance");
        text.ShouldContain("hello?");

        refuse = false;
        await chat.SendAsync("hello again");
        chat.Notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyTheLastFewRefusalsAreKept()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, send: text => Task.FromResult<string?>($"refused: no · {text}"));

        for (var i = 0; i < 5; i++)
        {
            await chat.SendAsync($"m{i}");
        }

        chat.Notices.Count.ShouldBe(ChatView<RgbaImage>.NoticesKept);
        chat.Notices[^1].ShouldContain("m4");
    }

    [Fact]
    public async Task ASendThatThrowsIsARefusalNotACrash()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, send: _ => throw new HttpRequestException("no route to the node"));

        await chat.SendAsync("anyone there?");

        chat.Notices.ShouldHaveSingleItem().ShouldContain("no route to the node");
    }

    [Fact]
    public void WithNoHostToSendThePageHasNoBoxAndNoButtons()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        var arranged = Arranged(chat);

        arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor }).ShouldBeFalse();
        CellLayout.Describe(arranged).ShouldContain("The chat is read from a live node.");
    }

    [Fact]
    public void ABoxAtTheFootHoldsTheMessage()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer);

        var arranged = Arranged(chat);
        var input = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor });

        // At the foot: under every other thing on the page.
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ShouldAllBe(a => a.Bounds.Y <= input.Bounds.Y + input.Bounds.Height);
        // The box is the multi-line editor on the chat's own state (Console.Lib's TextInputs lists one-line fields only).
        input.Node.ShouldBeOfType<Layout.Node.Leaf>().Content.ShouldBeOfType<Layout.Content.TextEditor>().State.ShouldBe(chat.Input);
    }

    [Fact]
    public void TheChatIsOnTheRailAndOpeningItFollowsTheFeedAndLeavingStopsIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Chat.Load = (_, _) => Task.FromResult(Page([]));

        PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Chat).Label.ShouldBe("Chat");
        chrome.Show(PilotPage.Chat);
        chrome.Page.ShouldBe(PilotPage.Chat);
        chrome.Chat.IsOpen.ShouldBeTrue();
        chrome.Widgets.ShouldContain(chrome.Chat);

        chrome.Show(PilotPage.Home);
        chrome.Chat.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void TheWatchtowersShortcutOpensTheChat()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

        chrome.Cards.OnChat.ShouldNotBeNull().Invoke();

        chrome.Page.ShouldBe(PilotPage.Chat);
    }

    [Fact]
    public void ShiftEnterReachesTheBoxThroughTheWindowsOwnKeys()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Chat.Tree();
        chrome.Sessions.Ui.Focus.Focus(chrome.Chat.Input);

        chrome.HandleInput(new InputEvent.KeyDown(InputKey.Enter, InputModifier.Shift)).ShouldBeTrue();
        chrome.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeFalse();

        chrome.Chat.Input.Text.ShouldBe("\n");
    }

    [Fact]
    public async Task ACpuRenderPaintsTheChatWithItsChoicesAndBox()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Chat.Load = (_, _) => Task.FromResult(Page(working: true, question: "Which repo should the flight fly in?", choices: ["tianwen", "pilot"]));
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        chrome.Cards.CanTell = true;
        chrome.Show(PilotPage.Chat);
        for (var i = 0; i < 100 && chrome.Chat.Entries.Count < 6; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.Empty, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chrome.Chat.CollectPaintedRegions(regions);
        chrome.Show(PilotPage.Home);
        var actions = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        actions.ShouldContain("chat:choice:1");
        actions.ShouldContain("chat:choice:2");
        // The page's ground is painted over the room beside the rail, left of the panel.
        var px = renderer.Surface.Pixels;
        var ground = TranscriptPane.Ground;
        var at = (400 * 1280 + 400) * 4;
        (px[at], px[at + 1], px[at + 2]).ShouldBe((ground.Red, ground.Green, ground.Blue));
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            File.WriteAllBytes(capture, SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, 1280, 800));
        }
    }

    [Fact]
    public async Task AQuestionSitsUnderTheFeedAboveTheBoxAndCoversNoneOfIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var entries = Enumerable.Range(0, 30).Select(i => new TranscriptEntry
        {
            Index = i, Kind = TranscriptKind.Assistant, At = At.AddSeconds(i),
            Text = i == 29 ? "Three flights are in the air.\n\nShould I file the five pilot feature issues and start tianwen#1824?" : $"Reply {i}: a line long enough to wrap once the window is narrow, said by the watchtower about its flights.",
        }).ToArray();
        var chat = NewChat(renderer, Page(entries, question: "Should I file the five pilot feature issues and start tianwen#1824?",
            choices: ["Yes, file all five and take #1824", "File the five issues only", "Take #1824 only", "Leave it for now"]));
        await Read(chat, 30);

        var arranged = Layout.Engine.Arrange(chat.Tree(), new Rect<float>(0, 0, 1280, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        // The feed's scroll is the first; the question's card has its own under it.
        var feed = arranged.Where(a => a.Node.Scroll is not null).OrderBy(a => a.Bounds.Y).First().Bounds;
        var choice = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "chat:choice:1" }).Bounds;
        var box = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor }).Bounds;

        // The choices are below the feed's last pixel and above the box: nothing of the conversation is under them.
        choice.Y.ShouldBeGreaterThanOrEqualTo(feed.Y + feed.Height);
        (choice.Y + choice.Height).ShouldBeLessThanOrEqualTo(box.Y);

        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            var dir = Path.GetDirectoryName(capture) ?? ".";
            foreach (var (width, height) in new[] { (900, 700), (2000, 900) })
            {
                using var shot = new RgbaImageRenderer((uint)width, (uint)height);
                var view = NewChat(shot, Page(entries, question: "Should I file the five pilot feature issues and start tianwen#1824?",
                    choices: ["Yes, file all five and take #1824", "File the five issues only", "Take #1824 only", "Leave it for now"]));
                await Read(view, 30);
                view.Render(new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"chat-question-{width}.png"), SharpAstro.Png.PngWriter.Encode(shot.Surface.Pixels, width, height));
            }
        }
    }

    private const string LongQuestion =
        "Three flights are in the air and two more are waiting on a review. Before I file the five pilot feature issues I need to know " +
        "whether tianwen#1824 should start now, on the node that holds the training flights, or wait until the reviews have landed, " +
        "because starting it now takes the last free slot and the two waiting reviews would then queue behind it for about an hour. " +
        "If you would rather I file the issues first and leave the flight for later, say so; if the order does not matter to you, " +
        "I will file the five issues, start tianwen#1824, and tell you when the reviews are free to run.";

    private static readonly string[] LongChoices =
    [
        "Yes, file all five and take #1824 now",
        "File the five issues only, leave #1824",
        "Take #1824 only, file the issues later",
        "Wait for the two reviews, then do both",
        "Leave it for now",
    ];

    [Fact]
    public async Task ALongQuestionOnAShortWindowKeepsTheFeedAndStaysAboveTheBox()
    {
        var entries = Enumerable.Range(0, 30).Select(i => new TranscriptEntry
        {
            Index = i, Kind = TranscriptKind.Assistant, At = At.AddSeconds(i),
            Text = $"Reply {i}: a line long enough to wrap once the window is narrow, said by the watchtower about its flights.",
        }).ToArray();
        var capture = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } c ? c : null;

        foreach (var (width, height) in new[] { (900, 400), (1600, 900) })
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var chat = NewChat(renderer, Page(entries, question: LongQuestion, choices: LongChoices));
            await Read(chat, 30);

            // The card's cap follows the page's height, which a render tells it; a second one draws the bar the first one measured.
            chat.Render(new RectF32(0, 0, width, height));
            chat.Render(new RectF32(0, 0, width, height));
            if (capture is not null)
            {
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(capture) ?? ".", $"chat-ask-{width}x{height}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }

            var arranged = Layout.Engine.Arrange(chat.Tree(), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
            var box = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor }).Bounds;
            // Two scrolls: the feed above and, under it, the card's own.
            var scrolls = arranged.Where(a => a.Node.Scroll is not null).Select(a => a.Bounds).OrderBy(b => b.Y).ToList();
            scrolls.Count.ShouldBe(2);
            var (feed, card) = (scrolls[0], scrolls[1]);

            // The feed keeps room to read in, and the card (and so everything in it) ends above the box.
            if (height == 400)
            {
                feed.Height.ShouldBeGreaterThanOrEqualTo(100f);
            }
            (card.Y + card.Height).ShouldBeLessThanOrEqualTo(box.Y);
            card.Y.ShouldBeGreaterThanOrEqualTo(feed.Y + feed.Height);

            // The choices sit under the card's text, outside its scroll: every one in view, below the text, above the box.
            var pills = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("chat:choice:", StringComparison.Ordinal)).Select(a => a.Bounds).ToList();
            pills.Count.ShouldBe(LongChoices.Length);
            pills.ShouldAllBe(p => p.Y >= card.Y + card.Height && p.Y + p.Height <= box.Y);
            // The text keeps room for its header and a line or more, and the choices do not cost the page its box.
            card.Height.ShouldBeGreaterThanOrEqualTo(40f);
        }
    }

    [Fact]
    public async Task ATypedMessageIsWorkingAtOnceKeepsItsTextTakesNoSecondSendAndClearsWhenTheNodeTakesIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var node = new TaskCompletionSource<string?>();
        var sent = 0;
        var chat = NewChat(renderer, send: _ => { sent++; return node.Task; });
        await Read(chat, 6);
        chat.Input.InsertText("hello");

        var first = chat.SendAsync();
        Described(chat).ShouldContain("sending…");
        chat.Input.Text.ShouldBe("hello");
        await chat.SendAsync();
        sent.ShouldBe(1);

        node.SetResult(null);
        await first;
        chat.Input.Text.ShouldBe("");
        Described(chat).ShouldNotContain("sending…");
    }

    [Fact]
    public async Task ARefusedMessageStaysInTheBoxAndIsSaidInTheNodesWords()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = NewChat(renderer, send: _ => Task.FromResult<string?>("the watchtower is down"));
        await Read(chat, 6);
        chat.Input.InsertText("hello");

        await chat.SendAsync();

        chat.Input.Text.ShouldBe("hello");
        Described(chat).ShouldContain("the watchtower is down");
    }

    [Fact]
    public async Task AChoiceIsWorkingAtOnceWithNoHitThenTakenOrRefusedInTheNodesWords()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var node = new TaskCompletionSource<string?>();
        var chat = NewChat(renderer, Page(question: "Which?", choices: ["a", "b"]), send: _ => node.Task);
        await Read(chat, 6);

        Click(chat, "chat:choice:1");
        Described(chat).ShouldContain("a …");
        chat.Render(new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        regions.Any(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("chat:choice:", StringComparison.Ordinal)).ShouldBeFalse("no second press while the node has it");

        node.SetResult("no such watchtower");
        for (var i = 0; i < 100 && chat.Presses.PendingOf("chat:choice") is not null; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Described(chat).ShouldContain("no such watchtower");
        Described(chat).ShouldNotContain("a …");
    }

    [Fact]
    public async Task RestartIsWorkingAtOnceThenSaysTheNodesRefusal()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var node = new TaskCompletionSource<string?>();
        var chat = NewChat(renderer);
        chat.OnRestart = () => node.Task;
        await Read(chat, 6);
        Click(chat, "chat:restart");
        Click(chat, "chat:restart-yes");

        Described(chat).ShouldContain("Restarting…");
        node.SetResult("a restart is already running");
        for (var i = 0; i < 100 && chat.Presses.PendingOf("chat:restart") is not null; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Described(chat).ShouldContain("a restart is already running");
    }

    [Fact]
    public async Task AMessageAboutACardKeepsItsQuoteUntilTheNodeTakesIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var node = new TaskCompletionSource<string?>();
        var chat = NewChat(renderer);
        chat.OnSendAboutCard = (_, _, _) => node.Task;
        await Read(chat, 6);
        chat.Attach(new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "k", Subject = "flight/X", Title = "t", At = At, State = CardState.Open }, null);
        chat.Input.InsertText("why?");

        var send = chat.SendAsync();
        chat.QuotedCard.ShouldNotBeNull();
        node.SetResult(null);
        await send;
        chat.QuotedCard.ShouldBeNull();
        chat.Input.Text.ShouldBe("");
    }

    private static RGBAColor32? ColorOf(ChatView<RgbaImage> chat, string contains) =>
        Arranged(chat).Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>()
            .Where(t => t.Value.Contains(contains, StringComparison.Ordinal))
            .Select(t => (RGBAColor32?)t.Color)
            .FirstOrDefault();

    [Fact]
    public async Task ASentMessageShowsAtOnceDimmedAndSendingAndTheMarkClearsWhenTheNodeTookIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = NewChat(renderer, Page(queued: ["an earlier one"]), _ => held.Task);
        await Read(chat, 6);

        var send = chat.SendAsync("launch it now");

        Described(chat).ShouldContain("launch it now");
        Described(chat).ShouldContain("sending…");
        ColorOf(chat, "launch it now").ShouldBe(ColorOf(chat, "an earlier one"), "dimmed as a queued line is");

        held.SetResult(null);
        await send;

        Described(chat).ShouldContain("launch it now");
        Described(chat).ShouldNotContain("sending…");
        chat.Notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedMessageLosesItsEntryAndIsSaidAsTheRefusalNotice()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = NewChat(renderer, send: _ => held.Task);
        await Read(chat, 6);

        var send = chat.SendAsync("launch it now");
        Described(chat).ShouldContain("sending…");

        held.SetResult("refused: no watchtower");
        await send;

        Described(chat).ShouldNotContain("sending…");
        Described(chat).ShouldContain("refused: no watchtower");
        Described(chat).Split("launch it now").Length.ShouldBe(2, "the words show once, in the notice");
    }

    [Fact]
    public async Task ADeliveredMessageIsNotShownTwiceOnceTheFeedCarriesIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var feed = Conversation();
        var chat = NewChat(renderer, send: _ => Task.FromResult<string?>(null));
        await Read(chat, 6);
        await chat.SendAsync("and the next one?");

        // The feed already has a message with these words (index 5): only its copy shows.
        Described(chat).Split("and the next one?").Length.ShouldBe(2);
        feed.Length.ShouldBe(6);

        await chat.SendAsync("something new");
        Described(chat).ShouldContain("something new");
    }
}
