using System.Data;
using System.Text.Json;

namespace Netor.Cortana.Networks;

internal static class ConversationExportRecordMapper
{
    public static ConversationExportRecord Read(IDataRecord record)
    {
        var metadata = ConversationExportSessionMetadata.Parse(GetString(record, "RawDiscription"));
        var role = GetString(record, "Role") ?? string.Empty;
        var modelName = FirstNonEmpty(GetString(record, "ModelName"), metadata.ModelName);

        return new ConversationExportRecord
        {
            Id = GetString(record, "Id") ?? string.Empty,
            AgentId = FirstNonEmpty(GetString(record, "MessageAgentId"), GetString(record, "SessionAgentId"), metadata.AgentId, "global"),
            WorkspaceId = GetString(record, "WorkspaceId"),
            SessionId = GetString(record, "SessionId") ?? string.Empty,
            MessageId = GetString(record, "Id"),
            EventType = ResolveEventType(role),
            Role = role,
            Content = GetString(record, "Content"),
            CreatedTimestamp = GetInt64(record, "CreatedTimestamp"),
            ProviderId = metadata.ProviderId,
            ProviderName = metadata.ProviderName,
            AgentName = FirstNonEmpty(GetString(record, "MessageAgentName"), metadata.AgentName),
            ModelId = metadata.ModelId,
            ModelName = modelName,
        };
    }

    private static string ResolveEventType(string role)
    {
        if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return "conversation.user.message";
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) return "conversation.turn.completed";
        return "conversation.message";
    }

    private static string? GetString(IDataRecord record, string name)
    {
        var ordinal = TryGetOrdinal(record, name);
        if (ordinal < 0) return null;
        return record.IsDBNull(ordinal) ? null : record.GetString(ordinal);
    }

    private static long GetInt64(IDataRecord record, string name)
    {
        var ordinal = TryGetOrdinal(record, name);
        if (ordinal < 0) return 0L;
        return record.IsDBNull(ordinal) ? 0L : record.GetInt64(ordinal);
    }

    private static int TryGetOrdinal(IDataRecord record, string name)
    {
        for (var i = 0; i < record.FieldCount; i++)
        {
            if (string.Equals(record.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}

internal sealed record ConversationExportSessionMetadata(
    string? ProviderId,
    string? ProviderName,
    string? AgentId,
    string? AgentName,
    string? ModelId,
    string? ModelName)
{
    public static ConversationExportSessionMetadata Parse(string? rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription)) return Empty;

        try
        {
            using var doc = JsonDocument.Parse(rawDescription);
            var root = doc.RootElement;
            return new ConversationExportSessionMetadata(
                FindString(root, "providerid", "providerId", "ProviderId"),
                FindString(root, "providername", "providerName", "ProviderName"),
                FindString(root, "agentid", "agentId", "AgentId"),
                FindString(root, "agentname", "agentName", "AgentName"),
                FindString(root, "modeldbid", "modelId", "ModelId"),
                FindString(root, "modelName", "ModelName", "modelid"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static readonly ConversationExportSessionMetadata Empty = new(null, null, null, null, null, null);

    private static string? FindString(JsonElement element, params string[] names) => FindString(element, names, 0);

    private static string? FindString(JsonElement element, string[] names, int depth)
    {
        if (depth > 6) return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.ToString();
                }

                var nested = FindString(property.Value, names, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, names, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }

        return null;
    }
}