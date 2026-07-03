namespace Netor.Cortana.Entitys;

/// <summary>
/// 文件版 Agent 的运行期兼容 DTO。
/// </summary>
/// <remarks>
/// Agent 数据已经迁移到 manifest.yaml + prompt.md。本类型保留为兼容 DTO，
/// 供 UI、AI 构建器和插件链路继续传递 Agent 快照。
/// </remarks>
public class AgentEntity : BaseEntity
{
    /// <summary>智能体显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>系统指令文本。</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>智能体描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>智能体头像文件路径或资源标识。</summary>
    public string Avatar { get; set; } = string.Empty;

    /// <summary>是否启用该智能体。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>排序权重，数值越小越靠前。</summary>
    public int SortOrder { get; set; }

    /// <summary>绑定到该 Agent 的插件 ID 列表。</summary>
    public List<string> BoundPlugins { get; set; } = [];

    /// <summary>绑定到该 Agent 的 MCP Server ID 列表。</summary>
    public List<string> BoundMcp { get; set; } = [];

    /// <summary>是否允许 Workflow 任务结果进入此智能体的长期记忆。</summary>
    public bool AllowWorkflowMemory { get; set; } = true;
}
