using Cortana.Plugins.Memory.Mcp;
using Cortana.Plugins.Memory.Processing;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Storage;
using Cortana.Plugins.Memory.ToolHandlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;

namespace Cortana.Plugins.Memory;

/// <summary>
/// 记忆插件的 MCP 控制台入口。
/// 仅在使用 <c>-p:OutputType=Exe</c> 发布时编译；插件库分发不会包含本类型。
/// 通过标准输入输出（stdio）暴露 <see cref="Mcp.MemoryMcpTools"/> 中声明的工具，
/// 业务规则全部复用 <see cref="ToolHandlers"/> 层，不重复实现。
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // MCP stdio 模式下，stdout 仅用于协议帧。日志必须输出到 stderr。
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // 复用插件相同的服务注册，保证 MCP 与插件行为一致。
        builder.Services.AddSingleton(LoadPluginSettings(args));
        builder.Services.AddMemoryStorage();
        builder.Services.AddSingleton<IMemorySettingsService, MemorySettingsService>();
        builder.Services.AddSingleton<IMemoryRecallService, MemoryRecallService>();
        builder.Services.AddSingleton<IMemorySupplyService, MemorySupplyService>();
        builder.Services.AddSingleton<IMemoryStatusService, MemoryStatusService>();
        builder.Services.AddSingleton<IMemoryNoteService, MemoryNoteService>();
        builder.Services.AddSingleton<IMemoryRecentService, MemoryRecentService>();
        builder.Services.AddSingleton<IMemoryReadToolHandler, MemoryReadToolHandler>();
        builder.Services.AddSingleton<IMemoryWriteToolHandler, MemoryWriteToolHandler>();
        builder.Services.AddSingleton<IMemorySemanticProcessor, FallbackMemorySemanticProcessor>();
        builder.Services.AddSingleton<IMemoryProcessingService, MemoryProcessingService>();
        builder.Services.AddSingleton<IMemoryAbstractionGenerator, FallbackMemoryAbstractionGenerator>();
        builder.Services.AddSingleton<IMemoryAbstractionService, MemoryAbstractionService>();
        builder.Services.AddHostedService<MemoryProcessingHostedService>();
        // 说明：MemoryIngestService 依赖宿主内部 conversation-feed，
        // MCP 独立运行模式默认不订阅；若后续需要请按需开启。

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<MemoryMcpTools>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 解析 MCP 模式下的 <see cref="PluginSettings"/>。
    /// 优先级：<c>--config &lt;path&gt;</c> 命令行参数 &gt; <c>CORTANA_PLUGIN_CONFIG</c> 环境变量 &gt; 空配置。
    /// 在 MCP 模式下宿主无法注入配置，必须由本入口自行构造。
    /// </summary>
    private static PluginSettings LoadPluginSettings(string[] args)
    {
        string? configJson = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
            {
                var path = args[i + 1];
                if (File.Exists(path))
                {
                    configJson = File.ReadAllText(path);
                }
                break;
            }
        }

        if (configJson is null)
        {
            var envJson = Environment.GetEnvironmentVariable("CORTANA_PLUGIN_CONFIG");
            if (!string.IsNullOrWhiteSpace(envJson))
            {
                configJson = envJson;
            }
        }

        return PluginSettings.FromJson(configJson ?? "{}");
    }
}
