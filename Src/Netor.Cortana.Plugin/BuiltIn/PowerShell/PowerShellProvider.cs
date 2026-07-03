using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using System.Diagnostics;

namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;

/// <summary>
/// 为 AI 智能体提供 PowerShell 执行能力的工具提供者。
/// 通过 AIContextProvider 机制暴露快速执行和会话管理工具给 AI 使用。
/// </summary>
public sealed class PowerShellProvider : AIContextProvider
{
    private readonly PowerShellExecutor _executor;
    private readonly ILogger<PowerShellProvider> _logger;
    private readonly IRealtimeProcessOutput? _realtimeOutput;
    private readonly SessionRegistry _sessionRegistry;
    private readonly Lazy<List<AITool>> _lazyTools;

    public PowerShellProvider(
        PowerShellExecutor executor,
        ILogger<PowerShellProvider> logger,
        SessionRegistry sessionRegistry,
        IRealtimeProcessOutput? realtimeOutput = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionRegistry);

        _executor = executor;
        _logger = logger;
        _realtimeOutput = realtimeOutput;
        _sessionRegistry = sessionRegistry;
        _lazyTools = new Lazy<List<AITool>>(RegisterTools);
    }

    /// <summary>
    /// 重写 ProvideAIContextAsync 以向 AI 注册工具。
    /// 这样 AI 就能在需要时调用 PowerShell 执行功能。
    /// </summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AIContext>(new AIContext { Tools = _lazyTools.Value, Instructions = """
    ### PowerShell Execution Tools — Usage Guidelines

    #### Execution Mode Selection
    - **Background by default**: All scripts and sessions run in background (background=true), invisible to user, no window popup.
    - **Foreground only when**: user explicitly asks to see the window, or script requires interactive input.
    - In all other cases, you **MUST** use background execution.

    #### ⚠️ Timeout & Process Cleanup (CRITICAL)
    - You **MUST** set a reasonable timeout. NEVER use timeout=0.
    - Quick commands (query info, list files): timeout=15000 (15s)
    - Normal commands (install, build): timeout=60000 (60s)
    - Long-running commands (large file ops, downloads): timeout=120000~300000
    - When uncertain, **set longer rather than shorter**, but **NEVER omit it**.
    - Background processes that hang are invisible to user — timeout is the ONLY safeguard.
    - **MUST close sessions after task completion** by calling sys_close_session immediately.
    - **Do NOT leave sessions idle** — close when not needed; create new ones later.

    #### Quick Execution (sys_execute_powershell)
    - Runs a single script/command, process auto-terminates after completion.
    - Best for quick queries, data collection, one-off commands.
    - Background by default, 30s default timeout.

    #### Session Mode
    - sys_start_local_session — Start local session (background by default).
    - sys_get_session_status — Query a session's current state.
    - sys_send_command — Send command to session (30s default timeout).
    - sys_close_session — Close session (**MUST call after task completion**).

    #### Session Lifecycle Management
    - Every session MUST follow a clear "create → use → close" lifecycle.
    - Close immediately after sending all commands. Do NOT wait.
    - If a command times out or errors, still close the session to release resources.
    - Sessions idle for over 3 minutes are auto-cleaned, but do NOT rely on this.

    #### Slow Commands / Long Waits
    - Large scripts, module installs, builds, and heavy file operations may require waiting.
    - sys_send_command defaults to 30000ms; explicitly increase timeoutMs for long commands.
    - Only proceed when state is Ready. If Busy, previous command is still running — do NOT send another.

    #### Tool Boundary
    - These tools start a **local PowerShell host process**.
    - You MAY use standard PowerShell remoting inside scripts and commands, such as `Invoke-Command`, `New-PSSession`, `Copy-Item -ToSession`, and `Copy-Item -FromSession`.
    - Do NOT use this toolset for raw `ssh` / `ssh.exe` login, SSH shell workflows, or SSH-based server management; use the dedicated SSH plugin instead.
    - Prefer non-interactive PowerShell remoting patterns. Avoid interactive shell-taking commands that can occupy the output stream.

    #### ⚠️ Windows PowerShell Execution Policy (CRITICAL)

    **Problem**: Windows blocks unsigned scripts by default (ExecutionPolicy = Restricted).

    **Running script code**: Automatically uses `-ExecutionPolicy Bypass`.

    **Running .ps1 script files** ⭐:
    - Prepend in script or command:
      ```powershell
      Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
      & "C:\path\to\script.ps1"
      ```
    - Or use: `powershell.exe -ExecutionPolicy Bypass -File "C:\path\to\script.ps1"`

    **Recommended patterns**:
    
    Option A (recommended) — Session mode:
    ```
    1. sys_start_local_session (get sessionId)
    2. sys_send_command sessionId "Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force"
    3. sys_send_command sessionId "& 'C:\path\to\script.ps1'"
    4. sys_close_session sessionId  ← MUST close!
    ```

    Option B — Quick mode:
    ```
    sys_execute_powershell with script:
    Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
    & "C:\path\to\script.ps1"
    ```

    Option C — Absolute path (safest):
    ```powershell
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\path\to\script.ps1"
    ```

    #### Tool List
    1. sys_execute_powershell — Quick script execution (background by default, auto timeout)
    2. sys_start_local_session — Start local session (background by default)
    3. sys_get_session_status — Query session state
    4. sys_send_command — Send command (MUST set reasonable timeout)
    5. sys_close_session — Close session (**MUST call when task is done**)
    6. sys_list_sessions — List active sessions
    """ });
    }

    /// <summary>
    /// 注册工具供 AI 调用。
    /// </summary>
    private List<AITool> RegisterTools()
    {
        var tools = new List<AITool>();

        // Tool 1: Quick local script execution
        tools.Add(AIFunctionFactory.Create(
            name: "sys_execute_powershell",
            description: """
Execute a local PowerShell script. Process auto-terminates after completion.
Runs in background by default (no window popup). Returns full execution output.
Best for quick queries and one-off commands.
The host process is local, but the script may use standard PowerShell remoting commands to reach remote PowerShell endpoints.

Parameters:
- script: PowerShell script code to execute
- timeout: Timeout in milliseconds. MUST set a reasonable value, default 30s. NEVER set to 0
- background: Run in background (default true). Set false ONLY when user interaction is needed
""",
            method: ExecutePowerShellAsync));

        // Tool 2: Start local interactive session
        tools.Add(AIFunctionFactory.Create(
            name: "sys_start_local_session",
            description: """
Start a local PowerShell interactive session. Session stays alive for multiple commands.
Runs in background by default (no window popup). Suitable for complex multi-step operations.
The host session is local, but commands may use standard PowerShell remoting to reach remote PowerShell endpoints.

⚠️ You MUST call sys_close_session after completing your task. Do NOT leave sessions idle!

Parameters:
- background: Run in background (default true). Set false ONLY when user interaction is needed

Returns: Session ID for subsequent sys_send_command and sys_close_session calls.
""",
            method: StartLocalSessionAsync));

        // Tool 3: Get session status
        tools.Add(AIFunctionFactory.Create(
            name: "sys_get_session_status",
            description: "Query a single PowerShell host session's current state. Use before retrying a command or to confirm readiness after a long-running operation. Parameter: sessionId",
            method: GetSessionStatusAsync));

        // Tool 4: Send command to session
        tools.Add(AIFunctionFactory.Create(
            name: "sys_send_command",
            description: """
Send a command to an active session and return output in real-time.
Supports multi-step operations that depend on previous command results.

⚠️ Timeout guidelines:
- Quick commands: 15000ms
- Normal commands: 30000ms (default)
- Long-running commands: 60000~300000ms
- When uncertain, set longer. NEVER omit timeout.

Parameters:
- sessionId: Session ID
- command: Command to execute
- timeoutMs: Timeout in milliseconds, default 30000
""",
            method: SendCommandAsync));

        // Tool 5: Close session
        tools.Add(AIFunctionFactory.Create(
            name: "sys_close_session",
            description: "Close an active session and release resources. ⚠️ You MUST call this after task completion! Do NOT leave sessions idle. Parameter: sessionId",
            method: CloseSessionAsync));

        // Tool 6: List all active sessions
        tools.Add(AIFunctionFactory.Create(
            name: "sys_list_sessions",
            description: "List all active execution sessions.",
            method: ListSessionsAsync));

        return tools;
    }

    /// <summary>
    /// AI 工具实现：执行 PowerShell 命令。
    /// 此方法由 AI 智能体通过 execute_powershell 工具调用。
    /// </summary>
    /// <param name="script">要执行的 PowerShell 脚本代码</param>
    /// <param name="timeout">超时时间（毫秒），0 表示无限制，默认30秒</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>执行结果，包含输出、错误和退出代码</returns>
    private async Task<string> ExecutePowerShellAsync(
        string script,
        int timeout = 30000,
        bool background = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(script))
            return "错误：脚本不能为空";

        var processId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        void OnOutput(string line) => _ = PublishProcessEventAsync(processId, "running", line, null, stopwatch.ElapsedMilliseconds, ct);
        void OnError(string line) => _ = PublishProcessEventAsync(processId, "running", $"[stderr] {line}", null, stopwatch.ElapsedMilliseconds, ct);

        try
        {
            _logger.LogInformation("AI 正在执行 PowerShell 脚本，长度: {ScriptLength} 字符, 后台: {Background}", script.Length, background);

            await PublishProcessEventAsync(processId, "running", script, null, 0, ct);
            _executor.OnOutputLineReceived += OnOutput;
            _executor.OnErrorReceived += OnError;

            var result = await _executor.ExecuteAsync(script, timeout, background, ct);

            await PublishProcessEventAsync(
                processId,
                result.Success ? "success" : "failed",
                string.Empty,
                result.ExitCode,
                stopwatch.ElapsedMilliseconds,
                ct);

            _logger.LogInformation(
                "PowerShell 执行完成，成功: {Success}, 退出代码: {ExitCode}",
                result.Success,
                result.ExitCode);

            // 返回格式化的结果给 AI
            return FormatResultForAI(result);
        }
        catch (OperationCanceledException)
        {
            var errorMsg = "PowerShell 执行被取消";
            _logger.LogWarning(errorMsg);
            await PublishProcessEventAsync(processId, "cancelled", errorMsg, null, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            return $"错误：{errorMsg}";
        }
        catch (TimeoutException)
        {
            var errorMsg = $"PowerShell 执行超时（{timeout}ms）";
            _logger.LogWarning(errorMsg);
            await PublishProcessEventAsync(processId, "failed", errorMsg, -1, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            return $"错误：{errorMsg}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell 执行异常");
            await PublishProcessEventAsync(processId, "failed", ex.Message, null, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            return $"错误：{ex.Message}";
        }
        finally
        {
            _executor.OnOutputLineReceived -= OnOutput;
            _executor.OnErrorReceived -= OnError;
        }
    }

    private Task PublishProcessEventAsync(
        string processId,
        string status,
        string content,
        int? exitCode,
        long durationMs,
        CancellationToken ct)
    {
        if (_realtimeOutput is null)
        {
            return Task.CompletedTask;
        }

        return _realtimeOutput.OnProcessEventAsync(new RealtimeProcessEvent
        {
            TurnId = string.Empty,
            ProcessId = processId,
            Kind = "command",
            Title = "PowerShell",
            Status = status,
            Content = content,
            ExitCode = exitCode,
            DurationMs = durationMs,
            Timestamp = DateTimeOffset.UtcNow,
        }, ct);
    }

    /// <summary>
    /// 启动本地交互式 PowerShell 会话
    /// </summary>
    private Task<string> StartLocalSessionAsync(bool background = true, CancellationToken ct = default)
    {
        try
        {
            var session = _sessionRegistry.CreateSession(background);

            if (!session.IsActive)
                return Task.FromResult("✗ 启动失败：PowerShell 进程启动后立即退出");

            _logger.LogInformation("PowerShell 会话已启动: {SessionId}", session.Id);

            return Task.FromResult($@"✓ PowerShell 会话已启动
会话ID: {session.Id}
类型: 本机托管交互式
执行模式: {(background ? "后台" : "前台")}

使用说明：
1. 调用 send_command 发送命令并获取输出
2. 如需远程 PowerShell，请在命令中使用 Invoke-Command / New-PSSession 等标准 remoting
3. 任务完成后必须调用 close_session 关闭会话");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动本地会话失败");
            return Task.FromResult($"✗ 启动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 查询单个会话状态
    /// </summary>
    private Task<string> GetSessionStatusAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult("✗ 错误：sessionId 不能为空");

        var session = _sessionRegistry.GetSession(sessionId);
        if (session == null)
            return Task.FromResult($"✗ 错误：找不到会话 {sessionId}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"会话ID: {session.Id}");
        sb.AppendLine($"类型: {session.Type}");
        sb.AppendLine($"活跃: {(session.IsActive ? "是" : "否")}");
        sb.AppendLine($"状态: {session.State}");
        sb.AppendLine($"说明: {session.StatusMessage}");
        if (!string.IsNullOrWhiteSpace(session.LastError))
            sb.AppendLine($"错误: {session.LastError}");
        sb.AppendLine($"创建时间: {session.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"最后活动: {session.LastActivityAt:yyyy-MM-dd HH:mm:ss}");

        if (session.State == ExecutionSessionState.Ready)
        {
            sb.AppendLine();
            sb.AppendLine("提示:");
            sb.AppendLine("1. 会话已就绪，可以继续调用 sys_send_command");
            sb.AppendLine("2. 长时间命令请显式传更大的 timeoutMs");
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// 向会话发送命令
    /// </summary>
    private async Task<string> SendCommandAsync(
        string sessionId,
        string command,
        int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(command))
            return "✗ 错误：sessionId 和 command 不能为空";

        var session = _sessionRegistry.GetSession(sessionId);
        if (session == null)
            return $"✗ 错误：找不到会话 {sessionId}";

        if (!session.IsActive)
            return $"✗ 错误：会话 {sessionId} 已关闭";

        if (LooksLikeInteractiveSshCommand(command))
        {
            return "✗ 错误：不要在此 PowerShell 工具中直接发起 ssh/ssh.exe 登录或 SSH 服务器管理。SSH 工作流请使用专用 SSH 插件；如果目标是 PowerShell 远程端点，请改用 Invoke-Command、New-PSSession 等标准 PowerShell remoting 命令。";
        }

        if (session.State == ExecutionSessionState.Busy)
        {
            return $"⚠ 当前会话仍被上一条命令占用。{session.StatusMessage}";
        }

        try
        {
            _logger.LogInformation("向会话 {SessionId} 发送命令", sessionId);

            var sb = new System.Text.StringBuilder();
            await foreach (var line in session.ExecuteCommandAsync(command, timeoutMs, ct))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(line);
            }
            return sb.Length > 0 ? sb.ToString() : "(命令执行完成，无输出)";
        }
        catch (OperationCanceledException)
        {
            return "✗ 命令执行被取消";
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "会话 {SessionId} 当前不可执行命令", sessionId);
            return session.IsActive ? $"⚠ {ex.Message}" : $"✗ 会话已关闭：{ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "向会话 {SessionId} 发送命令失败", sessionId);
            return $"✗ 执行失败：{ex.Message}";
        }
    }

    private static bool LooksLikeInteractiveSshCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var trimmed = command.TrimStart();
        return trimmed.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ssh.exe ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 关闭会话
    /// </summary>
    private async Task<bool> CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return false;

            await _sessionRegistry.RemoveSessionAsync(sessionId);
            _logger.LogInformation("会话已关闭: {SessionId}", sessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭会话失败: {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// 列出所有活跃会话
    /// </summary>
    private Task<string> ListSessionsAsync(CancellationToken ct = default)
    {
        var sessions = _sessionRegistry.GetActiveSessions();

        if (sessions.Count == 0)
            return Task.FromResult("当前没有活跃的会话");

        var sb = new System.Text.StringBuilder("活跃的会话列表：\n");
        foreach (var (id, type, createdAt, state, statusMessage) in sessions)
        {
            sb.AppendLine($"- ID: {id}");
            sb.AppendLine($"  类型: {type}");
            sb.AppendLine($"  状态: {state}");
            sb.AppendLine($"  说明: {statusMessage}");
            sb.AppendLine($"  创建时间: {createdAt:yyyy-MM-dd HH:mm:ss}");
        }

        return Task.FromResult(sb.ToString());
    }

    /// <summary>
    /// 为 AI 格式化 PowerShell 执行结果。
    /// </summary>
    private static string FormatResultForAI(PowerShellExecutionResult result)
    {
        if (result.Success)
        {
            return $@"✓ PowerShell 执行成功
代码: {result.ExitCode}

输出：
{result.Output}";
        }

        return $@"✗ PowerShell 执行失败
代码: {result.ExitCode}

输出：
{result.Output}

错误：
{result.Error}";
    }
}
