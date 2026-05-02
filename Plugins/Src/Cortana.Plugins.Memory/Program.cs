using Cortana.Plugins.Memory.Mcp;
using Cortana.Plugins.Memory.Processing;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Storage;
using Cortana.Plugins.Memory.ToolHandlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        var runtimeOptions = MemoryMcpRuntimeOptionsLoader.Load(args);

        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton<IMemoryRuntimeContext>(_ => new MemoryRuntimeContext(
            runtimeOptions.DefaultAgentId,
            runtimeOptions.DefaultWorkspaceId,
            runtimeOptions.DefaultSource));
        builder.Services.AddSingleton<IMemoryDatabaseOptions>(_ => new MemoryDatabaseOptions(
            runtimeOptions.DataDirectory,
            runtimeOptions.DatabaseFileName));
        builder.Services.AddMemoryStorage();
        builder.Services.AddSingleton<IMemorySettingsService, MemorySettingsService>();
        builder.Services.AddSingleton<IMemoryRecallService, MemoryRecallService>();
        builder.Services.AddSingleton<IMemorySupplyService, MemorySupplyService>();
        builder.Services.AddSingleton<IMemoryStatusService, MemoryStatusService>();
        builder.Services.AddSingleton<IMemoryNoteService, MemoryNoteService>();
        builder.Services.AddSingleton<IMemoryRecentService, MemoryRecentService>();
        builder.Services.AddSingleton<IMemoryObservationWriter, MemoryObservationWriter>();
        builder.Services.AddSingleton<IMemoryReadToolHandler, MemoryReadToolHandler>();
        builder.Services.AddSingleton<IMemoryWriteToolHandler, MemoryWriteToolHandler>();
        builder.Services.AddSingleton<IMemoryMcpToolHandler, MemoryMcpToolHandler>();
        builder.Services.AddSingleton<IMemorySemanticProcessor, FallbackMemorySemanticProcessor>();
        builder.Services.AddSingleton<IMemoryProcessingService, MemoryProcessingService>();
        builder.Services.AddSingleton<IMemoryAbstractionGenerator, FallbackMemoryAbstractionGenerator>();
        builder.Services.AddSingleton<IMemoryAbstractionService, MemoryAbstractionService>();
        if (runtimeOptions.EnableAutoProcessing)
        {
            builder.Services.AddHostedService<MemoryProcessingHostedService>();
        }

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<MemoryMcpTools>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
    }
}
