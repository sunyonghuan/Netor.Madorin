using Cortana.Plugins.WsBridge.Models;

namespace Cortana.Plugins.WsBridge.Core;

/// <summary>
/// 外部应用适配器接口。
/// 每个外部应用实现此接口完成协议转换，核心层不感知应用业务字段。
/// </summary>
public interface IExternalAppAdapter
{
    /// <summary>应用标识。</summary>
    string AdapterId { get; }

    /// <summary>将外部应用原始消息转换为标准信封。</summary>
    BridgeEnvelope? ConvertInbound(string sessionId, string rawMessage);

    /// <summary>将标准信封转换为外部应用协议消息。</summary>
    string? ConvertOutbound(BridgeEnvelope envelope);

    /// <summary>校验连接配置是否合法。</summary>
    bool ValidateConfig(BridgeConfig config, out string? error);
}
