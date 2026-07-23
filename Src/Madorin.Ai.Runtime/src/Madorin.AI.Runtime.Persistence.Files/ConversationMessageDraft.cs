using System.Text.Json;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Describes a canonical message before its identity and sequence are allocated.</summary>
public sealed record ConversationMessageDraft(
    string InvocationId,
    string AgentId,
    string Role,
    JsonElement Content,
    DateTimeOffset Timestamp,
    string? UsageReference = null,
    JsonElement? SummaryMetadata = null);
