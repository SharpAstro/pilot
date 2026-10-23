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
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(NodeEvent))]
[JsonSerializable(typeof(SnapshotEvent))]
[JsonSerializable(typeof(SessionRow))]
[JsonSerializable(typeof(FlightRow))]
[JsonSerializable(typeof(LegRow))]
[JsonSerializable(typeof(CardRow))]
[JsonSerializable(typeof(UsageRow))]
[JsonSerializable(typeof(AccountRow))]
[JsonSerializable(typeof(JobRow))]
[JsonSerializable(typeof(PrReviewRow))]
[JsonSerializable(typeof(GrantRequest))]
[JsonSerializable(typeof(ResponseEnvelope<GrantTicket>))]
[JsonSerializable(typeof(ResponseEnvelope<GrantInfo[]>))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(BlackBoxLine))]
[JsonSerializable(typeof(NodeInfo))]
[JsonSerializable(typeof(ResponseEnvelope<NodeInfo>))]
[JsonSerializable(typeof(ResponseEnvelope<CommandResult>))]
[JsonSerializable(typeof(ResponseEnvelope<SessionRow[]>))]
[JsonSerializable(typeof(ResponseEnvelope<RepoInfo[]>))]
[JsonSerializable(typeof(RepoStatus[]))]
[JsonSerializable(typeof(ResponseEnvelope<RepoStatus[]>))]
[JsonSerializable(typeof(ResponseEnvelope<IssueInfo[]>))]
[JsonSerializable(typeof(ResponseEnvelope<TranscriptPage>))]
[JsonSerializable(typeof(ResponseEnvelope<JobLog>))]
[JsonSerializable(typeof(ResponseEnvelope<SettingsPage>))]
public sealed partial class PilotJsonContext : JsonSerializerContext;
