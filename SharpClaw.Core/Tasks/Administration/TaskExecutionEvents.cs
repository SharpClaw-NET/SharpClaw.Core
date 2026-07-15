namespace SharpClaw.Core.Tasks.Administration;

/// <summary>A storage-neutral task diagnostic emitted in execution order.</summary>
public sealed record TaskExecutionLog(
    Guid RecordId,
    Guid InstanceId,
    string Message,
    string Level,
    DateTimeOffset Timestamp);

/// <summary>A storage-neutral task output emitted in execution order.</summary>
public sealed record TaskOutputEmission(
    Guid RecordId,
    Guid InstanceId,
    long Sequence,
    string? Data,
    DateTimeOffset Timestamp);
