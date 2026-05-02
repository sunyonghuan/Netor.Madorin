using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Serialization;
using Cortana.Plugins.Memory.Storage;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.Processing;

public sealed class MemoryAbstractionService(
    IMemoryStore store,
    IMemoryAbstractionGenerator generator,
    ILogger<MemoryAbstractionService> logger) : IMemoryAbstractionService
{
    public void RunAbstractionPass(string? agentId = null, string? workspaceId = null, int minSupportCount = 3, int topPerTopic = 50)
    {
        var generatedCount = 0;
        var agents = string.IsNullOrWhiteSpace(agentId) ? store.GetDistinctAgentIds() : new[] { agentId };
        foreach (var ag in agents)
        {
            // 拉取候选 fragment，按 topic 聚合
            var fragments = store.GetFragmentsForAbstraction(ag, workspaceId, null, minSupportCount, topPerTopic);
            var byTopic = fragments.GroupBy(f => f.Topic ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            foreach (var g in byTopic)
            {
                var topic = g.Key;
                var list = g.ToList();
                if (list.Count < minSupportCount) continue;

                var abstraction = generator.GenerateAbstraction(ag, workspaceId, topic, list, Guid.NewGuid().ToString("N"));
                store.UpsertMemoryAbstraction(abstraction);
                InsertMutation(ag, abstraction.Id, "abstraction", "create", null, JsonSerializer.Serialize(abstraction, MemoryInternalJsonContext.Default.MemoryAbstraction), "抽象记忆批处理生成。", null!);
                InsertAbstractionCreatedEvent(ag, abstraction.Id, abstraction.SupportingMemoryIdsJson);
                generatedCount++;
            }
        }

        logger.LogInformation("抽象记忆批处理完成。AgentId={AgentId}, WorkspaceId={WorkspaceId}, GeneratedCount={GeneratedCount}", agentId, workspaceId, generatedCount);
    }

    private void InsertAbstractionCreatedEvent(string agentId, string abstractionId, string supportingMemoryIdsJson)
    {
        var payload = new AbstractionCreatedEventPayload
        {
            AbstractionId = abstractionId,
            SupportingMemoryIdsJson = supportingMemoryIdsJson
        };
        store.InsertMemoryEvent(new MemoryEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            AgentId = string.IsNullOrWhiteSpace(agentId) ? "global" : agentId,
            EventType = "abstraction.created",
            PayloadJson = JsonSerializer.Serialize(payload, MemoryInternalJsonContext.Default.AbstractionCreatedEventPayload),
            ProcessedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private void InsertMutation(string agentId, string memoryId, string memoryKind, string mutationType, string? beforeJson, string? afterJson, string reason, string traceId)
    {
        store.InsertMemoryMutation(new MemoryMutation
        {
            Id = Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            MemoryId = memoryId,
            MemoryKind = memoryKind,
            MutationType = mutationType,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            TraceId = traceId,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }
}
