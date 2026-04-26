using Microsoft.Extensions.DependencyInjection;
using Cortana.Plugins.Memory.Processing;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Storage;
using Cortana.Plugins.Memory.ToolHandlers;
using Netor.Cortana.Plugin;

namespace Cortana.Plugins.Memory;

/// <summary>
/// 记忆引擎插件入口：注册后台摄取服务，订阅宿主内部对话 feed。
/// </summary>
[Plugin(
    Id = "memory_engine",
    Name = "Memory Engine",
    Version = "1.0.0",
    Description = "订阅宿主内部对话事实流，为长期记忆构建做摄取与预处理。",
    Tags = ["memory", "ingest", "conversation-feed"],
    Instructions = "本插件自动在后台连接内部 conversation-feed，采集事实流用于长期记忆。")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddMemoryStorage();
        services.AddSingleton<IMemorySettingsService, MemorySettingsService>();
        services.AddSingleton<IMemoryRecallService, MemoryRecallService>();
        services.AddSingleton<IMemorySupplyService, MemorySupplyService>();
        services.AddSingleton<IMemoryStatusService, MemoryStatusService>();
        services.AddSingleton<IMemoryNoteService, MemoryNoteService>();
        services.AddSingleton<IMemoryRecentService, MemoryRecentService>();
        services.AddSingleton<IMemoryReadToolHandler, MemoryReadToolHandler>();
        services.AddSingleton<IMemoryWriteToolHandler, MemoryWriteToolHandler>();
        services.AddSingleton<IMemorySemanticProcessor, FallbackMemorySemanticProcessor>();
        services.AddSingleton<IMemoryProcessingService, MemoryProcessingService>();
        services.AddSingleton<IMemoryAbstractionGenerator, FallbackMemoryAbstractionGenerator>();
        services.AddSingleton<IMemoryAbstractionService, MemoryAbstractionService>();
        services.AddHostedService<Services.MemoryIngestService>();
        services.AddHostedService<MemoryProcessingHostedService>();
    }
}
