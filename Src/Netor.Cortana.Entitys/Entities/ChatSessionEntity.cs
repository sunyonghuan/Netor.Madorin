using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// 聊天会话实体，表示一次完整的对话会话记录。
    /// 每次在插件中开启新的聊天窗口或话题即创建一条会话记录。
    /// </summary>
    /// <remarks>
    /// <para>ChatSession 用于组织一系列 <see cref="ChatMessageEntity"/> 消息，
    /// 并与特定的 <see cref="AgentEntity"/> 智能体关联，
    /// 以确定该会话使用的模型、系统指令和参数配置。</para>
    /// <para>支持归档、置顶等会话管理功能。</para>
    /// </remarks>
    public class ChatSessionEntity : BaseEntity
    {
        /// <summary>
        /// 归类，用于将会话分类到不同的主题或类别中。
        /// </summary>
        [MaxLength(32)]
        public string Categorize { get; set; } = string.Empty;

        /// <summary>
        /// 会话标题，用于在 UI 中展示或快速识别会话内容。
        /// 可由用户手动编辑，也可由 AI 根据首条消息自动生成摘要标题。
        /// </summary>
        [MaxLength(256)]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 会话摘要，简要概括会话的主要内容和讨论主题。
        /// 可在会话列表中作为预览信息展示。
        /// </summary>
        [MaxLength(1024)]
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// 会话描述，用于详细描述会话的背景、目的和上下文信息。
        /// </summary>
        [MaxLength(4096)]
        public string RawDiscription { get; set; } = string.Empty;

        /// <summary>
        /// 该会话关联的智能体外键标识，关联到 <see cref="AgentEntity.Id"/>。
        /// </summary>
        [MaxLength(32)]
        public string AgentId { get; set; } = string.Empty;

        /// <summary>
        /// 是否已归档。归档后的会话在默认列表中不再显示，但数据仍然保留可供查询。
        /// </summary>
        public bool IsArchived { get; set; }

        /// <summary>
        /// 是否已置顶。置顶的会话会在列表中优先显示，方便快速访问常用对话。
        /// </summary>
        public bool IsPinned { get; set; }

        /// <summary>
        /// 最后活动时间戳（UTC 毫秒数），用于按最近使用时间排序。
        /// 每次发送或接收消息时自动更新此值。
        /// </summary>
        public long LastActiveTimestamp { get; set; }

        /// <summary>
        /// 该会话累计消耗的总令牌数，用于统计和展示 API 用量。
        /// </summary>
        public long TotalTokenCount { get; set; }
    }
}