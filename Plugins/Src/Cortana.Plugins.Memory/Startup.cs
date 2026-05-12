using Microsoft.Extensions.DependencyInjection;
using Cortana.Plugins.Memory.Processing;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Storage;
using Cortana.Plugins.Memory.ToolHandlers;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Memory;

/// <summary>
/// 记忆引擎插件入口：注册后台摄取服务，订阅宿主内部对话 feed。
/// </summary>
[Plugin(
    Id = "memory_engine",
    Name = "增强记忆引擎",
    Version = "1.0.19",
    Description = "订阅宿主内部对话事实流，为长期记忆构建做摄取与预处理。",
    Tags = ["memory", "ingest", "conversation-feed"],
    Instructions = """
        记忆工具使用指引：
        【何时写入记忆】当用户明确说"记住这个"、"帮我记下"、"加入长期记忆"时，调用 memory_add_note 写入。不要在用户未授权时静默写入。
        【何时召回记忆】当用户提到"之前说过"、"上次聊的"、"你还记得吗"、"我的偏好"等回忆性表述时，调用 memory_recall 查询相关记忆。当你不确定用户的偏好或历史上下文时也可主动召回。
        【何时查看记忆】当用户问"你记住了什么"、"最近记了什么"、"记忆状态"时，调用 memory_list_recent 或 memory_get_status。
        【何时删除记忆】当用户说"忘掉这个"、"删除那条记忆"、"不要再记这个"时，先用 memory_list_recent 找到对应记忆 ID，再调用 memory_delete。
        【何时调整配置】当用户要求调整记忆系统行为（如"记忆太多了"、"召回不够精准"、"关闭记忆供应"）时，调用 memory_get_settings 查看后用 memory_update_setting 修改。
        【自动行为】记忆的采集、提取、衰减和供应均为后台自动完成，无需 AI 主动触发。memory_trigger_processing 仅用于调试。
        【注意事项】不要在每轮对话都调用记忆工具，只在上述触发条件满足时使用。记忆供应（memory_supply_context）由系统自动注入，通常不需要 AI 手动调用。
        """)]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<IMemoryRuntimeContext>(_ => new MemoryRuntimeContext("default", null, "plugin"));
        services.AddMemoryStorage();
        services.AddSingleton<IMemorySettingsService, MemorySettingsService>();
        services.AddSingleton<IMemoryRecallService, MemoryRecallService>();
        services.AddSingleton<IMemorySupplyService, MemorySupplyService>();
        services.AddSingleton<MemorySupplyControlHandler>();
        services.AddSingleton<IMemoryStatusService, MemoryStatusService>();
        services.AddSingleton<IMemoryNoteService, MemoryNoteService>();
        services.AddSingleton<IMemoryRecentService, MemoryRecentService>();
        services.AddSingleton<IMemoryObservationWriter, MemoryObservationWriter>();
        services.AddSingleton<IMemoryReadToolHandler, MemoryReadToolHandler>();
        services.AddSingleton<IMemoryWriteToolHandler, MemoryWriteToolHandler>();
        services.AddSingleton<HostModelCapabilityClient>();
        services.AddSingleton<FallbackMemorySemanticProcessor>();
        services.AddSingleton<IMemorySemanticProcessor, HostModelMemorySemanticProcessor>();
        services.AddSingleton<IMemoryProcessingService, MemoryProcessingService>();
        services.AddSingleton<IMemoryAbstractionGenerator, HostModelMemoryAbstractionGenerator>();
        services.AddSingleton<IMemoryAbstractionService, MemoryAbstractionService>();
        services.AddHostedService<Services.MemoryIngestService>();
        services.AddHostedService<MemoryProcessingHostedService>();
    }
}
