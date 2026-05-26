using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

namespace Netor.Cortana.Voice;

/// <summary>
/// 语音输入通道。监听 STT 语音识别最终结果事件，
/// 将识别文本转发到 AI 引擎统一处理。
/// </summary>
public sealed class VoiceInputChannel(
    ILogger<VoiceInputChannel> logger,
    IAiChatEngine chatEngine,
    IPublisher publisher,
    ISubscriber subscriber) : IAiInputChannel, IHostedService, IDisposable
{
    private CancellationTokenSource? _serviceCts;
    private bool _disposed;

    /// <inheritdoc />
    public string Name => "Voice/STT";

    /// <summary>
    /// 启动语音输入通道：订阅 STT 最终结果事件。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, (_, args) =>
        {
            logger.LogInformation("语音识别结果：{Text}", args.Text);

            Task.Run(async () =>
            {
                try
                {
                    publisher.Publish(Events.OnSttPartial, new VoiceTextArgs("思考中..."));
                    await chatEngine.SendMessageAsync(args.Text, _serviceCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AI 语音对话处理失败");
                }
            });

            return Task.FromResult(false);
        });

        logger.LogInformation("语音输入通道已启动");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止语音输入通道。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _serviceCts?.Cancel();
        logger.LogInformation("语音输入通道已停止");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        subscriber.Dispose();
    }
}