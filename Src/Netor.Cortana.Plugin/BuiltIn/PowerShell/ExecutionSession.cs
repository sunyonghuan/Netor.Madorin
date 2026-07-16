using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ProcessDiag = System.Diagnostics.Process;

namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;

/// <summary>
/// 执行会话 - 持续交互的本机托管 PowerShell 会话
/// </summary>
public sealed class ExecutionSession : IAsyncDisposable
{
    private readonly ILogger<SessionRegistry> _logger;
    private readonly ProcessDiag? _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly bool _background;
    private bool _disposed;
    private volatile ExecutionSessionState _state;
    private string _statusMessage = string.Empty;
    private string? _lastError;
    private string? _currentCommand;
    private DateTime _stateChangedAtUtc = DateTime.UtcNow;

    public string Id { get; }
    public string Type { get; } = "local";
    public DateTime CreatedAt { get; }
    public DateTime LastActivityAt { get; set; }
    public bool IsActive => !_disposed && _process is { HasExited: false };
    public ExecutionSessionState State => _state;
    public string StatusMessage => _statusMessage;
    public string? LastError => _lastError;
    public string? CurrentCommand => _currentCommand;

    public ExecutionSession(ILogger<SessionRegistry> logger, bool background = true)
    {
        Id = Guid.NewGuid().ToString("N");
        CreatedAt = DateTime.Now;
        LastActivityAt = DateTime.Now;
        _logger = logger;
        _background = background;
        SetState(ExecutionSessionState.Ready, "PowerShell 会话已就绪。");

        _process = CreateLocalSession();

        // 验证进程是否启动成功
        if (_process is null || _process.HasExited)
        {
            _logger.LogError("会话进程启动失败或已立即退出: {SessionId}", Id);
            SetFailure("会话进程启动失败或已立即退出。");
            _disposed = true;
        }
        else
        {
            // 启动后台 stderr 排空任务，防止 stderr 缓冲区满导致死锁
            _ = DrainStderrAsync(_process);
        }
    }

    /// <summary>
    /// 创建本机托管的 PowerShell 会话
    /// </summary>
    private ProcessDiag CreateLocalSession()
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPathHelper.GetPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = _background,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = new ProcessDiag { StartInfo = psi };
        process.Start();
        _logger.LogInformation("PowerShell 会话已启动: {SessionId} (background={Background})", Id, _background);
        return process;
    }

    /// <summary>
    /// 后台排空 stderr，防止缓冲区满导致进程死锁。
    /// 会话模式当前仅返回 stdout，stderr 只做日志记录。
    /// </summary>
    private async Task DrainStderrAsync(ProcessDiag process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                _logger.LogWarning("[Session {SessionId} stderr] {Line}", Id, line);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // 进程已关闭，正常退出
        }
    }

    /// <summary>
    /// 向会话发送命令并异步返回输出
    /// </summary>
    public async IAsyncEnumerable<string> ExecuteCommandAsync(
        string command,
        int timeoutMs = 30000,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsActive)
            throw new InvalidOperationException("会话已关闭");

        if (!CanAcceptCommand())
            throw new InvalidOperationException(GetBlockedCommandMessage());

        LastActivityAt = DateTime.Now;

        // 抢锁也响应外部取消：用户点停止时不应卡在等待前一条命令释放锁上。
        await _writeLock.WaitAsync(ct);
        var commandTimedOut = false;
        try
        {
            _currentCommand = command.Trim();

            if (_state != ExecutionSessionState.Failed)
            {
                SetState(ExecutionSessionState.Busy,
                    $"正在执行命令：{SummarizeCommand(_currentCommand)}");
            }

            // 发送命令
            await _process!.StandardInput.WriteLineAsync($"{command}; echo '___COMMAND_END___'");
            await _process.StandardInput.FlushAsync();

            // 命令执行时间同时受 timeoutMs 与外部取消令牌控制：
            // - timeout 到期：命令超时保护，输出流可能仍被占用，建议重建会话；
            // - 外部 ct 取消（用户点停止）：立即杀进程树，会话流状态已污染，标记失败。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            // 读取输出直到命令标记
            while (!cts.Token.IsCancellationRequested)
            {
                // 使用 Task.WhenAny + 延迟实现可取消的 ReadLineAsync
                var readTask = _process.StandardOutput.ReadLineAsync();
                var delayTask = Task.Delay(Timeout.Infinite, cts.Token);

                var completed = await Task.WhenAny(readTask, delayTask);

                if (completed == delayTask)
                {
                    // 外部 ct 取消（用户点停止）：立即杀进程树，会话无法继续使用。
                    if (ct.IsCancellationRequested)
                    {
                        if (_process is { HasExited: false })
                        {
                            try { _process.Kill(entireProcessTree: true); } catch { }
                        }
                        SetFailure("用户停止：进程树已终止，请关闭并重建会话。");
                        throw new OperationCanceledException(ct);
                    }

                    // timeout 到期：输出流可能仍被占用，保持 Busy 状态并告知调用方。
                    commandTimedOut = true;
                    SetState(ExecutionSessionState.Busy,
                        $"上一条命令执行超时，输出流可能仍被占用。当前命令：{SummarizeCommand(_currentCommand)}。建议关闭并重建会话。");
                    yield return $"[超时：命令执行超过 {timeoutMs}ms。当前会话输出流可能仍被占用，建议关闭并重建会话。]";
                    yield break;
                }

                var line = await readTask;

                if (line == null)
                    break;

                if (line.TrimEnd() == "___COMMAND_END___")
                    break;

                yield return line;
            }

            LastActivityAt = DateTime.Now;
        }
        finally
        {
            if (!commandTimedOut)
            {
                _currentCommand = null;

                if (!IsActive)
                {
                    SetFailure(_lastError ?? "会话在命令执行期间已关闭。");
                }
                else if (_state != ExecutionSessionState.Failed)
                {
                    SetState(ExecutionSessionState.Ready, "会话已就绪，可继续发送命令。");
                }
            }

            _writeLock.Release();
        }
    }

    /// <summary>
    /// 关闭会话
    /// </summary>
    public async Task CloseAsync()
    {
        if (_process == null)
            return;

        try
        {
            await _writeLock.WaitAsync();
            try
            {
                if (!_process.HasExited)
                {
                    // 尝试优雅退出
                    try
                    {
                        await _process.StandardInput.WriteLineAsync("exit");
                        await _process.StandardInput.FlushAsync();
                    }
                    catch { /* stdin 可能已关闭 */ }

                    if (!_process.WaitForExit(3000))
                    {
                        _logger.LogWarning("会话 {SessionId} 优雅退出超时，强制终止进程树", Id);
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            finally
            {
                _writeLock.Release();
            }

            _logger.LogInformation("会话已关闭: {SessionId}", Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭会话失败: {SessionId}", Id);
            // 确保进程被终止
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await CloseAsync();
        _disposed = true;
        SetState(ExecutionSessionState.Closed, "会话已关闭。");
        _process?.Dispose();
        _writeLock.Dispose();
    }

    private bool CanAcceptCommand()
    {
        if (!IsActive)
            return false;

        if (_state == ExecutionSessionState.Failed)
            return false;

        if (_state == ExecutionSessionState.Busy)
            return false;

        return _state == ExecutionSessionState.Ready;
    }

    private string GetBlockedCommandMessage()
    {
        return _state switch
        {
            ExecutionSessionState.Busy =>
                $"会话输出流仍被上一条命令占用。{_statusMessage}",
            ExecutionSessionState.Failed =>
                _lastError ?? "PowerShell 会话已失败。",
            _ =>
                $"会话仍在初始化中。当前状态：{_statusMessage}"
        };
    }

    private void SetFailure(string message)
    {
        _lastError = message;
        SetState(ExecutionSessionState.Failed, message);
    }

    private void SetState(ExecutionSessionState state, string message)
    {
        _state = state;
        _statusMessage = message;
        _stateChangedAtUtc = DateTime.UtcNow;
    }

    private static string SummarizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "(空命令)";

        var normalized = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 80 ? normalized : $"{normalized[..77]}...";
    }
}

public enum ExecutionSessionState
{
    Busy,
    Ready,
    Failed,
    Closed,
}
