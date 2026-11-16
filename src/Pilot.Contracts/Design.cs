namespace Pilot.Contracts;

/// <summary>
/// One design (PIL1096): a prototype of DIR.Lib screens the owner briefs, kept in versions on the node. The record the node keeps in
/// <c>designs/DSN-&lt;n&gt;/design.json</c>. Every member after the id is nullable or defaulted, as a stored record's must be (PILOT.md).
/// </summary>
public sealed record DesignRow
{
    /// <summary>The design's id, <c>DSN-&lt;n&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Its title; the brief's first line, cut to 80 characters, when none was given.</summary>
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
