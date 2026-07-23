using System.Text.Json;

namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Validates catalog schemas and tool payloads without exposing a schema engine.</summary>
public interface IToolSchemaValidator
{
    /// <summary>Validates both schemas declared by a tool.</summary>
    public ToolSchemaValidationResult ValidateDescriptor(ToolDescriptor descriptor);

    /// <summary>Validates model-produced arguments before execution.</summary>
    public ToolSchemaValidationResult ValidateArguments(
        ToolDescriptor descriptor,
        JsonElement arguments);

    /// <summary>Validates executor output before it is returned to the model.</summary>
    public ToolSchemaValidationResult ValidateResult(
        ToolDescriptor descriptor,
        JsonElement result);
}

/// <summary>Contains a stable schema-validation outcome.</summary>
public sealed record ToolSchemaValidationResult(
    bool IsValid,
    string? Error = null)
{
    public static ToolSchemaValidationResult Valid { get; } = new(true);
}
