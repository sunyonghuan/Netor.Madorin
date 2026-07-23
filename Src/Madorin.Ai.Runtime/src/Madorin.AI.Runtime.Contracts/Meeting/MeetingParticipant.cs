using System.Text.Json;
using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// A meeting participant with stable identity, agent reference, and availability status.
/// Supports deserialization from the legacy <see cref="AgentRef"/> JSON shape for backward compatibility.
/// </summary>
[JsonConverter(typeof(MeetingParticipantConverter))]
public sealed record MeetingParticipant(
    string ParticipantId,
    AgentRef Agent,
    string DisplayName,
    int JoinOrder,
    ParticipantStatus Status = ParticipantStatus.Active)
{
    /// <summary>Minimum number of participants required to start a meeting.</summary>
    public const int MinCount = 2;

    /// <summary>Recommended upper bound for participants.</summary>
    public const int RecommendedMaxCount = 10;

    /// <summary>Hard upper bound for participants.</summary>
    public const int HardMaxCount = 20;

    [JsonIgnore]
    internal bool UsesLegacyWireShape { get; init; }

    internal static MeetingParticipant FromLegacy(AgentRef agent, int joinOrder) =>
        new(agent.AgentId, agent, agent.AgentId, joinOrder)
        {
            UsesLegacyWireShape = true
        };
}

/// <summary>
/// AOT-safe converter for <see cref="MeetingParticipant"/>.
/// Reads both the new format (nested <c>agent</c> object) and the legacy format
/// (flat <c>agentId</c> etc. at root). Writes only the new format.
/// Never calls generic <c>JsonSerializer.Serialize/Deserialize</c> with <see cref="JsonSerializerOptions"/>.
/// </summary>
public sealed class MeetingParticipantConverter : JsonConverter<MeetingParticipant>
{
    public override MeetingParticipant Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        AgentRef agentRef;
        if (root.TryGetProperty("agent", out var agentEl) && agentEl.ValueKind == JsonValueKind.Object)
        {
            agentRef = ReadAgentRef(agentEl);
        }
        else
        {
            agentRef = ReadAgentRef(root);
        }

        var participantId = root.TryGetProperty("participantId", out var pid)
            ? pid.GetString() ?? agentRef.AgentId
            : agentRef.AgentId;

        var displayName = root.TryGetProperty("displayName", out var dn)
            ? dn.GetString() ?? participantId
            : participantId;

        var joinOrder = root.TryGetProperty("joinOrder", out var jo)
            ? jo.GetInt32()
            : 0;

        var status = ParticipantStatus.Active;
        if (root.TryGetProperty("status", out var statusElement)
            && !Enum.TryParse(
                statusElement.GetString(),
                ignoreCase: true,
                out status))
        {
            throw new JsonException("The meeting participant status is invalid.");
        }

        return new MeetingParticipant(participantId, agentRef, displayName, joinOrder, status)
        {
            UsesLegacyWireShape = !root.TryGetProperty("agent", out _)
        };
    }

    public override void Write(
        Utf8JsonWriter writer, MeetingParticipant value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.UsesLegacyWireShape)
        {
            WriteAgentRef(writer, value.Agent);
            writer.WriteEndObject();
            return;
        }

        writer.WriteString("participantId", value.ParticipantId);

        writer.WritePropertyName("agent");
        writer.WriteStartObject();
        WriteAgentRef(writer, value.Agent);
        writer.WriteEndObject();

        writer.WriteString("displayName", value.DisplayName);
        writer.WriteNumber("joinOrder", value.JoinOrder);
        writer.WriteString("status", value.Status.ToString());
        writer.WriteEndObject();
    }

    private static void WriteAgentRef(Utf8JsonWriter writer, AgentRef agent)
    {
        writer.WriteString("agentId", agent.AgentId);
        writer.WriteString("promptTemplateVersion", agent.PromptTemplateVersion);
        writer.WriteString("systemPrompt", agent.SystemPrompt);
        if (agent.ProviderId is not null)
        {
            writer.WriteString("providerId", agent.ProviderId);
        }

        if (agent.ModelId is not null)
        {
            writer.WriteString("modelId", agent.ModelId);
        }

        WriteStringArray(writer, "allowedToolIds", agent.AllowedToolIds);
        WriteStringArray(writer, "skillIds", agent.SkillIds);
    }

    private static AgentRef ReadAgentRef(JsonElement el)
    {
        if (!el.TryGetProperty("agentId", out var agentIdElement)
            || string.IsNullOrWhiteSpace(agentIdElement.GetString()))
        {
            throw new JsonException("A meeting participant agentId is required.");
        }

        var agentId = agentIdElement.GetString()!;
        var ptfVersion = el.TryGetProperty("promptTemplateVersion", out var ptv)
            ? ptv.GetString() ?? "v1"
            : "v1";
        var systemPrompt = el.TryGetProperty("systemPrompt", out var sp)
            ? sp.GetString() ?? string.Empty
            : string.Empty;
        var providerId = el.TryGetProperty("providerId", out var pi)
            ? pi.GetString()
            : null;
        var modelId = el.TryGetProperty("modelId", out var mi)
            ? mi.GetString()
            : null;
        var allowedToolIds = ReadStringArray(el, "allowedToolIds");
        var skillIds = ReadStringArray(el, "skillIds");

        return new AgentRef(
            agentId,
            ptfVersion,
            systemPrompt,
            providerId,
            modelId,
            allowedToolIds,
            skillIds);
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        string[]? values)
    {
        if (values is null)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string[]? ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The {propertyName} meeting participant property must be an array.");
        }

        return property.EnumerateArray()
            .Select(item => item.GetString()
                ?? throw new JsonException(
                    $"The {propertyName} meeting participant property must contain strings."))
            .ToArray();
    }
}
