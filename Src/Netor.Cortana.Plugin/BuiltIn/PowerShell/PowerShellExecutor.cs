using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;
using ProcessDiag = System.Diagnostics.Process;

namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;

/// <summary>
/// PowerShell 执行引擎：负责创建 PowerShell 进程、捕获输出、并将结果返回给 AI。
/// 默认后台执行（无窗口），可选前台执行（用户可见窗口）。
/// </summary>
public sealed class PowerShellExecutor : IAsyncDisposable
{
    private readonly ILogger<PowerShellExecutor> _logger;
    private ProcessDiag? _process;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly string _psPath;

    /// <summary>
    /// 当输出行接收时触发，用于实时推送给前端显示。
    /// </summary>
    public event Action<string>? OnOutputLineReceived;

    /// <summary>
    /// 当发生错误时触发。
    /// </summary>
    public event Action<string>? OnErrorReceived;

    public PowerShellExecutor(ILogger<PowerShellExecutor> logger)
    {
        _logger = logger;
        _psPath = PowerShellPathHelper.GetPath();
    }

    /// <summary>
    /// 执行 PowerShell 脚本或命令，返回完整输出。
    /// </summary>
    /// <param name="script">PowerShell 脚本代码</param>
    /// <param name="timeout">执行超时时间（毫秒），0 表示使用默认 60 秒保护超时</param>
    /// <param name="background">true=后台执行（无窗口，默认）；false=前台执行（用户可见窗口）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>完整的输出结果（包括错误流）</returns>
    public async Task<PowerShellExecutionResult> ExecuteAsync(
        string script,
        int timeout = 0,
        bool background = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        // 确保只有一个 PowerShell 进程在执行
        // 不使用外部 ct 获取锁 —— 锁获取不应因 AI 框架取消而失败
        await _executionLock.WaitAsync(CancellationToken.None);
        try
        {
            // 清理上一个进程
            if (_process?.HasExited == false)
            {
                _logger.LogWarning("前一个 PowerShell 进程仍在运行，正在终止...");
                _process.Kill(entireProcessTree: true);
                await Task.Delay(500); // 等待进程终止
            }

            _process?.Dispose();

            // 强制保护：如果 timeout <= 0，使用默认 60 秒防止进程永久挂起
            var effectiveTimeout = timeout > 0 ? timeout : 60_000;
            var result = await RunPowerShellProcessAsync(script, effectiveTimeout, background, ct);
            return result;
        }
        finally
        {
            _executionLock.Release();
        }
    }

    /// <summary>
    /// 核心方法：启动 PowerShell 进程并捕获输出。
    /// </summary>
    private async Task<PowerShellExecutionResult> RunPowerShellProcessAsync(
        string script,
        int timeout,
        bool background,
        CancellationToken ct)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        try
        {
            _process = new ProcessDiag
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _psPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    // background=true 时无窗口后台执行，false 时用户可见
                    CreateNoWindow = background,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };

            _logger.LogInformation("启动 PowerShell 进程: {PsPath}", _psPath);

            _process.Start();

            // 仅使用 timeout 控制进程生命周期，不链接外部 CancellationToken
            // 外部 token 来自 AI 框架，可能因流式输出结束等非预期原因被取消
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(timeout);

            var exitTask = _process.WaitForExitAsync(cts.Token);

            // 异步读取标准输出和错误
            var outputTask = ReadStreamAsync(_process.StandardOutput, outputBuilder, isError: false);
            var errorTask = ReadStreamAsync(_process.StandardError, errorBuilder, isError: true);

            // 写入脚本到标准输入
            await _process.StandardInput.WriteLineAsync(script);
            _process.StandardInput.Close();

            // 等待进程结束
            await exitTask;

            // 等待所有输出被读取
            await Task.WhenAll(outputTask, errorTask);

            var exitCode = _process.ExitCode;
            _logger.LogInformation("PowerShell 进程已退出，代码: {ExitCode}", exitCode);

            return new PowerShellExecutionResult
            {
                Success = exitCode == 0,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                ExitCode = exitCode,
                ExecutedAt = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PowerShell 执行超时 ({Timeout}ms)", timeout);

            // 超时：终止进程并尝试收集已有输出
            if (_process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }

            return new PowerShellExecutionResult
            {
                Success = false,
                Output = outputBuilder.ToString(),
                Error = $"执行超时（{timeout}ms）\n{errorBuilder}",
                ExitCode = -1,
                ExecutedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell 执行异常");
            throw;
        }
    }

    /// <summary>
    /// 异步读取输出流，逐行触发事件。
    /// </summary>
    private async Task ReadStreamAsync(
        StreamReader reader,
        StringBuilder builder,
        bool isError)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                builder.AppendLine(line);

                // 实时推送输出
                if (isError)
                {
                    OnErrorReceived?.Invoke(line);
                    _logger.LogError("PS错误: {Line}", line);
                }
                else
                {
                    OnOutputLineReceived?.Invoke(line);
                    _logger.LogInformation("PS输出: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取{StreamType}失败", isError ? "错误流" : "输出流");
        }
    }

    /// <summary>
    /// 优雅关闭 PowerShell 进程。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _executionLock.WaitAsync();
        try
        {
            if (_process?.HasExited == false)
            {
                _logger.LogInformation("关闭 PowerShell 进程");
                _process.Kill(entireProcessTree: true);
            }

            _process?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭 PowerShell 进程时出错");
        }
        finally
        {
            _executionLock.Release();
        }

        _executionLock.Dispose();
    }
}

/// <summary>
/// PowerShell 执行结果。
/// </summary>
public class PowerShellExecutionResult
{
    /// <summary>
    /// 是否执行成功（退出代码为 0）。
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 标准输出内容。
    /// </summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>
    /// 标准错误内容。
    /// </summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// 进程退出代码。
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// 执行时间（UTC）。
    /// </summary>
    public DateTime ExecutedAt { get; init; }

    /// <summary>
    /// 获取完整输出（包含标准输出和错误）。
    /// </summary>
    public string FullOutput =>
        string.IsNullOrEmpty(Error)
            ? Output
            : $"{Output}\n--- 错误 ---\n{Error}";
}
