using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using Netor.Cortana.Platform.Admin.Operations;

namespace Netor.Cortana.Platform.Admin.Mcp.Tools;

[McpServerToolType]
public sealed class AccountsTool
{
    private const string SearchToolName = "accounts_search";
    private const string GetToolName = "accounts_get";
    private const string ListTransactionsToolName = "accounts_list_transactions";
    private const string SetStatusToolName = "accounts_set_status";
    private const string RechargeToolName = "accounts_recharge_wallet";

    [McpServerTool(Name = SearchToolName, ReadOnly = true, OpenWorld = false)]
    [Description("搜索平台账户。pageSize 最大 20。错误码：VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<AccountSearchResult>> SearchAsync(
        AccountSearchInput input,
        AccountOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.SearchAsync(input.Keyword, input.Status, input.Page, input.PageSize, cancellationToken);
    }

    [McpServerTool(Name = GetToolName, ReadOnly = true, OpenWorld = false)]
    [Description("获取指定账户详情。错误码：NOT_FOUND / VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<AccountDetailResult>> GetAsync(
        AccountGetInput input,
        AccountOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.GetAsync(input.AccountId, cancellationToken);
    }

    [McpServerTool(Name = ListTransactionsToolName, ReadOnly = true, OpenWorld = false)]
    [Description("列出指定账户交易流水。pageSize 最大 20。错误码：NOT_FOUND / VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<AccountTransactionSearchResult>> ListTransactionsAsync(
        AccountListTransactionsInput input,
        AccountOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.ListTransactionsAsync(input.AccountId, input.Page, input.PageSize, cancellationToken);
    }

    [McpServerTool(Name = SetStatusToolName, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("设置指定账户状态。仅超级管理员可用。错误码：NOT_FOUND / FORBIDDEN / VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<AccountStatusResult>> SetStatusAsync(
        AccountSetStatusInput input,
        AccountOperations operations,
        AdminMcpContext context,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<AccountStatusResult>() is { } forbidden)
        {
            return forbidden;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.SetStatusAsync(input.AccountId, input.Status, cancellationToken);
        stopwatch.Stop();

        await audit.LogAsync(new AdminAuditEntry(
            ManagerId: context.ManagerId,
            TokenId: context.TokenId,
            ToolName: SetStatusToolName,
            Source: "MCP",
            Ip: context.RemoteIp,
            Ua: context.UserAgent,
            Success: result.Success,
            ErrorCode: result.ErrorCode,
            DurationMs: (int)stopwatch.ElapsedMilliseconds),
            cancellationToken);

        return result;
    }

    [McpServerTool(Name = RechargeToolName, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("给指定账户充值钱包余额。仅超级管理员可用。建议传 requestId，重复调用将回放上次结果。错误码：NOT_FOUND / FORBIDDEN / VALIDATION / IDEMPOTENT_REPLAY / INTERNAL。")]
    public static async Task<OperationResult<AccountRechargeResult>> RechargeWalletAsync(
        AccountRechargeWalletInput input,
        AccountOperations operations,
        AdminMcpContext context,
        AdminMcpIdempotencyService idempotency,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<AccountRechargeResult>() is { } forbidden)
        {
            return forbidden;
        }

        var replay = await idempotency.TryReplayAsync(
            context.ManagerId,
            RechargeToolName,
            input.RequestId,
            AdminMcpJsonContext.Default.OperationResultAccountRechargeResult,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.RechargeWalletAsync(
            context.ManagerId,
            input.AccountId,
            input.Amount,
            source: "MCP",
            requestId: input.RequestId,
            reason: input.Reason,
            cancellationToken);
        stopwatch.Stop();

        await audit.LogAsync(new AdminAuditEntry(
            ManagerId: context.ManagerId,
            TokenId: context.TokenId,
            ToolName: RechargeToolName,
            Source: "MCP",
            Ip: context.RemoteIp,
            Ua: context.UserAgent,
            Success: result.Success,
            ErrorCode: result.ErrorCode,
            DurationMs: (int)stopwatch.ElapsedMilliseconds),
            cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(input.RequestId))
        {
            await idempotency.RecordAsync(
                context.ManagerId,
                RechargeToolName,
                input.RequestId,
                result,
                AdminMcpJsonContext.Default.OperationResultAccountRechargeResult,
                cancellationToken);
        }

        return result;
    }
}

public sealed record AccountSearchInput(
    [property: Description("搜索关键字，可匹配账号、手机号、邮箱、昵称、姓名或账号编号。")]
    string? Keyword,
    [property: Description("账户状态：0=正常，1=禁用，2=冻结。")]
    byte? Status,
    [property: Description("页码，从 1 开始。")]
    int Page = 1,
    [property: Description("每页数量，最大 20。")]
    int PageSize = 20);

public sealed record AccountGetInput(
    [property: Description("账户ID。")]
    string AccountId);

public sealed record AccountListTransactionsInput(
    [property: Description("账户ID。")]
    string AccountId,
    [property: Description("页码，从 1 开始。")]
    int Page = 1,
    [property: Description("每页数量，最大 20。")]
    int PageSize = 20);

public sealed record AccountSetStatusInput(
    [property: Description("账户ID。")]
    string AccountId,
    [property: Description("目标账户状态：0=正常，1=禁用，2=冻结。")]
    byte Status);

public sealed record AccountRechargeWalletInput(
    [property: Description("账户ID。")]
    string AccountId,
    [property: Description("充值金额，必须大于 0。")]
    decimal Amount,
    [property: Description("幂等请求ID，建议使用 UUID/ULID。")]
    string? RequestId,
    [property: Description("充值原因。")]
    string? Reason);
