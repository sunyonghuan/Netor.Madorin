namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示一次记忆召回请求及其命中结果的日志。
/// </summary>
public sealed class RecallLog
{
    /// <summary>
    /// 召回日志唯一标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 召回请求标识。
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// 发起召回的智能体标识。
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 召回请求所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// 召回查询文本。
    /// </summary>
    public string? QueryText { get; set; }

    /// <summary>
    /// 查询意图，用于召回策略选择。
    /// </summary>
    public string? QueryIntent { get; set; }

    /// <summary>
    /// 触发召回的来源，例如对话、工具调用或后台任务。
    /// </summary>
    public string? TriggerSource { get; set; }

    /// <summary>
    /// 命中的记忆标识集合 JSON。
    /// </summary>
    public string HitMemoryIdsJson { get; set; } = "[]";

    /// <summary>
    /// 被用于支撑回答的记忆标识集合 JSON。
    /// </summary>
    public string SupportingMemoryIdsJson { get; set; } = "[]";

    /// <summary>
    /// 因策略、权限或冲突被抑制的记忆标识集合 JSON。
    /// </summary>
    public string SuppressedMemoryIdsJson { get; set; } = "[]";

    /// <summary>
    /// 本次召回结果摘要。
    /// </summary>
    public string? RecallSummary { get; set; }

    /// <summary>
    /// 本次召回整体可信度评分。
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// 召回预算信息的 JSON 表示。
    /// </summary>
    public string? BudgetJson { get; set; }

    /// <summary>
    /// 本次召回应用策略的 JSON 表示。
    /// </summary>
    public string? AppliedPolicyJson { get; set; }

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// 数据结构版本号。
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// 当前记录版本号。
    /// </summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>
    /// 召回日志创建时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
