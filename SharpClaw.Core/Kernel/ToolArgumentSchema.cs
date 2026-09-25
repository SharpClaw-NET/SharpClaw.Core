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
        if (definition.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Tool parameters must have an object or Boolean JSON Schema.");
        RejectExternalReferences(definition);
        return JsonSchema.Build(definition, new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry(),
        });
    }

    private static void RejectExternalReferences(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "$ref" or "$dynamicRef" or "$recursiveRef"
                && (property.Value.ValueKind != JsonValueKind.String
                    || !property.Value.GetString()!.StartsWith('#')))
            {
                throw new JsonException("Tool schemas may reference only their own definitions.");
            }

            // Unknown keywords and annotation values are opaque. Descend only through
            // Draft 2020-12 keyword locations that actually contain subschemas.
            switch (property.Name)
            {
                case "$defs":
                case "properties":
                case "patternProperties":
                case "dependentSchemas":
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var child in property.Value.EnumerateObject())
                            RejectExternalReferences(child.Value);
                    }
                    break;

                case "allOf":
                case "anyOf":
                case "oneOf":
                case "prefixItems":
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var child in property.Value.EnumerateArray())
                            RejectExternalReferences(child);
                    }
                    break;

                case "not":
                case "if":
                case "then":
                case "else":
                case "items":
                case "contains":
                case "additionalProperties":
                case "unevaluatedProperties":
                case "unevaluatedItems":
                case "propertyNames":
                case "contentSchema":
                    RejectExternalReferences(property.Value);
                    break;
            }
        }
    }
}
