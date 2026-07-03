using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;
public sealed class PowerShellOutputBridge
{
    private readonly ILogger<PowerShellOutputBridge> _logger;

    public PowerShellOutputBridge(ILogger<PowerShellOutputBridge> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 关联 PowerShellExecutor，接收实时输出并推送给前端。
    /// </summary>
    public void LinkExecutor(PowerShellExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        executor.OnOutputLineReceived += line => BroadcastToFrontend("ps_output", line);
        executor.OnErrorReceived += line => BroadcastToFrontend("ps_error", line);

        _logger.LogInformation("PowerShell 执行器已链接到输出桥接器");
    }

    /// <summary>
    /// 广播消息给所有前端客户端。
    /// </summary>
    private void BroadcastToFrontend(string type, string message)
    {
        try
        {
            _logger.LogDebug("[PS {Type}] {Message}", type, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "推送 PowerShell 输出到前端失败");
        }
    }
}
