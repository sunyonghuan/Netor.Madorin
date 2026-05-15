using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// Workflow 任务消息实体（OrchestrationMessage 表）。
    /// 一个 OrchestrationStep 关联多条 Message（如 Magentic Manager 一轮内发 facts/plan 多条）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §3.4。
    /// </summary>
    /// <remarks>
    /// 主键采用 AUTOINCREMENT 整型；定序使用 (TaskId, StepId, Sequence) 三元组，
    /// 不依赖时间戳归一化（避免 [05] §风险 3 的并发问题）。
    /// 阶段 2B 占位实现不写本表；阶段 3B 起按 GroupChat / Magentic 实际写入。
    /// </remarks>
    public class OrchestrationMessageEntity
    {
        /// <summary>自增主键。</summary>
        public long Id { get; set; }

        /// <summary>所属任务 ID，关联 <see cref="OrchestrationTaskEntity.Id"/>。</summary>
        [MaxLength(64)]
        public string TaskId { get; set; } = string.Empty;

        /// <summary>所属步骤 ID，关联 <see cref="OrchestrationStepEntity.Id"/>。</summary>
        [MaxLength(64)]
        public string StepId { get; set; } = string.Empty;

        /// <summary>(TaskId, StepId, Sequence) 三元组定序的序号。</summary>
        public int Sequence { get; set; }

        /// <summary>消息角色 "user" / "assistant" / "tool" / "system"。</summary>
        [MaxLength(32)]
        public string Role { get; set; } = string.Empty;

        /// <summary>消息作者显示名称（如智能体名称）。</summary>
        [MaxLength(128)]
        public string? AuthorName { get; set; }

        /// <summary>消息文本内容。</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>消息创建时间（Unix ms）。</summary>
        public long CreatedAt { get; set; }

        /// <summary>JSON 数组，附件路径与描述。</summary>
        public string? AttachmentsJson { get; set; }
    }
}
