using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Networks;
using Netor.Cortana.Plugin;
using Netor.Cortana.Store;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.UI.Views;
using Netor.Cortana.UI.Views.Proxy;
using Netor.Cortana.Voice;
using Netor.EventHub;

using Serilog;
using Serilog.Events;

using System.Text;

namespace Netor.Cortana.UI;

/// <summary>
/// Avalonia 应用程序入口，负责 DI 配置、窗口初始化和后台服务生命周期。
/// </summary>
public partial class App : Application
{
    internal const string FloatWindowEnabledKey = "UI.FloatWindow.Enabled";

    /// <summary>
    /// 应用级取消令牌源，用于通知后台任务和服务停止。
    /// </summary>
    private static CancellationTokenSource _cts = new();

    /// <summary>
    /// 标记应用是否正在执行关闭流程，避免重复退出。
    /// </summary>
    private static volatile bool _isShuttingDown;

    /// <summary>
    /// 系统托盘图标实例。
    /// </summary>
    private TrayIcon? _trayIcon;

    /// <summary>
    /// 浮动唤醒窗口实例。
    /// </summary>
    private FloatWindow? _floatWindow;

    /// <summary>
    /// 浮动气泡通知窗口实例。
    /// </summary>
    private BubbleWindow? _bubbleWindow;

#pragma warning disable CS8618
    /// <summary>
    /// 应用全局服务提供程序。
    /// </summary>
    internal static IServiceProvider Services { get; private set; }
#pragma warning restore CS8618

    /// <summary>
    /// 应用名称
    /// </summary>
    internal static string AppName { get; } = AppBranding.DisplayName;

    /// <summary>
    /// 当前应用生命周期共享的取消令牌源。
    /// </summary>
    internal static CancellationTokenSource CancellationTokenSource => _cts;

    /// <summary>
    /// 获取应用是否正在关闭。
    /// </summary>
    internal static bool IsShuttingDown => _isShuttingDown;

    /// <summary>
    /// 用户数据路径（exe 所在目录）。
    /// </summary>
    internal static string UserDataDirectory => ResolveUserDataDirectory();

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
    /// 用户数据智能体目录路径。
    /// </summary>
    internal static string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");

    /// <summary>
    /// 用户数据解决方案目录路径。
    /// </summary>
    internal static string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");

    private static string ResolveUserDataDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && !IsDotnetHost(processPath))
        {
            return Path.GetDirectoryName(processPath) ?? Directory.GetCurrentDirectory();
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsDotnetHost(string processPath)
    {
        return string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// 加载 Avalonia XAML 资源。
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 在 Avalonia 框架初始化完成后配置服务、窗口和应用生命周期事件。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        ConfigureServices();
        EnsureDirectories();
        InitializeWorkspace();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.ApplySavedPlacement();
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
                if (IsFloatWindowEnabled())
                {
                    _floatWindow.Show();
                }
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

                var fileMinimumLevel = ResolveFileLogMinimumLevel(logDir);

                options.AddSerilog(new LoggerConfiguration()
                    .MinimumLevel
                    .Information()
                    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                    .WriteTo
                    .File(
                        // 将日志写入可覆写目录（支持 CORTANA_LOG_DIR 环境变量）
                        Path.Combine(logDir, "app-.log"),
                        restrictedToMinimumLevel: fileMinimumLevel,
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
            .AddSingleton<Netor.Cortana.AI.Handoff.IWorkModeSwitcher>(sp => sp.GetRequiredService<MainWindow>())
            .AddSingleton<FloatWindow>()
            .AddSingleton<BubbleWindow>()
            .AddSingleton<SettingsWindow>()
            .AddTransient<AboutWindow>()
            .AddSingleton<ProxyViewModel>()
            .AddSingleton<ProxyWindow>()
            // 界面重设计 C2：主窗口 VM + 草稿暂存服务（决策 UI-3 + UI-7）。
            // ChatDraftService 必须 Singleton（跨模式切换时复用同一实例）。
            // MainWindowVm 也 Singleton（与 MainWindow 单例对齐）。
            // 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §2.2。
            .AddSingleton<Services.ChatDraftService>()
            .AddSingleton<ViewModels.Shared.MainWindowVm>()
            // 界面重设计 C3：左侧面板 VM（决策 UI-1 L2 + UI-9）。Singleton 与 LeftPanel 单例对齐。
            // 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §3。
            .AddSingleton<ViewModels.Shared.LeftPanelVm>()
            .AddSingleton<ViewModels.ExpertMode.ChatInputVm>()
            .AddSingleton<ViewModels.WorkModeVm.WorkModeInputVm>()
            .AddSingleton<ViewModels.Meeting.MeetingInputVm>()
            // 数据库
            .AddSingleton<CortanaDbContext>()
            .AddTransient<SystemSettingsService>()
            .AddTransient<LegacyDbImporter>()
            .AddTransient<AgentService>()
            .AddSingleton<AgentManifestSerializer>()
            .AddSingleton<AgentManifestValidator>()
            .AddSingleton<AgentFileIndex>()
            .AddSingleton<AgentFileWatcher>()
            .AddSingleton<AgentFileService>()
            .AddSingleton<AgentSeedFromFactoryService>()
            .AddTransient<GlobalPluginService>()
            .AddTransient<AiProviderService>()
            .AddTransient<AiModelService>()
            .AddTransient<ChatMessageService>()
            .AddTransient<ChatMessageAssetService>()
            .AddTransient<CompactionSegmentService>()
            .AddTransient<McpServerService>()
            .AddTransient<WorkTaskService>()
            .AddTransient<WorkExecutionLogService>()
            .AddTransient<WorkPendingInputService>()
            .AddTransient<WorkPlanTemplateService>()
            .AddTransient<WorkBackgroundJobsService>()
            .AddTransient<DelegatedAgentJobService>()
            .AddTransient<WorkTaskContextService>()
            .AddTransient<WorkTaskEventService>()
            .AddTransient<WorkTaskFileService>()
            .AddTransient<IProjectStepDispatcher, ProjectStepDispatcher>()
            .AddTransient<ProjectLeadService>()
            .AddTransient<MeetingSessionService>()
            .AddTransient<MeetingMessageService>()
            .AddTransient<MeetingPendingInputService>()
            .AddTransient<MeetingAttachmentService>()
            .AddTransient<MeetingCompactionSegmentService>()
            // 跨层契约
            .AddSingleton<IAppPaths, AppPaths>()
            .AddSingleton<IWindowController, WindowController>()
            .AddSingleton<IAssetInstallTargetResolver, StoreAssetInstallTargetResolver>()
            .AddSingleton<IPluginActivationService, StorePluginActivationService>()
            .AddSingleton<IPlatformBaseUrlProvider, StorePlatformBaseUrlProvider>()
            // UI 输出通道：将 AI 流式回复渲染到 MainWindow
            .AddSingleton<UiChatOutputChannel>()
            .AddSingleton<IAiOutputChannel>(sp => sp.GetRequiredService<UiChatOutputChannel>())
            .AddSingleton<IRealtimeProcessOutput, NonExpertSuppressingRealtimeOutput>()
            // AI 工具提供者
            .AddSingleton<AIContextProvider, Providers.WindowToolProvider>()
            .AddSingleton<AIContextProvider, Providers.AiConfigToolProvider>()
            .AddSingleton<AIContextProvider, Providers.PluginManagementProvider>()
            // 业务模块
            .AddCortanaVoice()
            .AddCortanaAI()
            .AddCortanaPlugin()
            .AddCortanaStore()
            .AddCortanaNetworks();

        Services = services.BuildServiceProvider();
    }

    private static LogEventLevel ResolveFileLogMinimumLevel(string logDir)
    {
        var environmentValue = Environment.GetEnvironmentVariable("CORTANA_FILE_LOG_MINIMUM_LEVEL");
        if (TryParseLogEventLevel(environmentValue, out var environmentLevel))
        {
            return environmentLevel;
        }

        try
        {
            using var db = new CortanaDbContext();
            var raw = db.ExecuteScalar<string>(
                "SELECT Value FROM SystemSettings WHERE Id = @Id",
                cmd => cmd.Parameters.AddWithValue("@Id", "Logging.File.MinimumLevel"));

            if (TryParseLogEventLevel(raw, out var settingLevel))
            {
                return settingLevel;
            }
        }
        catch (Exception ex)
        {
            try
            {
                var fallbackPath = Path.Combine(logDir, "logging-bootstrap-error.log");
                File.AppendAllText(fallbackPath, $"{DateTimeOffset.Now:O} 读取日志级别设置失败：{ex}\n");
            }
            catch
            {
                // ignore bootstrap logging failure
            }
        }

        return LogEventLevel.Warning;
    }

    private static bool TryParseLogEventLevel(string? value, out LogEventLevel level)
    {
        level = LogEventLevel.Warning;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (string.Equals(normalized, "Warn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "WRN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Warm", StringComparison.OrdinalIgnoreCase))
        {
            level = LogEventLevel.Warning;
            return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out level);
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
        if (!Directory.Exists(UserAgentsDirectory))
            Directory.CreateDirectory(UserAgentsDirectory);
        if (!Directory.Exists(UserSolutionsDirectory))
            Directory.CreateDirectory(UserSolutionsDirectory);
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
        sysSettings.EnsurePlatformSettings();

        Services.GetRequiredService<LegacyDbImporter>().EnsureImported();

        var agentSeedFromFactoryService = Services.GetRequiredService<AgentSeedFromFactoryService>();
        agentSeedFromFactoryService.EnsureSeedAgents();

        var agentFileService = Services.GetRequiredService<AgentFileService>();
        agentFileService.RebuildIndex();
        var agentFileWatcher = Services.GetRequiredService<AgentFileWatcher>();
        var toolContextVersionService = Services.GetRequiredService<ToolContextVersionService>();
        agentFileWatcher.AgentChanged += (_, agentName) =>
        {
            var version = toolContextVersionService.Bump();
            Log.Information("Agent 文件 {AgentName} 已变更，工具上下文版本推进到 {Version}", agentName, version);
        };
        agentFileWatcher.Start(agentFileService.AgentsDirectory);

        // 版本迁移：为已有数据库补充新增设置项
        sysSettings.EnsureSetting("Agent.DefaultName",
            group: "Agent", displayName: "默认智能体",
            description: "启动新会话或未显式选择智能体时使用的 Agent 名称。",
            defaultValue: "default", valueType: "string", sortOrder: 0);

        sysSettings.EnsureSetting("WebSocket.Port",
            group: "网络", displayName: "服务端口",
            description: "统一 WebSocket 服务监听端口。聊天、插件总线、记忆和模型能力均通过同一端口的 /internal 端点按协议字段区分。修改后重启软件生效。",
            defaultValue: "52841", valueType: "int", sortOrder: 0);

        sysSettings.EnsureSetting("Voice.Kws.Enabled",
            group: "语音服务", displayName: "使用插件关键词唤醒",
            description: "开启后使用已安装的 voice.kws 插件提供关键词唤醒；未安装或关闭时默认软件不提供语音唤醒。",
            defaultValue: "false", valueType: "bool", sortOrder: 0);

        sysSettings.EnsureSetting("Voice.Kws.PluginId",
            group: "语音服务", displayName: "关键词唤醒插件",
            description: "选择当前使用的 KWS 插件，留空表示自动选择。修改后重启软件生效。",
            defaultValue: "", valueType: "voicePlugin:kws", sortOrder: 1);

        sysSettings.EnsureSetting("Voice.Stt.Enabled",
            group: "语音服务", displayName: "使用插件语音识别",
            description: "开启后使用已安装的 voice.stt 插件提供语音识别；未安装或关闭时默认软件不提供语音输入。",
            defaultValue: "false", valueType: "bool", sortOrder: 2);

        sysSettings.EnsureSetting("Voice.Stt.PluginId",
            group: "语音服务", displayName: "语音识别插件",
            description: "选择当前使用的 STT 插件，留空表示自动选择。修改后重启软件生效。",
            defaultValue: "", valueType: "voicePlugin:stt", sortOrder: 3);

        sysSettings.EnsureSetting("Voice.Tts.Enabled",
            group: "语音服务", displayName: "使用插件语音合成",
            description: "开启后使用已安装的 voice.tts 插件提供语音合成；未安装或关闭时默认软件不朗读 AI 回复。",
            defaultValue: "false", valueType: "bool", sortOrder: 4);

        sysSettings.EnsureSetting("Voice.Tts.PluginId",
            group: "语音服务", displayName: "语音合成插件",
            description: "选择当前使用的 TTS 插件，留空表示自动选择。修改后重启软件生效。",
            defaultValue: "", valueType: "voicePlugin:tts", sortOrder: 5);

        NormalizeLegacyVoicePluginDefault(sysSettings, "Voice.Kws.Enabled", "Voice.Kws.PluginId");
        NormalizeLegacyVoicePluginDefault(sysSettings, "Voice.Stt.Enabled", "Voice.Stt.PluginId");
        NormalizeLegacyVoicePluginDefault(sysSettings, "Voice.Tts.Enabled", "Voice.Tts.PluginId");

        sysSettings.EnsureSetting(FloatWindowEnabledKey,
            group: "界面", displayName: "悬浮球开关",
            description: "开启后显示桌面悬浮球；关闭后隐藏悬浮球和悬浮字幕气泡。",
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

        sysSettings.EnsureSetting("Meeting.Compaction.SegmentSize",
            group: "会议模式", displayName: "会议压缩段落大小",
            description: "会议模式每多少条完整消息生成一个压缩摘要段落。",
            defaultValue: "30", valueType: "int", sortOrder: 0);

        sysSettings.EnsureSetting("Meeting.Compaction.RawTailSize",
            group: "会议模式", displayName: "会议尾部原始消息数",
            description: "会议模式保留最近多少条原始消息不压缩，确保主持人看到完整近期上下文。",
            defaultValue: "20", valueType: "int", sortOrder: 1);

        sysSettings.EnsureSetting("Meeting.Compaction.MaxDisplaySegments",
            group: "会议模式", displayName: "会议最大摘要段数",
            description: "会议 LLM 上下文最多携带多少个摘要段，超出的旧段落不再加载但不删除。",
            defaultValue: "10", valueType: "int", sortOrder: 2);

        sysSettings.EnsureSetting("AI.Trace.Enabled",
            group: "调试", displayName: "AI 全量调试日志",
            description: "记录 AI 请求、流式更新、响应和异常的完整调试日志。发布版默认关闭，开启后可用于排查工具调用与上下文问题。",
            defaultValue: "false", valueType: "bool", sortOrder: 0);
        sysSettings.EnsureSetting("AI.Provider.Kimi.MaxTools",
            group: "AI", displayName: "Kimi 最大工具数量",
            description: "Kimi 当前工具数量上限。默认 128；设置为 0 表示不限制，用于兼容 Kimi 后续放开限制的情况。环境变量 CORTANA_KIMI_MAX_TOOLS 优先于此设置。",
            defaultValue: "128", valueType: "int", sortOrder: 20);

        sysSettings.EnsureSetting("Logging.File.MinimumLevel",
            group: "日志", displayName: "文件日志最小级别",
            description: "写入 app 日志文件的最小日志级别。选择 Warning 时会记录 Warning、Error、Critical；选择 Information 时会额外记录普通运行信息。修改后重启应用生效。",
            defaultValue: "Warning", valueType: "logLevel", sortOrder: 0);

        // 阶段 5B Phase 4：Magentic 成本警告阈值 + 估算系数
        // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.4 / 04 §4B.6。
        sysSettings.EnsureSetting("Workflow.Magentic.CostWarningThreshold",
            group: "工作模式", displayName: "Magentic 成本警告阈值",
            description: "新建 Magentic 任务时若预估 token 消耗 ≥ 此阈值，会在 NewTaskDialog 上弹黄色警告 banner。设为 0 表示禁用警告。",
            defaultValue: "100000", valueType: "int", sortOrder: 0);

        sysSettings.EnsureSetting("Workflow.Magentic.EstimatedTokenMultiplier",
            group: "工作模式", displayName: "Magentic 单轮 token 系数",
            description: "估算公式：MaxRounds × 此系数 × 参与者数量。默认 2000 = 假设每个参与者每轮平均消耗约 2k token（含 manager 反思 + 子 agent 输出）。",
            defaultValue: "2000", valueType: "int", sortOrder: 1);

        // v1.3: Ollama 兼容代理配置，供 ProxyWindow 设置页和网络代理服务读取。
        sysSettings.EnsureOllamaProxySettings();
        // 版本迁移：移除已废弃的旧配置项
        sysSettings.DeleteSetting("ChatHistory.MaxContentLength");
        sysSettings.DeleteSetting("ChatHistory.MaxContentCount");
        sysSettings.DeleteSetting("Memory.ModelId");
        sysSettings.DeleteSetting("PluginBus.Port");
        sysSettings.DeleteSetting("Voice.WakeWordEnabled");

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
        var floatItem = new NativeMenuItem();
        UpdateFloatWindowMenuItemHeader(floatItem, IsFloatWindowEnabled());
        floatItem.Click += (_, _) =>
        {
            var enabled = !IsFloatWindowEnabled();
            Services.GetRequiredService<SystemSettingsService>()
                .SetValue(FloatWindowEnabledKey, enabled.ToString().ToLowerInvariant());
            UpdateFloatWindowMenuItemHeader(floatItem, enabled);

            if (enabled)
            {
                _floatWindow?.Show();
                _floatWindow?.Activate();
            }
            else
            {
                _bubbleWindow?.Hide();
                _floatWindow?.Hide();
            }
        };

        var exitItem = new NativeMenuItem("退出助理");
        exitItem.Click += (_, _) =>
        {
            _ = ShutdownFromTrayAsync(desktop);
        };

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(floatItem);
        menu.Items.Add(proxyItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        var iconStream = AssetLoader.Open(new Uri(AppBranding.AssetUri(AppBranding.ApplicationIconPath)));

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = $"{AppName} · {AppBranding.AssistantSubtitle}",
            Menu = menu,
            IsVisible = true,
        };

        _trayIcon.Clicked += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
        };
    }

    /// <summary>
    /// 根据悬浮球状态刷新托盘菜单项文本。
    /// </summary>
    private static void UpdateFloatWindowMenuItemHeader(NativeMenuItem item, bool enabled)
    {
        item.Header = enabled ? "悬浮球：开" : "悬浮球：关";
    }

    internal static bool IsFloatWindowEnabled()
    {
        return Services.GetRequiredService<SystemSettingsService>()
            .GetValue(FloatWindowEnabledKey, true);
    }

    /// <summary>
    /// 将旧版本默认开启的语音插件覆盖迁移为默认关闭，保留已显式选择插件的用户配置。
    /// </summary>
    private static void NormalizeLegacyVoicePluginDefault(
        SystemSettingsService settings,
        string enabledKey,
        string pluginIdKey)
    {
        var enabled = settings.GetValue(enabledKey, false);
        var pluginId = settings.GetValue(pluginIdKey, string.Empty);
        if (enabled && string.IsNullOrWhiteSpace(pluginId))
        {
            settings.SetValue(enabledKey, "false");
        }
    }

    /// <summary>
    /// 托盘菜单触发的退出入口。先释放后台资源，再关闭 Avalonia，避免插件子进程驻留。
    /// </summary>
    private async Task ShutdownFromTrayAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await ShutdownFromUiAsync(desktop);
    }

    /// <summary>
    /// UI 入口触发的完整退出流程。
    /// </summary>
    internal static async Task ShutdownFromUiAsync(IClassicDesktopStyleApplicationLifetime? desktop = null)
    {
        if (_isShuttingDown) return;
        var mainWindow = desktop?.MainWindow as MainWindow ?? Services.GetService<MainWindow>();
        try
        {
            await ShutdownApplicationAsync();
        }
        catch (Exception ex)
        {
            var logger = Services.GetRequiredService<ILogger<App>>();
            logger?.LogError(ex, "退出过程中发生异常");
        }

        mainWindow?.ForceClose();

        if (Current is App app)
        {
            app._trayIcon?.Dispose();
            app._trayIcon = null;
        }

        desktop ??= Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        desktop?.Shutdown();
        Environment.Exit(0);
    }

    /// <summary>
    /// 统一退出流程：取消业务任务、卸载插件/MCP、停止托管服务、释放 DI 容器。
    /// </summary>
    private static async Task ShutdownApplicationAsync()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        Services.GetService<MainWindow>()?.SaveCurrentPlacement();

        try { _cts.Cancel(); } catch { }
        await RunShutdownStepAsync(UnloadPluginsAsync(), "卸载插件/MCP 系统");
        await RunShutdownStepAsync(StopBackgroundServicesAsync(CancellationToken.None), "停止后台服务");

        if (Services is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// 执行单个退出步骤，并在超时后记录警告继续后续退出流程。
    /// </summary>
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

            if (!mainWindow.IsVisible && IsFloatWindowEnabled())
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

            if (!mainWindow.IsVisible && IsFloatWindowEnabled())
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
            var pluginBusServer = Services.GetRequiredService<WebSocketPluginBusServerService>();
            var pluginLoader = Services.GetRequiredService<PluginLoader>();
            pluginLoader.PluginBusPort = pluginBusServer.Port;

            if (pluginLoader.PluginBusPort <= 0)
            {
                logger.LogWarning("PluginBus 端口未初始化，插件可能无法连接宿主：pluginBus={PluginBusPort}", pluginLoader.PluginBusPort);
            }

            await pluginLoader.ScanAndLoadAsync(cancellationToken);
            pluginLoader.StartWatching();

            var mcpService = Services.GetRequiredService<McpServerService>();
            await pluginLoader.LoadMcpServersAsync(mcpService, cancellationToken);
            await InitializeVoicePluginsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "插件/MCP 系统启动失败");
        }
    }

    private static async Task InitializeVoicePluginsAsync(CancellationToken cancellationToken)
    {
        var logger = Services.GetRequiredService<ILogger<App>>();
        var kwsPluginAdapter = Services.GetRequiredService<KwsPluginAdapter>();
        var kwsConfigure = await kwsPluginAdapter.ConfigureAsync(cancellationToken);
        if (!kwsConfigure.Ok && kwsConfigure.Code != "skipped")
        {
            logger.LogWarning("KWS 插件配置初始化失败：{Code} {Message}", kwsConfigure.Code, kwsConfigure.Message);
        }

        var sttPluginAdapter = Services.GetRequiredService<SttPluginAdapter>();
        var sttConfigure = await sttPluginAdapter.ConfigureAsync(cancellationToken);
        if (!sttConfigure.Ok && sttConfigure.Code != "skipped")
        {
            logger.LogWarning("STT 插件配置初始化失败：{Code} {Message}", sttConfigure.Code, sttConfigure.Message);
        }

        // KWS 与 STT 都独占麦克风，启动顺序由 VoicePipelineCoordinator 编排：
        // 启动后先开 KWS，唤醒命中再切到 STT，STT 结束/TTS 播报完成再切回 KWS。
        // 这里只配置，不再各自 StartAsync。

        var ttsPluginAdapter = Services.GetRequiredService<TtsPluginAdapter>();
        var configure = await ttsPluginAdapter.ConfigureAsync(cancellationToken);
        if (!configure.Ok && configure.Code != "skipped")
        {
            logger.LogWarning("TTS 插件配置初始化失败：{Code} {Message}", configure.Code, configure.Message);
            return;
        }

        var greeting = await ttsPluginAdapter.RegenerateGreetingAsync(cancellationToken);
        if (!greeting.Ok && greeting.Code != "skipped")
        {
            logger.LogWarning("TTS 插件欢迎语初始化失败：{Code} {Message}", greeting.Code, greeting.Message);
        }

        // 所有语音插件配置就绪后再启动协调器，由它来控制 KWS↔STT 的麦克风切换。
        var pipelineCoordinator = Services.GetRequiredService<VoicePipelineCoordinator>();
        await pipelineCoordinator.StartAsync(cancellationToken);
    }

    /// <summary>
    /// 退出时显式卸载插件、断开 MCP，并杀掉进程通道插件子进程。
    /// </summary>
    private static async Task UnloadPluginsAsync()
    {
        try
        {
            var pipelineCoordinator = Services.GetService<VoicePipelineCoordinator>();
            if (pipelineCoordinator is not null)
            {
                try { await pipelineCoordinator.StopAsync(CancellationToken.None); }
                catch (Exception ex)
                {
                    Services.GetService<ILogger<App>>()?.LogError(ex, "停止语音流水线协调器失败");
                }
            }

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
