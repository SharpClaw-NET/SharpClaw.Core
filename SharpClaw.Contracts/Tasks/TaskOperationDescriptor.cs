namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Describes a single module task operation that can be registered in the
/// task operation registry. Core intrinsic C# statements are parsed directly
/// and do not use module operation descriptors.
/// </summary>
public sealed record TaskOperationDescriptor
{
    /// <summary>
    /// The script method name as it appears in a task script body for
    /// descriptor-backed module operations.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Stable wire-style operation key owned by the registering module.
    /// </summary>
    public required string OperationKey { get; init; }

    /// <summary>
    /// The module ID that owns this operation. There are no core-owned
    /// operation descriptors; every descriptor must declare a module owner.
    /// </summary>
    public required string OwnerId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the first method argument is captured
    /// as <c>Expression</c> on the parsed statement.
    /// </summary>
    public bool FirstArgIsExpression { get; init; }

    /// <summary>
    /// Optional constant value to prepend to the parsed statement's
    /// <c>Arguments</c> list. Lets a module encode descriptor-specific
    /// data (e.g. an HTTP verb) without leaking it into the
    /// host-side statement contract.
    /// </summary>
    public string? PrefixArgument { get; init; }

    /// <summary>
    /// <see langword="true"/> when the method uses a generic type argument
    /// that should be captured as <c>TypeName</c>.
    /// </summary>
    public bool CapturesGenericType { get; init; }

    /// <summary>
    /// <see langword="true"/> when the captured generic type must name a
    /// data type declared inside the task script. This is an opt-in module
    /// contract for operations that consume task-defined structured schemas.
    /// </summary>
    public bool RequiresDeclaredGenericType { get; init; }

    /// <summary>
    /// When set, the index of the argument that becomes <c>Expression</c>
    /// (overrides <see cref="FirstArgIsExpression"/> when non-zero).
    /// </summary>
    public int ExpressionArgIndex { get; init; }
}
