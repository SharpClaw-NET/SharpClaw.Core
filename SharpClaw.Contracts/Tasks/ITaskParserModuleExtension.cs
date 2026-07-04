namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Allows a module to extend the task script parser with additional
/// context-API operation names and event-trigger handler names.
/// Implementations are registered once at startup via
/// <c>TaskScriptParser.RegisterModule</c>.
/// </summary>
public interface ITaskParserModuleExtension
{
    /// <summary>
    /// Maps context-API method names (as they appear in task scripts) to a
    /// module-owned operation key and the owning module ID.
    /// The parser records the operation key in <c>TaskStatementDefinition.StatementKey</c>.
    /// </summary>
    IReadOnlyDictionary<string, (string OperationKey, string ModuleId)> OperationKeyMappings { get; }

    /// <summary>
    /// Maps event-handler method names (as they appear in task scripts) to a
    /// module-owned trigger key and the owning module ID.
    /// The parser stores the key in <c>TaskStatementDefinition.ModuleTriggerKey</c>.
    /// </summary>
    IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; }

    /// <summary>
    /// Method names in <see cref="OperationKeyMappings"/> whose first argument
    /// should be captured as <c>Expression</c> on the parsed statement.
    /// </summary>
    IReadOnlySet<string> SingleArgExpressionMethods { get; }

    /// <summary>
    /// Maps trigger-attribute names (short form, e.g. <c>"Schedule"</c>) to
    /// module-owned handlers that emit a <see cref="TaskTriggerDefinition"/>
    /// for each matching attribute occurrence. The parser also accepts the
    /// <c>"…Attribute"</c> long form for the same handler. A registered
    /// handler returning <see langword="null"/> from
    /// <see cref="ITaskTriggerAttributeHandler.Handle"/> declines the
    /// attribute and the parser falls back to its built-in switch.
    /// <para>
    /// Phase 1 of the trigger-attribute module migration. No core
    /// attribute is moved out yet; the legacy switch in
    /// <c>TaskScriptParser</c> remains the source of truth for any
    /// attribute name that no module claims.
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers
        => new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal);
}
