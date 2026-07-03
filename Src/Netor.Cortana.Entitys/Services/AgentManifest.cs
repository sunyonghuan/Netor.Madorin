namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 文件版 Agent 的 manifest.yaml 数据模型。
/// </summary>
public sealed class AgentManifest
{
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Kind { get; set; } = AgentManifestKinds.Agent;

    public string Avatar { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }

    public bool AllowWorkflowMemory { get; set; } = true;

    public List<string> BoundPlugins { get; set; } = [];

    public List<string> BoundMcp { get; set; } = [];
}

/// <summary>
/// 文件版 Agent 的 kind 常量。
/// </summary>
public static class AgentManifestKinds
{
    public const string Agent = "agent";
    public const string BuiltinSystem = "builtin/system";
}
