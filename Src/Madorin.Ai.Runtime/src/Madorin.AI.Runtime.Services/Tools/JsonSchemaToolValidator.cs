using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Validates tool payloads with cached JSON Schema 2020-12 schemas.</summary>
public sealed class JsonSchemaToolValidator : IToolSchemaValidator
{
    private readonly ConcurrentDictionary<string, JsonSchema> _schemas =
        new(StringComparer.Ordinal);

    public ToolSchemaValidationResult ValidateDescriptor(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        try
        {
            _ = GetSchema(descriptor.InputSchemaJson);
            _ = GetSchema(descriptor.OutputSchemaJson);
            return ToolSchemaValidationResult.Valid;
        }
        catch (JsonException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
    }

    public ToolSchemaValidationResult ValidateArguments(
        ToolDescriptor descriptor,
        JsonElement arguments) =>
        Evaluate(descriptor.InputSchemaJson, arguments, "arguments");

    public ToolSchemaValidationResult ValidateResult(
        ToolDescriptor descriptor,
        JsonElement result) =>
        Evaluate(descriptor.OutputSchemaJson, result, "result");

    private ToolSchemaValidationResult Evaluate(
        string schemaJson,
        JsonElement instance,
        string payloadName)
    {
        try
        {
            var evaluation = GetSchema(schemaJson).Evaluate(
                instance,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            return evaluation.IsValid
                ? ToolSchemaValidationResult.Valid
                : new ToolSchemaValidationResult(
                    false,
                    $"Tool {payloadName} does not satisfy its JSON Schema.");
        }
        catch (JsonException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
    }

    private JsonSchema GetSchema(string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        return _schemas.GetOrAdd(schemaJson, static json => JsonSchema.FromText(json));
    }
}
