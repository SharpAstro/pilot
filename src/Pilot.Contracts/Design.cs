namespace Pilot.Contracts;

/// <summary>
/// One design (PIL1096): a prototype of DIR.Lib screens the owner briefs, kept in versions on the node. The record the node keeps in
/// <c>designs/DSN-&lt;n&gt;/design.json</c>. Every member after the id is nullable or defaulted, as a stored record's must be (PILOT.md).
/// </summary>
public sealed record DesignRow
{
    /// <summary>The design's id, <c>DSN-&lt;n&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Its title (<see cref="DesignTitle"/>): at most 40 characters, given by the designer in DESIGN.md's heading or by <c>design title</c>; null until one is given.</summary>
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

    /// <summary>
    /// Every idea this design is for (#1185), each qualified by its repo, e.g. <c>pilot I381</c>, in the order they were linked; empty while it is for none, and in a record an older
    /// release wrote. New links are written here only: <see cref="FromIdea"/> stays readable as the first link of a record that has no list (<see cref="Ideas"/>).
    /// </summary>
    public string[] ForIdeas { get; init => field = value ?? []; } = [];

    /// <summary>The ideas the design is for: <see cref="ForIdeas"/>, with <see cref="FromIdea"/> of an older record read as the first link when the list does not name it.</summary>
    public string[] Ideas() =>
        FromIdea is { Length: > 0 } from && !ForIdeas.Contains(from, StringComparer.OrdinalIgnoreCase) ? [from, .. ForIdeas] : ForIdeas;

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

    /// <summary>
    /// What the owner typed into DESIGN.md that the designer has not been told yet (PIL1171), oldest first, each in the words it gets. Unlike <see cref="DesignerPending"/>
    /// it never wakes the designer: it goes with the next say, resume or start.
    /// </summary>
    public string[] DesignerTyped { get; init => field = value ?? []; } = [];

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

    /// <summary>The GitHub repo (<c>owner/name</c>) of the issue the design is kept in (PIL1171); null until the design is linked to one or its first version is written.</summary>
    public string? IssueRepo { get; init; }

    /// <summary>The number of the issue the design is kept in; null until the design is linked to one or its first version is written.</summary>
    public int? IssueNumber { get; init; }

    /// <summary>The newest version written to the issue (<see cref="DesignSyncText"/>); null while none was.</summary>
    public int? SyncedVersion { get; init; }

    /// <summary>The line the page shows in the error colour while the last try to write a version failed (<see cref="DesignSyncText.Failure"/>); null once the version is written.</summary>
    public string? SyncError { get; init; }

    /// <summary>When the last try failed.</summary>
    public DateTimeOffset? SyncErrorAt { get; init; }

    /// <summary>Whether the last try found <c>pilot: hold</c> on the issue and wrote nothing (<see cref="DesignSyncText.Held"/>); null and false once a try was not held.</summary>
    public bool? SyncHeld { get; init; }

    /// <summary>
    /// The node a design was made on, set only on a design this node read from its GitHub issue (PIL1171): such a design is read-only here, with no draft, no typing and no designer.
    /// Null for a design made on this node, and in a record an older release wrote.
    /// </summary>
    public string? MadeOn { get; init; }

    /// <summary>Whether this node wants the design's issue locked (PIL1171): its designer drafts or the owner typed, and the draft is not published or discarded. The sync service puts the labels on and keeps trying; null and false mean no.</summary>
    public bool? LockWanted { get; init; }

    /// <summary>Whether this node put <see cref="DesignLockText.EditingLabel"/> and its node label on the issue and has not taken them off.</summary>
    public bool? LockHeld { get; init; }

    /// <summary>The node whose label lock is on the issue (read at the last sweep); the design is read only here while it is set and is not this node's.</summary>
    public string? LockedBy { get; init; }

    /// <summary>Why the last try to put on or take off the labels failed, in words; null once it went through. A local edit is never blocked by it, and the sync service tries again.</summary>
    public string? LockError { get; init; }

    /// <summary>Edits of the issue's prose made on GitHub while the design was locked here (PIL1171): kept beside the design, oldest first, shown to the editor with Apply and Dismiss.</summary>
    public DesignGitHubEdit[] GitHubEdits { get; init => field = value ?? []; } = [];

    /// <summary>What the last read of the issue found in the design's block that is not applied (a frame's link or a block changed on GitHub, a cell added or removed), as one line each; the page says so.</summary>
    public string[] GitHubIgnored { get; init => field = value ?? []; } = [];

    /// <summary>The SHA-256 of the issue's design block at the last read that handled it, so one edit on GitHub is handled once.</summary>
    public string? GitHubBlockSha256 { get; init; }

    /// <summary>The why a typed change read from the issue gives the draft (<c>owner on GitHub: edited objective</c>); the page offers it to the owner who publishes. Cleared at a publish.</summary>
    public string? DraftWhy { get; init; }

    /// <summary>Every build of a version of this design (<c>design-build</c>, PIL1153), oldest first. Empty while there is none, and in a record an older release wrote.</summary>
    public DesignBuildRow[] Builds { get; init => field = value ?? []; } = [];

    /// <summary>What GitHub was told of each flight that built this design (PIL1185): one row per flight and version, oldest first. Empty while there is none, and in a record an older release wrote.</summary>
    public DesignRecordRow[] Record { get; init => field = value ?? []; } = [];
}

/// <summary>
/// What a flight's PR did on one design's issue (PIL1185): the opening comment, remembered so a push or a retry never comments twice, and how the merge was recorded.
/// <see cref="Landed"/> is null until the flight merged and its result was written to GitHub.
/// </summary>
public sealed record DesignRecordRow
{
    /// <summary>The flight's code.</summary>
    public required string Flight { get; init; }

    /// <summary>The version the flight built.</summary>
    public required int Version { get; init; }

    /// <summary>The flight's PR number.</summary>
    public int? Pr { get; init; }

    /// <summary>Whether the issue got its comment when the PR opened.</summary>
    public bool? Opened { get; init; }

    /// <summary>The commit the PR merged as, once it did.</summary>
    public string? MergedAt { get; init; }

    /// <summary>How the merge was recorded: <c>closed</c> (issue closed, lock labels off) or <c>commented</c> (a comment only); null while not yet written.</summary>
    public string? Landed { get; init; }

    /// <summary>The folder in the flight's repo the built version's files were committed to (<see cref="DesignSyncText.RepoFolder"/>); null when they were not placed.</summary>
    public string? Folder { get; init; }

    /// <summary>Why the files were not placed: the render or the version is over its limit, in words; null otherwise.</summary>
    public string? Oversize { get; init; }

    /// <summary>The full commit the PR merged as (PIL1208); null for a row written before it was kept, whose <see cref="MergedAt"/> is the short one.</summary>
    public string? MergeCommit { get; init; }

    /// <summary>True once the design issue's picture links and the PR's Designs section name <see cref="MergeCommit"/> (PIL1208); null while they are not written.</summary>
    public bool? LinksWritten { get; init; }

    /// <summary>True once every link to the merged files answered and the branch <c>design/&lt;n&gt;</c> was deleted (PIL1208); null otherwise.</summary>
    public bool? BranchGone { get; init; }
}

/// <summary>A prose part the issue's text says differently from the published version (PIL1171): <see cref="Part"/> is objective, heading or notes, <see cref="Cell"/> the cell's number (empty for the design itself).</summary>
public sealed record DesignGitHubEdit
{
    public required string Part { get; init; }

    public string Cell { get; init; } = "";

    public required string Text { get; init; }

    public DateTimeOffset? At { get; init; }

    /// <summary>What the part is called on the page and in a why: <c>objective</c>, or <c>heading of cell 2</c>.</summary>
    public string Where => Cell.Length == 0 ? Part + " of the design" : $"{Part} of cell {Cell}";

    /// <summary>The key a verb names it by: <c>part:cell</c>.</summary>
    public string Key => Part + ":" + Cell;
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

    /// <summary>What a version the owner made by typing starts its why with (PIL1171): <c>owner: &lt;what changed&gt;</c>.</summary>
    public const string OwnerPrefix = "owner: ";

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

    /// <summary>When the version was written to the design's issue (PIL1171): its comment <c>v&lt;k&gt;: &lt;why&gt;</c> is on the issue from then; null while it is not.</summary>
    public DateTimeOffset? SyncedAt { get; init; }
}

/// <summary>The words of a design's sync to its GitHub issue (PIL1171), one source for the node's record and the page.</summary>
public static class DesignSyncText
{
    /// <summary>The label of a design's issue, which another node lists the designs by.</summary>
    public const string Label = "pilot: design";

    /// <summary>The marker lines the document sits between in the issue's body; the rest of the body is left as it was.</summary>
    public const string Begin = "<!-- pilot:design -->";

    public const string End = "<!-- /pilot:design -->";

    /// <summary>The reason a failed write gives when GitHub did not answer in time; any other failure gives its own message.</summary>
    public const string NoAnswer = "GitHub did not answer";

    /// <summary>The folder in the repo a built design's files are committed to: the issue number, unique in the repo (a DSN id repeats across nodes), as the branch is.</summary>
    public static string RepoFolder(int issue) => "docs/designs/" + N(issue);

    /// <summary>The largest render a flight commits to the repo: 500 KB.</summary>
    public const int RenderLimitBytes = 500 * 1024;

    /// <summary>The largest version a flight commits to the repo, all its files together: 3 MB.</summary>
    public const int VersionLimitBytes = 3 * 1024 * 1024;

    /// <summary>The Designs line of a design whose files are committed.</summary>
    public static string Placed(string folder) => $"files in {folder}";

    /// <summary>The permalink of <paramref name="folder"/> at <paramref name="commit"/> (PIL1208).</summary>
    public static string FolderLink(string repo, string commit, string folder) => $"https://github.com/{repo}/tree/{commit}/{folder}";

    /// <summary>The Designs line of a built design whose files are on the default branch: the folder at the merge commit.</summary>
    public static string PlacedAt(string repo, string commit, string folder) => $"files in [{folder}]({FolderLink(repo, commit, folder)})";

    /// <summary>
    /// Where a design's built version lives in its repo (PIL1208): the latest landed record row with a folder and a merge commit, as
    /// <c>&lt;owner/repo&gt; docs/designs/&lt;n&gt; at &lt;short sha&gt;, v&lt;k&gt;</c>. Null while no placed build has landed. One source for
    /// <c>pilot design show</c> and the Designs page.
    /// </summary>
    public static string? BuiltPlace(DesignRow row)
    {
        if ((row.IssueRepo ?? row.Repo) is not { Length: > 0 } repo)
        {
            return null;
        }
        var landed = row.Record.LastOrDefault(r => r.Landed is { Length: > 0 } && r.Folder is { Length: > 0 } && r.MergeCommit is { Length: > 0 });
        if (landed is null)
        {
            return null;
        }
        var commit = landed.MergeCommit!;
        var shortSha = commit.Length > 7 ? commit[..7] : commit;
        return $"{repo} {landed.Folder} at {shortSha}, v{N(landed.Version)}";
    }

    /// <summary>The Designs line of a design whose files stay on its branch because its issue is in another repo.</summary>
    public static string Elsewhere(int issue, string repo) => $"files stay on {Branch(issue)} of {repo}";

    /// <summary>Why a render is not committed.</summary>
    public static string RenderOversize(string render, long bytes) => $"not committed: render {render} is {Kb(bytes)} KB, over {Kb(RenderLimitBytes)} KB";

    /// <summary>Why a version is not committed.</summary>
    public static string TotalOversize(long bytes) => $"not committed: version is {Kb(bytes)} KB, over {Kb(VersionLimitBytes)} KB";

    private static string Kb(long bytes) => ((bytes + 1023) / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The branch a design's files are written to: <c>design/&lt;issue number&gt;</c>.</summary>
    public static string Branch(int issue) => "design/" + N(issue);

    /// <summary>While version <paramref name="version"/> is being written.</summary>
    public static string Writing(int version, string repo, int issue) => $"Writing v{N(version)} to {repo}#{N(issue)}...";

    /// <summary>Once version <paramref name="version"/> is on the issue.</summary>
    public static string Synced(int version, string repo, int issue) => $"v{N(version)} is on {repo}#{N(issue)}";

    /// <summary>While <c>pilot: hold</c> is on the issue: nothing is written, and the version waits for the hold to come off.</summary>
    public static string Held(int version, string repo, int issue) => $"Held: pilot: hold is on {repo}#{N(issue)}; v{N(version)} will be sent when it is off.";

    /// <summary>The failure line, in the error colour: why, which version is kept and where, and that it is sent again.</summary>
    public static string Failure(string reason, int version, string node) => $"Not synced: {reason}. v{N(version)} is saved on {node} and will be sent again.";

    /// <summary>The state of a design that names no repo (#1183): nothing failed, a version has nowhere to be written, so it is kept on the node only. Shown in the neutral colour.</summary>
    public static string KeptOnly(string node) => $"Kept on {node} only";

    /// <summary>The command that links a design to its issue, as the page's tooltip and the CLI say it.</summary>
    public static string LinkCommand(string design) => $"pilot design issue {design} <repo>#<n>";

    /// <summary>Whether a stored <c>SyncError</c> is the old no-repo line an earlier release wrote as a failure: that is the state of a design kept here only, not a failed write.</summary>
    public static bool IsOldNoRepoLine(string? line) => line is { Length: > 0 } && line.StartsWith("Not synced: ", StringComparison.Ordinal) && line.Contains(" names no repo, so ", StringComparison.Ordinal);

    /// <summary>What the version list says of a version: its comment on the issue, or that it is not there yet.</summary>
    public static string Comment(int version, string? why, bool written) =>
        written ? $"Comment on the issue: v{N(version)}: {why}" : "Not on the issue yet";

    private static string N(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The words of a design's label lock (PIL1171), one source for the node's record and the page.</summary>
public static class DesignLockText
{
    /// <summary>The state label a node puts on the design's issue while it has the design open for editing;.</summary>
    public const string EditingLabel = "pilot: editing";

    /// <summary>The line the page shows for a design locked by another node.</summary>
    public static string BeingEdited(string node) => $"Being edited on {node}. Read only here until it is published or the lock is taken off.";

    /// <summary>The line for a labelling that failed.</summary>
    public static string LabelFailed(string reason) => $"The issue could not be labelled: {reason}. It is tried again; editing here goes on.";

    /// <summary>The line above a cell for a GitHub edit that waits for the editor.</summary>
    public static string Waiting(string where) => $"Edited on GitHub: the {where}. Apply it to the draft or dismiss it.";

    /// <summary>The why a typed change read from the issue gets.</summary>
    public static string OwnerOnGitHub(string part) => $"owner on GitHub: edited {part}";
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

/// <summary>The most a <c>design-renders</c> answer carries (PIL1186), so one call cannot flood a session's context: pictures per call, bytes per call and the longest side of a picture.</summary>
public static class DesignRenderCaps
{
    /// <summary>Pictures in one answer; the rest are named in <see cref="DesignRenders.NotSent"/>.</summary>
    public const int Pictures = 4;

    /// <summary>PNG bytes in one answer, 3 MB; a picture over its share is scaled down.</summary>
    public const int Bytes = 3 * 1024 * 1024;

    /// <summary>The longest side of a picture in pixels; a larger one is scaled down to it.</summary>
    public const int LongestSide = 1600;
}

/// <summary>One render of a <c>design-renders</c> answer (PIL1186): which screen at which size, the prototype hash it was drawn from, and the PNG itself.</summary>
public sealed record DesignRenderPicture
{
    public required string Screen { get; init; }

    /// <summary>The size as the file names it: <c>design</c> for the design size, a target's name, or <c>&lt;cols&gt;x&lt;rows&gt;</c> for a terminal.</summary>
    public required string Size { get; init; }

    /// <summary>The hash the renders folder was stamped with, null when it has no stamp.</summary>
    public string? PrototypeSha256 { get; init; }

    public required byte[] Png { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The size the file has when <see cref="Png"/> was scaled down to fit the caps, as <c>WxH</c>; null when it is the file's own bytes.</summary>
    public string? ScaledFrom { get; init; }

    /// <summary>Where the file is on the node.</summary>
    public string? Path { get; init; }
}

/// <summary>A <c>design-renders</c> answer (PIL1186).</summary>
public sealed record DesignRenders
{
    public required string Design { get; init; }

    /// <summary>The version the renders belong to; null for the draft.</summary>
    public int? Version { get; init; }

    public DesignRenderPicture[] Pictures { get; init; } = [];

    /// <summary>The screens that were not sent: the entry screen's siblings when no screen was named, or the ones past the picture cap.</summary>
    public string[] NotSent { get; init; } = [];

    /// <summary>True for a draft whose renders are older than its prototype, or that has none.</summary>
    public bool Stale { get; init; }

    /// <summary>What a person should know beyond the pictures, such as why they are stale or what the cap did.</summary>
    public string? Note { get; init; }
}
