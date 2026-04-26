using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Processing;
using Cortana.Plugins.Memory.Storage;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 基于记忆服务层读取系统基础状态。
/// </summary>
public sealed class MemoryStatusService(IMemoryStore store) : IMemoryStatusService
{
    public MemoryStatusResult GetStatus(MemoryStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = store.GetStatusSnapshot(request.AgentId, request.WorkspaceId);
        var processingState = store.GetProcessingState(MemoryProcessingService.FragmentExtractionProcessorName, request.AgentId, request.WorkspaceId);
        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(processingState.LastError)) issues.Add(processingState.LastError);

        return new MemoryStatusResult
        {
            ObservationCount = snapshot.ObservationCount,
            FragmentCount = snapshot.FragmentCount,
            AbstractionCount = snapshot.AbstractionCount,
            RecallLogCount = snapshot.RecallLogCount,
            ProcessingState = processingState.State,
            LastProcessedAt = string.IsNullOrWhiteSpace(processingState.UpdatedAt) ? null : processingState.UpdatedAt,
            KnownIssues = issues
        };
    }
}
