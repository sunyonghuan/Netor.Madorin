using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// Workflow 任务步骤实体（OrchestrationStep 表）。
    /// 一个任务由多个步骤组成；步骤支持嵌套（ParentStepId 指向父步骤，用于 Magentic 重规划场景）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §3.3。
    /// </summary>
    /// <remarks>
    /// 主键采用 GUID（TEXT），便于跨进程引用和事件转发。
    /// 与 BaseEntity 不同：本表不需要全局 CreatedTimestamp / UpdatedTimestamp，
    /// 使用 StartedAt / CompletedAt 表达步骤生命周期。
    /// </remarks>
    public class OrchestrationStepEntity
    {
        /// <summary>步骤主键（GUID）。</summary>
        [MaxLength(64)]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>所属任务 ID，关联 <see cref="OrchestrationTaskEntity.Id"/>。</summary>
        [MaxLength(64)]
        public string TaskId { get; set; } = string.Empty;

        /// <summary>父步骤 ID，用于嵌套步骤（如 Magentic 重规划下的子步骤），无父时为 null。</summary>
        [MaxLength(64)]
        public string? ParentStepId { get; set; }

        /// <summary>任务内排序序号（与 TaskId 组合作为查询索引）。</summary>
        public int Sequence { get; set; }

        /// <summary>执行该步骤的智能体 ID（如适用），关联 <see cref="AgentEntity.Id"/>。</summary>
        [MaxLength(64)]
        public string? AgentId { get; set; }

        /// <summary>执行智能体的显示名称（冗余存储，便于审计）。</summary>
        [MaxLength(128)]
        public string? AgentName { get; set; }

        /// <summary>动作类型 "plan" / "replan" / "execute" / "review" / "summarize" / "vote"。</summary>
        [MaxLength(32)]
        public string Action { get; set; } = string.Empty;

        /// <summary>状态 "running" / "completed" / "failed" / "skipped"。</summary>
        [MaxLength(32)]
        public string Status { get; set; } = string.Empty;

        /// <summary>步骤启动时间（Unix ms）。</summary>
        public long StartedAt { get; set; }

        /// <summary>步骤完成时间（Unix ms），未完成时为 null。</summary>
        public long? CompletedAt { get; set; }

        /// <summary>步骤耗时（毫秒），未完成时为 null。</summary>
        public long? DurationMs { get; set; }

        /// <summary>本步骤输入 token 数（如可统计）。</summary>
        public int? TokenInputCount { get; set; }

        /// <summary>本步骤输出 token 数（如可统计）。</summary>
        public int? TokenOutputCount { get; set; }

        /// <summary>失败时的错误摘要。</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 步骤摘要（JSON），不存完整内容。完整内容在 <see cref="OrchestrationMessageEntity"/> 中。
        /// 用于步骤级检索（如"显示所有 plan 步骤的摘要"）。
        /// </summary>
        public string? SummaryJson { get; set; }
    }
}
