namespace Netor.Cortana.Entitys;

/// <summary>
/// 工作流计划模板（WorkPlanTemplates 表）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/10-数据模型与持久化设计.md §3.5。
/// </summary>
public sealed class WorkPlanTemplateEntity
{
    /// <summary>模板 ID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户可见的模板名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>模板描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>自定义分类（如"销售月报"、"代码审查"）。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>WorkPlan 的 JSON 序列化结果。</summary>
    public string PlanJson { get; set; } = string.Empty;

    /// <summary>来源任务 ID（NULL = 用户/系统直接创建）。</summary>
    public string? SourceTaskId { get; set; }

    /// <summary>来源类型："task" / "chat" / "groupchat" / "manual"。</summary>
    public string? SourceKind { get; set; }

    /// <summary>使用计数。</summary>
    public long UseCount { get; set; }

    /// <summary>最后使用时间（Unix 毫秒）。</summary>
    public long? LastUsedAt { get; set; }

    /// <summary>作用范围："user" / "workspace" / "system"。</summary>
    public string Scope { get; set; } = "user";

    /// <summary>Scope=workspace 时关联的 workspace ID。</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>最后更新时间（Unix 毫秒）。</summary>
    public long UpdatedAt { get; set; }
}
