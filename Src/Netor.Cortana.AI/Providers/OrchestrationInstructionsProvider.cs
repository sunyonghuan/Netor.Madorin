using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 当用户 @ 了 2 个及以上子智能体时，给主 Agent 注入 "Coordinator" 协调指令。
/// 阶段 1 仅通过 instructions 文本约束行为，阶段 2A 起转为代码层强制。
///
/// 参见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 1 与 §1.2。
/// </summary>
internal sealed class OrchestrationInstructionsProvider(
    IReadOnlyList<AgentEntity> mentionedAgents) : AIContextProvider
{
    /// <summary>
    /// 阶段 1 软约束：建议最多拆分的子任务数量。
    /// 阶段 2A 起会下沉到 SystemSettingsService 配置项 orchestration.maxSubTasks，并在代码层强制。
    /// </summary>
    private const int MaxSubTasksHint = 3;

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // 单 mention 或零 mention 时不参与，避免干扰普通对话与轻量子 Agent 模式
        if (mentionedAgents.Count < 2)
        {
            return new ValueTask<AIContext>(new AIContext());
        }

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = BuildCoordinatorInstructions(mentionedAgents)
        });
    }

    private static string BuildCoordinatorInstructions(IReadOnlyList<AgentEntity> agents)
    {
        var lines = new List<string>(capacity: 16 + agents.Count)
        {
            $"你是一个多智能体协调者。用户在本轮对话中 @ 提及了 {agents.Count} 个子智能体：",
            string.Empty,
        };

        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            var desc = string.IsNullOrWhiteSpace(a.Description) ? "（未提供描述）" : a.Description!;
            lines.Add($"  {i + 1}. {a.Name} — {desc}");
        }

        lines.AddRange(
        [
            string.Empty,
            "请按以下规则工作：",
            string.Empty,
            "1. 先制定一个简短计划（不超过 5 个步骤），然后按计划执行。",
            $"2. 根据任务需要调用一个或多个子智能体工具，最多 {MaxSubTasksHint} 个子任务。调用工具时：",
            "   - 必须按工具签名传入 attachmentPaths 和 attachmentDescriptions（如果用户提供了附件）",
            "   - 不要把不相关的附件传给所有子智能体，按主题分发",
            "3. 不要把子智能体的输出直接拼接，必须做冲突检查和汇总。",
            "4. 最终输出包括：",
            "   - 最终回答（清晰、结构化）",
            "   - 简短的「参与智能体」列表（在末尾，作为过渡方案；阶段 2A 起由宿主接管）",
            string.Empty,
            "注意：你的工作是协调和总结，不是亲自完成所有子任务。",
        ]);

        return string.Join("\n", lines);
    }
}
