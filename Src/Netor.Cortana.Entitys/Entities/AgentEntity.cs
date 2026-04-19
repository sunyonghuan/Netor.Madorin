using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Entitys;

/// <summary>
/// 智能体（Agent）实体，保存智能体的所有相关设置和配置。
/// 包括关联的模型、系统指令、生成参数（温度/令牌数等）以及展示信息。
/// </summary>
/// <remarks>
/// <para>智能体对应于特定的对话角色或任务场景。通过调整各项参数，
/// 可以精确控制智能体的行为风格和输出质量。</para>
/// <para>每个智能体关联一个 <see cref="AiProviderEntity"/>（AI 服务提供商）
/// 和一个 <see cref="AiModelEntity"/>（具体模型），并可拥有多个会话记录。</para>
/// </remarks>
public class AgentEntity : BaseEntity
{
    /// <summary>
    /// 智能体的显示名称，用于在 UI 中展示和识别。
    /// 例如："代码助手"、"翻译专家"、"文档写手"。
    /// </summary>
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 系统指令文本（System Prompt），在每次对话开始时发送给模型。
    /// 用于定义智能体的角色、行为规范、任务目标和输出格式要求。
    /// </summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// 智能体的描述信息，用于在 UI 中向用户说明该智能体的用途、能力或适用场景。
    /// </summary>
    [MaxLength(1024)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 智能体的图片 URL 或资源标识，用于在 UI 中展示头像或图标。
    /// </summary>
    [MaxLength(512)]
    public string Image { get; set; } = string.Empty;

    /// <summary>
    /// 智能体的头像文件路径（相对于资源目录），用于在聊天气泡中展示。
    /// 为空时使用默认头像。
    /// </summary>
    [MaxLength(512)]
    public string Avatar { get; set; } = string.Empty;

    /// <summary>
    /// 子智能体的默认 AI 提供商 ID。
    /// 为空时跟随当前会话的提供商。
    /// </summary>
    [MaxLength(64)]
    public string DefaultProviderId { get; set; } = string.Empty;

    /// <summary>
    /// 子智能体的默认 AI 模型 ID。
    /// 为空时跟随当前会话的模型。
    /// </summary>
    [MaxLength(64)]
    public string DefaultModelId { get; set; } = string.Empty;

    // ─────────────────────────────────────────────────
    //  模型调用参数设置
    // ─────────────────────────────────────────────────

    /// <summary>
    /// 采样温度（Temperature），取值范围 [0, 2]。
    /// 值越高生成内容越随机多样，值越低越确定和集中。
    /// 默认值 0.7 适用于一般对话场景。
    /// </summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// 单次请求中模型生成的最大令牌数（Max Tokens）。
    /// 用于限制输出长度，避免过长的响应消耗过多资源。0 表示使用模型默认值。
    /// </summary>
    public int MaxTokens { get; set; }

    /// <summary>
    /// Top-P（核采样）参数，取值范围 [0, 1]。
    /// 模型仅在累计概率达到 TopP 的候选词中采样。
    /// 与 Temperature 配合使用可精细控制生成多样性。
    /// </summary>
    public double TopP { get; set; } = 1.0;

    /// <summary>
    /// 频率惩罚（Frequency Penalty），取值范围 [-2, 2]。
    /// 正值会降低已出现词汇的重复概率，有助于减少输出中的冗余内容。
    /// </summary>
    public double FrequencyPenalty { get; set; }

    /// <summary>
    /// 存在惩罚（Presence Penalty），取值范围 [-2, 2]。
    /// 正值会鼓励模型讨论新话题，减少对已提及内容的反复引用。
    /// </summary>
    public double PresencePenalty { get; set; }

    // ─────────────────────────────────────────────────
    //  会话行为设置
    // ─────────────────────────────────────────────────

    /// <summary>
    /// 会话中保留的最大历史消息条数。
    /// 超过此数量时将自动截断早期消息以节省 token 消耗。0 表示不限制。
    /// </summary>
    public int MaxHistoryMessages { get; set; }

    /// <summary>
    /// 是否为默认智能体。启动新会话时若未指定智能体，则自动使用标记为默认的智能体。
    /// 同一时间只应有一个智能体被标记为默认。
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// 是否启用该智能体。设为 false 时将在 UI 中隐藏或禁止选择。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 排序权重，数值越小越靠前，用于在智能体列表中控制显示顺序。
    /// </summary>
    public int SortOrder { get; set; }

    // ─────────────────────────────────────────────────
    //  工具管理
    // ─────────────────────────────────────────────────

    /// <summary>
    /// 该智能体已启用的插件 ID 列表。
    /// 仅当插件 ID 出现在此列表中时，对应插件的工具才会注入到 AI Agent。
    /// 为空列表时表示不使用任何插件工具。
    /// </summary>
    public List<string> EnabledPluginIds { get; set; } = [];

    /// <summary>
    /// 该智能体已启用的 MCP 服务器 ID 列表。
    /// 仅当 MCP Server ID 出现在此列表中时，对应服务器的工具才会注入到 AI Agent。
    /// 为空列表时表示不使用任何 MCP 工具。
    /// </summary>
    public List<string> EnabledMcpServerIds { get; set; } = [];
}