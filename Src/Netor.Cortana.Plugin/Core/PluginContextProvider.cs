using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Netor.Cortana.Plugin;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 将 <see cref="IPlugin"/> 适配为 <see cref="AIContextProvider"/>，
/// 使插件的工具和指令对 AI Agent 透明可用。
/// </summary>
public sealed class PluginContextProvider : AIContextProvider
{
    private readonly IPlugin _plugin;
    private readonly HashSet<string>? _excludedToolNames;

    public PluginContextProvider(IPlugin plugin, HashSet<string>? excludedToolNames = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugin = plugin;
        _excludedToolNames = excludedToolNames;
    }

    /// <summary>
    /// 使用插件 ID 作为唯一 state key，避免多个插件实例之间的 key 冲突。
    /// </summary>
    public override IReadOnlyList<string> StateKeys => [_plugin.Id];

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = _excludedToolNames is { Count: > 0 }
            ? _plugin.Tools.Where(t => !_excludedToolNames.Contains(t.Name)).ToList()
            : _plugin.Tools.ToList();

        return ValueTask.FromResult(new AIContext
        {
            Instructions = _plugin.Instructions,
            Tools = tools
        });
    }
}
