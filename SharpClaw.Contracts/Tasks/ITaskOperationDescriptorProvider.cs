namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Implemented by modules that contribute task operation descriptors. The host
/// collects all <see cref="ITaskOperationDescriptorProvider"/> implementations
/// at startup and registers their descriptors with the task operation registry
/// before any task script is parsed.
/// </summary>
public interface ITaskOperationDescriptorProvider
{
    /// <summary>The module ID contributing these descriptors.</summary>
    string ModuleId { get; }

    /// <summary>
    /// All descriptors contributed by this module. Each descriptor's
    /// <see cref="TaskOperationDescriptor.OwnerId"/> must equal <see cref="ModuleId"/>.
    /// </summary>
    IReadOnlyList<TaskOperationDescriptor> Descriptors { get; }
}
