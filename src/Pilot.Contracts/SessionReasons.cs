namespace Pilot.Contracts;

/// <summary>What started a session the node launched. Text on the wire, so a reason added later needs no new wire.</summary>
public static class SessionStartReasons
{
    public const string LegLaunch = "leg launch";
    public const string LegValidator = "leg validator";
    public const string Planner = "planner";
    public const string FlightReview = "flight review";
    public const string PrReview = "PR review";
    public const string PrFix = "PR fix";
    public const string WatchtowerStart = "watchtower start";
    public const string WatchtowerRestart = "watchtower restart";
    public const string Compaction = "compaction";
    public const string Other = "other";
}

/// <summary>Why a session the node launched ended.</summary>
public static class SessionEndReasons
{
    public const string Finished = "finished";
    public const string Landed = "stopped on land or merge";
    public const string Watchtower = "stopped by the watchtower";
    public const string Person = "stopped by a person";
    public const string Crashed = "crashed";
}
