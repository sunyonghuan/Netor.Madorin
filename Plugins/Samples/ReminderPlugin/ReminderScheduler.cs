using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReminderPlugin;

/// <summary>
/// 后台调度器：每 30 秒检查到期提醒并通过 WebSocket 通知 Cortana。
/// </summary>
public sealed class ReminderScheduler(
    ReminderStore store,
    CortanaWsClient wsClient,
    ILogger<ReminderScheduler> logger) : IHostedService, IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ReminderScheduler 正在启动");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ReminderScheduler 正在停止");

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("ReminderScheduler 已停止");
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        logger.LogInformation("提醒调度循环已启动，轮询间隔 30 秒");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var now = DateTimeOffset.Now;
                var due = store.GetDueReminders(now);
                foreach (var item in due)
                {
                    logger.LogInformation("触发提醒: {Title} ({Id})", item.Title, item.Id);
                    var text = $"[定时提醒] {item.Title}：{item.Message}";
                    var sent = await wsClient.SendReminderAsync(text, ct);
                    if (sent)
                    {
                        store.AdvanceOrRemove(item);
                    }
                    else
                    {
                        logger.LogWarning("提醒通知发送失败，将在下次轮询重试: {Title} ({Id})", item.Title, item.Id);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "提醒调度出错");
            }
        }
    }
}
