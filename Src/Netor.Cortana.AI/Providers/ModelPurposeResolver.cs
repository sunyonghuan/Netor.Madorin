using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys.Services;

using System.Collections.Concurrent;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 用途级模型路由解析器。按「用途键」从 SystemSettings 读取对应 ModelId，
/// 再通过厂商驱动实例化并缓存 <see cref="IChatClient"/>。
/// </summary>
/// <remarks>
/// <para>典型用途键：
/// <list type="bullet">
/// <item><description><c>Compaction.ModelId</c> —— 会话压缩摘要模型。</description></item>
/// <item><description><c>Memory.ModelId</c> —— 插件申请用于记忆加工的模型。</description></item>
/// </list>
/// </para>
/// <para>当对应 SettingKey 为空时，<see cref="TryResolve"/> 返回 <c>null</c>，调用方应回退到当前对话模型。</para>
/// <para>按 ModelId 缓存 <see cref="IChatClient"/>，配置变更时自动释放旧实例并重建，避免连接泄漏。</para>
/// </remarks>
public sealed class ModelPurposeResolver(
    SystemSettingsService systemSettings,
    AiModelService modelService,
    IServiceProvider services,
    ILogger<ModelPurposeResolver> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 解析指定用途键对应的 <see cref="IChatClient"/>。SettingKey 为空或配置无效时返回 <c>null</c>。
    /// </summary>
    public IChatClient? TryResolve(string settingKey)
    {
        if (string.IsNullOrWhiteSpace(settingKey)) return null;

        var modelId = systemSettings.GetValue(settingKey);
        if (string.IsNullOrEmpty(modelId))
        {
            // 用户清空了配置 → 释放该键缓存
            if (_cache.TryRemove(settingKey, out var removed)) removed.Client.Dispose();
            return null;
        }

        if (_cache.TryGetValue(settingKey, out var cached)
            && string.Equals(cached.ModelId, modelId, StringComparison.Ordinal))
        {
            return cached.Client;
        }

        var model = modelService.GetById(modelId);
        if (model is null)
        {
            logger.LogWarning("用途键 {Key} 指向的模型 {ModelId} 不存在。", settingKey, modelId);
            return null;
        }

        var providerService = services.GetRequiredService<AiProviderService>();
        var provider = providerService.GetById(model.ProviderId);
        if (provider is null)
        {
            logger.LogWarning("用途键 {Key} 对应模型 {ModelId} 的厂商 {ProviderId} 不存在。",
                settingKey, modelId, model.ProviderId);
            return null;
        }

        var registry = services.GetRequiredService<AiProviderDriverRegistry>();
        var driver = registry.Resolve(provider);
        var client = driver.CreateChatClient(provider, model);

        // 替换缓存：释放旧实例
        if (_cache.TryRemove(settingKey, out var old)) old.Client.Dispose();
        _cache[settingKey] = new CachedEntry(modelId, client);
        return client;
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values) entry.Client.Dispose();
        _cache.Clear();
    }

    private readonly record struct CachedEntry(string ModelId, IChatClient Client);
}
