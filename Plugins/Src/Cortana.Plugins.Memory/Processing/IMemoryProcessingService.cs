namespace Cortana.Plugins.Memory.Processing;

/// <summary>
/// 记忆数据处理服务。
/// </summary>
public interface IMemoryProcessingService
{
    /// <summary>
    /// 执行一轮记忆数据处理。
    /// </summary>
    MemoryProcessingResult Process(MemoryProcessingRequest request);

    /// <summary>
    /// 获取指定处理器状态。
    /// </summary>
    MemoryProcessingState GetState(string processorName, string? agentId, string? workspaceId);
}
