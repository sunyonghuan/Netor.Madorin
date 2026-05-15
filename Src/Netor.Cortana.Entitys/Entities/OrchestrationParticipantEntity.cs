using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// Workflow 任务参与者实体（OrchestrationParticipant 表）。
    /// 一个 OrchestrationTask 关联多个 Participant（manager / member / moderator）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §3.2。
    /// </summary>
    /// <remarks>
    /// 主键采用 AUTOINCREMENT 整型（与 BaseEntity 不同），因为参与者本身是任务的 N:1 关联，
    /// 不需要全局唯一 GUID；查询主要按 (TaskId) 或 (TaskId, AgentId) 进行。
    /// </remarks>
    public class OrchestrationParticipantEntity
    {
        /// <summary>自增主键。</summary>
        public long Id { get; set; }

        /// <summary>所属任务 ID，关联 <see cref="OrchestrationTaskEntity.Id"/>。</summary>
        [MaxLength(64)]
        public string TaskId { get; set; } = string.Empty;

        /// <summary>参与的智能体 ID，关联 <see cref="AgentEntity.Id"/>。</summary>
        [MaxLength(64)]
        public string AgentId { get; set; } = string.Empty;

        /// <summary>智能体显示名称（冗余存储，便于审计）。</summary>
        [MaxLength(128)]
        public string AgentName { get; set; } = string.Empty;

        /// <summary>角色 "manager" / "member" / "moderator"。</summary>
        [MaxLength(32)]
        public string Role { get; set; } = string.Empty;

        /// <summary>加入任务的时间戳（Unix ms）。</summary>
        public long JoinedAt { get; set; }
    }
}
