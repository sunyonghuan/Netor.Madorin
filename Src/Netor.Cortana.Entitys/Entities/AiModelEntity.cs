using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// AI 模型实体，表示某个 AI 服务提供商下的一个可用大语言模型。
    /// 例如：deepseek-chat、gpt-4o、Qwen/Qwen3-8B 等。
    /// </summary>
    /// <remarks>
    /// <para>此类保存模型的基础元数据（名称、描述、上下文窗口大小、模型类型等），
    /// 并通过外键与 <see cref="AiProviderEntity"/> 服务提供商建立一对多关联。</para>
    /// <para>智能体（<see cref="AgentEntity"/>）创建时需要选择一个具体的模型。</para>
    /// </remarks>
    public class AiModelEntity : BaseEntity
    {
        /// <summary>
        /// 模型的标识名称（即调用 API 时传入的 model 参数值）。
        /// 例如："deepseek-chat"、"gpt-4o"、"Qwen/Qwen3-8B"。
        /// </summary>
        [MaxLength(128)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 模型的显示名称，用于在 UI 中向用户展示更友好的名称。
        /// 若为空则默认使用 <see cref="Name"/> 显示。
        /// </summary>
        [MaxLength(128)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 模型的简要描述，说明该模型的特点、优势和适用场景。
        /// </summary>
        [MaxLength(1024)]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 模型支持的最大上下文窗口长度（以 token 为单位）。
        /// 用于在会话中控制历史消息的截断策略。0 表示未知或不限制。
        /// </summary>
        public int ContextLength { get; set; } = 128000;

        /// <summary>
        /// 模型类型标识，用于在代码和 UI 中区分模型的能力范围。
        /// 例如："chat"（对话）、"completion"（补全）、"embedding"（向量嵌入）、"vision"（视觉）。
        /// </summary>
        [MaxLength(32)]
        public string ModelType { get; set; } = "chat";

        /// <summary>
        /// 是否为默认模型。启动新会话时若未指定模型，则自动使用标记为默认的模型。
        /// 同一时间只应有一个模型被标记为默认。
        /// </summary>
        public bool IsDefault { get; set; }

        /// <summary>
        /// 是否启用该模型。设为 false 时将在模型选择列表中隐藏。
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 所属 AI 服务提供商的外键标识，关联到 <see cref="AiProviderEntity.Id"/>。
        /// </summary>
        [MaxLength(32)]
        public string ProviderId { get; set; } = string.Empty;
    }
}
