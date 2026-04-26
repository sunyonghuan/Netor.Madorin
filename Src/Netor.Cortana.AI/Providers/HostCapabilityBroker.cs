using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 具体实现：基于 <see cref="ModelPurposeResolver"/> 做用途级模型路由与实例缓存复用。
/// </summary>
public sealed class HostCapabilityBroker(
    ModelPurposeResolver resolver,
    ILogger<HostCapabilityBroker> logger) : IHostCapabilityBroker
{
    public IChatClient? ResolveModelByPurpose(string purposeSettingKey)
    {
        try
        {
            return resolver.TryResolve(purposeSettingKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "按用途键解析模型失败：{Key}", purposeSettingKey);
            return null;
        }
    }
}
