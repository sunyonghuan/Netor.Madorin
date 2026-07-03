using Microsoft.Extensions.Hosting;

namespace Netor.Cortana.AI.Delegation;

/// <summary>
/// 应用生命周期内维护专家后台委派任务状态。
/// </summary>
public sealed class DelegatedAgentJobRunner(DelegatedAgentJobExecutor executor) : IHostedService
{
    private static readonly TimeSpan CompletedJobRetention = TimeSpan.FromDays(30);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        executor.StopAll("应用启动时清理未完成的后台委派任务。");
        executor.CleanupCompletedOlderThan(CompletedJobRetention);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        executor.StopAll("应用正在停止，后台委派任务已终止。");
        return Task.CompletedTask;
    }
}
