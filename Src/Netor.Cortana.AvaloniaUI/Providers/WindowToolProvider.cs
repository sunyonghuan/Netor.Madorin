using Avalonia.Threading;

using Netor.Cortana.AvaloniaUI.Views;

namespace Netor.Cortana.AvaloniaUI.Providers;

/// <summary>
/// 窗口管理工具提供者，向 AI 提供窗口显示/隐藏/移动/状态查询能力。
/// </summary>
internal sealed class WindowToolProvider(
    ILogger<WindowToolProvider> logger,
    IWindowController windowController,
    IAppPaths appPaths,
    IServiceProvider serviceProvider) : AIContextProvider
{
    private readonly List<AITool> _tools = [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
            RegisterTools();

        return ValueTask.FromResult(new AIContext
        {
            Instructions = BuildInstructions(),
            Tools = _tools
        });
    }

    // ──────── 工具注册 ────────

    private void RegisterTools()
    {
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_show_main_window",
            description: "Shows and activates the main window (conversation interface).",
            method: ShowMainWindow));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_hide_main_window",
            description: "Hides the main window (minimizes to system tray).",
            method: HideMainWindow));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_show_settings_window",
            description: "Opens the settings window.",
            method: ShowSettingsWindow));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_show_float_window",
            description: "Shows the desktop floating ball window.",
            method: ShowFloatWindow));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_move_float_window",
            description: "Moves the floating ball to a specified screen position. Parameters: x, y (screen pixel coordinates).",
            method: (int x, int y) => MoveFloatWindow(x, y)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_main_window_status",
            description: "Gets the current status of the main window, including visibility, position, and size.",
            method: GetMainWindowStatus));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_settings_window_status",
            description: "Gets the current status of the settings window, including whether it is open.",
            method: GetSettingsWindowStatus));

        // Path Queries
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_workspace_directory",
            description: "Gets the current working directory (Dashboard,Workstation) path. The working directory is used to store user work files.",
            method: GetWorkspaceDirectory));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_user_data_directory",
            description: "Gets the user data storage directory path. The data directory is used to store application data, databases, configurations, etc.",
            method: GetUserDataDirectory));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_workspace_skills_directory",
            description: "Gets the skills directory path for the current working environment.",
            method: GetWorkspaceSkillsDirectory));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_workspace_plugins_directory",
            description: "Gets the plugins directory path for the current working environment.",
            method: GetWorkspacePluginsDirectory));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_user_skills_directory",
            description: "Gets the global skills directory path. Skills installed here are available in all working environments.",
            method: GetUserSkillsDirectory));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_user_plugins_directory",
            description: "Gets the global plugins directory path. Plugins installed here are available in all working environments.",
            method: GetUserPluginsDirectory));

        // Working Directory Management
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_change_workspace_directory",
            description: "Change the current workspace directory. Call this only after the user explicitly agrees to change the workspace boundary. The target directory must already exist.",
            method: (string path) => ChangeWorkspaceDirectory(path)));

        // Session Management
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_new_session",
            description: "Creates a new conversation session. Call when the user requests to start a new conversation, change topics, or restart. The interface automatically switches to the new session upon creation.",
            method: NewSession));
    }

    // ──────── 窗口管理 ────────

    private string ShowMainWindow()
    {
        try
        {
            windowController.ShowMainWindow();
            return "✓ 主窗口已显示";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示主窗口失败");
            return $"✗ 显示主窗口失败：{ex.Message}";
        }
    }

    private string HideMainWindow()
    {
        try
        {
            windowController.HideMainWindow();
            return "✓ 主窗口已隐藏";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "隐藏主窗口失败");
            return $"✗ 隐藏主窗口失败：{ex.Message}";
        }
    }

    private string ShowSettingsWindow()
    {
        try
        {
            windowController.ShowSettingsWindow();
            return "✓ 设置窗口已打开";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "打开设置窗口失败");
            return $"✗ 打开设置窗口失败：{ex.Message}";
        }
    }

    private string ShowFloatWindow()
    {
        try
        {
            windowController.ShowFloatWindow();
            return "✓ 浮动球已显示";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "显示浮动球失败");
            return $"✗ 显示浮动球失败：{ex.Message}";
        }
    }

    private string MoveFloatWindow(int x, int y)
    {
        try
        {
            windowController.MoveFloatWindow(x, y);
            return $"✓ 浮动球已移动到 ({x}, {y})";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "移动浮动球失败");
            return $"✗ 移动浮动球失败：{ex.Message}";
        }
    }

    // ──────── 窗口状态查询 ────────

    private string GetMainWindowStatus()
    {
        try
        {
            return Dispatcher.UIThread.Invoke(() =>
            {
                var main = serviceProvider.GetRequiredService<MainWindow>();
                var visible = main.IsVisible;
                var state = main.WindowState;
                var pos = main.Position;
                var size = main.ClientSize;

                return $"主窗口状态：可见={visible}, 窗口状态={state}, 位置=({pos.X},{pos.Y}), 大小={size.Width}x{size.Height}";
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取主窗口状态失败");
            return $"✗ 获取主窗口状态失败：{ex.Message}";
        }
    }

    private string GetSettingsWindowStatus()
    {
        try
        {
            return Dispatcher.UIThread.Invoke(() =>
            {
                var settings = serviceProvider.GetRequiredService<SettingsWindow>();
                if (!settings.IsVisible)
                    return "设置窗口状态：未打开";

                var pos = settings.Position;
                var size = settings.ClientSize;
                return $"设置窗口状态：已打开, 位置=({pos.X},{pos.Y}), 大小={size.Width}x{size.Height}";
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取设置窗口状态失败");
            return $"✗ 获取设置窗口状态失败：{ex.Message}";
        }
    }

    // ──────── 路径查询 ────────

    private string GetWorkspaceDirectory() => appPaths.WorkspaceDirectory;

    private string GetUserDataDirectory() => appPaths.UserDataDirectory;

    private string GetWorkspaceSkillsDirectory() => appPaths.WorkspaceSkillsDirectory;

    private string GetWorkspacePluginsDirectory() => appPaths.WorkspacePluginsDirectory;

    private string GetUserSkillsDirectory() => appPaths.UserSkillsDirectory;

    private string GetUserPluginsDirectory() => appPaths.UserPluginsDirectory;

    private string ChangeWorkspaceDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "✗ 路径不能为空";
        if (!Directory.Exists(path))
            return $"✗ 目录不存在：{path}";

        try
        {
            var publisher = serviceProvider.GetRequiredService<IPublisher>();
            publisher.Publish(Events.OnWorkspaceChanged, new WorkspaceChangedArgs(path));
            return $"✓ 工作目录已切换到：{path}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "切换工作目录失败");
            return $"✗ 切换工作目录失败：{ex.Message}";
        }
    }

    private async Task<string> NewSession()
    {
        try
        {
            var chatEngine = serviceProvider.GetRequiredService<IAiChatEngine>();
            await chatEngine.NewSessionAsync();
            return "✓ 已创建新会话";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "创建新会话失败");
            return $"✗ 创建新会话失败：{ex.Message}";
        }
    }

    // ──────── 指令 ────────

    private static string BuildInstructions() => """
        ### 自身窗口管理工具使用规范

        你拥有控制自身应用窗口的能力。

        - 用户要求打开/显示主界面时，调用 sys_show_main_window
        - 用户要求隐藏/关闭主界面时，调用 sys_hide_main_window
        - 用户要求打开设置时，调用 sys_show_settings_window
        - 用户要求显示/打开浮动球时，调用 sys_show_float_window
        - 用户要求移动浮动球时，调用 sys_move_float_window 并传入坐标
        - 执行窗口操作后，可调用 sys_get_main_window_status 或 sys_get_settings_window_status 验证操作结果
        - 用户询问窗口是否打开/关闭时，调用对应的状态查询工具

        ### 路径查询
        - 需要知道工作目录时，调用 sys_get_workspace_directory
        - 需要知道数据存储目录时，调用 sys_get_user_data_directory
        - 需要知道当前工作环境的技能目录时，调用 sys_get_workspace_skills_directory
        - 需要知道当前工作环境的插件目录时，调用 sys_get_workspace_plugins_directory
        - 需要知道全局技能目录时，调用 sys_get_user_skills_directory
        - 需要知道全局插件目录时，调用 sys_get_user_plugins_directory

        ### 工作目录管理
        - 只有在用户明确同意切换工作目录边界后，才调用 sys_change_workspace_directory 并传入完整目录路径
        - 目录必须已存在，不会自动创建
        - 切换后所有相关模块（文件树、会话列表、设置）会自动同步

        ### 会话管理
        - 用户要求开始新对话、换个话题、重新开始时，调用 sys_new_session
        - 创建后界面会自动切换到新会话
        """;
}