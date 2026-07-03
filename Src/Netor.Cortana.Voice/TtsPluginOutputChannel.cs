using System.Text;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Voice;

/// <summary>
/// 基于 TTS 插件的 AI 输出通道。
/// </summary>
public sealed class TtsPluginOutputChannel(
    ILogger<TtsPluginOutputChannel> logger,
    ITtsPluginAdapter ttsPluginAdapter,
    IWindowController windowController) : IAiOutputChannel
{
    private static readonly HashSet<char> SentenceBreaks = ['。', '！', '？', '；', '\n', '!', '?', ';'];
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly StringBuilder _sentenceBuffer = new();
    private string? _sessionId;
    private string? _activeTurnId;

    public string Name => "Voice/TTS Plugin";

    public bool IsActive => !windowController.IsMainWindowVisible() && ttsPluginAdapter.IsAvailable;

    public async Task OnTokenAsync(string turnId, string token, string sessionId, CancellationToken cancellationToken = default)
    {
        _activeTurnId = turnId;
        _sessionId = sessionId;

        foreach (var ch in token)
        {
            _sentenceBuffer.Append(ch);

            if (SentenceBreaks.Contains(ch) && _sentenceBuffer.Length > 1)
            {
                await FlushSentenceAsync(cancellationToken);
            }
        }
    }

    public async Task OnDoneAsync(string turnId, string sessionId, CancellationToken cancellationToken = default)
    {
        _activeTurnId = turnId;
        _sessionId = sessionId;
        await FlushSentenceAsync(cancellationToken);
        await ttsPluginAdapter.FinishAsync(sessionId, cancellationToken);

        logger.LogDebug("TTS 插件输出通道完成，Turn：{TurnId}，Session：{SessionId}", turnId, sessionId);
    }

    public async Task OnCancelledAsync(string turnId)
    {
        if (!string.IsNullOrWhiteSpace(_activeTurnId) && !string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
        {
            return;
        }

        _sentenceBuffer.Clear();
        using var cts = new CancellationTokenSource(StopTimeout);
        try
        {
            await ttsPluginAdapter.StopAsync(_sessionId, cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("TTS 插件停止超时，已跳过等待");
        }

        logger.LogDebug("TTS 插件输出通道已取消，Turn：{TurnId}", turnId);
    }

    public async Task OnErrorAsync(string turnId, string message, CancellationToken cancellationToken = default)
    {
        _activeTurnId = turnId;
        _sentenceBuffer.Clear();
        await ttsPluginAdapter.StopAsync(_sessionId, cancellationToken);

        logger.LogWarning("TTS 插件输出通道收到错误，Turn：{TurnId}，Message：{Message}", turnId, message);
    }

    private async Task FlushSentenceAsync(CancellationToken cancellationToken)
    {
        var sentence = _sentenceBuffer.ToString().Trim();
        _sentenceBuffer.Clear();

        if (!string.IsNullOrWhiteSpace(sentence))
        {
            await ttsPluginAdapter.EnqueueAsync(sentence, _sessionId, cancellationToken);
        }
    }
}
