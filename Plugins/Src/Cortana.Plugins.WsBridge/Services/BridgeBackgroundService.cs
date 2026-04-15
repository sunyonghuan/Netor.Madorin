using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.WsBridge.Services;

/// <summary>
/// 后台托管服务，负责会话生命周期监控与停机清理。
/// </summary>
public sealed class BridgeBackgroundService(
    BridgeSessionManager sessionManager,
    ILogger<BridgeBackgroundService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("WsBridge 后台服务已启动");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("WsBridge 后台服务正在停止，清理所有会话...");
        await sessionManager.DisposeAllAsync();
        logger.LogInformation("WsBridge 所有会话已清理");
    }
}
