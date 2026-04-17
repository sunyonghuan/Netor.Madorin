using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;

/// <summary>
/// 为 AI 智能体提供 PowerShell 执行能力的工具提供者。
/// 通过 AIContextProvider 机制暴露快速执行和会话管理工具给 AI 使用。
///
/// 支持两种执行模式：
/// - 快速模式：run_powershell（本地）/ execute_remote_script（远程） - 执行完自动关闭
/// - 会话模式：start_session / send_command / close_session - 持续交互
/// </summary>
public sealed class PowerShellProvider : AIContextProvider
{
    private readonly PowerShellExecutor _executor;
    private readonly ILogger<PowerShellProvider> _logger;
    private readonly SessionRegistry _sessionRegistry;
    private readonly Lazy<List<AITool>> _lazyTools;

    public PowerShellProvider(
        PowerShellExecutor executor,
        ILogger<PowerShellProvider> logger,
        SessionRegistry sessionRegistry)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionRegistry);

        _executor = executor;
        _logger = logger;
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
    ### PowerShell 执行工具使用规范

    #### 快速执行（sys_execute_powershell）
    - 执行单条命令或脚本代码
    - 适合快速查询、数据采集

    #### 会话模式
    - sys_start_local_session 启动本地会话
    - sys_start_remote_session 启动远程会话（支持密钥或密码认证）
    - sys_get_session_status 查询单个会话当前状态
    - sys_send_command 发送命令
    - sys_close_session 关闭会话

    #### 长时间等待/慢命令处理
    - SSH 建连、认证、网络波动、远程脚本执行都可能需要等待
    - sys_send_command 默认超时为 30000ms，长命令必须显式增大 timeoutMs
    - 如果启动远程会话后返回“等待用户输入”或“尚未完成认证”，不要立即继续发命令
    - 这时应先等待用户完成一次输入，再调用 sys_get_session_status 或 sys_list_sessions 查询状态
    - 只有状态变为 Ready/已就绪后，才继续发送下一条命令
    - 如果状态为 Busy/输出流被占用，说明上一条命令还没真正结束，不要继续发下一条命令

    #### 严格限制
    - 不要在 sys_start_local_session 创建的本地持久会话里再执行 ssh/ssh.exe 进入交互式远程 shell
    - 这种嵌套交互会占住本地会话输出流，导致后续 sessionId 复用失败
    - 远程登录必须使用 sys_start_remote_session，而不是本地会话 + ssh

    #### 远程 SSH 认证方式
    - **密钥认证（推荐）**：提供 privateKeyPath 参数，如 `~/.ssh/id_rsa` 或 `C:\Users\xxx\.ssh\id_rsa`
    - **密码认证**：仅在不提供 privateKeyPath 时使用 password 参数
    - 两者必须提供其一

    #### ⚠️ Windows PowerShell 执行策略处理（关键）

    **问题**：Windows 默认禁止运行未签名脚本（ExecutionPolicy = Restricted）

    **执行脚本代码**：
    - 自动添加 `-ExecutionPolicy Bypass` 参数
    - 格式：`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "代码"`

    **执行脚本文件** ⭐：
    - 如果要运行 .ps1 文件，必须在脚本开头或发送命令时添加：
      ```powershell
      Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
      & "C:\path\to\script.ps1"
      ```
    - 或者直接用：`powershell.exe -ExecutionPolicy Bypass -File "C:\path\to\script.ps1"`

    **两种执行文件方式**：
    
    方案A（推荐）- 会话模式：
    ```
    1. sys_start_local_session 启动会话（获得sessionId）
    2. sys_send_command sessionId "Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force"
    3. sys_send_command sessionId "& 'C:\path\to\script.ps1'"
    ```

    方案B - 快速模式：
    ```
    sys_execute_powershell 的脚本内容改为：
    Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
    & "C:\path\to\script.ps1"
    ```

    方案C - 绝对路径（最安全）：
    ```powershell
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\path\to\script.ps1"
    ```

    #### 工具列表
    1. sys_execute_powershell - 快速执行脚本
    2. sys_start_local_session - 启动本地会话
    3. sys_start_remote_session - 启动远程会话（支持密钥/密码认证）
    4. sys_get_session_status - 查询单个会话状态
    5. sys_send_command - 发送命令
    6. sys_close_session - 关闭会话
    7. sys_list_sessions - 列出活跃会话
    """ });
    }

    /// <summary>
    /// 注册工具供 AI 调用。
    /// </summary>
    private List<AITool> RegisterTools()
    {
        var tools = new List<AITool>();

        // 工具1：快速本地脚本执行
        tools.Add(AIFunctionFactory.Create(
            name: "sys_execute_powershell",
            description: """
执行本地 PowerShell 脚本。脚本执行完成后自动关闭窗口。
用户可以看到 PowerShell 窗口运行过程，AI 获取完整的执行结果。
适合快速查询和一次性命令。
""",
            method: ExecutePowerShellAsync));

        // 工具2：启动本地交互式会话
        tools.Add(AIFunctionFactory.Create(
            name: "sys_start_local_session",
            description: """
启动本地 PowerShell 交互式会话。会话保持活跃，支持后续多条命令执行。
适合复杂的多步骤操作或需要依赖前一个命令结果的场景。

返回：会话ID，用于后续 sys_send_command 和 sys_close_session 调用。
""",
            method: StartLocalSessionAsync));

        // 工具3：启动远程 SSH 交互式会话
        tools.Add(AIFunctionFactory.Create(
            name: "sys_start_remote_session",
            description: """
启动远程 SSH 交互式会话（支持 Windows/Linux）。会话保持活跃，支持后续多条命令执行。
适合远程部署、代码拉取、数据库操作等耗时场景。

认证方式（优先级）：
1. 密钥认证：提供 privateKeyPath 参数（推荐，更安全）
2. 密码认证：仅在不提供 privateKeyPath 时使用 password 参数

参数：
- host: 远程服务器地址
- username: 用户名
- password: 密码（可选，无密钥时使用）
- privateKeyPath: SSH 私钥文件路径（可选，如 ~/.ssh/id_rsa）

返回：会话ID，用于后续 sys_send_command 和 sys_close_session 调用。
""",
            method: StartRemoteSessionAsync));

        // 工具4：向会话发送命令
        tools.Add(AIFunctionFactory.Create(
            name: "sys_get_session_status",
            description: "查询单个会话当前状态。适用于 SSH 认证等待、网络慢、用户已输入密码后确认是否已就绪。参数：sessionId",
            method: GetSessionStatusAsync));

        // 工具5：向会话发送命令
        tools.Add(AIFunctionFactory.Create(
            name: "sys_send_command",
            description: """
向已启动的会话发送命令，实时返回输出。
支持依赖前一命令结果的多步骤操作。
长时间命令请显式增大 timeoutMs。

参数：
- sessionId: 会话ID
- command: 要执行的命令
- timeoutMs: 超时时间（毫秒）
""",
            method: SendCommandAsync));

        // 工具6：关闭会话
        tools.Add(AIFunctionFactory.Create(
            name: "sys_close_session",
            description: "关闭已启动的会话，释放资源。参数：sessionId",
            method: CloseSessionAsync));

        // 工具7：列出所有活跃会话
        tools.Add(AIFunctionFactory.Create(
            name: "sys_list_sessions",
            description: "列出所有活跃的执行会话",
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(script))
            return "错误：脚本不能为空";

        try
        {
            _logger.LogInformation("AI 正在执行 PowerShell 脚本，长度: {ScriptLength} 字符", script.Length);

            var result = await _executor.ExecuteAsync(script, timeout, ct);

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
            return $"错误：{errorMsg}";
        }
        catch (TimeoutException)
        {
            var errorMsg = $"PowerShell 执行超时（{timeout}ms）";
            _logger.LogWarning(errorMsg);
            return $"错误：{errorMsg}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell 执行异常");
            return $"错误：{ex.Message}";
        }
    }

    /// <summary>
    /// 启动本地交互式 PowerShell 会话
    /// </summary>
    private Task<string> StartLocalSessionAsync(CancellationToken ct = default)
    {
        try
        {
            var session = _sessionRegistry.CreateSession("local");

            if (!session.IsActive)
                return Task.FromResult("✗ 启动失败：PowerShell 进程启动后立即退出");

            _logger.LogInformation("本地会话已启动: {SessionId}", session.Id);

            return Task.FromResult($@"✓ 本地 PowerShell 会话已启动
会话ID: {session.Id}
类型: 本地交互式

使用说明：
1. 调用 send_command 发送命令并获取输出
2. 任务完成后调用 close_session 关闭会话
3. 用户可以在窗口中看到所有命令执行过程");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动本地会话失败");
            return Task.FromResult($"✗ 启动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动远程 SSH 交互式会话
    /// </summary>
    private async Task<string> StartRemoteSessionAsync(
        string host,
        string username,
        string? password = null,
        string? privateKeyPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username))
            return "✗ 错误：host 和 username 不能为空";

        if (string.IsNullOrWhiteSpace(privateKeyPath) && string.IsNullOrWhiteSpace(password))
            return "✗ 错误：必须提供 privateKeyPath 或 password 之一";

        try
        {
            var session = _sessionRegistry.CreateSession("remote", host, username, password, privateKeyPath);
            await session.WaitForStartupAsync(1500, ct);

            if (!session.IsActive || session.State == ExecutionSessionState.Failed)
            {
                await _sessionRegistry.RemoveSessionAsync(session.Id);
                return $"✗ 启动失败：{session.LastError ?? "SSH 连接失败，请检查主机地址、用户名和认证信息。"}";
            }

            var authType = string.IsNullOrWhiteSpace(privateKeyPath) ? "密码" : "密钥";
            _logger.LogInformation("远程会话已启动: {SessionId} - {Host} (认证: {AuthType})", session.Id, host, authType);

            if (session.State == ExecutionSessionState.AwaitingUserInput)
            {
                return $@"⚠ 远程 SSH 会话已创建，尚未完成认证
会话ID: {session.Id}
主机: {host}
用户: {username}
认证方式: {authType}
当前状态: {session.StatusMessage}

处理说明：
1. 请让用户只在 SSH 窗口中输入一次，不要重复要求输入
2. 用户输入后先等待连接完成，再调用 sys_send_command
3. 若认证失败，工具会返回明确错误；密钥模式不会回退到密码认证";
            }

            return $@"✓ 远程 SSH 会话已启动
会话ID: {session.Id}
主机: {host}
用户: {username}
认证方式: {authType}
类型: 远程交互式
当前状态: {session.StatusMessage}

使用说明：
1. 调用 send_command 发送命令并获取输出
2. 任务完成后调用 close_session 关闭会话
        3. 支持 Windows/Linux 远程操作";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动远程会话失败: {Host}", host);
            return $"✗ 启动失败：{ex.Message}";
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
        sb.AppendLine($"认证: {session.AuthenticationMode}");
        if (!string.IsNullOrWhiteSpace(session.Host))
            sb.AppendLine($"主机: {session.Host}");
        if (!string.IsNullOrWhiteSpace(session.Username))
            sb.AppendLine($"用户: {session.Username}");
        sb.AppendLine($"活跃: {(session.IsActive ? "是" : "否")}");
        sb.AppendLine($"状态: {session.State}");
        sb.AppendLine($"说明: {session.StatusMessage}");
        if (!string.IsNullOrWhiteSpace(session.LastError))
            sb.AppendLine($"错误: {session.LastError}");
        sb.AppendLine($"创建时间: {session.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"最后活动: {session.LastActivityAt:yyyy-MM-dd HH:mm:ss}");

        if (session.State == ExecutionSessionState.AwaitingUserInput)
        {
            sb.AppendLine();
            sb.AppendLine("提示:");
            sb.AppendLine("1. 当前正在等待用户一次性输入，不要重复要求用户输入");
            sb.AppendLine("2. 用户输入完成后，先再次查询状态，确认已就绪后再发送命令");
        }
        else if (session.State == ExecutionSessionState.Ready)
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

        if (session.Type == "local" && LooksLikeInteractiveSshCommand(command))
        {
            return "✗ 错误：不要在本地持久会话中再次执行 ssh/ssh.exe 进入交互式远程 shell。这会占住当前会话输出流，导致后续 sessionId 复用失败。请直接使用 sys_start_remote_session 创建远程 SSH 会话。";
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
        foreach (var (id, type, authType, host, createdAt, state, statusMessage) in sessions)
        {
            sb.AppendLine($"- ID: {id}");
            sb.AppendLine($"  类型: {type}");
            sb.AppendLine($"  认证: {authType}");
            if (!string.IsNullOrEmpty(host))
                sb.AppendLine($"  主机: {host}");
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