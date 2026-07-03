using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 自评工具：self_evaluate。
/// 总经理在每个子步骤完成后调用，对执行质量打分。
/// 低于阈值时自动触发重做循环（最多 5 次）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/16-长任务可靠性与重试策略.md §6。
/// </summary>
public sealed class SelfEvaluationTools
{
    private const int MaxIterations = 5;
    private const int PassThreshold = 7;

    private readonly WorkExecutionLogService _logService;
    private readonly IPublisher _publisher;
    private readonly string _taskId;

    public SelfEvaluationTools(
        WorkExecutionLogService logService,
        IPublisher publisher,
        string taskId)
    {
        _logService = logService;
        _publisher = publisher;
        _taskId = taskId;
    }

    public AIFunction CreateSelfEvaluateTool()
    {
        [Description("对当前步骤的执行质量进行自评")]
        Task<string> SelfEvaluateAsync(
            [Description("步骤标题")] string stepTitle,
            [Description("当前是第几次自评迭代（从 1 开始）")] int iteration,
            [Description("质量评分（1-10，7 分及以上为通过）")] int score,
            [Description("评价结论：PASS 或 NEEDS_IMPROVEMENT")] string verdict,
            [Description("发现的问题列表（JSON 数组）")] string issuesJson,
            CancellationToken ct)
        {
            if (iteration > MaxIterations)
                return Task.FromResult(
                    $"已达到最大自评迭代次数（{MaxIterations}），强制通过。请调用 verify_step 提交当前结果。");

            List<string> issues;
            try
            {
                issues = JsonSerializer.Deserialize(issuesJson, WorkModeJsonContext.Default.ListString) ?? [];
            }
            catch
            {
                issues = [issuesJson];
            }

            var logRecord = new WorkExecutionLogRecords.SelfEvaluation(
                stepTitle, iteration, score, verdict, issues);
            _logService.Append(_taskId, WorkExecutionLogTypes.SelfEvaluation,
                JsonSerializer.Serialize(logRecord, WorkModeJsonContext.Default.SelfEvaluation));

            if (score >= PassThreshold || verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    $"自评通过（{score}/10，迭代 {iteration}/{MaxIterations}）。请调用 verify_step 正式验收。");
            }

            var issueList = issues.Count > 0
                ? string.Join("\n", issues.Select((iss, i) => $"  {i + 1}. {iss}"))
                : "  （无具体问题描述）";

            return Task.FromResult(
                $"自评未通过（{score}/10，迭代 {iteration}/{MaxIterations}）。\n" +
                $"问题：\n{issueList}\n\n" +
                $"请修正上述问题后重新执行，然后再次调用 self_evaluate（iteration={iteration + 1}）。");
        }

        return AIFunctionFactory.Create(SelfEvaluateAsync, new AIFunctionFactoryOptions
        {
            Name = "self_evaluate",
            Description = "对当前步骤的执行质量进行自评。评分 7 分及以上为通过，低于 7 分需要改进后重新自评。最多迭代 5 次。"
        });
    }
}
