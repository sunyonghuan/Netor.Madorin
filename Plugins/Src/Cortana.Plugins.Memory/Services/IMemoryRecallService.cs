using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 提供记忆召回能力。
/// </summary>
public interface IMemoryRecallService
{
    /// <summary>
    /// 根据请求召回相关记忆，并记录召回日志。
    /// </summary>
    MemoryRecallResult Recall(MemoryRecallRequest request);
}
