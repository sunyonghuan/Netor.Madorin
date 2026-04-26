using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Storage;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 默认最近记忆查看服务。
/// </summary>
public sealed class MemoryRecentService(IMemoryStore store) : IMemoryRecentService
{
    private const int DefaultLimit = 20;
    private const int MaximumLimit = 50;

    /// <inheritdoc />
    public MemoryListRecentResult ListRecent(MemoryListRecentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId)) throw new ArgumentException("智能体标识不能为空。", nameof(request));

        var limit = request.Limit is null or <= 0 ? DefaultLimit : Math.Min(request.Limit.Value, MaximumLimit);
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? null : request.Kind.Trim().ToLowerInvariant();
        if (kind is not null and not "fragment" and not "abstraction")
            throw new ArgumentException("记忆类别只支持 fragment、abstraction 或空值。", nameof(request));

        var items = store.ListRecentMemories(
            request.AgentId.Trim(),
            string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : request.WorkspaceId.Trim(),
            kind,
            limit);

        return new MemoryListRecentResult
        {
            Count = items.Count,
            Limit = limit,
            Kind = kind,
            Items = items
        };
    }
}
