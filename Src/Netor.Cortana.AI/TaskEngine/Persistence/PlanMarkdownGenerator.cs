using System.Text;

using Netor.Cortana.AI.TaskEngine.Models;

namespace Netor.Cortana.AI.TaskEngine.Persistence;

/// <summary>
/// 从 <see cref="ExecutionPlan"/> 生成人类可读的 Markdown 摘要。
/// 格式参照 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §7.3。
/// </summary>
public static class PlanMarkdownGenerator
{
    /// <summary>
    /// 从 ExecutionPlan 生成 Markdown 文本。
    /// </summary>
    public static string Generate(ExecutionPlan plan)
    {
        var sb = new StringBuilder();

        // ── 标题 ──
        sb.AppendLine($"# 执行计划：{(string.IsNullOrWhiteSpace(plan.TaskSummary) ? "(未命名任务)" : plan.TaskSummary)}");
        sb.AppendLine();

        // ── 元信息 ──
        sb.AppendLine($"> 任务 ID: {plan.TaskId}");
        sb.AppendLine($"> 计划 ID: {plan.PlanId}");
        sb.AppendLine($"> 创建时间: {plan.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"> 最后修改: {plan.LastModifiedAt:yyyy-MM-dd HH:mm}");

        var completedCount = plan.Steps.Count(s => s.Status == PlanStepStatus.Completed);
        sb.AppendLine($"> 状态: {FormatPlanStatus(plan.Status)} ({completedCount}/{plan.Steps.Count} 步骤完成)");
        sb.AppendLine($"> 版本: v{plan.Version}");

        if (!string.IsNullOrEmpty(plan.SourceTemplateId))
            sb.AppendLine($"> 来源模板: {plan.SourceTemplateId}");

        sb.AppendLine();

        // ── 需求要点 ──
        if (plan.Requirements is { } req && req.KeyPoints.Count > 0)
        {
            sb.AppendLine("## 需求要点");
            sb.AppendLine();
            foreach (var point in req.KeyPoints)
            {
                sb.AppendLine($"- {point}");
            }
            if (req.Constraints.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**约束条件**：");
                foreach (var c in req.Constraints)
                {
                    sb.AppendLine($"- {c}");
                }
            }
            sb.AppendLine();
        }

        // ── 最终目标 ──
        if (!string.IsNullOrWhiteSpace(plan.FinalGoal))
        {
            sb.AppendLine("## 最终目标");
            sb.AppendLine();
            sb.AppendLine(plan.FinalGoal);
            sb.AppendLine();
        }

        // ── 执行步骤表格 ──
        sb.AppendLine("## 执行步骤");
        sb.AppendLine();
        sb.AppendLine("| # | 步骤 | 模式 | 智能体 | 状态 | 耗时 |");
        sb.AppendLine("|---|------|------|--------|------|------|");

        foreach (var step in plan.Steps.OrderBy(s => s.Sequence))
        {
            var icon = GetStatusIcon(step.Status);
            var mode = FormatExecutionMode(step.ExecutionMode);
            var agent = step.AssignedAgentName ?? step.AgentTypeDescription;
            if (agent.Length > 20) agent = agent[..20] + "…";
            var duration = FormatDuration(step.StartedAt, step.CompletedAt);

            sb.AppendLine($"| {step.Sequence} | {step.Title} | {mode} | {agent} | {icon} {FormatStepStatus(step.Status)} | {duration} |");
        }

        sb.AppendLine();

        // ── 步骤详情（已完成的步骤展示结果摘要） ──
        var detailSteps = plan.Steps
            .Where(s => s.Status is PlanStepStatus.Completed or PlanStepStatus.Running or PlanStepStatus.Failed)
            .OrderBy(s => s.Sequence);

        if (detailSteps.Any())
        {
            sb.AppendLine("## 步骤详情");
            sb.AppendLine();

            foreach (var step in detailSteps)
            {
                var icon = GetStatusIcon(step.Status);
                sb.AppendLine($"### 步骤 {step.Sequence}：{step.Title} {icon}");
                sb.AppendLine();

                if (!string.IsNullOrEmpty(step.AssignedAgentName))
                    sb.AppendLine($"**智能体**: {step.AssignedAgentName}");

                if (step.Status == PlanStepStatus.Running && step.ProgressPercent > 0)
                    sb.AppendLine($"**进度**: {step.ProgressPercent}%");

                if (!string.IsNullOrEmpty(step.ResultSummary))
                    sb.AppendLine($"**结果**: {step.ResultSummary}");

                if (step.Status == PlanStepStatus.Failed && !string.IsNullOrEmpty(step.ErrorMessage))
                    sb.AppendLine($"**错误**: {step.ErrorMessage}");

                if (step.SubTasks.Count > 0)
                {
                    sb.AppendLine("**子任务**:");
                    foreach (var sub in step.SubTasks)
                    {
                        var subIcon = GetStatusIcon(sub.Status);
                        var subSummary = !string.IsNullOrEmpty(sub.ResultSummary) ? $" — {sub.ResultSummary}" : "";
                        sb.AppendLine($"- {subIcon} {sub.Title}{subSummary}");
                    }
                }

                sb.AppendLine();
            }
        }

        // ── 页脚 ──
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("*此文件由任务执行引擎自动生成，每步完成后更新。*");

        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════

    private static string GetStatusIcon(PlanStepStatus status) => status switch
    {
        PlanStepStatus.Completed => "✅",
        PlanStepStatus.Running => "🔄",
        PlanStepStatus.Retrying => "🔄",
        PlanStepStatus.Failed => "❌",
        PlanStepStatus.WaitingUser => "⏸️",
        PlanStepStatus.WaitingDeps => "⏳",
        PlanStepStatus.Pending => "⏳",
        PlanStepStatus.Skipped => "⏭️",
        PlanStepStatus.Cancelled => "🚫",
        _ => "⏳",
    };

    private static string FormatPlanStatus(PlanStatus status) => status switch
    {
        PlanStatus.Draft => "草稿",
        PlanStatus.Confirmed => "已确认",
        PlanStatus.Executing => "执行中",
        PlanStatus.Paused => "已暂停",
        PlanStatus.Completed => "已完成",
        PlanStatus.Failed => "失败",
        PlanStatus.Cancelled => "已取消",
        _ => status.ToString(),
    };

    private static string FormatStepStatus(PlanStepStatus status) => status switch
    {
        PlanStepStatus.Completed => "完成",
        PlanStepStatus.Running => "执行中",
        PlanStepStatus.Retrying => "重试中",
        PlanStepStatus.Failed => "失败",
        PlanStepStatus.WaitingUser => "等待确认",
        PlanStepStatus.WaitingDeps => "等待依赖",
        PlanStepStatus.Pending => "等待",
        PlanStepStatus.Skipped => "跳过",
        PlanStepStatus.Cancelled => "取消",
        _ => status.ToString(),
    };

    private static string FormatExecutionMode(string mode) => mode switch
    {
        "sequential" => "顺序",
        "parallel" => "并行",
        "await_user" => "需确认",
        _ => mode,
    };

    private static string FormatDuration(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null) return "-";
        if (end is null) return "-";

        var duration = end.Value - start.Value;
        if (duration.TotalHours >= 1)
            return $"{duration.Hours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        return $"{duration.Minutes}:{duration.Seconds:D2}";
    }
}
