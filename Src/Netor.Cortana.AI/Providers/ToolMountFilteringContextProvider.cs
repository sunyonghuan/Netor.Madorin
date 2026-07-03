using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 按后台委派任务声明的工具挂载清单过滤工具。
/// </summary>
internal sealed class ToolMountFilteringContextProvider(IReadOnlyCollection<string> mountedToolNames) : AIContextProvider
{
    private readonly HashSet<string> _mountedToolNames = new(mountedToolNames, StringComparer.OrdinalIgnoreCase);

    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_mountedToolNames.Count == 0)
        {
            context.AIContext.Tools = [];
            return ValueTask.FromResult(context.AIContext);
        }

        context.AIContext.Tools = FilterTools(context.AIContext.Tools, _mountedToolNames);
        return ValueTask.FromResult(context.AIContext);
    }

    internal static IReadOnlyList<AITool> FilterTools(IEnumerable<AITool>? tools, IReadOnlySet<string> mountedToolNames)
    {
        if (mountedToolNames.Count == 0)
        {
            return [];
        }

        var filtered = new List<AITool>();
        foreach (var tool in tools ?? [])
        {
            if (!string.IsNullOrWhiteSpace(tool.Name) && mountedToolNames.Contains(tool.Name))
            {
                filtered.Add(tool);
            }
        }

        return filtered;
    }
}
