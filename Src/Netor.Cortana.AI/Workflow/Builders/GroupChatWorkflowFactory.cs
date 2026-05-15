using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

// SDK 的 Workflow 类型名与本项目 namespace `Netor.Cortana.AI.Workflow` 同名，
// 编译器会优先解析为命名空间，因此用类型别名消除歧义。
using SdkWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace Netor.Cortana.AI.Workflow.Builders;

/// <summary>
/// 阶段 3B：把 SDK <see cref="GroupChatWorkflowBuilder"/> 的调用细节封装为单一职责工厂。
/// 输入是已经构建好的参与者集合（<see cref="AIAgent"/>），输出是 SDK 原生
/// <see cref="SdkWorkflow"/>，由 <c>WorkflowExecutor</c> 通过 <see cref="InProcessExecution.RunStreamingAsync"/>
/// 启动并消费 <see cref="WorkflowEvent"/> 流。
///
/// 设计要点：
/// - 默认采用 <see cref="RoundRobinGroupChatManager"/>（按顺序轮询参与者发言），最简单的群聊调度器。
/// - <c>MaximumIterationCount</c> 显式覆盖：SDK 默认 40 太长，按 [04] §3B.5 验收要求覆盖为
///   <see cref="WorkflowExecutorOptions.GroupChatMaxIterations"/>（默认 3）。
/// - 工厂保持纯函数：不持有状态，不订阅事件；所有运行期状态由 WorkflowExecutor 管理。
///
/// 详见：docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 3B.2 / §3B.5。
/// </summary>
internal static class GroupChatWorkflowFactory
{
    /// <summary>
    /// 构建 GroupChat <see cref="SdkWorkflow"/>。
    /// </summary>
    /// <param name="participants">参与群聊的 Agent 集合，至少 1 个。</param>
    /// <param name="taskId">任务 ID，用于设置 workflow 名称便于诊断。</param>
    /// <param name="options">编排器选项，用于读取 <see cref="WorkflowExecutorOptions.GroupChatMaxIterations"/>。</param>
    /// <param name="logger">日志输出器。</param>
    /// <returns>SDK 原生 <see cref="SdkWorkflow"/>，由 <c>InProcessExecution.RunStreamingAsync</c> 启动。</returns>
    /// <exception cref="InvalidOperationException">参与者为空。</exception>
    public static SdkWorkflow Build(
        IReadOnlyList<AIAgent> participants,
        string taskId,
        WorkflowExecutorOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (participants.Count == 0)
        {
            throw new InvalidOperationException("GroupChat workflow requires at least 1 participant.");
        }

        var maxIter = options.GroupChatMaxIterations;

        var workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents)
            {
                MaximumIterationCount = maxIter,
            })
            .AddParticipants(participants)
            .WithName($"GroupChat-{taskId}")
            .Build();

        logger.LogInformation(
            "GroupChat workflow 已构建：taskId={TaskId}, participants={Count}, maxIter={MaxIter}",
            taskId, participants.Count, maxIter);

        return workflow;
    }
}
