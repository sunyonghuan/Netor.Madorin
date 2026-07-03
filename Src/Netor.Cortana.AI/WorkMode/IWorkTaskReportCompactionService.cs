namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作任务最终汇报缩略服务。
/// 只影响展示给总经理的汇报口径，不修改 plan.yaml 或归档全文。
/// </summary>
public interface IWorkTaskReportCompactionService
{
    /// <summary>
    /// 按需将原始工作汇报压缩为适合界面展示的短版内容。
    /// 返回 null 或空白表示保持原文。
    /// </summary>
    Task<string?> CompactAsync(string taskId, string rawReport, CancellationToken cancellationToken = default);
}
