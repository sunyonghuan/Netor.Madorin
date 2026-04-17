using Microsoft.Extensions.DependencyInjection;

using Serilog;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 设置高 DPI 支持（建议在 ApplicationConfiguration.Initialize() 之前调用）
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // 初始化 WinForms 应用配置
        ApplicationConfiguration.Initialize();
        App.ConfigureServices(ConfiguarServices);
        App.Configure(options =>
        {
            options.Domain = "cortana.me";
            options.Scheme = "https";
        });

        // 首次启动时将 appsettings.json 的默认值写入数据库（后续启动幂等跳过）
        var appSettings = App.Services.GetRequiredService<AppSettings>();
        var settingsService = App.Services.GetRequiredService<SystemSettingsService>();
        settingsService.EnsureSeedData(
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
            workspaceDirectory: App.UserDataDirectory);

        var agentSeedService = App.Services.GetRequiredService<AgentSeedService>();
        agentSeedService.EnsureSeedData();

        // 1. 创建构建器
        var builder = WinFormedgeApp.CreateAppBuilder();

        // 2. 链式配置
        var app = builder

#if DEBUG
            .UseDevTools()                    // 启用开发者工具
#endif

            .UseWinFormedgeApp<App>()         // 指定 AppStartup
            .Build();

        // 3. 运行应用
        app.Run();
    }

    private static void ConfiguarServices(IServiceCollection services)
    {
        // 从嵌入资源加载应用配置
        var appSettings = EmbeddedConfigurationExtensions.LoadEmbeddedJson<AppSettings>("Netor.Cortana.appsettings.json");

        services
            .AddSingleton(appSettings)
            .AddLogging(static options =>
         {
             options.AddConsole().AddDebug();
             options.AddSerilog(new LoggerConfiguration()
                 .WriteTo.File(
                     Path.Combine(App.WorkspaceDirectory, "logs", ".log"),
                     rollingInterval: RollingInterval.Day,
                     outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                 .CreateLogger(), dispose: true);
         })
            .AddEventHub()
            .AddHttpClient()
            // 窗口
            .AddSingleton<MainWindow>()
            .AddSingleton<FloatWindow>()
            .AddSingleton<WakeWordBubbleWindow>()
            .AddSingleton<SettingsWindow>()
            // 数据库交互
            .AddSingleton<CortanaDbContext>()
            .AddTransient<SystemSettingsService>()
            .AddTransient<AgentSeedService>()
            .AddTransient<AgentService>()
            .AddTransient<AiProviderService>()
            .AddTransient<AiModelService>()
            .AddTransient<ChatMessageService>()
            .AddTransient<McpServerService>()
            // UI 壳提供的跨层契约
            .AddSingleton<CortanaProvider>()
            .AddSingleton<AIContextProvider>(sp => sp.GetRequiredService<CortanaProvider>())
            .AddSingleton<IAppPaths, AppPaths>()
            .AddSingleton<IWindowController, WindowController>()
            // 各业务模块
            .AddCortanaVoice()
            .AddCortanaAI()
            .AddCortanaPlugin()
            .AddCortanaNetworks();
    }
}