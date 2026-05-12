using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

using Microsoft.Extensions.Hosting;

using Netor.Cortana.UI.Views;
using Netor.Cortana.UI.Views.Proxy;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Networks;
using Netor.Cortana.Plugin;
using Netor.Cortana.Voice;
using Netor.EventHub;

using Serilog;

using System.Text;

namespace Netor.Cortana.UI;

/// <summary>
/// Avalonia 应用程序入口，负责 DI 配置、窗口初始化和后台服务生命周期。
/// </summary>
public partial class App : Application
{
    private static CancellationTokenSource _cts = new();
    private static volatile bool _isShuttingDown;

    private TrayIcon? _trayIcon;
    private FloatWindow? _floatWindow;
    private BubbleWindow? _bubbleWindow;

#pragma warning disable CS8618
    internal static IServiceProvider Services { get; private set; }
#pragma warning restore CS8618
    /// <summary>
    /// 应用名称
    /// </summary>
    internal static string AppName {get;}="玛得令";
    internal static CancellationTokenSource CancellationTokenSource => _cts;
    internal static bool IsShuttingDown => _isShuttingDown;

    /// <summary>
    /// 用户数据路径（exe 所在目录）。
    /// </summary>
    internal static string UserDataDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();

    /// <summary>
    /// 工作区路径。
    /// </summary>
    internal static string WorkspaceDirectory { get; private set; } = UserDataDirectory;

    /// <summary>
    /// 插件目录路径。
    /// </summary>
    internal static string PluginDirectory => UserPluginsDirectory;

    /// <summary>
    /// 工作区技能目录路径。
    /// </summary>
    internal static string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

    /// <summary>
    /// 工作区插件目录路径。已废弃，插件统一安装到用户全局插件目录。
    /// </summary>
    [Obsolete("工作区插件目录已废弃，请使用 UserPluginsDirectory。")]
    internal static string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");

    /// <summary>
    /// 用户数据技能目录路径。
    /// </summary>
    internal static string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");

    /// <summary>
    /// 用户数据插件目录路径。
    /// </summary>
    internal static string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");

    /// <summary>
    /// 更改当前工作区目录。
    /// </summary>
    internal static void ChangeWorkspaceDirectory(string path)
    {
        WorkspaceDirectory = path;
        var cortanaPath = Path.Combine(WorkspaceDirectory, ".cortana");
        if (!Directory.Exists(cortanaPath))
            Directory.CreateDirectory(cortanaPath);
        if (!Directory.Exists(WorkspaceSkillsDirectory))
            Directory.CreateDirectory(WorkspaceSkillsDirectory);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigureServices();
        EnsureDirectories();
        InitializeWorkspace();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 浮动窗口和气泡窗口
            _floatWindow = Services.GetRequiredService<FloatWindow>();
            _bubbleWindow = Services.GetRequiredService<BubbleWindow>();
            _bubbleWindow.SetAnchorWindow(_floatWindow);
            _floatWindow.FloatPositionChanged += () => _bubbleWindow.OnAnchorMoved();

            // 主窗口显示后再启动浮动窗口
            mainWindow.Opened += (_, _) =>
            {
                _floatWindow.Show();
            };

            // 初始化托盘图标
            InitializeTrayIcon(desktop);

            // 订阅全局 UI 通知事件
            SubscribeGlobalNotifications();

            // 启动后台服务
            desktop.Startup += (_, _) =>
            {
                _ = Task.Run(async () =>
                {
                    await StartBackgroundServicesAsync(_cts.Token);
                    await LoadPluginsAsync(_cts.Token);
                });
            };

            desktop.Exit += (_, _) =>
            {
                ShutdownApplicationAsync().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 配置 DI 容器。
    /// </summary>
    private static void ConfigureServices()
    {
        var appSettings = LoadEmbeddedSettings();

        IServiceCollection services = new ServiceCollection();

        services
            .AddSingleton(appSettings)
            .AddLogging(static options =>
            {
                options.AddConsole()
                .AddDebug();
                var logDir = Environment.GetEnvironmentVariable("CORTANA_LOG_DIR")
                    ?? Path.Combine(WorkspaceDirectory, "logs");
                try { Directory.CreateDirectory(logDir); } catch { }

                options.AddSerilog(new LoggerConfiguration()
                    .MinimumLevel
                    .Information()
                    .WriteTo
                    .File(
                        // 将日志写入可覆写目录（支持 CORTANA_LOG_DIR 环境变量）
                        Path.Combine(logDir, "app-.log"),
                        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning,
                        rollingInterval: RollingInterval.Minute,
                        fileSizeLimitBytes: 100 * 1024 * 1024,
                        retainedFileCountLimit: 72,
                        rollOnFileSizeLimit: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .CreateLogger(), dispose: true);
            })
            .AddEventHub()
            .AddHttpClient()
            // 窗口
            .AddSingleton<MainWindow>()
            .AddSingleton<FloatWindow>()
            .AddSingleton<BubbleWindow>()
            .AddSingleton<SettingsWindow>()
            .AddSingleton<ProxyViewModel>()
            .AddSingleton<ProxyWindow>()
            // 数据库
            .AddSingleton<CortanaDbContext>()
            .AddTransient<SystemSettingsService>()
            .AddTransient<AgentSeedService>()
            .AddTransient<AgentService>()
            .AddTransient<GlobalPluginService>()
            .AddTransient<AiProviderService>()
            .AddTransient<AiModelService>()
            .AddTransient<ChatMessageService>()
            .AddTransient<ChatMessageAssetService>()
            .AddTransient<CompactionSegmentService>()
            .AddTransient<McpServerService>()
            // 跨层契约
            .AddSingleton<IAppPaths, AppPaths>()
            .AddSingleton<IWindowController, WindowController>()
            // UI 输出通道：将 AI 流式回复渲染到 MainWindow
            .AddSingleton<IAiOutputChannel, UiChatOutputChannel>()
            // AI 工具提供者
            .AddSingleton<AIContextProvider, Providers.WindowToolProvider>()
            .AddSingleton<AIContextProvider, Providers.AiConfigToolProvider>()
            .AddSingleton<AIContextProvider, Providers.PluginManagementProvider>()
            // 业务模块
            .AddCortanaVoice()
            .AddCortanaAI()
            .AddCortanaPlugin()
            .AddCortanaNetworks();

        Services = services.BuildServiceProvider();
    }

    /// <summary>
    /// 从嵌入资源加载 appsettings.json。
    /// </summary>
    private static AppSettings LoadEmbeddedSettings()
    {
        var assembly = typeof(App).Assembly;
        using var stream = assembly.GetManifestResourceStream("Netor.Cortana.UI.appsettings.json")
            ?? throw new InvalidOperationException("嵌入资源 appsettings.json 未找到。");

        return JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings)
            ?? throw new InvalidOperationException("反序列化 appsettings.json 失败。");
    }

    /// <summary>
    /// 确保必要目录存在。
    /// </summary>
    private static void EnsureDirectories()
    {
        var cortanaPath = Path.Combine(WorkspaceDirectory, ".cortana");
        if (!Directory.Exists(cortanaPath))
            Directory.CreateDirectory(cortanaPath);
        if (!Directory.Exists(UserSkillsDirectory))
            Directory.CreateDirectory(UserSkillsDirectory);
        if (!Directory.Exists(UserPluginsDirectory))
            Directory.CreateDirectory(UserPluginsDirectory);
    }

    /// <summary>
    /// 初始化工作区目录。
    /// </summary>
    private static void InitializeWorkspace()
    {
        var sysSettings = Services.GetRequiredService<SystemSettingsService>();

        // 首次启动写入默认配置
        var appSettings = Services.GetRequiredService<AppSettings>();
        sysSettings.EnsureSeedData(
            sherpaOnnx: new SherpaOnnxSeedValues
            {
                KeywordsThreshold = appSettings.SherpaOnnx.KeywordsThreshold,
                KeywordsScore = appSettings.SherpaOnnx.KeywordsScore,
                NumTrailingBlanks = appSettings.SherpaOnnx.NumTrailingBlanks,
                Rule1MinTrailingSilence = appSettings.SherpaOnnx.Rule1MinTrailingSilence,
                Rule2MinTrailingSilence = appSettings.SherpaOnnx.Rule2MinTrailingSilence,
                Rule3MinUtteranceLength = appSettings.SherpaOnnx.Rule3MinUtteranceLength,
                RecognitionTimeoutSeconds = appSettings.SherpaOnnx.RecognitionTimeoutSeconds
            },
            ttsSpeed: appSettings.Tts.Speed,
            workspaceDirectory: UserDataDirectory);

        var agentSeedService = Services.GetRequiredService<AgentSeedService>();
        agentSeedService.EnsureSeedData();

        // 版本迁移：为已有数据库补充新增设置项
        sysSettings.EnsureSetting("WebSocket.Port",
            group: "网络", displayName: "WebSocket 端口",
            description: "WebSocket 服务监听端口，修改后立即生效，建议重启软件以确保插件正常工作。",
            defaultValue: "52841", valueType: "int", sortOrder: 0);

        sysSettings.EnsureSetting("Voice.WakeWordEnabled",
            group: "语音唤醒", displayName: "语音唤醒开关",
            description: "开启后启动麦克风唤醒词监听；关闭后不启动语音唤醒服务。",
            defaultValue: "true", valueType: "bool", sortOrder: 0);

        sysSettings.EnsureSetting("Tts.WelcomeGreeting",
            group: "语音合成", displayName: "唤醒欢迎语",
            description: "AI 被唤醒时播放的欢迎语，修改后需要重启应用才能生效。",
            defaultValue: "主人，我在!", valueType: "string", sortOrder: 1);

        sysSettings.EnsureSetting("Compaction.ModelId",
            group: "对话历史", displayName: "缩略专用模型",
            description: "用于会话压缩摘要的模型，留空则跟随当前对话模型。",
            defaultValue: "", valueType: "model", sortOrder: 0);

        sysSettings.EnsureSetting("Compaction.SegmentSize",
            group: "对话历史", displayName: "压缩段落大小",
            description: "每多少条消息生成一个压缩摘要段落（建议 20-50）。",
            defaultValue: "30", valueType: "int", sortOrder: 1);

        sysSettings.EnsureSetting("Compaction.RawTailSize",
            group: "对话历史", displayName: "尾部原始消息数",
            description: "保留最近多少条原始消息不压缩，确保 AI 看到完整的近期对话细节。",
            defaultValue: "20", valueType: "int", sortOrder: 2);

        sysSettings.EnsureSetting("Compaction.MaxDisplaySegments",
            group: "对话历史", displayName: "最大显示段落数",
            description: "加载历史时最多携带多少个摘要段落，超出的旧段落不再加载（但不删除）。",
            defaultValue: "15", valueType: "int", sortOrder: 3);

        sysSettings.EnsureSetting("AI.Trace.Enabled",
            group: "调试", displayName: "AI 全量调试日志",
            description: "记录 AI 请求、流式更新、响应和异常的完整调试日志。发布版默认关闭，开启后可用于排查工具调用与上下文问题。",
            defaultValue: "false", valueType: "bool", sortOrder: 0);

        // v1.3: Ollama 兼容代理配置，供 ProxyWindow 设置页和网络代理服务读取。
        sysSettings.EnsureOllamaProxySettings();
        // 版本迁移：移除已废弃的旧配置项
        sysSettings.DeleteSetting("ChatHistory.MaxContentLength");
        sysSettings.DeleteSetting("ChatHistory.MaxContentCount");
        sysSettings.DeleteSetting("Memory.ModelId");

        var savedWorkspace = sysSettings.GetValue("System.WorkspaceDirectory");
        var workspacePath = (!string.IsNullOrWhiteSpace(savedWorkspace) && Directory.Exists(savedWorkspace))
            ? savedWorkspace
            : UserDataDirectory;
        ChangeWorkspaceDirectory(workspacePath);

        // 订阅工作目录变更事件：统一处理全局状态 + 持久化
        var subscriber = Services.GetRequiredService<ISubscriber>();
        subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, args) =>
        {
            ChangeWorkspaceDirectory(args.Path);
            var settings = Services.GetRequiredService<SystemSettingsService>();
            settings.SetValue("System.WorkspaceDirectory", args.Path);
            return Task.FromResult(false);
        });
    }

    /// <summary>
    /// 初始化系统托盘图标和菜单。
    /// </summary>
    private void InitializeTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var showItem = new NativeMenuItem("显示界面");
        showItem.Click += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
        };

        var settingsItem = new NativeMenuItem("软件设置");
        settingsItem.Click += (_, _) =>
        {
            var settings = Services.GetRequiredService<SettingsWindow>();
            settings.Show();
            settings.Activate();
        };
        var proxyItem = new NativeMenuItem("AI代理");
        proxyItem.Click += (_, _) =>
        {
            var proxy = Services.GetRequiredService<ProxyWindow>();
            proxy.Show();
            proxy.Activate();
        };
        var sysSettings = Services.GetRequiredService<SystemSettingsService>();
        var voiceItem = new NativeMenuItem();
        UpdateVoiceMenuItemHeader(voiceItem, sysSettings.GetValue("Voice.WakeWordEnabled", true));
        voiceItem.Click += (_, _) =>
        {
            var settings = Services.GetRequiredService<SystemSettingsService>();
            var wakeWordService = Services.GetRequiredService<WakeWordService>();
            var enabled = !settings.GetValue("Voice.WakeWordEnabled", true);
            settings.SetValue("Voice.WakeWordEnabled", enabled.ToString().ToLowerInvariant());
            UpdateVoiceMenuItemHeader(voiceItem, enabled);

            _ = enabled
                ? wakeWordService.StartAsync(_cts.Token)
                : wakeWordService.StopAsync(CancellationToken.None);
        };
        var exitItem = new NativeMenuItem("退出助理");
        exitItem.Click += (_, _) =>
        {
            _ = ShutdownFromTrayAsync(desktop);
        };

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(voiceItem);
        menu.Items.Add(proxyItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        var iconStream = AssetLoader.Open(new Uri("avares://Cortana/Assets/logo.200.png"));

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = $"{AppName} · AI 助手",
            Menu = menu,
            IsVisible = true,
        };

        _trayIcon.Clicked += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
        };
    }

    private static void UpdateVoiceMenuItemHeader(NativeMenuItem item, bool enabled)
    {
        item.Header = enabled ? "语音唤醒：开" : "语音唤醒：关";
    }

    /// <summary>
    /// 托盘菜单触发的退出入口。先释放后台资源，再关闭 Avalonia，避免插件子进程驻留。
    /// </summary>
    private async Task ShutdownFromTrayAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_isShuttingDown) return;

        await ShutdownApplicationAsync();

        if (desktop.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ForceClose();
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        desktop.Shutdown();
    }

    /// <summary>
    /// 统一退出流程：取消业务任务、卸载插件/MCP、停止托管服务、释放 DI 容器。
    /// </summary>
    private static async Task ShutdownApplicationAsync()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try { _cts.Cancel(); } catch { }

        await RunShutdownStepAsync(UnloadPluginsAsync(), "卸载插件/MCP 系统");
        await RunShutdownStepAsync(StopBackgroundServicesAsync(CancellationToken.None), "停止后台服务");

        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static async Task RunShutdownStepAsync(Task task, string stepName)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            var logger = Services.GetService<ILogger<App>>();
            logger?.LogWarning("退出流程超时，跳过步骤：{StepName}", stepName);
        }
    }

    /// <summary>
    /// 订阅需要由 App 统一协调显示的全局通知事件。
    /// </summary>
    private void SubscribeGlobalNotifications()
    {
        var subscriber = Services.GetRequiredService<ISubscriber>();
        subscriber.Subscribe<WebSocketClientConnectionChangedArgs>(
            Events.OnWebSocketClientConnectionChanged,
            (_, args) =>
            {
                HandleWebSocketClientConnectionChanged(args);
                return Task.FromResult(false);
            });

        subscriber.Subscribe<McpConnectionStateChangedArgs>(
            Events.OnMcpConnectionStateChanged,
            (_, args) =>
            {
                HandleMcpConnectionStateChanged(args);
                return Task.FromResult(false);
            });
    }

    /// <summary>
    /// 处理 WebSocket 客户端连接/断开通知。
    /// 主窗口可见时写入临时系统提示；主窗口隐藏时额外弹出浮动气泡提示。
    /// </summary>
    private void HandleWebSocketClientConnectionChanged(WebSocketClientConnectionChangedArgs args)
    {
        var message = args.IsConnected
            ? $"客户端 {args.RemoteEndpoint} 已连接到助理。"
            : $"客户端 {args.RemoteEndpoint} 已断开与助理的连接。";

        var bubbleText = args.IsConnected
            ? $"客户端 {args.RemoteEndpoint} 已连接"
            : $"客户端 {args.RemoteEndpoint} 已断开";

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.AddSystemNotice(new SystemNoticeArgs(
                message,
                args.IsConnected ? "外部连接" : "外部断开",
                args.IsConnected ? "success" : "warning",
                "WebSocket",
                DateTimeOffset.UtcNow));

            if (!mainWindow.IsVisible)
            {
                _bubbleWindow?.ShowSystemNotification(bubbleText, args.IsConnected);
            }
        });
    }

    /// <summary>
    /// 处理 MCP 服务器连接状态变更通知。
    /// 断线时通知用户（主窗口可见写入临时系统提示，否则弹浮动气泡）。
    /// </summary>
    private void HandleMcpConnectionStateChanged(McpConnectionStateChangedArgs args)
    {
        // 仅在断线和重连成功时通知，重连中不反复弹窗
        if (args.IsReconnecting) return;

        var message = args.IsConnected
            ? $"MCP 服务「{args.ServerName}」已恢复连接。"
            : $"MCP 服务「{args.ServerName}」连接已断开，正在自动重连…";

        var bubbleText = args.IsConnected
            ? $"MCP {args.ServerName} 已恢复"
            : $"MCP {args.ServerName} 已断开";

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.AddSystemNotice(new SystemNoticeArgs(
                message,
                args.IsConnected ? "MCP 已连接" : "MCP 已断开",
                args.IsConnected ? "success" : "warning",
                args.ServerName,
                DateTimeOffset.UtcNow));

            if (!mainWindow.IsVisible)
            {
                _bubbleWindow?.ShowSystemNotification(bubbleText, args.IsConnected);
            }
        });
    }

    /// <summary>
    /// 启动后台服务。
    /// </summary>
    private static async Task StartBackgroundServicesAsync(CancellationToken cancellationToken)
    {
        var logger = Services.GetRequiredService<ILogger<App>>();
        try
        {
            var hostedServices = Services.GetServices<IHostedService>();
            foreach (var service in hostedServices)
            {
                try
                {
                    await service.StartAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "启动服务失败: {Service}", service.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动后台服务失败");
        }
    }

    /// <summary>
    /// 加载插件和 MCP 服务器。
    /// </summary>
    private static async Task LoadPluginsAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        var logger = Services.GetRequiredService<ILogger<App>>();
        try
        {
            var wsServer = Services.GetRequiredService<WebSocketInteractionServerService>();
            var feedServer = Services.GetRequiredService<WebSocketFeedServerService>();
            var pluginLoader = Services.GetRequiredService<PluginLoader>();
            pluginLoader.WsPort = wsServer.Port;
            pluginLoader.FeedPort = feedServer.Port;

            if (pluginLoader.WsPort <= 0 || pluginLoader.FeedPort <= 0)
            {
                logger.LogWarning("WS 端口未初始化，插件可能无法连接宿主：ws={WsPort} feed={FeedPort}", pluginLoader.WsPort, pluginLoader.FeedPort);
            }

            await pluginLoader.ScanAndLoadAsync(cancellationToken);
            pluginLoader.StartWatching();

            var mcpService = Services.GetRequiredService<McpServerService>();
            await pluginLoader.LoadMcpServersAsync(mcpService, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "插件/MCP 系统启动失败");
        }
    }

    /// <summary>
    /// 退出时显式卸载插件、断开 MCP，并杀掉进程通道插件子进程。
    /// </summary>
    private static async Task UnloadPluginsAsync()
    {
        try
        {
            var pluginLoader = Services.GetService<PluginLoader>();
            if (pluginLoader is null) return;

            await pluginLoader.DisposeAsync();
        }
        catch (Exception ex)
        {
            var logger = Services.GetService<ILogger<App>>();
            logger?.LogError(ex, "卸载插件/MCP 系统失败");
        }
    }

    /// <summary>
    /// 停止后台服务。
    /// </summary>
    private static async Task StopBackgroundServicesAsync(CancellationToken cancellationToken)
    {
        var logger = Services.GetRequiredService<ILogger<App>>();
        var hostedServices = Services.GetServices<IHostedService>();
        foreach (var service in hostedServices)
        {
            try
            {
                await service.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "停止服务失败: {Service}", service.ToString());
            }
        }
    }
}