using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Processing;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆业务存储门面，向业务层提供稳定的记忆读写能力。
/// </summary>
/// <remarks>
/// 业务服务应依赖该接口，而不是直接依赖数据库连接、SQL、SQLite 实现或具体表服务。
/// </remarks>
public interface IMemoryStore
{
    void EnsureInitialized();
    void InsertObservation(ObservationRecord record);
    void BulkInsertObservations(IEnumerable<ObservationRecord> records);
    void UpsertMemoryFragment(MemoryFragment fragment);
    void UpsertMemoryAbstraction(MemoryAbstraction abstraction);
    void UpsertMemoryLink(MemoryLink link);
    void InsertMemoryEvent(MemoryEvent memoryEvent);
    void InsertRecallLog(RecallLog recallLog);
    void InsertMemoryMutation(MemoryMutation mutation);
    void AddManualMemory(MemoryFragment fragment, MemoryMutation mutation);
    void UpsertMemorySetting(MemorySetting setting);
    IReadOnlyList<MemorySetting> GetMemorySettings(string? agentId, string? workspaceId);
    IReadOnlyList<ObservationRecord> GetUnprocessedObservations(string processorName, string? agentId, string? workspaceId, int limit);
    MemoryProcessingState GetProcessingState(string processorName, string? agentId, string? workspaceId);
    void UpsertProcessingState(MemoryProcessingState state);
    IReadOnlyList<MemoryFragment> SearchSimilarFragments(string agentId, string? workspaceId, string memoryType, string summary, int limit);
    IReadOnlyList<MemoryRecallItem> SearchRecallCandidates(string agentId, string? workspaceId, string? queryText, double minimumConfidence, bool includeCandidateMemories, int limit);
    void RecordMemoryAccesses(IEnumerable<MemoryRecallItem> items, string accessedAt);
    IReadOnlyList<MemoryRecentItem> ListRecentMemories(string? agentId, string? workspaceId, string? kind, int limit);
    IReadOnlyList<MemoryFragment> GetFragmentsForAbstraction(string agentId, string? workspaceId, string? topic, int minSupportCount, int limit);
    IReadOnlyList<string> GetDistinctAgentIds();
    MemoryStoreStatusSnapshot GetStatusSnapshot(string? agentId, string? workspaceId);
}
