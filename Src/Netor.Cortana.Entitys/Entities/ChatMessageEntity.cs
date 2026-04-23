using System;
using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// 聊天消息实体，表示一次会话中的单条对话消息。
    /// 隶属于某个 <see cref="ChatSessionEntity"/> 会话记录。
    /// </summary>
    /// <remarks>
    /// <para>每条消息记录了发送者角色（用户/助手/系统）、正文内容、令牌消耗等信息，
    /// 支持完整追踪用户与 AI 之间的对话流程。</para>
    /// <para>消息正文支持 Markdown 格式，便于在 UI 中渲染代码块、列表等富文本内容。</para>
    /// </remarks>
    public class ChatMessageEntity : BaseEntity
    {
        /// <summary>
        /// 所属会话的外键标识，关联到 <see cref="ChatSessionEntity.Id"/>。
        /// </summary>
        [MaxLength(32)]
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// 消息角色标识，用于区分消息的来源和用途。
        /// 可选值："user"（用户输入）、"assistant"（AI 响应）、"system"（系统指令）、"tool"（工具调用结果）。
        /// 该值将直接传递给 AI API。
        /// </summary>
        [MaxLength(32)]
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// 消息作者的显示名称。
        /// 对于用户消息可以是用户昵称，对于 AI 消息可以是智能体名称。可为 null。
        /// </summary>
        [MaxLength(128)]
        public string AuthorName { get; set; } = string.Empty;

        /// <summary>
        /// 消息文本内容（支持 Markdown 格式）。
        /// 存储用户输入或 AI 响应的完整文本。
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 结构化的消息内容 JSON 快照（Microsoft.Extensions.AI.AIContent 列表），用于在历史恢复时
        /// 精确还原工具调用 / 工具结果等非纯文本内容。
        /// 为空字符串时表示仅有文本内容，走兼容路径。
        /// </summary>
        public string ContentsJson { get; set; } = string.Empty;

        /// <summary>
        /// 该消息消耗的令牌数量（Token Count），用于统计和展示 API 用量。
        /// 0 表示未统计或不适用（例如用户消息通常不单独统计）。
        /// </summary>
        public int TokenCount { get; set; }

        /// <summary>
        /// 生成此消息时使用的模型名称，便于后续查看和审计。
        /// 对于用户消息此字段可为 null。
        /// </summary>
        [MaxLength(128)]
        public string ModelName { get; set; } = string.Empty;

        /// <summary>
        /// 消息创建时间（带时区偏移量）。
        /// 若为 null 表示时间未知或不适用。
        /// </summary>
        public DateTimeOffset? CreatedAt { get; set; }
    }
}