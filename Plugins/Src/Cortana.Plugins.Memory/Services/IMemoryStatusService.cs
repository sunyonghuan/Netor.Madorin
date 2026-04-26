using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 提供记忆系统状态读取能力。
/// </summary>
public interface IMemoryStatusService
{
    /// <summary>
    /// 获取记忆系统基础状态。
    /// </summary>
    MemoryStatusResult GetStatus(MemoryStatusRequest request);
}
