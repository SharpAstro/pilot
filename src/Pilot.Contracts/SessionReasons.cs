namespace Pilot.Contracts;

/// <summary>What started a session the node launched. Text on the wire, so a reason added later needs no new wire.</summary>
public static class SessionStartReasons
{
    public const string LegLaunch = "leg launch";
    public const string LegValidator = "leg validator";
    /// <summary>A short session the node starts in a plane whose worker committed and ended with nothing ticked, to tick what the commits cover.</summary>
    public const string LegTicker = "leg ticker";
    public const string Planner = "planner";
    public const string FlightReview = "flight review";
    public const string PrReview = "PR review";
    public const string PrFix = "PR fix";
    public const string WatchtowerStart = "watchtower start";
    public const string WatchtowerRestart = "watchtower restart";
    public const string Compaction = "compaction";
    /// <summary>A live session the node knows by its role but has no ledger entry for: adopted by the sweep.</summary>
    public const string BeforeLedger = "before the ledger";
    public const string Other = "other";
}

/// <summary>Why a session the node launched ended.</summary>
public static class SessionEndReasons
{
    public const string Finished = "finished";
    public const string Landed = "stopped on land";
    public const string Merged = "stopped on merge";
    public const string Watchtower = "stopped by the watchtower";
    public const string Person = "stopped by a person";
    public const string Crashed = "crashed";
    /// <summary>The node itself stopped, and ended the stream session (the watchtower) it owns.</summary>
    public const string NodeStopped = "stopped with the node";
    /// <summary>A flight's planner the node ended once its first leg's worker started: its plan is done, and it holds no Opus slot (#568).</summary>
    public const string Planned = "stopped once planned";
}
