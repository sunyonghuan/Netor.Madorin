using Netor.Cortana.Entitys.Memory;

namespace Netor.Cortana.AI.Memory;

/// <summary>
/// 长期记忆上下文供应客户端。
/// </summary>
public interface ILongMemorySupplyClient
{
    /// <summary>
    /// 请求长期记忆上下文供应包。
    /// </summary>
    /// <param name="request">供应请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>供应包；不可用或降级时返回 <see langword="null"/>。</returns>
    Task<MemoryContextSupplyPackage?> SupplyAsync(
        MemoryContextSupplyRequest request,
        CancellationToken cancellationToken = default);
}
