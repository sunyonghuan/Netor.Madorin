using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 宿主能力代理：按用途键解析模型并返回可复用的 IChatClient。
/// 插件或宿主内部组件只表达“用途”，不直接绑定具体 Provider/Model。
/// </summary>
public interface IHostCapabilityBroker
{
    /// <summary>
    /// 根据用途键解析对应的聊天客户端；配置缺失或无效时返回 null，调用方应做降级。
    /// </summary>
    /// <param name="purposeSettingKey">例如 <see cref="ModelPurposeKeys.CompactionModelId"/>、<see cref="ModelPurposeKeys.MemoryProcessingModelId"/>。</param>
    IChatClient? ResolveModelByPurpose(string purposeSettingKey);
}
