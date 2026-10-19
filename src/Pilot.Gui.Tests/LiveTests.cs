using System.Threading.Channels;
using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Rows and events for the live tests.</summary>
internal static class Live
{
    public static readonly DateTimeOffset At = new(2026, 10, 19, 1, 0, 0, TimeSpan.Zero);

    public static SessionRow Row(string id, string name, SessionState state = SessionState.Idle) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = "C:\\work\\pilot",
        Role = SessionRole.AdHoc,
        ContextTokens = 1000,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 2000,
        StartedAt = At,
    };

    public static SnapshotEvent Snapshot(long seq, string stream, params SessionRow[] rows) =>
        new() { Seq = seq, At = At, Stream = stream, Sessions = rows, Notes = [] };

    public static SessionChangedEvent Changed(long seq, SessionRow row) => new() { Seq = seq, At = At, Session = row };

    public static SessionAddedEvent Added(long seq, SessionRow row) => new() { Seq = seq, At = At, Session = row };
}

/// <summary>A node's event stream the test writes into, and every open the feed asked for.</summary>
internal sealed class FakeNode
{
    private readonly Lock _lock = new();
    private Channel<NodeEvent?>? _current;

    public List<(long? Since, string? Stream)> Opens { get; } = [];

    /// <summary>When set, an open fails with this, as when no node listens.</summary>
    public Exception? Refuse { get; set; }

    public Task<INodeEvents> Open(long? since, string? stream, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Opens.Add((since, stream));
            if (Refuse is { } refuse)
            {
                return Task.FromException<INodeEvents>(refuse);
            }

            _current = Channel.CreateUnbounded<NodeEvent?>();
            return Task.FromResult<INodeEvents>(new Events(_current.Reader));
        }
    }

    public int OpenCount
    {
        get
        {
            lock (_lock)
            {
                return Opens.Count;
            }
        }
    }

    public void Send(NodeEvent nodeEvent)
    {
        lock (_lock)
        {
            _current!.Writer.TryWrite(nodeEvent);
        }
    }

    /// <summary>Closes the open stream, as a node does that goes away or drops a slow client.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _current!.Writer.TryWrite(null);
        }
    }

    private sealed class Events(ChannelReader<NodeEvent?> reader) : INodeEvents
    {
        public async ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken) => await reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class NodeFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task TheRowsFollowTheSnapshotAndItsDeltas()
    {
        var node = new FakeNode();
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();
        await Until(() => node.OpenCount == 1);

        node.Send(Live.Snapshot(4, "s1", Live.Row("aaaa1111", "one")));
        node.Send(Live.Added(5, Live.Row("bbbb2222", "two", SessionState.Working)));
        node.Send(Live.Changed(6, Live.Row("aaaa1111", "one", SessionState.Ended)));
        await Until(() => feed.Screen(Live.At).Lines.Count == 2 && feed.Screen(Live.At).Lines[1].Row.State == SessionState.Ended);

        var screen = feed.Screen(Live.At);
        feed.Connected.ShouldBeTrue();
        screen.Source.ShouldBe("live from the node on node.sock");
        // Live sessions first: the working one, then the one that ended.
        screen.Lines.Select(l => l.Name).ShouldBe(["two", "one"]);
    }

    [Fact]
    public async Task ADroppedStreamResumesAfterTheLastEventApplied()
    {
        var node = new FakeNode();
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();
        await Until(() => node.OpenCount == 1);
        node.Send(Live.Snapshot(9, "s1", Live.Row("aaaa1111", "one")));
        node.Send(Live.Added(10, Live.Row("bbbb2222", "two")));
        await Until(() => feed.Screen(Live.At).Lines.Count == 2);

        node.Close();
        await Until(() => node.OpenCount == 2);

        node.Opens[0].ShouldBe((null, null));
        node.Opens[1].ShouldBe((10L, "s1"));
        // The rows stay on screen while it reconnects.
        feed.Screen(Live.At).Lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ADeltaOutOfOrderStartsAgainFromASnapshot()
    {
        var node = new FakeNode();
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();
        await Until(() => node.OpenCount == 1);
        node.Send(Live.Snapshot(1, "s1", Live.Row("aaaa1111", "one")));
        node.Send(Live.Added(3, Live.Row("bbbb2222", "two")));

        await Until(() => node.OpenCount == 2);

        node.Opens[1].ShouldBe((null, null));
    }

    [Fact]
    public async Task NoNodeSaysSoAndKeepsTrying()
    {
        var node = new FakeNode { Refuse = new HttpRequestException("refused") };
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();

        await Until(() => node.OpenCount >= 3);

        feed.Connected.ShouldBeFalse();
        feed.Screen(Live.At).Source.ShouldStartWith("no node on node.sock: start pilot-node");
    }
}

public sealed class GuiSayTests
{
    private static readonly SessionRow[] Rows =
    [
        Live.Row("aaaa1111", "pil5-say-int"),
        Live.Row("bbbb2222", "twin"),
        Live.Row("bbbb3333", "twin"),
    ];

    [Theory]
    [InlineData("aaaa1111", "aaaa1111")]
    [InlineData("aaaa1111-0000-4000-8000-000000000000", "aaaa1111")]
    [InlineData("AAAA", "aaaa1111")]
    [InlineData("pil5-say-int", "aaaa1111")]
    public void ASessionIsFoundByIdSessionIdOrName(string to, string id) =>
        GuiSay.Find(Rows, to, out _).ShouldNotBeNull().Id.ShouldBe(id);

    [Fact]
    public void ANameOrStartTwoSessionsShareIsRefused()
    {
        GuiSay.Find(Rows, "twin", out var why).ShouldBeNull();
        why.ShouldBe("2 sessions match 'twin': twin (bbbb2222), twin (bbbb3333)");
        GuiSay.Find(Rows, "bbbb", out why).ShouldBeNull();
        why.ShouldNotBeNull().ShouldStartWith("2 session ids match 'bbbb'");
        GuiSay.Find(Rows, "nobody", out why).ShouldBeNull();
        why.ShouldBe("no session matches 'nobody'");
    }

    [Fact]
    public void AResultReadsAsWhatBecameOfIt()
    {
        CommandResult Result(bool ok, string? way = null, string? error = null, string? sid = null) =>
            new() { Verb = Verbs.Say, Target = "session/x", Who = GuiSay.Who, Seq = 1, Ok = ok, Way = way, Error = error, SessionId = sid };

        GuiSay.Describe(Result(true, SayWays.Resumed, sid: "201dda08-9080"), null).ShouldBe("resumed it with your text (now 201dda08)");
        GuiSay.Describe(Result(true, SayWays.Relayed), null).ShouldBe("delivered as a message; its answer shows in its own turn");
        GuiSay.Describe(Result(false, error: "twin is ambiguous"), "twin is ambiguous").ShouldBe("refused: twin is ambiguous");
        GuiSay.Describe(null, "").ShouldBe("refused: the node gave no reason");
    }
}

public sealed class GuiOptionsTests
{
    [Fact]
    public void SayTakesTheSessionAndEverythingAfterAsTheText()
    {
        var options = GuiOptions.Parse(["--node-socket", "x.sock", "--say", "pil5-say-int", "Reply", "with", "ok"], out var error).ShouldNotBeNull(error);

        options.NodeSocket.ShouldBe("x.sock");
        options.SayTo.ShouldBe("pil5-say-int");
        options.SayText.ShouldBe("Reply with ok");
    }

    [Fact]
    public void ASnapshotTakesNoNode()
    {
        GuiOptions.Parse(["--snapshot", "s.json", "--node-socket", "x.sock"], out var error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldStartWith("--snapshot shows a file");
        GuiOptions.Parse(["--say", "only-a-session"], out error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldStartWith("--say needs a session and the text");
    }
}

public sealed class SayBarTests
{
    private static SessionsScreen Screen(params SessionRow[] rows) => new(SessionLine.Build(rows, Live.At), "test", Live.At);

    [Fact]
    public void TheSelectionFollowsItsSessionWhenTheRowsReorder()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanSay = true };
        var one = Live.Row("aaaa1111", "one");
        var two = Live.Row("bbbb2222", "two");
        view.Render(Screen(one, two), new RectF32(0, 0, 1280, 800));
        view.Select(1);
        view.SelectedId.ShouldBe("bbbb2222");

        // "one" starts working, so it moves above "two": the selection stays on "two", now the second row still,
        // and when a third session comes in ahead of both it moves with "two" to the third place.
        view.Render(Screen(one with { State = SessionState.Working }, two), new RectF32(0, 0, 1280, 800));
        view.SelectedId.ShouldBe("bbbb2222");
        view.Render(Screen(one with { State = SessionState.Working }, two, Live.Row("cccc3333", "three", SessionState.Waiting)), new RectF32(0, 0, 1280, 800));
        view.SelectedId.ShouldBe("bbbb2222");
        view.Selected.ShouldBe(2);
        view.SayInput.Placeholder.ShouldBe("Say to two · Enter sends");
    }

    [Fact]
    public async Task EnterSaysTheTextToTheSelectedSessionAndClearsTheBar()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanSay = true };
        var said = new List<(string Id, string Text)>();
        view.OnSay = (line, text) =>
        {
            said.Add((line.Row.Id, text));
            return Task.CompletedTask;
        };
        view.Render(Screen(Live.Row("aaaa1111", "one"), Live.Row("bbbb2222", "two")), new RectF32(0, 0, 1280, 800));
        view.Select(1);
        view.Render(Screen(Live.Row("aaaa1111", "one"), Live.Row("bbbb2222", "two")), new RectF32(0, 0, 1280, 800));

        view.SayInput.Text = "  hello there ";
        await view.SayInput.OnCommit.ShouldNotBeNull()(view.SayInput.Text);

        said.ShouldBe([("bbbb2222", "hello there")]);
        view.SayInput.Text.ShouldBe("");
    }

    [Fact]
    public void TheSayBarShowsOnlyWithANode()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        view.Render(Screen(Live.Row("aaaa1111", "one")), new RectF32(0, 0, 1280, 800));
        view.GetRegisteredTextInputs().ShouldBeEmpty();

        view.CanSay = true;
        view.Render(Screen(Live.Row("aaaa1111", "one")), new RectF32(0, 0, 1280, 800));
        view.GetRegisteredTextInputs().ShouldBe([view.SayInput]);
    }
}
