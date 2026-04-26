using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 提供最近记忆查看能力。
/// </summary>
public interface IMemoryRecentService
{
    /// <summary>
    /// 按更新时间读取最近记忆。
    /// </summary>
    /// <param name="request">最近记忆列表请求。</param>
    /// <returns>最近记忆列表结果。</returns>
    MemoryListRecentResult ListRecent(MemoryListRecentRequest request);
}
