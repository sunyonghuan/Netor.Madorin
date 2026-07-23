using System.Text.Json;
using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContentBlock), "text")]
[JsonDerivedType(typeof(ReasoningContentBlock), "reasoning")]
[JsonDerivedType(typeof(ToolCallContentBlock), "tool_call")]
[JsonDerivedType(typeof(ToolResultContentBlock), "tool_result")]
[JsonDerivedType(typeof(BlobRefContentBlock), "blob_ref")]
public abstract record ContentBlock;

public sealed record TextContentBlock(string Text) : ContentBlock;

public sealed record ReasoningContentBlock(
    string Content,
    ProviderExtensionData[]? ProviderExtensions = null) : ContentBlock;

public sealed record ToolCallContentBlock(
    string CallId,
    string ToolId,
    string Name,
    JsonElement Arguments) : ContentBlock;

public sealed record ToolResultContentBlock(
    string CallId,
    string ToolId,
    bool Success,
    ContentBlock[] Content) : ContentBlock;

public sealed record BlobRefContentBlock(BlobReference Blob) : ContentBlock;
