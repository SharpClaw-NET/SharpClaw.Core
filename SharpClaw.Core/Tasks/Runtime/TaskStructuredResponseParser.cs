using System.Text.Json;
using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Extracts and validates structured JSON responses produced by module-owned
/// task operations.
/// </summary>
public sealed class TaskStructuredResponseParser
{
    /// <summary>
    /// Extracts the JSON object from <paramref name="text"/> and validates it
    /// against a declared task data type when one is provided and known.
    /// </summary>
    public string Parse(
        string text,
        string? typeName,
        IReadOnlyList<TaskDataTypeDefinition>? dataTypes = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var jsonText = ExtractJsonObject(text)
            ?? throw new InvalidOperationException(
                "ParseResponse expected a JSON object in the source text.");

        using var doc = JsonDocument.Parse(jsonText);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "ParseResponse expected a JSON object payload.");
        }

        if (!string.IsNullOrWhiteSpace(typeName) && dataTypes is not null)
        {
            var dataType = dataTypes.FirstOrDefault(dt => dt.Name == typeName);
            if (dataType is not null)
                ValidateParsedResponseShape(doc.RootElement, dataType);
        }

        return JsonSerializer.Serialize(doc.RootElement);
    }

    /// <summary>
    /// Returns the first-to-last brace slice used by SharpClaw task response
    /// parsing.
    /// </summary>
    public string? ExtractJsonObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        return jsonStart >= 0 && jsonEnd > jsonStart
            ? text[jsonStart..(jsonEnd + 1)]
            : null;
    }

    /// <summary>
    /// Validates a JSON object against the required properties in a task data
    /// type definition.
    /// </summary>
    public void ValidateParsedResponseShape(
        JsonElement element,
        TaskDataTypeDefinition dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);

        foreach (var property in dataType.Properties)
        {
            if (!element.TryGetProperty(property.Name, out var propertyElement))
            {
                throw new InvalidOperationException(
                    $"ParseResponse<{dataType.Name}> missing property '{property.Name}'.");
            }

            if (property.IsCollection)
            {
                if (propertyElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"Property '{property.Name}' must be a JSON array.");
                }

                continue;
            }

            if (!IsCompatibleJsonValue(propertyElement, property.TypeName))
            {
                throw new InvalidOperationException(
                    $"Property '{property.Name}' does not match declared type '{property.TypeName}'.");
            }
        }
    }

    /// <summary>
    /// Returns true when a JSON value matches the task script runtime type.
    /// </summary>
    public bool IsCompatibleJsonValue(JsonElement value, string typeName)
    {
        var normalizedType = typeName.TrimEnd('?');
        return normalizedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "int" or "long" or "double" or "decimal" => value.ValueKind == JsonValueKind.Number,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "Guid" or "DateTime" or "DateTimeOffset" or "TimeSpan" => value.ValueKind == JsonValueKind.String,
            _ => value.ValueKind == JsonValueKind.Object
        };
    }
}
