using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// 事件中转站：订阅 EventHub 事件，通过 WebSocket 广播到所有前端客户端。
/// 取代原来 AI 层直接调用 WebSocketServer.BroadcastAsync 的耦合方式。
/// </summary>
public sealed class WebSocketEventRelayService(
    WebSocketServerService server,
    ISubscriber subscriber) : IHostedService
{
    /// <summary>
    /// 启动事件中转，订阅语音/AI 事件并通过 WebSocket 广播。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeEvents();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止事件中转。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 订阅 EventHub 事件，通过 WebSocket 广播到所有前端客户端。
    /// </summary>
    private void SubscribeEvents()
    {
        // STT
        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttPartial, async (_, args) =>
        {
            await server.BroadcastAsync("stt_partial", args.Text);
            return false;
        });

        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, async (_, args) =>
        {
            await server.BroadcastAsync("stt_final", args.Text);
            return false;
        });

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnSttStopped, async (_, _) =>
        {
            await server.BroadcastAsync("stt_stopped", "");
            return false;
        });

        // TTS
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsStarted, async (_, _) =>
        {
            await server.BroadcastAsync("tts_started", "");
            return false;
        });

        subscriber.Subscribe<VoiceTextArgs>(Events.OnTtsSubtitle, async (_, args) =>
        {
            await server.BroadcastAsync("tts_subtitle", args.Text);
            return false;
        });

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsCompleted, async (_, _) =>
        {
            await server.BroadcastAsync("tts_completed", "");
            return false;
        });

        // Chat
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnChatCompleted, async (_, _) =>
        {
            await server.BroadcastAsync("chat_completed", "");
            return false;
        });

        // WakeWord
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnWakeWordDetected, async (_, _) =>
        {
            await server.BroadcastAsync("wakeword_detected", "");
            return false;
        });
    }
}