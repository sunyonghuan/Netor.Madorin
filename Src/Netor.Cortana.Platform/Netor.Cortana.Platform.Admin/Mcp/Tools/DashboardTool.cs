using System.ComponentModel;
using ModelContextProtocol.Server;
using Netor.Cortana.Platform.Admin.Operations;

namespace Netor.Cortana.Platform.Admin.Mcp.Tools;

[McpServerToolType]
public sealed class DashboardTool
{
    [McpServerTool(Name = "dashboard_summary", ReadOnly = true, OpenWorld = false)]
    [Description("获取后台首页级系统状态摘要。只返回聚合统计，不包含令牌、连接串、管理员明细或敏感配置。错误码：INTERNAL。")]
    public static async Task<OperationResult<DashboardSummaryResult>> SummaryAsync(
        DashboardOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.GetSummaryAsync(cancellationToken);
    }
}
