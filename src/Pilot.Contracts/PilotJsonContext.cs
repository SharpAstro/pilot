using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// The one source-generated JSON context for the node's wire, shared by the node and its clients so both serialise
/// with the same generated metadata. AOT-safe: every type that crosses is registered here by its concrete type, and
/// none carries an <see cref="object"/> payload.
/// </summary>
/// <remarks>
/// camelCase, enums as their names (each enum's own <c>JsonStringEnumConverter&lt;T&gt;</c>), nulls written out so a
/// reader sees every field of the frozen rows. Nullable annotations are respected: a null where the contract has no
/// <c>?</c> is refused, not read as a default.
///
/// A field added later must read a store written before it: the generator sets every init property from the JSON, one
/// the JSON lacks to null, whatever its <c>= []</c> says, and writing that row back is then refused. So a non-nullable
/// init property with a default takes it for null too: <c>{ get; init =&gt; field = value ?? []; } = [];</c>. Without it,
/// beta.312's <c>CardRow.AlsoAskedBy</c> stopped every node with an older card in its journal at start (2026-10-25).
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(NodeEvent))]
[JsonSerializable(typeof(SnapshotEvent))]
[JsonSerializable(typeof(SessionRow))]
[JsonSerializable(typeof(Artifact))]
[JsonSerializable(typeof(FlightRow))]
[JsonSerializable(typeof(FlightMoved))]
[JsonSerializable(typeof(FlightMovedTo))]
[JsonSerializable(typeof(FlightMoveUnknown))]
[JsonSerializable(typeof(FlightMove))]
[JsonSerializable(typeof(OverlapRow))]
[JsonSerializable(typeof(FoldedIssue))]
[JsonSerializable(typeof(PackageWait))]
[JsonSerializable(typeof(PeerConnection))]
[JsonSerializable(typeof(LegWait))]
[JsonSerializable(typeof(SubTask))]
[JsonSerializable(typeof(SubTaskItem))]
[JsonSerializable(typeof(LegRecheck))]
[JsonSerializable(typeof(LegRow))]
[JsonSerializable(typeof(CardRow))]
[JsonSerializable(typeof(UsageRow))]
[JsonSerializable(typeof(AccountRow))]
[JsonSerializable(typeof(MachineRow))]
[JsonSerializable(typeof(MachineGpu))]
[JsonSerializable(typeof(MachineProcess))]
[JsonSerializable(typeof(JobRow))]
[JsonSerializable(typeof(MergeRow))]
[JsonSerializable(typeof(PrReviewRow))]
[JsonSerializable(typeof(IdeaRow))]
[JsonSerializable(typeof(IdeaMoveRow))]
[JsonSerializable(typeof(IdeaMoveRow[]))]
[JsonSerializable(typeof(IdeaRow[]))]
[JsonSerializable(typeof(IdeaImage))]
[JsonSerializable(typeof(IdeaImage[]))]
[JsonSerializable(typeof(ResponseEnvelope<string>))]
[JsonSerializable(typeof(MilestoneRow))]
[JsonSerializable(typeof(MilestoneRow[]))]
[JsonSerializable(typeof(IssueRow))]
[JsonSerializable(typeof(IssueRow[]))]
[JsonSerializable(typeof(SummaryRow))]
[JsonSerializable(typeof(PullRequestsRow))]
[JsonSerializable(typeof(PullRequestRow))]
[JsonSerializable(typeof(GrantRequest))]
[JsonSerializable(typeof(ResponseEnvelope<GrantTicket>))]
[JsonSerializable(typeof(ResponseEnvelope<GrantInfo[]>))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(IdeaTidyProposal))]
[JsonSerializable(typeof(MilestoneProposal))]
[JsonSerializable(typeof(MilestoneChoice))]
[JsonSerializable(typeof(IssueReadiness))]
[JsonSerializable(typeof(ReadinessResult))]
[JsonSerializable(typeof(MilestoneSuggestions))]
[JsonSerializable(typeof(ItemSuggestion))]
[JsonSerializable(typeof(NewMilestoneSuggestion))]
[JsonSerializable(typeof(BlackBoxLine))]
[JsonSerializable(typeof(NodeInfo))]
[JsonSerializable(typeof(WatchtowerHealth))]
[JsonSerializable(typeof(NodeTrafficRow))]
[JsonSerializable(typeof(ResponseEnvelope<NodeInfo>))]
[JsonSerializable(typeof(ResponseEnvelope<CommandResult>))]
[JsonSerializable(typeof(ResponseEnvelope<SessionRow[]>))]
[JsonSerializable(typeof(ResponseEnvelope<RepoInfo[]>))]
[JsonSerializable(typeof(RepoStatus[]))]
[JsonSerializable(typeof(ResponseEnvelope<RepoStatus[]>))]
[JsonSerializable(typeof(ResponseEnvelope<IssueInfo[]>))]
[JsonSerializable(typeof(ResponseEnvelope<TranscriptPage>))]
[JsonSerializable(typeof(ResponseEnvelope<ChatPage>))]
[JsonSerializable(typeof(ResponseEnvelope<JobLog>))]
[JsonSerializable(typeof(SpendReport))]
[JsonSerializable(typeof(ResponseEnvelope<SpendReport>))]
[JsonSerializable(typeof(SpendWasteReport))]
[JsonSerializable(typeof(ResponseEnvelope<SpendWasteReport>))]
[JsonSerializable(typeof(ResponseEnvelope<SettingsPage>))]
[JsonSerializable(typeof(ResponseEnvelope<SetupChecks>))]
public sealed partial class PilotJsonContext : JsonSerializerContext;
