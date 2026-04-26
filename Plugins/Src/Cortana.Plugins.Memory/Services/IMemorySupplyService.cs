using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 提供主动记忆上下文供应能力。
/// </summary>
public interface IMemorySupplyService
{
    /// <summary>
    /// 根据当前任务上下文生成结构化记忆供应包。
    /// </summary>
    MemorySupplyResult Supply(MemorySupplyRequest request);
}
