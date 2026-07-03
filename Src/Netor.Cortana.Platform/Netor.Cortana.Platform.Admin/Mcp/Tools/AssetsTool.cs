using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using Netor.Cortana.Platform.Admin.Operations;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Mcp.Tools;

[McpServerToolType]
public sealed class AssetsTool
{
    private const string SearchToolName = "assets_search";
    private const string GetToolName = "assets_get";
    private const string SetFeaturedToolName = "assets_set_featured";
    private const string SetStatusToolName = "assets_set_status";

    [McpServerTool(Name = SearchToolName, ReadOnly = true, OpenWorld = false)]
    [Description("搜索平台资源。pageSize 最大 20。错误码：VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<AssetSearchResult>> SearchAsync(
        AssetSearchInput input,
        AssetOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.SearchAsync(
            input.Keyword,
            input.Type,
            input.Status,
            input.CategoryId,
            input.Featured,
            input.Page,
            input.PageSize,
            cancellationToken);
    }

    [McpServerTool(Name = GetToolName, ReadOnly = true, OpenWorld = false)]
    [Description("获取指定资源详情。错误码：NOT_FOUND / VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<AssetDetailResult>> GetAsync(
        AssetGetInput input,
        AssetOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.GetAsync(input.AssetId, cancellationToken);
    }

    [McpServerTool(Name = SetFeaturedToolName, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("设置或取消资源推荐。仅超级管理员可用。建议传 requestId，重复调用将回放上次结果。错误码：NOT_FOUND / FORBIDDEN / VALIDATION / IDEMPOTENT_REPLAY / INTERNAL。")]
    public static async Task<OperationResult<AssetFeaturedResult>> SetFeaturedAsync(
        AssetSetFeaturedInput input,
        AssetOperations operations,
        AdminMcpContext context,
        AdminMcpIdempotencyService idempotency,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<AssetFeaturedResult>() is { } forbidden)
        {
            return forbidden;
        }

        var replay = await idempotency.TryReplayAsync(
            context.ManagerId,
            SetFeaturedToolName,
            input.RequestId,
            AdminMcpJsonContext.Default.OperationResultAssetFeaturedResult,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.SetFeaturedAsync(input.AssetId, input.Featured, cancellationToken);
        stopwatch.Stop();

        await LogAsync(audit, context, SetFeaturedToolName, result.Success, result.ErrorCode, stopwatch, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(input.RequestId))
        {
            await idempotency.RecordAsync(
                context.ManagerId,
                SetFeaturedToolName,
                input.RequestId,
                result,
                AdminMcpJsonContext.Default.OperationResultAssetFeaturedResult,
                cancellationToken);
        }

        return result;
    }

    [McpServerTool(Name = SetStatusToolName, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("设置资源状态。发布状态会执行审核与资源包风险校验。仅超级管理员可用。建议传 requestId，重复调用将回放上次结果。错误码：NOT_FOUND / FORBIDDEN / VALIDATION / IDEMPOTENT_REPLAY / INTERNAL。")]
    public static async Task<OperationResult<AssetStatusResult>> SetStatusAsync(
        AssetSetStatusInput input,
        AssetOperations operations,
        AdminMcpContext context,
        AdminMcpIdempotencyService idempotency,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<AssetStatusResult>() is { } forbidden)
        {
            return forbidden;
        }

        var replay = await idempotency.TryReplayAsync(
            context.ManagerId,
            SetStatusToolName,
            input.RequestId,
            AdminMcpJsonContext.Default.OperationResultAssetStatusResult,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.SetStatusAsync(input.AssetId, input.Status, cancellationToken);
        stopwatch.Stop();

        await LogAsync(audit, context, SetStatusToolName, result.Success, result.ErrorCode, stopwatch, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(input.RequestId))
        {
            await idempotency.RecordAsync(
                context.ManagerId,
                SetStatusToolName,
                input.RequestId,
                result,
                AdminMcpJsonContext.Default.OperationResultAssetStatusResult,
                cancellationToken);
        }

        return result;
    }

    private static async Task LogAsync(
        AdminAuditService audit,
        AdminMcpContext context,
        string toolName,
        bool success,
        string? errorCode,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        await audit.LogAsync(new AdminAuditEntry(
            ManagerId: context.ManagerId,
            TokenId: context.TokenId,
            ToolName: toolName,
            Source: "MCP",
            Ip: context.RemoteIp,
            Ua: context.UserAgent,
            Success: success,
            ErrorCode: errorCode,
            DurationMs: (int)stopwatch.ElapsedMilliseconds),
            cancellationToken);
    }
}

public sealed record AssetSearchInput(
    [property: Description("搜索关键字，可匹配资源名称、标识、开发者、简介或标签。")]
    string? Keyword,
    [property: Description("资源类型：1=插件，2=技能，3=智能体，4=解决方案。")]
    AssetType? Type,
    [property: Description("资源状态：1=草稿，2=已发布，3=隐藏，4=下架。")]
    AssetStatus? Status,
    [property: Description("分类ID。")]
    string? CategoryId,
    [property: Description("是否推荐。")]
    bool? Featured,
    [property: Description("页码，从 1 开始。")]
    int Page = 1,
    [property: Description("每页数量，最大 20。")]
    int PageSize = 20);

public sealed record AssetGetInput(
    [property: Description("资源ID。")]
    string AssetId);

public sealed record AssetSetFeaturedInput(
    [property: Description("资源ID。")]
    string AssetId,
    [property: Description("是否推荐。")]
    bool Featured,
    [property: Description("幂等请求ID，建议使用 UUID/ULID。")]
    string? RequestId);

public sealed record AssetSetStatusInput(
    [property: Description("资源ID。")]
    string AssetId,
    [property: Description("目标资源状态：1=草稿，2=已发布，3=隐藏，4=下架。")]
    AssetStatus Status,
    [property: Description("幂等请求ID，建议使用 UUID/ULID。")]
    string? RequestId);
