using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 解析"压缩 / 标题生成 / 摘要等次要 LLM 用途"使用的 <see cref="IChatClient"/>。
/// 阶段 2B：从 ChatHistoryDataProvider.ResolveCompactionClient 抽出来的公共组件。
/// Chat 路径（ChatHistoryDataProvider）和 TaskEngine 路径（SubAgentRunner）都依赖本接口。
///
/// 解析顺序：
/// 1. 若 SystemSettings 配置了 Compaction.ModelId，复用 <see cref="ModelPurposeResolver"/> 缓存的 IChatClient；
/// 2. 否则回退到当前 Agent 自身的 IChatClient（agent.GetService&lt;IChatClient&gt;()）。
///
/// 调用方应当对返回的 client 用 (client as TokenTrackingChatClient)?.SuppressUsage() 隔离 usage 上报，
/// 避免次要用途的 token 消耗污染主对话进度条。
/// </summary>
public interface IChatCompactionClientResolver
{
    /// <summary>
    /// 解析压缩用 IChatClient。返回 null 表示既未配置专用模型、Agent 自身也无 IChatClient 可用，
    /// 此时调用方应放弃当前任务（不抛异常）。
    /// </summary>
    IChatClient? Resolve(AIAgent agent);
}
