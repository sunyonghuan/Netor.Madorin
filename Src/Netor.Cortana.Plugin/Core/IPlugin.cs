using Microsoft.Extensions.AI;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 插件统一接口。
/// </summary>
public interface IPlugin
{
    /// <summary>插件唯一标识。</summary>
    string Id { get; }

    /// <summary>插件显示名称。</summary>
    string Name { get; }

    /// <summary>插件版本。</summary>
    Version Version { get; }

    /// <summary>插件描述。</summary>
    string Description { get; }

    /// <summary>AI 指令片段。</summary>
    string? Instructions { get; }

    /// <summary>插件标签。</summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>插件暴露的工具列表。</summary>
    IReadOnlyList<AITool> Tools { get; }

    /// <summary>
    /// 初始化插件实例。
    /// </summary>
    Task InitializeAsync(IPluginContext context);
}