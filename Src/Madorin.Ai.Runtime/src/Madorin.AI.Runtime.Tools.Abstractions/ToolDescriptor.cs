namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolDescriptor(
    string ToolId,
    string DisplayName,
    string Description,
    string InputSchemaJson);
