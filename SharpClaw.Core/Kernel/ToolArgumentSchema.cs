using System.Text.Json;
using Json.Schema;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Core.Kernel;

internal static class ToolArgumentSchema
{
    internal static void ValidateDefinition(ToolDescriptor descriptor)
    {
        try
        {
            _ = Build(descriptor.ParametersSchema);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new KernelGraphCompilationException(
                $"Tool '{descriptor.Name}' has an invalid JSON Schema: {exception.Message}");
        }
    }

    internal static void ValidateArguments(ToolDescriptor descriptor, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new KernelActionExecutionException(
                $"Tool '{descriptor.Name}' arguments must be a JSON object.");

        try
        {
            if (!Build(descriptor.ParametersSchema).Evaluate(arguments).IsValid)
                throw new KernelActionExecutionException(
                    $"Tool '{descriptor.Name}' arguments do not satisfy its JSON Schema.");
        }
        catch (KernelActionExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new KernelActionExecutionException(
                $"Tool '{descriptor.Name}' has an unusable JSON Schema: {exception.Message}");
        }
    }

    private static JsonSchema Build(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
            throw new JsonException("Tool parameters must have an object JSON Schema.");
        RejectExternalReferences(definition);
        return JsonSchema.Build(definition, new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry(),
        });
    }

    private static void RejectExternalReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectExternalReferences(item);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "$ref" or "$dynamicRef" or "$recursiveRef"
                && (property.Value.ValueKind != JsonValueKind.String
                    || !property.Value.GetString()!.StartsWith('#')))
            {
                throw new JsonException("Tool schemas may reference only their own definitions.");
            }

            RejectExternalReferences(property.Value);
        }
    }
}
