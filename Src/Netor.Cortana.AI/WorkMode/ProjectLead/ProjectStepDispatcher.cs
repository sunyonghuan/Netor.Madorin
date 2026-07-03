using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.ProjectLead;

/// <summary>
/// C 层专员派发器：按步骤 role 构建单个文件版 Agent，并执行一次性局部任务。
/// </summary>
public sealed class ProjectStepDispatcher(
    WorkTaskService taskService,
    AgentService agentService,
    AiProviderService providerService,
    AiModelService modelService,
    AIAgentFactory agentFactory,
    WorkExecutionLogService logService,
    IPublisher publisher,
    ILogger<ProjectStepDispatcher> logger) : IProjectStepDispatcher
{
    public async Task<string> DispatchAsync(
        string taskId,
        WorkTaskPlanStepFile step,
        WorkTaskEnvironmentFile? environment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(step);
        using var nonExpertScope = NonExpertProcessScope.Enter();

        if (string.IsNullOrWhiteSpace(step.Role))
        {
            throw new InvalidOperationException($"计划步骤 {step.Id} 缺少 role，无法选择 C 层专员。");
        }

        var task = taskService.GetById(taskId)
            ?? throw new InvalidOperationException($"工作任务不存在：{taskId}");
        var agent = ResolveAgentFromMentions(task.MentionsJson, step.Role)
            ?? agentService.FindByNameOrDisplayName(step.Role)
            ?? throw new InvalidOperationException($"未找到步骤角色对应的智能体：{step.Role}");
        var provider = providerService.GetById(task.Provider)
            ?? throw new InvalidOperationException($"工作任务缺少有效 AI 提供商：{task.Provider}");
        var model = modelService.GetById(task.Model)
            ?? throw new InvalidOperationException($"工作任务缺少有效模型：{task.Model}");

        logger.LogInformation(
            "派发 C 层专员步骤：TaskId={TaskId}, StepId={StepId}, Role={Role}, Agent={Agent}",
            taskId,
            step.Id,
            step.Role,
            agent.Name);

        var subAgent = agentFactory.BuildWorkModeSubAgent(agent, provider, model);
        var message = new ChatMessage(ChatRole.User, BuildStepInput(step, environment));
        var streamProcessor = new WorkModeStreamProcessor(taskId, publisher, logService, persistThinking: true);
        await foreach (var chunk in subAgent.RunStreamingAsync([message], cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Contents.Count > 0)
            {
                await streamProcessor.ProcessChunkAsync(chunk.Contents, cancellationToken).ConfigureAwait(false);
            }
        }

        await streamProcessor.FlushAsync(cancellationToken).ConfigureAwait(false);
        var summary = streamProcessor.GetAccumulatedText().Trim();

        return string.IsNullOrWhiteSpace(summary)
            ? $"步骤 {step.Id} 已执行，但模型未返回文本摘要。"
            : summary;
    }

    private AgentEntity? ResolveAgentFromMentions(string? mentionsJson, string role)
    {
        if (string.IsNullOrWhiteSpace(mentionsJson))
        {
            return null;
        }

        try
        {
            var mentions = JsonSerializer.Deserialize(mentionsJson, WorkModeJsonContext.Default.ListAgentMentionDto);
            var mention = mentions?.FirstOrDefault(item =>
                string.Equals(item.AgentId, role, StringComparison.Ordinal) ||
                string.Equals(item.AgentName, role, StringComparison.Ordinal));

            if (mention is null)
            {
                return null;
            }

            return agentService.GetById(mention.AgentId)
                ?? agentService.FindByNameOrDisplayName(mention.AgentName);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "工作任务 MentionsJson 解析失败，回退到全局 Agent 查找。");
            return null;
        }
    }

    private static string BuildStepInput(WorkTaskPlanStepFile step, WorkTaskEnvironmentFile? environment)
    {
        var environmentBlock = environment is null
            ? "（无环境信息）"
            : BuildEnvironmentBlock(environment);

        return $"""
            # 当前任务环境
            {environmentBlock}

            # 当前步骤
            - step_id: {step.Id}
            - title: {step.Title}
            - role: {step.Role}

            # 输入
            {step.Input}

            # 输出要求
            请只完成当前步骤，并遵守以下规则：
            - 涉及目录、文件、脚本或系统状态时，必须调用真实工具完成或检查。
            - 禁止在文本中模拟工具调用、伪造工具结果或假设文件已经存在。
            - 如果缺少必要环境信息或没有可用工具，请明确说明阻塞原因，不要声称已完成。
            - 完成后用简洁中文总结本步骤产出、关键文件或外部系统变更。
            """;
    }

    private static string BuildEnvironmentBlock(WorkTaskEnvironmentFile environment)
    {
        var lines = new List<string>();
        foreach (var pair in environment.Values)
        {
            lines.Add($"- {pair.Key}: {pair.Value}");
        }

        if (!string.IsNullOrWhiteSpace(environment.Notes))
        {
            lines.Add($"- notes: {environment.Notes}");
        }

        return lines.Count == 0 ? "（无环境信息）" : string.Join(Environment.NewLine, lines);
    }
}
