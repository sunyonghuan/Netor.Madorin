using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

using System.Text;

namespace Netor.Cortana.Voice;

/// <summary>
/// TTS 语音输出通道。将 AI 流式回复断句后通过事件驱动 TTS 合成播放。
/// 当 MainWindow 可见时不走 TTS（前端已通过 WebSocket 通道接收）。
/// </summary>
public sealed class VoiceChatOutputChannel(
    ILogger<VoiceChatOutputChannel> logger,
    IPublisher publisher,
    IWindowController windowController,
    ITtsPluginAdapter ttsPluginAdapter) : IAiOutputChannel
{
    private readonly StringBuilder _sentenceBuffer = new();
    private static readonly HashSet<char> SentenceBreaks = ['。', '！', '？', '；', '\n', '!', '?', ';'];
    private string? _activeTurnId;

    /// <inheritdoc />
    public string Name => "Voice/TTS";

    /// <inheritdoc />
    public bool IsActive => !windowController.IsMainWindowVisible() && !ttsPluginAdapter.IsAvailable;

    /// <inheritdoc />
    public Task OnTokenAsync(string turnId, string token, string sessionId, CancellationToken cancellationToken = default)
    {
        _activeTurnId = turnId;
        foreach (var ch in token)
        {
            _sentenceBuffer.Append(ch);

            if (SentenceBreaks.Contains(ch) && _sentenceBuffer.Length > 1)
            {
                FlushSentence();
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnDoneAsync(string turnId, string sessionId, CancellationToken cancellationToken = default)
    {
        _activeTurnId = turnId;
        // 发送剩余缓冲区内容
        FlushSentence();

        // 通知 TTS 没有更多文本了
        publisher.Publish(Events.OnTtsFinish, new VoiceSignalArgs());

        logger.LogDebug("语音输出通道完成，Turn：{TurnId}，Session：{SessionId}", turnId, sessionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnCancelledAsync(string turnId)
    {
        if (!string.IsNullOrWhiteSpace(_activeTurnId) && !string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        // 清空缓冲区，停止 TTS 播放
        _sentenceBuffer.Clear();
        publisher.Publish(Events.OnTtsFinish, new VoiceSignalArgs());

        logger.LogDebug("语音输出通道已取消，Turn：{TurnId}，TTS 已停止", turnId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnErrorAsync(string turnId, string message, CancellationToken cancellationToken = default)
    {
        _activeTurnId = turnId;
        // 清空缓冲区，停止 TTS
        _sentenceBuffer.Clear();
        publisher.Publish(Events.OnTtsFinish, new VoiceSignalArgs());

        logger.LogWarning("语音输出通道收到错误，Turn：{TurnId}，Message：{Message}", turnId, message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 将缓冲区中的文本作为一句话发送到 TTS 队列。
    /// </summary>
    private void FlushSentence()
    {
        var sentence = _sentenceBuffer.ToString().Trim();
        _sentenceBuffer.Clear();

        if (!string.IsNullOrWhiteSpace(sentence))
        {
            publisher.Publish(Events.OnTtsEnqueue, new TtsEnqueueArgs(sentence));
        }
    }
}
