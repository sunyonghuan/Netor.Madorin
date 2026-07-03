namespace Netor.Cortana.Entitys;

/// <summary>
/// 工作模式任务实体（WorkTasks 表）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/10-数据模型与持久化设计.md §3.1。
/// </summary>
/// <remarks>
/// 不继承 <see cref="BaseEntity"/>：工作模式表使用 CreatedAt / UpdatedAt（Unix 毫秒）而非
/// 基类的 CreatedTimestamp / UpdatedTimestamp，命名与表 schema 严格对齐。
/// </remarks>
public sealed class WorkTaskEntity
{
    /// <summary>任务 ID（GUID(N)）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联的 ChatSessions.Id。工作模式与对话模式共享 Session。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>workspace MD5（与 ChatSession.Categorize 一致）。</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>任务标题（AI 生成或用户手输）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>触发任务的第一条用户输入（用于回看）。</summary>
    public string InitialInput { get; set; } = string.Empty;

    /// <summary>AF StreamingRun.RunId，恢复时必需。</summary>
    public string? RunId { get; set; }

    /// <summary>A 层总经理当前 RunId，用于 B 层事件回灌。</summary>
    public string? ManagerRunId { get; set; }

    /// <summary>1 = 活跃（含运行/HITL 等待），0 = 终结。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>当前计划的 JSON 快照（WorkPlan，覆盖式，不做版本管理）。</summary>
    public string? CurrentPlanJson { get; set; }

    /// <summary>HITL 挂起的 RequestInfo.RequestId。</summary>
    public string? PendingRequestId { get; set; }

    /// <summary>挂起请求类型："approval" / "ask_user"。</summary>
    public string? PendingRequestKind { get; set; }

    /// <summary>请求载荷的 JSON 快照（用于 UI 重建授权浮窗）。</summary>
    public string? PendingRequestData { get; set; }

    /// <summary>任务完成时间（Unix 毫秒）。</summary>
    public long? CompletedAt { get; set; }

    /// <summary>final_report 工具的输出。</summary>
    public string? FinalReport { get; set; }

    /// <summary>失败原因。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>任务启动时锁定的 AiProviders.Id。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>任务启动时锁定的 AiModels.Id。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>任务的总经理 Agent 文件名（manifest.name）。</summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>任务启动时 @ 的子智能体列表 JSON。</summary>
    public string? MentionsJson { get; set; }

    /// <summary>"再做一份"复用上一任务时的来源任务 ID。</summary>
    public string? SourceTaskId { get; set; }

    /// <summary>嵌套子工作流：指向父任务（v1.0 嵌套深度上限 2）。</summary>
    public string? ParentTaskId { get; set; }

    /// <summary>应用重启检测：上次未正常关闭。</summary>
    public bool IsOrphaned { get; set; }

    /// <summary>标记为孤儿的时刻（Unix 毫秒）。</summary>
    public long? OrphanedDetectedAt { get; set; }

    /// <summary>最近一次活动时间（用于死循环检测）。</summary>
    public long? HeartbeatAt { get; set; }

    /// <summary>B 层项目组长状态：idle/running/paused/done/failed/cancelled。</summary>
    public string? OrchestratorState { get; set; }

    /// <summary>B 层项目组长最近心跳（Unix 毫秒）。</summary>
    public long? OrchestratorHeartbeatAt { get; set; }

    /// <summary>用户输入了"立刻停"等抢占性指令。</summary>
    public bool HasPreemption { get; set; }

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>最后更新时间（Unix 毫秒）。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>用于"当前会话最近活跃任务"查询。</summary>
    public long LastActiveAt { get; set; }
}
