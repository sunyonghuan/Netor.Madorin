using System.Text.Json;

using Cortana.Plugins.WsBridge.Connectors;
using Cortana.Plugins.WsBridge.Core;
using Cortana.Plugins.WsBridge.Models;
using Cortana.Plugins.WsBridge.Services;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.WsBridge.Tools;

/// <summary>
/// 对 AI 暴露的中转桥接工具集。
/// </summary>
[Tool]
public sealed class BridgeTools(
    BridgeSessionManager sessionManager,
    AdapterRegistry adapterRegistry,
    PluginSettings settings,
    ILogger<BridgeTools> logger)
{
    [Tool(Name = "ws_bridge_connect",
        Description = "建立中转连接。连接 Cortana 与外部应用 WebSocket，返回 session_id。")]
    public async Task<string> ConnectAsync(
        [Parameter(Description = "适配器标识，当前支持 generic")] string adapterId,
        [Parameter(Description = "外部应用 WebSocket 地址，ws:// 或 wss://")] string wsUrl,
        [Parameter(Description = "鉴权令牌（可选），空字符串表示不使用")] string authToken)
    {
        var adapter = adapterRegistry.Get(adapterId);
        if (adapter is null)
            return ToolResult.Fail("ADAPTER_NOT_FOUND",
                $"适配器 [{adapterId}] 不存在，可用: {string.Join(", ", adapterRegistry.GetAdapterIds())}");

        var config = new BridgeConfig
        {
            AdapterId = adapterId,
            WsUrl = wsUrl,
            AuthToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken
        };

        if (!adapter.ValidateConfig(config, out var error))
            return ToolResult.Fail("INVALID_CONFIG", error ?? "配置校验失败。");

        var cortana = new CortanaConnector(settings.WsPort, logger);
        if (!await cortana.ConnectAsync(CancellationToken.None))
        {
            await cortana.DisposeAsync();
            return ToolResult.Fail("CORTANA_CONNECT_FAILED", "无法连接 Cortana WebSocket。");
        }

        var external = new ExternalConnector(wsUrl, logger, config.AuthToken);
        if (!await external.ConnectAsync(CancellationToken.None))
        {
            await cortana.DisposeAsync();
            await external.DisposeAsync();
            return ToolResult.Fail("EXTERNAL_CONNECT_FAILED", $"无法连接外部应用: {wsUrl}");
        }

        var session = new BridgeSession(config, adapter, cortana, external, logger);
        sessionManager.TryAdd(session);
        session.StartRelay();

        logger.LogInformation("会话 {SessionId} 已建立: {AdapterId} -> {WsUrl}", session.SessionId, adapterId, wsUrl);
        return ToolResult.Ok($"中转连接已建立。",
            JsonSerializer.Serialize(session.ToInfo(), PluginJsonContext.Default.BridgeSessionInfo));
    }

    [Tool(Name = "ws_bridge_send",
        Description = "通过中转桥向外部应用发送消息。")]
    public async Task<string> SendAsync(
        [Parameter(Description = "会话 ID")] string sessionId,
        [Parameter(Description = "要发送的消息内容")] string message)
    {
        var session = sessionManager.Get(sessionId);
        if (session is null)
            return ToolResult.Fail("SESSION_NOT_FOUND", $"会话 [{sessionId}] 不存在。");
        if (!session.External.IsConnected)
            return ToolResult.Fail("NOT_CONNECTED", "外部应用连接已断开。");

        var envelope = new BridgeEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Source = "cortana",
            Target = "external",
            EventType = "message",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = message
        };

        var outbound = session.Adapter.ConvertOutbound(envelope);
        if (outbound is not null)
            await session.External.SendAsync(outbound, CancellationToken.None);

        return ToolResult.Ok("消息已发送。");
    }

    [Tool(Name = "ws_bridge_stop",
        Description = "中止当前正在进行的 AI 回复（向 Cortana 发送 stop）。")]
    public async Task<string> StopAsync(
        [Parameter(Description = "会话 ID")] string sessionId)
    {
        var session = sessionManager.Get(sessionId);
        if (session is null)
            return ToolResult.Fail("SESSION_NOT_FOUND", $"会话 [{sessionId}] 不存在。");

        await session.StopCurrentReplyAsync(CancellationToken.None);
        return ToolResult.Ok("已发送 stop 指令。");
    }

    [Tool(Name = "ws_bridge_status",
        Description = "查询中转桥状态。传入 session_id 查单个会话，空字符串查所有。")]
    public string Status(
        [Parameter(Description = "会话 ID，空字符串表示查看所有会话")] string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var session = sessionManager.Get(sessionId);
            if (session is null)
                return ToolResult.Fail("SESSION_NOT_FOUND", $"会话 [{sessionId}] 不存在。");
            return ToolResult.Ok("查询成功。",
                JsonSerializer.Serialize(session.ToInfo(), PluginJsonContext.Default.BridgeSessionInfo));
        }

        var all = sessionManager.GetAllInfo();
        if (all.Count == 0)
            return ToolResult.Ok("当前没有活跃的中转会话。");
        return ToolResult.Ok($"共 {all.Count} 个活跃会话。",
            JsonSerializer.Serialize(all, PluginJsonContext.Default.ListBridgeSessionInfo));
    }

    [Tool(Name = "ws_bridge_disconnect",
        Description = "关闭中转连接并清理会话资源。")]
    public async Task<string> DisconnectAsync(
        [Parameter(Description = "会话 ID")] string sessionId)
    {
        if (!await sessionManager.RemoveAsync(sessionId))
            return ToolResult.Fail("SESSION_NOT_FOUND", $"会话 [{sessionId}] 不存在。");

        logger.LogInformation("会话 {SessionId} 已断开", sessionId);
        return ToolResult.Ok($"会话 [{sessionId}] 已断开并清理。");
    }
}
