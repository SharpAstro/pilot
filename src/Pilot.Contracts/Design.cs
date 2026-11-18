namespace Pilot.Contracts;

/// <summary>
/// One design (PIL1096): a prototype of DIR.Lib screens the owner briefs, kept in versions on the node. The record the node keeps in
/// <c>designs/DSN-&lt;n&gt;/design.json</c>. Every member after the id is nullable or defaulted, as a stored record's must be (PILOT.md).
/// </summary>
public sealed record DesignRow
{
    /// <summary>The design's id, <c>DSN-&lt;n&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Its title; the brief's first line (<see cref="DesignTitle.Of"/>: whole, or cut at a word, or at the limit when it has no space, with an ellipsis) when none was given.</summary>
    public string? Title { get; init; }

    /// <summary>The repo the design is for, as the owner named it; null when none was.</summary>
    public string? Repo { get; init; }

    /// <summary>The number of the current version, or null while there is none.</summary>
    public int? Current { get; init; }

    /// <summary>How many versions there are (v1 to v<c>Versions</c>).</summary>
    public int? Versions { get; init; }

    /// <summary>When the design was made.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Why the current version was made: the owner's words or the designer's note; null while there is none.</summary>
    public string? Why { get; init; }

    /// <summary>The SHA-256 (hex) of the current version's <c>prototype.json</c>; null while there is none.</summary>
    public string? PrototypeSha256 { get; init; }

    /// <summary>Every version made, oldest first: number, time, why and hash of each. Empty while there is none, and in a record an older release wrote.</summary>
    public DesignVersionRow[] History { get; init => field = value ?? []; } = [];

    /// <summary>
    /// The terminal sizes the design is also arranged in cells at (PIL1098), each <c>&lt;cols&gt;x&lt;rows&gt;</c>: <c>pilot design check</c> runs Console.Lib's cell rules on every screen at each,
    /// and <c>pilot design render</c> writes each screen at each as text and as a grid. Empty for a design with no terminal target, and in a record an older release wrote.
    /// </summary>
    public string[] Terminals { get; init => field = value ?? []; } = [];

    /// <summary>The idea this design was started from (PIL1152), qualified by its repo, e.g. <c>pilot I381</c>; null for a design not started from one. Kept when the idea is dropped or folded.</summary>
    public string? FromIdea { get; init; }

    /// <summary>The design's designer session (PIL1151), as <see cref="DesignerState"/> and the members after it say; null for a design that never had one, and in a record an older release wrote.</summary>
    public string? DesignerName { get; init; }

    /// <summary>The designer's job id (the first eight of its session id), kept so a restart finds the session and resumes it, never starts a second.</summary>
    public string? DesignerJob { get; init; }

    /// <summary>What the designer is doing: <see cref="DesignerStates.Idle"/>, <see cref="DesignerStates.Working"/>, <see cref="DesignerStates.Held"/> or <see cref="DesignerStates.Failed"/>; null while there is none.</summary>
    public string? DesignerState { get; init; }

    /// <summary>While <see cref="DesignerState"/> is held: when the hold ends (the usage limit's reset).</summary>
    public DateTimeOffset? DesignerHeldUntil { get; init; }

    /// <summary>Why the designer is held or failed, in words the design's page shows ("held until 22:00: the 5-hour window is 93 % spent ...").</summary>
    public string? DesignerWhy { get; init; }

    /// <summary>When the designer was started; a start the session tracker has not listed yet is not started again within a few minutes of it.</summary>
    public DateTimeOffset? DesignerStartedAt { get; init; }

    /// <summary>Pieces of the brief the designer has not been sent yet (it is held, or being started), oldest first; BRIEF.md has them all as well.</summary>
    public string[] DesignerPending { get; init => field = value ?? []; } = [];

    /// <summary>The session ids of every designer this design has had (PIL1151), oldest first: the design chat reads their transcripts, as the watchtower's reads its generations. Empty while there is none, and in a record an older release wrote.</summary>
    public string[] DesignerSessions { get; init => field = value ?? []; } = [];

    /// <summary>What the designer last did to the draft, for the chat's status: <see cref="DesignerPhases"/>; null while there is none.</summary>
    public string? DesignerPhase { get; init; }

    /// <summary>When the designer's current or last turn began (a start, or a say delivered to it); null before any and in a record an older release wrote.</summary>
    public DateTimeOffset? DesignerTurnStartedAt { get; init; }

    /// <summary>When the designer's last turn ended and it sat idle (PIL1159); null while it works, once the node has ended it, before any and in a record an older release wrote.</summary>
    public DateTimeOffset? DesignerIdleSince { get; init; }

    /// <summary>Whether the last finished turn published a version; null while a turn runs, before any, and in a record an older release wrote.</summary>
    public bool? DesignerTurnPublished { get; init; }

    /// <summary>How many findings the last <c>design-check</c> had; null before any.</summary>
    public int? CheckFindings { get; init; }

    /// <summary>Every build of a version of this design (<c>design-build</c>, PIL1153), oldest first. Empty while there is none, and in a record an older release wrote.</summary>
    public DesignBuildRow[] Builds { get; init => field = value ?? []; } = [];
}

/// <summary>One build of a design version (PIL1153): the flight <c>design-build</c> made, and the issue it flies on.</summary>
public sealed record DesignBuildRow
{
    public required int Version { get; init; }

    /// <summary>The flight's code.</summary>
    public string? Flight { get; init; }

    /// <summary>The repo's folder name the flight is in.</summary>
    public string? Repo { get; init; }

    /// <summary>The issue the flight flies on: the one the build filed, or the idea's.</summary>
    public int? Issue { get; init; }

    public DateTimeOffset? At { get; init; }
}

/// <summary>The values of <see cref="DesignRow.DesignerPhase"/>: the last step taken on the draft.</summary>
public static class DesignerPhases
{
    public const string Drafting = "drafting";
    public const string Checking = "checking";
    public const string Rendering = "rendering";
    public const string Published = "published";
}

/// <summary>The values of <see cref="DesignRow.DesignerState"/>.</summary>
public static class DesignerStates
{
    public const string Idle = "idle";
    public const string Working = "working";
    public const string Held = "held";
    public const string Failed = "failed";
}

/// <summary>
/// The sizes, in pixels, a prototype is checked at (<c>pilot design check</c>): the distinct width and height pairs of the GUI's wireframe sweep
/// (<c>WireframeSweepTests.LongSizes</c>), so a design that passes here fits the screens the GUI itself is held to. One list, read by the node's check
/// and by the tests that hold it to the sweep.
/// </summary>
public static class DesignSizes
{
    /// <summary>Width and height pairs: 1000, 1280, 1400, 1920 and 2560 wide at 900 high, and 1280 and 2560 wide at 1440 high.</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<(uint Width, uint Height)> All =
    [
        (1000, 900), (1280, 900), (1400, 900), (1920, 900), (2560, 900),
        (1280, 1440), (2560, 1440),
    ];
}

/// <summary>
/// The terminal sizes a design can name as targets (<c>pilot design new --terminal</c>, <c>pilot design terminals</c>): the sizes of the terminal wireframe sweep
/// (<c>TerminalWireframeSweepTests</c>), so a design that passes at them fits the screens pilot-tui itself is held to. One list, held to the sweep by a test.
/// </summary>
public static class DesignTerminals
{
    /// <summary><c>&lt;cols&gt;x&lt;rows&gt;</c> of the standard sizes.</summary>
    public static readonly string[] Standard = ["80x24", "100x30", "160x44", "250x60"];

    /// <summary>Reads <c>&lt;cols&gt;x&lt;rows&gt;</c> (both positive, at most 1000), or false.</summary>
    public static bool TryParse(string? text, out int columns, out int rows)
    {
        columns = rows = 0;
        var parts = (text ?? "").Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out columns)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out rows)
            && columns is > 0 and <= 1000 && rows is > 0 and <= 1000;
    }

    /// <summary>The canonical <c>&lt;cols&gt;x&lt;rows&gt;</c> of each of <paramref name="sizes"/>, in order, without repeats; false naming the first that is not a size.</summary>
    public static bool TryNormalise(IEnumerable<string> sizes, out string[] normalised, out string bad)
    {
        var list = new List<string>();
        bad = "";
        foreach (var size in sizes)
        {
            if (!TryParse(size.Trim(), out var c, out var r))
            {
                bad = size;
                normalised = [];
                return false;
            }
            var canon = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{c}x{r}");
            if (!list.Contains(canon, StringComparer.Ordinal))
            {
                list.Add(canon);
            }
        }
        normalised = [.. list];
        return true;
    }
}

/// <summary>
/// Why a version was made, as <c>pilot design publish --why</c> takes it (PIL1159): a short free-text reason, one line, saying what changed ("added the bind rows to the settings screen").
/// Versions an older release made carry <c>brief</c>, <c>message</c> or <c>fix</c>; they are read as they were written.
/// </summary>
public static class DesignWhys
{
    /// <summary>The longest a why may be, in characters.</summary>
    public const int MaxLength = 120;

    /// <summary>Null when <paramref name="why"/> is a usable why, else what is wrong with it.</summary>
    public static string? Problem(string? why)
    {
        if (string.IsNullOrWhiteSpace(why))
        {
            return "publish takes --why <what changed>, a short reason that is not empty";
        }
        if (why.Contains('\n') || why.Contains('\r'))
        {
            return "publish --why is one line: it has a line break";
        }
        return why.Trim().Length > MaxLength
            ? $"publish --why is at most {MaxLength} characters, not {why.Trim().Length}: say what changed in a short line"
            : null;
    }
}

/// <summary>What <c>pilot design render</c> leaves in <c>draft/renders/render.json</c>: the hash of the <c>prototype.json</c> it drew, which publish compares with the draft as it stands.</summary>
public sealed record DesignRenderStamp
{
    public string? PrototypeSha256 { get; init; }
}

/// <summary>One line of a design's chat file, <c>chat.jsonl</c> (PIL1151): something the owner said. Nullable or defaulted past the first two members, as a stored record's must be.</summary>
public sealed record DesignChatLine
{
    public required DateTimeOffset At { get; init; }

    public required string Text { get; init; }

    /// <summary>The picture sent with it, as a path relative to the design's folder (<c>pictures/...</c>); null with none.</summary>
    public string? Picture { get; init; }
}

/// <summary>One version of a design, <c>versions/v&lt;k&gt;/</c>: its number, when it was made, and the hash of its prototype.</summary>
public sealed record DesignVersionRow
{
    public required int Number { get; init; }

    public DateTimeOffset? At { get; init; }

    public string? Why { get; init; }

    public string? PrototypeSha256 { get; init; }
}

/// <summary>A design read whole by <c>design show</c>: the record, the brief as written, its versions and where its folders are.</summary>
public sealed record DesignDetail
{
    public required DesignRow Row { get; init; }

    /// <summary>BRIEF.md as written: each piece of the owner's words under a <c>## &lt;UTC time&gt;</c> heading, oldest first.</summary>
    public string? Brief { get; init; }

    public DesignVersionRow[] Versions { get; init => field = value ?? []; } = [];

    /// <summary>The design's folder on the node.</summary>
    public string? Folder { get; init; }

    /// <summary>Its <c>draft/</c> folder, whether or not it exists yet.</summary>
    public string? DraftFolder { get; init; }

    /// <summary>The current version's folder; null while there is none.</summary>
    public string? CurrentFolder { get; init; }
}

/// <summary>The designs list: every design, newest first.</summary>
public sealed record DesignList
{
    public DesignRow[] Rows { get; init => field = value ?? []; } = [];
}

/// <summary>The ids the store has handed out, <c>designs/ids.json</c>: the last number, so an id is never handed out twice.</summary>
public sealed record DesignIds
{
    public int Last { get; init; }
}
