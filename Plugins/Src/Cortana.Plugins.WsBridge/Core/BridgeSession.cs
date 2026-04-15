using Cortana.Plugins.WsBridge.Connectors;
using Cortana.Plugins.WsBridge.Models;

using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.WsBridge.Core;

/// <summary>
/// 单个中转会话，拥有双端连接器、适配器和串行队列。
/// 启动后自动运行双向消息路由循环，直到取消或连接断开。
/// </summary>
public sealed class BridgeSession : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly SessionQueue _queue = new();
    private CancellationTokenSource? _cts;
    private Task? _relayTask;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public BridgeConfig Config { get; }
    public IExternalAppAdapter Adapter { get; }
    public CortanaConnector Cortana { get; }
    public ExternalConnector External { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public bool IsRunning => _relayTask is not null && !_relayTask.IsCompleted;

    public BridgeSession(BridgeConfig config, IExternalAppAdapter adapter,
        CortanaConnector cortana, ExternalConnector external, ILogger logger)
    {
        Config = config;
        Adapter = adapter;
        Cortana = cortana;
        External = external;
        _logger = logger;
    }

    /// <summary>启动双向消息路由循环。</summary>
    public void StartRelay()
    {
        _cts = new CancellationTokenSource();
        _relayTask = Task.WhenAll(
            RunExternalToCortanaAsync(_cts.Token),
            RunCortanaToExternalAsync(_cts.Token));
    }

    /// <summary>向 Cortana 发送 stop 指令。</summary>
    public async Task StopCurrentReplyAsync(CancellationToken ct)
    {
        await Cortana.SendStopAsync(ct);
        _queue.SignalCompletion();
    }

    public BridgeSessionInfo ToInfo() => new()
    {
        SessionId = SessionId,
        AdapterId = Config.AdapterId,
        WsUrl = Config.WsUrl,
        State = IsRunning ? "running" : "stopped",
        CreatedAt = CreatedAt
    };

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_relayTask is not null)
            {
                try { await _relayTask; } catch (OperationCanceledException) { }
            }
            _cts.Dispose();
        }
        _queue.Dispose();
        await Cortana.DisposeAsync();
        await External.DisposeAsync();
    }

    /// <summary>外部应用 → Cortana 方向的消息路由。</summary>
    private async Task RunExternalToCortanaAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var raw = await External.ReceiveAsync(ct);
                if (raw is null) break;

                var envelope = Adapter.ConvertInbound(SessionId, raw);
                if (envelope is null || envelope.EventType != "send" || envelope.Payload is null) continue;

                await _queue.SendAndWaitAsync(
                    async innerCt => await Cortana.SendAsync(envelope.Payload, innerCt), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "会话 {SessionId} 外部→Cortana 路由异常", SessionId);
        }
    }

    /// <summary>Cortana → 外部应用方向的消息路由。</summary>
    private async Task RunCortanaToExternalAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await Cortana.ReceiveMessageAsync(ct);
                if (msg is null) break;

                // done / error 释放串行队列
                if (msg.Type is "done" or "error")
                    _queue.SignalCompletion();

                var envelope = new BridgeEnvelope
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    SessionId = SessionId,
                    Source = "cortana",
                    Target = "external",
                    EventType = msg.Type,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Payload = msg.Data
                };

                var outbound = Adapter.ConvertOutbound(envelope);
                if (outbound is not null)
                    await External.SendAsync(outbound, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "会话 {SessionId} Cortana→外部 路由异常", SessionId);
        }
    }
}
