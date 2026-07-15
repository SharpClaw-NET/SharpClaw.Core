namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Describes one provider-neutral mutation to task-scoped shared data.
/// Hosts can persist the mutation without receiving or rebuilding an
/// aggregate snapshot of every large entry.
/// </summary>
public sealed record TaskSharedDataChange(
    TaskSharedDataChangeKind Kind,
    string Description,
    string? LightData = null,
    BigDataEntry? BigData = null,
    string? BigDataId = null);

/// <summary>Identifies the neutral task shared-data mutation shape.</summary>
public enum TaskSharedDataChangeKind
{
    /// <summary>The bounded light-data value was replaced or cleared.</summary>
    LightDataReplaced,

    /// <summary>One individually addressed big-data value was written.</summary>
    BigDataUpserted,

    /// <summary>One individually addressed big-data value was removed.</summary>
    BigDataRemoved,
}
