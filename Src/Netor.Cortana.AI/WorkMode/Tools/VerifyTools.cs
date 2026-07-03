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
/// 验收工具：verify_step / send_back。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §3.3。
/// </summary>
public sealed class VerifyTools
{
    private readonly WorkTaskService _taskService;
    private readonly WorkExecutionLogService _logService;
    private readonly IPublisher _publisher;
    private readonly string _taskId;

    public VerifyTools(
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

    public AIFunction CreateVerifyStepTool()
    {
        [Description("验收当前步骤的执行结果")]
        async Task<string> VerifyStepAsync(
            [Description("步骤标题（如 '1.1 连接 ERP'）")] string stepTitle,
            [Description("验收结论：PASS 或 FAIL")] string verdict,
            [Description("验收说明（通过原因或失败原因）")] string reason,
            CancellationToken ct)
        {
            var normalizedVerdict = verdict.Trim().ToUpperInvariant();
            if (normalizedVerdict is not "PASS" and not "FAIL")
                return "错误：verdict 必须是 PASS 或 FAIL";

            var logRecord = new WorkExecutionLogRecords.Acceptance(stepTitle, normalizedVerdict, reason);
            _logService.Append(_taskId, WorkExecutionLogTypes.Acceptance,
                JsonSerializer.Serialize(logRecord, WorkModeJsonContext.Default.Acceptance));

            if (normalizedVerdict == "PASS")
            {
                var completeRecord = new WorkExecutionLogRecords.StepComplete(stepTitle, reason);
                _logService.Append(_taskId, WorkExecutionLogTypes.StepComplete,
                    JsonSerializer.Serialize(completeRecord, WorkModeJsonContext.Default.StepComplete));

                await _publisher.PublishAsync(
                    Events.OnWorkStepCompleted,
                    new WorkStepCompletedArgs(_taskId, stepTitle, $"PASS: {reason}"));

                return $"步骤 [{stepTitle}] 验收通过。可以继续下一步。";
            }

            return $"步骤 [{stepTitle}] 验收未通过：{reason}\n" +
                   "请调用 send_back 退回重做，或直接修正后再次 verify_step。";
        }

        return AIFunctionFactory.Create(VerifyStepAsync, new AIFunctionFactoryOptions
        {
            Name = "verify_step",
            Description = "验收当前步骤的执行结果。PASS 表示通过，FAIL 表示需要重做或退回。"
        });
    }

    public AIFunction CreateSendBackTool()
    {
        [Description("退回步骤要求重做")]
        async Task<string> SendBackAsync(
            [Description("退回的步骤标题")] string stepTitle,
            [Description("退回原因和改进要求")] string reason,
            CancellationToken ct)
        {
            var logRecord = new WorkExecutionLogRecords.Acceptance(stepTitle, "SEND_BACK", reason);
            _logService.Append(_taskId, WorkExecutionLogTypes.Acceptance,
                JsonSerializer.Serialize(logRecord, WorkModeJsonContext.Default.Acceptance));

            await _publisher.PublishAsync(
                Events.OnWorkStepStarted,
                new WorkStepStartedArgs(_taskId, $"{stepTitle}（重做）", "退回重做"));

            return $"步骤 [{stepTitle}] 已退回重做。\n" +
                   $"改进要求：{reason}\n\n" +
                   "请根据改进要求重新执行该步骤。";
        }

        return AIFunctionFactory.Create(SendBackAsync, new AIFunctionFactoryOptions
        {
            Name = "send_back",
            Description = "退回步骤要求重做。当 verify_step 判定 FAIL 时使用，提供改进要求后重新执行该步骤。"
        });
    }
}
