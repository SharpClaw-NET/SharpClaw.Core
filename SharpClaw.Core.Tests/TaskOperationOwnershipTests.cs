using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Compilation;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Parsing;
using SharpClaw.Core.Tasks.Registry;
using SharpClaw.Core.Tasks.Runtime;
using SharpClaw.Core.Tasks.Validation;

namespace SharpClaw.Core.Tests;

public sealed class TaskOperationOwnershipTests
{
    [Fact]
    public async Task Intrinsic_log_step_executes_without_module_executor()
    {
        var step = new TaskStepDefinition
        {
            StepKey = TaskLanguageStepKeys.Log,
            Line = 1,
            Column = 1,
            Expression = "\"core log\""
        };
        var host = new TestExecutionHost();
        using var runtime = TaskRuntimeEntry.Create(CancellationToken.None);
        var instanceId = Guid.NewGuid();
        var engine = new TaskPlanExecutionEngine(
            new TestScopeFactory(),
            []);

        var outcome = await engine.ExecuteAsync(new TaskPlanExecutionRequest(
            instanceId,
            Plan(step),
            runtime.CreateInstance(instanceId),
            new TestServiceProvider(),
            host,
            CancellationToken.None));

        Assert.Equal(TaskInstanceStatus.Completed, outcome.Status);
        Assert.Null(outcome.Error);
        Assert.Contains(host.Logs, log => log.Message == "core log");
    }

    [Fact]
    public async Task Missing_module_executor_fails_instead_of_continuing()
    {
        var step = new TaskStepDefinition
        {
            StepKey = "module.missing_operation",
            Line = 7,
            Column = 3
        };
        var host = new TestExecutionHost();
        using var runtime = TaskRuntimeEntry.Create(CancellationToken.None);
        var instanceId = Guid.NewGuid();
        var engine = new TaskPlanExecutionEngine(
            new TestScopeFactory(),
            []);

        var outcome = await engine.ExecuteAsync(new TaskPlanExecutionRequest(
            instanceId,
            Plan(step),
            runtime.CreateInstance(instanceId),
            new TestServiceProvider(),
            host,
            CancellationToken.None));

        Assert.Equal(TaskInstanceStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Error);
        var error = outcome.Error!;
        Assert.Contains("module.missing_operation", error);
        Assert.Contains("module or operation", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing or was not loaded", error);
        Assert.Equal(error, host.Failure);
    }

    [Fact]
    public void Module_operation_key_is_not_a_core_intrinsic_language_key()
    {
        Assert.False(TaskLanguageStepKeys.IsIntrinsic("module.schema_read"));
    }

    [Fact]
    public void Descriptor_backed_module_calls_parse_generically()
    {
        TaskStepRegistry.Default.Reset();
        try
        {
            TaskStepRegistry.Default.Register(new TaskStepDescriptor
            {
                MethodName = "ModuleCall",
                StepKey = "module.custom_operation",
                OwnerId = "ExampleModule",
                FirstArgIsExpression = true,
                CapturesGenericType = true
            });

            var result = TaskScriptParser.Parse("""
                using System.Threading;
                using System.Threading.Tasks;

                [Task("module-call")]
                public sealed class ModuleCallTask
                {
                    public async Task RunAsync(CancellationToken ct)
                    {
                        var output = await ModuleCall<Result>("payload", ct);
                    }

                    public sealed class Result
                    {
                        public string Value { get; set; }
                    }
                }
                """);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var step = Assert.Single(result.Definition!.Steps);
            Assert.Equal("module.custom_operation", step.StepKey);
            Assert.Equal("Result", step.TypeName);
            Assert.Equal("output", step.ResultVariable);
            Assert.Equal("payload", step.Expression);
            Assert.Equal(new[] { "payload", "ct" }, step.Arguments);
        }
        finally
        {
            TaskStepRegistry.Default.Reset();
        }
    }

    [Fact]
    public void Module_descriptor_requiring_declared_generic_type_fails_unknown_type()
    {
        TaskStepRegistry.Default.Reset();
        try
        {
            TaskStepRegistry.Default.Register(new TaskStepDescriptor
            {
                MethodName = "ReadSchema",
                StepKey = "module.schema_read",
                OwnerId = "SchemaModule",
                FirstArgIsExpression = true,
                CapturesGenericType = true,
                RequiresDeclaredGenericType = true
            });

            var result = TaskScriptParser.Parse("""
                using System.Threading;
                using System.Threading.Tasks;

                [Task("schema-read")]
                public sealed class SchemaReadTask
                {
                    public async Task RunAsync(CancellationToken ct)
                    {
                        var parsed = await ReadSchema<MissingSchema>(raw);
                    }
                }
                """);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var validation = TaskScriptValidator.Validate(result.Definition!);

            var diagnostic = Assert.Single(validation.Diagnostics);
            Assert.False(validation.IsValid);
            Assert.Equal("TASK108", diagnostic.Code);
            Assert.Contains("module.schema_read", diagnostic.Message);
            Assert.Contains("MissingSchema", diagnostic.Message);
        }
        finally
        {
            TaskStepRegistry.Default.Reset();
        }
    }

    [Fact]
    public void Module_descriptor_requiring_declared_generic_type_passes_declared_type()
    {
        TaskStepRegistry.Default.Reset();
        try
        {
            TaskStepRegistry.Default.Register(new TaskStepDescriptor
            {
                MethodName = "ReadSchema",
                StepKey = "module.schema_read",
                OwnerId = "SchemaModule",
                FirstArgIsExpression = true,
                CapturesGenericType = true,
                RequiresDeclaredGenericType = true
            });

            var result = TaskScriptParser.Parse("""
                using System.Threading;
                using System.Threading.Tasks;

                [Task("schema-read")]
                public sealed class SchemaReadTask
                {
                    public async Task RunAsync(CancellationToken ct)
                    {
                        var parsed = await ReadSchema<KnownSchema>(raw);
                    }

                    public sealed class KnownSchema
                    {
                        public string Value { get; set; }
                    }
                }
                """);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var validation = TaskScriptValidator.Validate(result.Definition!);

            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Diagnostics));
            Assert.DoesNotContain(validation.Diagnostics, diagnostic => diagnostic.Code == "TASK108");
        }
        finally
        {
            TaskStepRegistry.Default.Reset();
        }
    }

    [Fact]
    public void Generic_module_descriptor_without_declared_type_requirement_does_not_emit_task108()
    {
        TaskStepRegistry.Default.Reset();
        try
        {
            TaskStepRegistry.Default.Register(new TaskStepDescriptor
            {
                MethodName = "LooseGeneric",
                StepKey = "module.loose_generic",
                OwnerId = "LooseModule",
                FirstArgIsExpression = true,
                CapturesGenericType = true
            });

            var result = TaskScriptParser.Parse("""
                using System.Threading;
                using System.Threading.Tasks;

                [Task("loose-generic")]
                public sealed class LooseGenericTask
                {
                    public async Task RunAsync(CancellationToken ct)
                    {
                        var parsed = await LooseGeneric<ExternalRuntimeType>(raw);
                    }
                }
                """);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var step = Assert.Single(result.Definition!.Steps);
            Assert.Equal("module.loose_generic", step.StepKey);
            Assert.Equal("ExternalRuntimeType", step.TypeName);
            Assert.Equal("parsed", step.ResultVariable);

            var validation = TaskScriptValidator.Validate(result.Definition!);
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Diagnostics));
            Assert.DoesNotContain(validation.Diagnostics, diagnostic => diagnostic.Code == "TASK108");
        }
        finally
        {
            TaskStepRegistry.Default.Reset();
        }
    }

    private static CompiledTaskPlan Plan(params TaskStepDefinition[] steps)
    {
        var definition = new TaskScriptDefinition
        {
            Name = "test-task",
            SourceText = string.Empty,
            ClassName = "TestTask",
            EntryPointMethod = "RunAsync",
            Parameters = [],
            DataTypes = [],
            Steps = steps
        };

        return new CompiledTaskPlan
        {
            TaskName = definition.Name,
            Definition = definition,
            ParameterValues = new Dictionary<string, object?>(),
            ExecutionSteps = steps
        };
    }

    private sealed class TestExecutionHost : ITaskPlanExecutionHost
    {
        public List<(string Message, string Level)> Logs { get; } = [];
        public string? Failure { get; private set; }

        public Task<Guid?> LoadInitialChannelIdAsync(
            Guid instanceId,
            CancellationToken ct) =>
            Task.FromResult<Guid?>(Guid.NewGuid());

        public Task PersistOutputAsync(
            Guid instanceId,
            long sequence,
            string? outputJson,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSharedDataSnapshotAsync(
            Guid instanceId,
            string? lightSnapshot,
            string? bigSnapshotJson,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task AppendLogAsync(
            Guid instanceId,
            string message,
            string level,
            CancellationToken ct)
        {
            Logs.Add((message, level));
            return Task.CompletedTask;
        }

        public Task MarkTerminalStatusAsync(
            Guid instanceId,
            TaskInstanceStatus status,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkFailedAsync(
            Guid instanceId,
            string error,
            CancellationToken ct)
        {
            Failure = error;
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            new TestScope();
    }

    private sealed class TestScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new TestServiceProvider();

        public void Dispose()
        {
        }
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
