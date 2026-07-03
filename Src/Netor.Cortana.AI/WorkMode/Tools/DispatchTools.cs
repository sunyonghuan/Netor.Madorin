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
/// 派发工具：dispatch_step（同步执行单个步骤）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §3.2。
/// </summary>
public sealed class DispatchTools
{
    private readonly WorkTaskService _taskService;
    private readonly WorkExecutionLogService _logService;
    private readonly IPublisher _publisher;
    private readonly string _taskId;

    public DispatchTools(
        WorkTaskService taskService,
        WorkExecutionLogService logService,
        IPublisher publisher,
        string taskId)
    {
        _taskService = taskService;
        _logService = logService;
        _publisher = publisher;
        _taskId = taskId;
    }

    public AIFunction CreateDispatchStepTool()
    {
        [Description("执行计划中的一个步骤")]
        async Task<string> DispatchStepAsync(
            [Description("主步骤序号（从 1 开始）")] int stepIndex,
            [Description("子步骤序号（从 1 开始）")] int subStepIndex,
            [Description("执行该步骤的具体指令")] string instruction,
            CancellationToken ct)
        {
            var task = _taskService.GetById(_taskId);
            if (task is null)
                return "错误：任务不存在";

            var gateError = CheckPlanConfirmationGate(task);
            if (gateError is not null)
                return gateError;

            if (string.IsNullOrEmpty(task.CurrentPlanJson))
                return "错误：请先调用 set_plan 制定计划";

            var plan = JsonSerializer.Deserialize(task.CurrentPlanJson, WorkModeJsonContext.Default.WorkPlan);
            if (plan?.MainSteps is null || plan.MainSteps.Count == 0)
                return "错误：计划数据损坏";

            if (stepIndex < 1 || stepIndex > plan.MainSteps.Count)
                return $"错误：主步骤序号 {stepIndex} 超出范围（1-{plan.MainSteps.Count}）";

            var mainStep = plan.MainSteps[stepIndex - 1];
            if (subStepIndex < 1 || subStepIndex > mainStep.SubSteps.Count)
                return $"错误：子步骤序号 {subStepIndex} 超出范围（1-{mainStep.SubSteps.Count}）";

            var subStep = mainStep.SubSteps[subStepIndex - 1];
            var stepTitle = $"{stepIndex}.{subStepIndex} {subStep.Title}";

            await _publisher.PublishAsync(
                Events.OnWorkStepStarted,
                new WorkStepStartedArgs(_taskId, stepTitle, mainStep.Title));

            var logRecord = new WorkExecutionLogRecords.StepStart(stepTitle, mainStep.Title, instruction);
            _logService.Append(_taskId, WorkExecutionLogTypes.StepStart,
                JsonSerializer.Serialize(logRecord, WorkModeJsonContext.Default.StepStart));

            return $"步骤 [{stepTitle}] 已开始执行。\n" +
                   $"指令：{instruction}\n" +
                   $"验收标准：{subStep.AcceptanceCriteria}\n\n" +
                   "请使用相关工具完成此步骤，完成后调用 verify_step 进行验收。";
        }

        return AIFunctionFactory.Create(DispatchStepAsync, new AIFunctionFactoryOptions
        {
            Name = "dispatch_step",
            Description = "执行计划中的一个步骤。按主步骤和子步骤序号定位，提供具体执行指令。步骤执行完成后返回结果。"
        });
    }

    public AIFunction CreateDispatchParallelTool()
    {
        [Description("声明一组可并行推进的子步骤。调用后仍需分别调用 dispatch_step 执行每个子步骤。")]
        async Task<string> DispatchParallelAsync(
            [Description("主步骤序号（从 1 开始）")] int stepIndex,
            [Description("可并行执行的子步骤序号 JSON 数组，例如 [1,2,3]")] string subStepIndexesJson,
            [Description("并行执行说明")] string instruction,
            CancellationToken ct)
        {
            var task = _taskService.GetById(_taskId);
            if (task is null)
                return "错误：任务不存在";

            var gateError = CheckPlanConfirmationGate(task);
            if (gateError is not null)
                return gateError;

            if (string.IsNullOrEmpty(task.CurrentPlanJson))
                return "错误：请先调用 set_plan 制定计划";

            var plan = JsonSerializer.Deserialize(task.CurrentPlanJson, WorkModeJsonContext.Default.WorkPlan);
            if (plan?.MainSteps is null || plan.MainSteps.Count == 0)
                return "错误：计划数据损坏";

            if (stepIndex < 1 || stepIndex > plan.MainSteps.Count)
                return $"错误：主步骤序号 {stepIndex} 超出范围（1-{plan.MainSteps.Count}）";

            List<int> subStepIndexes;
            try
            {
                subStepIndexes = JsonSerializer.Deserialize(subStepIndexesJson, WorkModeJsonContext.Default.ListInt32) ?? [];
            }
            catch (JsonException ex)
            {
                return $"错误：子步骤序号 JSON 无效 - {ex.Message}";
            }

            if (subStepIndexes.Count == 0)
                return "错误：并行子步骤不能为空";

            var mainStep = plan.MainSteps[stepIndex - 1];
            var invalidIndexes = subStepIndexes
                .Where(i => i < 1 || i > mainStep.SubSteps.Count)
                .Distinct()
                .ToArray();
            if (invalidIndexes.Length > 0)
                return $"错误：子步骤序号 {string.Join(", ", invalidIndexes)} 超出范围（1-{mainStep.SubSteps.Count}）";

            var blockId = $"parallel-{Guid.NewGuid():N}";
            var stepTitles = subStepIndexes
                .Distinct()
                .Select(i => $"{stepIndex}.{i} {mainStep.SubSteps[i - 1].Title}")
                .ToArray();

            var startRecord = new WorkExecutionLogRecords.ParallelBlockStart(blockId, stepTitles);
            _logService.Append(_taskId, WorkExecutionLogTypes.ParallelBlockStart,
                JsonSerializer.Serialize(startRecord, WorkModeJsonContext.Default.ParallelBlockStart));

            await _publisher.PublishAsync(
                Events.OnWorkParallelBlockStarted,
                new WorkParallelBlockStartedArgs(_taskId, blockId, stepTitles));

            return $"并行块 {blockId} 已创建。\n" +
                   $"主步骤：{mainStep.Title}\n" +
                   $"并行子步骤：\n- {string.Join("\n- ", stepTitles)}\n\n" +
                   $"执行说明：{instruction}\n\n" +
                   "请分别调用 dispatch_step 执行上述子步骤，全部完成后调用 finish_parallel_block。";
        }

        return AIFunctionFactory.Create(DispatchParallelAsync, new AIFunctionFactoryOptions
        {
            Name = "dispatch_parallel",
            Description = "声明一组可并行推进的子步骤，并发布并行块开始事件。每个子步骤仍通过 dispatch_step 执行。"
        });
    }

    public AIFunction CreateFinishParallelBlockTool()
    {
        [Description("结束一个并行块，记录并行块完成情况。")]
        async Task<string> FinishParallelBlockAsync(
            [Description("dispatch_parallel 返回的并行块 ID")] string blockId,
            [Description("并行子步骤总数")] int totalCount,
            [Description("成功完成的子步骤数量")] int successCount,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(blockId))
                return "错误：并行块 ID 不能为空";

            if (totalCount < 0 || successCount < 0 || successCount > totalCount)
                return "错误：并行块数量参数无效";

            var endRecord = new WorkExecutionLogRecords.ParallelBlockEnd(blockId, totalCount, successCount);
            _logService.Append(_taskId, WorkExecutionLogTypes.ParallelBlockEnd,
                JsonSerializer.Serialize(endRecord, WorkModeJsonContext.Default.ParallelBlockEnd));

            await _publisher.PublishAsync(
                Events.OnWorkParallelBlockEnded,
                new WorkParallelBlockEndedArgs(_taskId, blockId, totalCount, successCount));

            return successCount == totalCount
                ? $"并行块 {blockId} 已完成，{successCount}/{totalCount} 个子步骤成功。"
                : $"并行块 {blockId} 已结束，{successCount}/{totalCount} 个子步骤成功。请检查失败子步骤并决定是否重做。";
        }

        return AIFunctionFactory.Create(FinishParallelBlockAsync, new AIFunctionFactoryOptions
        {
            Name = "finish_parallel_block",
            Description = "结束一个并行块，写入完成日志并发布并行块结束事件。"
        });
    }

    public AIFunction CreateDispatchNestedWorkflowTool()
    {
        [Description("声明一个嵌套子工作流，用于把复杂子任务拆成独立可追踪的子任务。当前为轻量编排，会记录日志并显示在时间线中。")]
        async Task<string> DispatchNestedWorkflowAsync(
            [Description("嵌套子工作流目标")] string goal,
            [Description("验收标准")] string acceptanceCriteria,
            CancellationToken ct)
        {
            var task = _taskService.GetById(_taskId);
            if (task is null)
                return "错误：任务不存在";

            var gateError = CheckPlanConfirmationGate(task);
            if (gateError is not null)
                return gateError;

            if (!string.IsNullOrWhiteSpace(task.ParentTaskId))
                return "错误：当前任务已经是子工作流，不能继续创建更深层嵌套";

            if (string.IsNullOrWhiteSpace(goal))
                return "错误：嵌套子工作流目标不能为空";

            var nestedTaskId = $"nested-{Guid.NewGuid():N}";
            var startRecord = new WorkExecutionLogRecords.NestedWorkflowStart(nestedTaskId, goal.Trim());
            _logService.Append(_taskId, WorkExecutionLogTypes.NestedWorkflowStart,
                JsonSerializer.Serialize(startRecord, WorkModeJsonContext.Default.NestedWorkflowStart));

            await _publisher.PublishAsync(
                Events.OnWorkNestedWorkflowStarted,
                new WorkNestedWorkflowStartedArgs(_taskId, nestedTaskId, goal.Trim()));

            return $"嵌套子工作流 {nestedTaskId} 已创建。\n" +
                   $"目标：{goal.Trim()}\n" +
                   $"验收标准：{acceptanceCriteria.Trim()}\n\n" +
                   "请把它作为当前任务的一段独立执行分支推进；完成后调用 finish_nested_workflow 写入结果。";
        }

        return AIFunctionFactory.Create(DispatchNestedWorkflowAsync, new AIFunctionFactoryOptions
        {
            Name = "dispatch_nested_workflow",
            Description = "声明一个嵌套子工作流，记录 NestedWorkflowStart 日志并发布 UI 事件。当前限制最多一层嵌套。"
        });
    }

    public AIFunction CreateFinishNestedWorkflowTool()
    {
        [Description("结束一个嵌套子工作流，记录最终结果。")]
        async Task<string> FinishNestedWorkflowAsync(
            [Description("dispatch_nested_workflow 返回的嵌套子工作流 ID")] string nestedTaskId,
            [Description("嵌套子工作流最终结果或交付说明")] string finalReport,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(nestedTaskId))
                return "错误：嵌套子工作流 ID 不能为空";

            if (string.IsNullOrWhiteSpace(finalReport))
                return "错误：最终结果不能为空";

            var endRecord = new WorkExecutionLogRecords.NestedWorkflowEnd(nestedTaskId.Trim(), finalReport.Trim());
            _logService.Append(_taskId, WorkExecutionLogTypes.NestedWorkflowEnd,
                JsonSerializer.Serialize(endRecord, WorkModeJsonContext.Default.NestedWorkflowEnd));

            await _publisher.PublishAsync(
                Events.OnWorkNestedWorkflowEnded,
                new WorkNestedWorkflowEndedArgs(_taskId, nestedTaskId.Trim(), finalReport.Trim()));

            return $"嵌套子工作流 {nestedTaskId.Trim()} 已结束。";
        }

        return AIFunctionFactory.Create(FinishNestedWorkflowAsync, new AIFunctionFactoryOptions
        {
            Name = "finish_nested_workflow",
            Description = "结束一个嵌套子工作流，写入 NestedWorkflowEnd 日志并发布 UI 事件。"
        });
    }

    private static string? CheckPlanConfirmationGate(WorkTaskEntity task)
    {
        return string.Equals(task.PendingRequestKind, PlanTools.PlanConfirmationKind, StringComparison.Ordinal)
            ? "错误：工作计划已制定，但用户尚未确认开始执行。请等待用户回复确认后再派发步骤。"
            : null;
    }
}
