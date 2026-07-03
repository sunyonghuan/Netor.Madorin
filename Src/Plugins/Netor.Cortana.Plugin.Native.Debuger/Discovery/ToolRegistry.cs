namespace Netor.Cortana.Plugin.Native.Debugger.Discovery;

/// <summary>
/// 工具注册表
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ToolMetadata> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 已注册的工具集合
    /// </summary>
    public IReadOnlyDictionary<string, ToolMetadata> Tools => _tools;

    /// <summary>
    /// 注册工具
    /// </summary>
    public void Register(ToolMetadata metadata)
    {
        if (_tools.ContainsKey(metadata.ToolName))
            throw new InvalidOperationException($"工具名称 '{metadata.ToolName}' 已存在。");
        _tools[metadata.ToolName] = metadata;
    }
}
