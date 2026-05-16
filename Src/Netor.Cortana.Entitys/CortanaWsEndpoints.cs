namespace Netor.Cortana.Entitys;

/// <summary>
/// Cortana 宿主对外和对内 WebSocket 端点常量。
/// </summary>
public static class CortanaWsEndpoints
{
    public const string PluginBusPath = "/internal";
    public const string ChatPath = PluginBusPath;
    public const string PluginBusProtocol = "cortana.plugin-bus";

    /// <summary>
    /// PluginBus 协议版本号。
    /// 阶段 2B：从 1.0.0 升到 1.1.0，新增 workflow topic 与对应 operations。
    /// 阶段 5B：从 1.1.0 升到 1.2.0，subscribe 帧新增 capabilities 字段（决策 5B-D：能力声明而非强制版本号锁）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/07-事件分流与插件兼容设计.md §3.1
    /// 与 04-实施阶段.md §5B.4 / Phase 4 实施计划 §5.2。
    /// 兼容性：旧 1.0.0 / 1.1.0 插件连接到 1.2.0 宿主仍然可用（缺少 capabilities 时仅记 warning 不断连，
    /// 决策 5B-D：避免单点故障导致全部插件断连）。
    /// </summary>
    public const string PluginBusVersion = "1.2.0";

    /// <summary>
    /// 阶段 5B Phase 4 新增：宿主已知能力 token 列表，用于校验插件 subscribe 帧的 capabilities 字段。
    /// 列表为白名单，未在白名单中的能力声明会被忽略但不阻断订阅。
    /// </summary>
    public const string CapabilityWorkflowV1 = "workflow.v1";
    public const string CapabilityMemoryV1 = "memory.v1";
    public const string CapabilityConversationV1 = "conversation.v1";

    public const string ConversationTopic = "conversation";
    public const string MemoryTopic = "memory";
    public const string ModelTopic = "model";
    public const string PluginTopic = "plugin";

    /// <summary>阶段 2B 新增 topic：Workflow 任务事件总线。</summary>
    public const string WorkflowTopic = "workflow";

    public const string ConversationEventPublishOperation = "conversation.event.publish";
    public const string ConversationHistoryReplayOperation = "conversation.history.replay";
    public const string ConversationHistoryBatchOperation = "conversation.history.batch";
    public const string ConversationHistoryCompletedOperation = "conversation.history.completed";
    public const string MemoryContextSupplyRequestOperation = "memory.context.supply.request";
    public const string MemoryContextSupplyResponseOperation = "memory.context.supply.response";
    public const string MemoryContextSupplyErrorOperation = "memory.context.supply.error";
    public const string ModelCapabilityRequestOperation = "model.capability.request";
    public const string ModelCapabilityResponseOperation = "model.capability.response";

    // ──── 阶段 2B 新增 workflow operations ────
    public const string WorkflowEventPublishOperation = "workflow.event.publish";
    public const string WorkflowHistoryReplayOperation = "workflow.history.replay";
    public const string WorkflowHistoryBatchOperation = "workflow.history.batch";
    public const string WorkflowHistoryCompletedOperation = "workflow.history.completed";

    public static string BuildChatEndpoint(int port) => BuildPluginBusEndpoint(port);

    public static string BuildPluginBusEndpoint(int port) =>
        $"ws://localhost:{port}{PluginBusPath}";

    public static string BuildModelCapabilityEndpoint(int port) =>
        BuildPluginBusEndpoint(port);
}