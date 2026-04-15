using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// AI 服务提供商实体，表示一个外部或内部 AI 服务的配置信息。
    /// 例如 DeepSeek、OpenAI、SiliconFlow、Azure OpenAI 等。
    /// </summary>
    /// <remarks>
    /// <para>此实体保存访问 AI 服务所需的 API 地址、名称、密钥和简介等信息，
    /// 并可包含该服务下可用的模型集合和智能体集合。</para>
    /// <para>每个提供商可以对应多个模型（一对多关系）和多个智能体（一对多关系）。</para>
    /// </remarks>
    public class AiProviderEntity : BaseEntity
    {
        /// <summary>
        /// AI 服务提供商的名称，用于在 UI 中显示或区分多个服务。
        /// 例如："DeepSeek"、"OpenAI"、"SiliconFlow"。
        /// </summary>
        [MaxLength(64)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// AI 服务的基础 API 地址（终结点 URL）。
        /// 例如："https://api.deepseek.com/v1"、"https://api.openai.com/v1"。
        /// </summary>
        [MaxLength(512)]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// 用于访问 AI 服务的 API 密钥（API Key）。
        /// 应妥善保管，避免泄露。传输和存储时建议加密处理。
        /// </summary>
        [MaxLength(256)]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// 用于访问 AI 服务的 Token
        /// </summary>
        [MaxLength(256)]
        public string AuthToken { get; set; } = string.Empty;

        /// <summary>
        /// AI 服务提供商的简介或描述信息，
        /// 用于向用户说明该服务的特点、能力范围和使用注意事项。
        /// </summary>
        [MaxLength(1024)]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 服务提供商的协议类型标识，用于在代码中区分不同的 API 协议或兼容模式。
        /// 例如："OpenAI"（兼容 OpenAI 协议）、"Azure"（Azure OpenAI）、"Custom"（自定义协议）。
        /// </summary>
        [MaxLength(32)]
        public string ProviderType { get; set; } = "OpenAI";

        /// <summary>
        /// 是否为默认服务提供商。启动新会话时若未指定提供商，则自动使用标记为默认的提供商。
        /// 同一时间只应有一个提供商被标记为默认。
        /// </summary>
        public bool IsDefault { get; set; }

        /// <summary>
        /// 是否启用该 AI 服务提供商。
        /// 设为 false 时将在 UI 中隐藏或禁止选择该提供商。
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 排序权重，数值越小越靠前，用于在 UI 列表中控制提供商的显示顺序。
        /// </summary>
        public int SortOrder { get; set; }
    }
}