using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Serialization;
using Cortana.Plugins.Memory.Storage;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin.Native;

using System.Text.Json;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 连接宿主内部 PluginBus 的最小后台服务：
/// - 握手 connected → subscribe → subscribed
/// - 接收 event 帧并通过记忆存储门面写入观察记录
/// </summary>
public sealed class MemoryIngestService(
    PluginSettings settings,
    ILogger<MemoryIngestService> logger,
    IMemoryStore store,
    MemoryPluginBusConnection pluginBus,
    MemoryPluginBusDispatcher dispatcher) : IHostedService
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.EnsureInitialized();
        _loopTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        logger.LogInformation("MemoryIngestService 已启动，目标：{Endpoint}", ResolvePluginBusEndpoint());
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts.Cancel(); } catch { }
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
        logger.LogInformation("MemoryIngestService 已停止");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var reconnectDelay = InitialReconnectDelay;

        while (!ct.IsCancellationRequested)
        {
            var endpoint = ResolvePluginBusEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                logger.LogWarning("PluginBus 配置缺失（端口与 Endpoint 均为空），{DelaySeconds} 秒后重试。", reconnectDelay.TotalSeconds);
                if (!await DelayReconnectAsync(reconnectDelay, ct).ConfigureAwait(false)) return;
                reconnectDelay = NextReconnectDelay(reconnectDelay);
                continue;
            }

            var uri = new Uri(endpoint);
            try
            {
                logger.LogInformation("连接 PluginBus：{Uri}", uri);
                await pluginBus.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                reconnectDelay = InitialReconnectDelay;
                await RunConnectedSessionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PluginBus 连接会话异常：{Uri}", uri);
            }
            finally
            {
                await pluginBus.CloseAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) return;

            logger.LogWarning("PluginBus 连接已断开，{DelaySeconds} 秒后尝试重连。", reconnectDelay.TotalSeconds);
            if (!await DelayReconnectAsync(reconnectDelay, ct).ConfigureAwait(false)) return;
            reconnectDelay = NextReconnectDelay(reconnectDelay);
        }
    }

    private async Task RunConnectedSessionAsync(CancellationToken ct)
    {
        // 1) 期望先收到 connected
        if (!await ReadUntilTypeAsync("connected", ct).ConfigureAwait(false))
        {
            logger.LogWarning("未收到 connected 握手，本轮连接终止。");
            return;
        }

        // 2) 发送 subscribe
        //    阶段 4B：新增 workflow topic（决策 5-B / 决策 4-A）
        //    阶段 5B Phase 4：协议版本 1.0.0 → 1.2.0；新增 capabilities 字段（决策 5B-D 能力声明）
        //    详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §4B.5 / §5B.4 / Phase 4 §5.2
        var sub = new PluginBusSubscribeFrame
        {
            Type = "subscribe",
            Topics = ["conversation", "memory", "model", "workflow"],
            Protocol = MemoryContextSupplyProtocol.Protocol,
            Version = "1.2.0",
            Capabilities = ["conversation.v1", "memory.v1", "workflow.v1"]
        };
        var json = JsonSerializer.Serialize(sub, MemoryInternalJsonContext.Chinese.PluginBusSubscribeFrame);
        await pluginBus.SendTextAsync(json, ct).ConfigureAwait(false);

        // 3) 等 subscribed
        if (!await ReadUntilTypeAsync("subscribed", ct).ConfigureAwait(false))
        {
            logger.LogWarning("未收到 subscribed 确认，本轮连接终止。");
            return;
        }

        // 3.5) 触发历史回放（since=0）
        var replay = new PluginBusReplayFrame { Payload = new PluginBusReplayPayload { SinceTimestamp = 0, BatchSize = 500 } };
        await pluginBus.SendTextAsync(JsonSerializer.Serialize(replay, MemoryInternalJsonContext.Chinese.PluginBusReplayFrame), ct).ConfigureAwait(false);

        // 4) 主接收循环：batch 入库 + 实时事件最小入库
        try
        {
            while (!ct.IsCancellationRequested && pluginBus.IsOpen)
            {
                var text = await pluginBus.ReadTextMessageAsync(ct).ConfigureAwait(false);
                if (text is null) break;
                try
                {
                    if (!await dispatcher.DispatchAsync(text, ct).ConfigureAwait(false))
                    {
                        logger.LogDebug("忽略非 event/response 帧：{Text}", text);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "解析 PluginBus 帧失败");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PluginBus 接收循环异常中止");
        }
        finally
        {
            await pluginBus.CloseAsync().ConfigureAwait(false);
        }
    }

    private string ResolvePluginBusEndpoint()
    {
        if (settings.Extensions.TryGetValue("pluginBusEndpoint", out var endpoint)
            && !string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        if (settings.Extensions.TryGetValue("pluginBusPort", out var portValue)
            && int.TryParse(portValue, out var port)
            && port > 0)
        {
            return $"ws://localhost:{port}/internal";
        }

        return string.Empty;
    }

    private async Task<bool> ReadUntilTypeAsync(string targetType, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            while (true)
            {
                var text = await pluginBus.ReadTextMessageAsync(timeoutCts.Token).ConfigureAwait(false);
                if (text is null) return false;
                var ok = TryGetType(text, out var type) && string.Equals(type, targetType, StringComparison.Ordinal);
                if (ok) return true;
                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    logger.LogWarning("PluginBus 握手阶段收到错误帧：{Text}", text);
                    return false;
                }

                if (!await dispatcher.DispatchAsync(text, timeoutCts.Token).ConfigureAwait(false))
                {
                    logger.LogDebug("PluginBus 握手阶段忽略未识别帧：{Text}", text);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 超时
        }
    }

    private static async Task<bool> DelayReconnectAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static TimeSpan NextReconnectDelay(TimeSpan current)
    {
        var nextSeconds = Math.Min(current.TotalSeconds * 2, MaxReconnectDelay.TotalSeconds);
        return TimeSpan.FromSeconds(nextSeconds);
    }

    private static bool TryGetType(string json, out string? type)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            return true;
        }
        catch
        {
            type = null; return false;
        }
    }

}