using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Reminder;

/// <summary>
/// 后台调度器：按需定时（Next-Fire 模式），仅在最近提醒到期时唤醒，
/// 提醒列表变更时自动重新计算唤醒时间。
/// </summary>
public sealed class ReminderScheduler(
    ReminderStore store,
    CortanaWsClient wsClient,
    ILogger<ReminderScheduler> logger) : IHostedService, IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>用于中断当前 Task.Delay，触发重新计算唤醒时间。</summary>
    private CancellationTokenSource? _rescheduleCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ReminderScheduler 正在启动");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        store.Changed += OnStoreChanged;
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ReminderScheduler 正在停止");
        store.Changed -= OnStoreChanged;

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
        _rescheduleCts?.Dispose();
    }

    /// <summary>提醒列表变更时，中断当前等待以重新计算唤醒时间。</summary>
    private void OnStoreChanged()
    {
        try { _rescheduleCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        logger.LogInformation("提醒调度循环已启动（Next-Fire 模式）");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1. 计算距下一个提醒的等待时间
                var nextTrigger = store.GetNextTriggerTime();
                TimeSpan delay;
                if (nextTrigger is null)
                {
                    logger.LogDebug("当前无提醒，挂起等待新提醒加入");
                    delay = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    delay = nextTrigger.Value - DateTimeOffset.Now;
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    logger.LogDebug("下次唤醒：{NextTrigger:yyyy-MM-dd HH:mm:ss}（{Delay}后）",
                        nextTrigger.Value, delay);
                }

                // 2. 等待到期或被 Store 变更中断
                _rescheduleCts?.Dispose();
                _rescheduleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    await Task.Delay(delay, _rescheduleCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Store 变更中断，重新计算
                    logger.LogDebug("提醒列表变更，重新计算唤醒时间");
                    continue;
                }

                // 3. 处理所有到期提醒
                var now = DateTimeOffset.Now;
                var due = store.GetDueReminders(now);
                if (due.Count == 0) continue;

                // 合并所有到期提醒为一条消息（宿主是单任务模型，多次连接会抢占）
                string text;
                if (due.Count == 1)
                {
                    var item = due[0];
                    logger.LogInformation("触发提醒: {Title} ({Id})", item.Title, item.Id);
                    text = $"[定时提醒] {item.Title}：{item.Message}";
                }
                else
                {
                    logger.LogInformation("批量触发 {Count} 条提醒", due.Count);
                    var lines = due.Select((item, i) => $"{i + 1}. {item.Title}：{item.Message}");
                    text = $"[定时提醒] 你有 {due.Count} 条提醒到期：\n{string.Join("\n", lines)}";
                }

                // WS 发送限时 30 秒
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(TimeSpan.FromSeconds(30));

                bool sent;
                try
                {
                    sent = await wsClient.SendReminderAsync(text, sendCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("提醒通知发送超时(30s)");
                    sent = false;
                }

                // 无论通知是否送达，都推进所有到期提醒的调度
                foreach (var item in due)
                {
                    store.AdvanceOrRemove(item);
                }

                if (!sent)
                {
                    logger.LogWarning("提醒通知发送失败，{Count} 条提醒调度已推进到下次触发时间", due.Count);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "提醒调度出错，5 秒后重试");
                // 出错后短暂延迟，防止异常时紧密循环
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
    }
}
