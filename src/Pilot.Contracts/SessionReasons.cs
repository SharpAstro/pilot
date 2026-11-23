namespace Pilot.Contracts;

/// <summary>What started a session the node launched. Text on the wire, so a reason added later needs no new wire.</summary>
public static class SessionStartReasons
{
    public const string LegLaunch = "leg launch";
    public const string LegValidator = "leg validator";
    /// <summary>A short session the node starts in a plane whose worker committed and ended with nothing ticked, to tick what the commits cover.</summary>
    public const string LegTicker = "leg ticker";
    public const string Planner = "planner";
    /// <summary>The designer of a design (PIL1151), started on its first brief.</summary>
    public const string Designer = "designer";
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
    /// <summary>A flight withdrawn as a mistake (PIL1111): its sessions end with it, its plane and branch go.</summary>
    public const string Withdrawn = "stopped on withdraw";
    public const string Watchtower = "stopped by the watchtower";
    public const string Person = "stopped by a person";
    public const string Crashed = "crashed";
    /// <summary>The node itself stopped, and ended the stream session (the watchtower) it owns.</summary>
    public const string NodeStopped = "stopped with the node";
    /// <summary>A design's designer (PIL1159) the node ended once it had sat idle for the designer-keep time; the next brief piece resumes it with its context.</summary>
    public const string DesignerIdle = "stopped when the designer sat idle";
    /// <summary>A flight's planner the node ended once its first leg's worker started: its plan is done, and it holds no Opus slot (#568).</summary>
    public const string Planned = "stopped once planned";
    /// <summary>A worker of a leg that was redone (PIL1206): the leg has another current worker, so the node ended this duplicate itself. Starts the why of an end-session.</summary>
    public const string DuplicateWorker = "duplicate worker of a redone leg";
}
