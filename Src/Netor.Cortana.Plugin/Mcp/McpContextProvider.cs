using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Netor.Cortana.Plugin.Mcp;

/// <summary>
/// 将 <see cref="McpServerHost"/> 适配为 <see cref="AIContextProvider"/>，
/// 使 MCP Server 的工具对 AI Agent 透明可用。
/// </summary>
public sealed class McpContextProvider : AIContextProvider
{
    private readonly McpServerHost _host;
    private readonly HashSet<string>? _excludedToolNames;

    public McpContextProvider(McpServerHost host, HashSet<string>? excludedToolNames = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _excludedToolNames = excludedToolNames;
    }

    /// <summary>
    /// 使用 MCP Server 的数据库 ID 作为唯一 state key，避免多实例冲突。
    /// </summary>
    public override IReadOnlyList<string> StateKeys => [$"mcp:{_host.Id}"];

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = _excludedToolNames is { Count: > 0 }
            ? _host.Tools.Where(t => !_excludedToolNames.Contains(t.Name)).ToList()
            : _host.Tools;

        return ValueTask.FromResult(new AIContext
        {
            Tools = tools
        });
    }
}
