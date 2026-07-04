using System.Text.Json;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Core.Modules.Foreign;

public sealed record ForeignModuleTaskOperationExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string OperationKey,
    ForeignModuleTaskOperationExecutionContextSnapshot Context,
    IReadOnlyList<string>? Arguments = null,
    string? Expression = null,
    string? ResultVariable = null);

public sealed record ForeignModuleTaskOperationInvocationRequest(
    int ProtocolVersion,
    string ModuleId,
    ForeignModuleTaskStatementInvocationDescriptor Statement,
    ForeignModuleTaskOperationExecutionContextSnapshot Context);

public sealed record ForeignModuleTaskOperationExecutionContextSnapshot(
    Guid InstanceId,
    Guid ChannelId,
    IReadOnlyDictionary<string, JsonElement>? Variables = null,
    IReadOnlyList<ForeignModuleTaskEventHandlerSnapshot>? EventHandlers = null,
    string? ContextCallbackId = null);

public sealed record ForeignModuleTaskEventHandlerSnapshot(
    string? ModuleTriggerKey,
    string? ParameterName,
    string? HandlerCallbackId = null);

public sealed record ForeignModuleTaskStatementInvocationDescriptor(
    string StatementKey,
    string? VariableName = null,
    string? TypeName = null,
    string? ResultVariable = null,
    string? RawExpression = null,
    IReadOnlyList<string>? Arguments = null,
    string? ModuleTriggerKey = null,
    string? HandlerParameter = null,
    IReadOnlyList<ForeignModuleTaskStatementInvocationDescriptor>? Body = null,
    IReadOnlyList<ForeignModuleTaskStatementInvocationDescriptor>? ElseBody = null)
{
    public static ForeignModuleTaskStatementInvocationDescriptor From(
        ITaskStatementInvocation statement) =>
        new(
            statement.StatementKey,
            statement.VariableName,
            statement.TypeName,
            statement.ResultVariable,
            statement.RawExpression,
            statement.Arguments,
            statement.ModuleTriggerKey,
            statement.HandlerParameter,
            statement.Body is null ? null : [.. statement.Body.Select(From)],
            statement.ElseBody is null ? null : [.. statement.ElseBody.Select(From)]);
}

public sealed record ForeignModuleTaskOperationExecutionResponse(
    TaskStatementResult Result = TaskStatementResult.Continue,
    bool? Continue = null,
    IReadOnlyDictionary<string, JsonElement>? VariableUpdates = null,
    JsonElement? ResultVariableValue = null,
    IReadOnlyList<string>? Logs = null,
    string? OutputJson = null,
    Guid? ChannelId = null,
    IReadOnlyList<ForeignModuleTaskRegisteredEventHandlerDescriptor>? RegisteredEventHandlers = null);

public sealed record ForeignModuleTaskRegisteredEventHandlerDescriptor(
    string ModuleTriggerKey,
    string? ParameterName,
    IReadOnlyList<ForeignModuleTaskStatementInvocationDescriptor> Body);
