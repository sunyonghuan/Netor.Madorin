using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;

/// <summary>
/// 执行会话 - 持续交互的 PowerShell 或 SSH 会话
/// </summary>
public sealed class ExecutionSession : IAsyncDisposable
{
    private readonly ILogger<SessionRegistry> _logger;
    private readonly Process? _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _authenticationMode;
    private bool _disposed;
    private volatile ExecutionSessionState _state;
    private string _statusMessage = string.Empty;
    private string? _lastError;
    private string? _currentCommand;
    private DateTime _stateChangedAtUtc = DateTime.UtcNow;
    private DateTime _lastPromptAtUtc = DateTime.MinValue;

    public string Id { get; }
    public string Type { get; }  // "local" 或 "remote"
    public string? Host { get; }
    public string? Username { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastActivityAt { get; set; }
    public bool IsActive => !_disposed && _process is { HasExited: false };
    public string AuthenticationMode => _authenticationMode;
    public ExecutionSessionState State => _state;
    public string StatusMessage => _statusMessage;
    public string? LastError => _lastError;
    public string? CurrentCommand => _currentCommand;

    public ExecutionSession(string type, string? host, string? username, string? password, string? privateKeyPath, ILogger<SessionRegistry> logger)
    {
        Id = Guid.NewGuid().ToString("N");
        Type = type;
        Host = host;
        Username = username;
        CreatedAt = DateTime.Now;
        LastActivityAt = DateTime.Now;
        _logger = logger;
        _authenticationMode = string.IsNullOrWhiteSpace(privateKeyPath) ? "password" : "key";
        SetState(type == "local" ? ExecutionSessionState.Ready : ExecutionSessionState.Starting,
            type == "local"
                ? "本地 PowerShell 会话已就绪。"
                : _authenticationMode == "key"
                    ? "正在验证 SSH 密钥认证。密钥失败时将直接报错，不会回退到密码认证。"
                    : "远程 SSH 会话已创建，正在等待用户在 SSH 窗口中输入密码。用户输入一次后不要重复要求输入。");

        if (type == "remote" && _authenticationMode == "password")
        {
            _state = ExecutionSessionState.AwaitingUserInput;
            _lastPromptAtUtc = DateTime.UtcNow;
        }

        if (type == "local")
        {
            _process = CreateLocalSession();
        }
        else if (type == "remote" && !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(username))
        {
            _process = CreateRemoteSession(host, username, password, privateKeyPath);
        }

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
    /// 创建本地 PowerShell 会话
    /// </summary>
    private Process CreateLocalSession()
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPathHelper.GetPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,  // 用户可见
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = new Process { StartInfo = psi };
        process.Start();
        _logger.LogInformation("本地 PowerShell 会话已启动: {SessionId}", Id);
        return process;
    }

    /// <summary>
    /// 创建远程 SSH 会话，优先使用密钥认证，无密钥时弹出窗口由用户输入密码
    /// </summary>
    private Process CreateRemoteSession(string host, string username, string? password, string? privateKeyPath)
    {
        var args = "-o StrictHostKeyChecking=no";

        if (!string.IsNullOrWhiteSpace(privateKeyPath))
        {
            // 密钥认证：显式禁用密码回退，但保留私钥口令(passphrase)交互提示。
            args += $" -i \"{privateKeyPath}\"";
            args += " -o PreferredAuthentications=publickey -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no";
            _logger.LogInformation("使用密钥认证: {KeyPath}", privateKeyPath);
        }
        else
        {
            args += " -o PreferredAuthentications=password,keyboard-interactive";
        }

        args += $" {username}@{host}";

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = new Process { StartInfo = psi };
        process.Start();

        _logger.LogInformation("远程 SSH 会话已启动: {SessionId} - {Host} (认证方式: {AuthType})",
            Id, host, string.IsNullOrWhiteSpace(privateKeyPath) ? "密码" : "密钥");
        return process;
    }

    /// <summary>
    /// 后台排空 stderr，防止缓冲区满导致进程死锁
    /// </summary>
    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                AnalyzeSessionLine(line, isError: true);
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
                    // 超时或取消
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
                else if (_state is not ExecutionSessionState.Failed and not ExecutionSessionState.AwaitingUserInput)
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
        if (_disposed || _process == null)
            return;

        try
        {
            await _writeLock.WaitAsync();
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("exit");
                    await _process.StandardInput.FlushAsync();

                    if (!_process.WaitForExit(5000))
                    {
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

        _disposed = true;
        SetState(ExecutionSessionState.Closed, "会话已关闭。");
        await CloseAsync();
        _process?.Dispose();
        _writeLock.Dispose();
    }

    public async Task WaitForStartupAsync(int timeoutMs, CancellationToken ct = default)
    {
        if (Type != "remote")
            return;

        var remaining = Math.Max(timeoutMs, 0);
        while (remaining > 0)
        {
            if (!IsActive)
            {
                if (_state != ExecutionSessionState.Failed)
                {
                    SetFailure(_lastError ?? "SSH 会话在认证完成前已关闭。");
                }
                return;
            }

            if (_state is ExecutionSessionState.AwaitingUserInput or ExecutionSessionState.Failed)
                return;

            const int intervalMs = 100;
            await Task.Delay(intervalMs, ct);
            remaining -= intervalMs;
        }

        if (IsActive && _state == ExecutionSessionState.Starting)
        {
            SetState(ExecutionSessionState.Ready,
                _authenticationMode == "key"
                    ? "SSH 密钥认证已通过初步校验，会话可用。"
                    : "SSH 会话已启动，可继续发送命令。");
        }
    }

    private bool CanAcceptCommand()
    {
        if (!IsActive)
            return false;

        if (_state == ExecutionSessionState.Failed)
            return false;

        if (_state == ExecutionSessionState.Busy)
            return false;

        if (_state == ExecutionSessionState.Ready)
            return true;

        if (_state == ExecutionSessionState.AwaitingUserInput)
        {
            return DateTime.UtcNow - _lastPromptAtUtc > TimeSpan.FromSeconds(8);
        }

        return DateTime.UtcNow - _stateChangedAtUtc > TimeSpan.FromSeconds(3);
    }

    private string GetBlockedCommandMessage()
    {
        return _state switch
        {
            ExecutionSessionState.Busy =>
                $"会话输出流仍被上一条命令占用。{_statusMessage}",
            ExecutionSessionState.AwaitingUserInput =>
                $"会话仍在等待用户在 SSH 窗口中完成认证输入。请不要重复要求输入；若用户刚输入过一次，请等待几秒后再重试。当前状态：{_statusMessage}",
            ExecutionSessionState.Failed =>
                _lastError ?? "SSH 会话认证失败。",
            _ =>
                $"会话仍在初始化中。当前状态：{_statusMessage}"
        };
    }

    private void AnalyzeSessionLine(string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var normalized = line.Trim();
        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("enter passphrase for key") || lower.Contains("passphrase for key"))
        {
            _lastPromptAtUtc = DateTime.UtcNow;
            SetState(ExecutionSessionState.AwaitingUserInput,
                "正在等待用户在 SSH 窗口中输入私钥口令。输入一次后不要重复要求。",
                updatePromptTime: false);
            return;
        }

        if (lower.Contains("password:"))
        {
            _lastPromptAtUtc = DateTime.UtcNow;
            SetState(ExecutionSessionState.AwaitingUserInput,
                "正在等待用户在 SSH 窗口中输入 SSH 密码。输入一次后不要重复要求。",
                updatePromptTime: false);
            return;
        }

        if (lower.Contains("permission denied"))
        {
            SetFailure(_authenticationMode == "key"
                ? "SSH 密钥认证失败，连接已终止，不会回退到密码认证。"
                : "SSH 密码认证失败，连接已终止。");
            return;
        }

        if (lower.Contains("identity file") && lower.Contains("not accessible"))
        {
            SetFailure($"SSH 私钥文件不可访问：{normalized}");
            return;
        }

        if (lower.Contains("bad permissions") || lower.Contains("could not resolve hostname") || lower.Contains("connection refused") || lower.Contains("connection timed out") || lower.Contains("connection closed"))
        {
            SetFailure($"SSH 连接失败：{normalized}");
            return;
        }

        if (!isError && _state != ExecutionSessionState.Ready)
        {
            SetState(ExecutionSessionState.Ready, "SSH 会话已就绪，可继续发送命令。");
        }
    }

    private void SetFailure(string message)
    {
        _lastError = message;
        SetState(ExecutionSessionState.Failed, message);
    }

    private void SetState(ExecutionSessionState state, string message, bool updatePromptTime = true)
    {
        _state = state;
        _statusMessage = message;
        _stateChangedAtUtc = DateTime.UtcNow;
        if (updatePromptTime && state == ExecutionSessionState.AwaitingUserInput)
        {
            _lastPromptAtUtc = _stateChangedAtUtc;
        }
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
    Starting,
    AwaitingUserInput,
    Busy,
    Ready,
    Failed,
    Closed,
}