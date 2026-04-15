using System.Text.Json;

using Cortana.Plugins.WsBridge.Core;
using Cortana.Plugins.WsBridge.Models;

namespace Cortana.Plugins.WsBridge.Adapters;

/// <summary>
/// 通用直通适配器。
/// 外部消息视为纯文本直接转发，Cortana 回包以标准 Envelope JSON 回发。
/// 适用于简单的 WebSocket 文本消息中转场景。
/// </summary>
public sealed class GenericAdapter : IExternalAppAdapter
{
    public string AdapterId => "generic";

    public BridgeEnvelope? ConvertInbound(string sessionId, string rawMessage)
    {
        return new BridgeEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Source = "external",
            Target = "cortana",
            EventType = "send",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = rawMessage
        };
    }

    public string? ConvertOutbound(BridgeEnvelope envelope)
    {
        return JsonSerializer.Serialize(envelope, PluginJsonContext.Default.BridgeEnvelope);
    }

    public bool ValidateConfig(BridgeConfig config, out string? error)
    {
        if (string.IsNullOrWhiteSpace(config.WsUrl))
        {
            error = "ws_url 不能为空。";
            return false;
        }
        if (!Uri.TryCreate(config.WsUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            error = "ws_url 必须是有效的 ws:// 或 wss:// 地址。";
            return false;
        }
        error = null;
        return true;
    }
}
