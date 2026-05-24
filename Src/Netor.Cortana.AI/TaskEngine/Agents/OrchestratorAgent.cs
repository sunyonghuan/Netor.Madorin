using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.TaskEngine.Models;
using Netor.Cortana.AI.TaskEngine.Persistence;

namespace Netor.Cortana.AI.TaskEngine.Agents;

/// <summary>
/// <see cref="IOrchestratorAgent"/> 的默认实现。
/// 每个阶段方法：构建 system prompt + user message → 调用 SubAgentRunner → 解析 JSON 结果。
/// 不持有任何对话状态。每次调用都是独立的 LLM 请求（无状态决策模型，doc 05 §1）。
///
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §8.3。
/// </summary>
internal sealed class OrchestratorAgent : IOrchestratorAgent
{
    private readonly SubAgentRunner _runner;
    private readonly IPlanPersistence _persistence;
    private readonly ILogger<OrchestratorAgent> _logger;

    public OrchestratorAgent(
        SubAgentRunner runner,
        IPlanPersistence persistence,
        ILogger<OrchestratorAgent> logger)
    {
        _runner = runner;
        _persistence = persistence;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 阶段 1: 需求分析
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<RequirementsAnalysis> RunRequirementsPhaseAsync(
        string taskId, string userInput, CancellationToken ct)
    {
        _logger.LogInformation("P4 需求分析阶段开始: {TaskId}", taskId);

        var userMessage = $"请分析以下任务需求：\n\n{userInput}";

        var result = await _runner.RunJsonAsync(
            OrchestratorPrompts.RequirementsAnalyst,
            userMessage,
            TaskEngineJsonContext.Default.RequirementsAnalysisDto,
            ct).ConfigureAwait(false);

        // 如果 JSON 解析失败，构建基础需求分析
        if (result is null)
        {
            _logger.LogWarning("P4 需求分析 JSON 解析失败，使用回退策略: {TaskId}", taskId);
            return new RequirementsAnalysis
            {
                OriginalInput = userInput,
                KeyPoints = [userInput.Length > 100 ? userInput[..100] : userInput],
                Constraints = [],
                ExpectedDeliverable = "完成用户描述的任务",
                ComplexityLevel = "medium",
            };
        }

        var analysis = new RequirementsAnalysis
        {
            OriginalInput = result.OriginalInput ?? userInput,
            KeyPoints = result.KeyPoints ?? [userInput],
            Constraints = result.Constraints ?? [],
            ExpectedDeliverable = result.ExpectedDeliverable ?? "完成用户描述的任务",
            ComplexityLevel = result.ComplexityLevel ?? "medium",
        };

        _logger.LogInformation("P4 需求分析完成: {TaskId}（{PointCount} 个要点，复杂度={Complexity}）",
            taskId, analysis.KeyPoints.Count, analysis.ComplexityLevel);

        return analysis;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 阶段 2: 计划制定
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<ExecutionPlan> RunPlanningPhaseAsync(
        string taskId,
        RequirementsAnalysis requirements,
        ExecutionTemplate? template,
        CancellationToken ct)
    {
        _logger.LogInformation("P4 计划制定阶段开始: {TaskId}", taskId);

        // 构建 user message
        var userMessageBuilder = new StringBuilder();
        userMessageBuilder.AppendLine("请根据以下需求分析结果制定执行计划：");
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 需求要点");
        foreach (var point in requirements.KeyPoints)
        {
            userMessageBuilder.AppendLine($"- {point}");
        }

        if (requirements.Constraints.Count > 0)
        {
            userMessageBuilder.AppendLine();
            userMessageBuilder.AppendLine("## 约束条件");
            foreach (var constraint in requirements.Constraints)
            {
                userMessageBuilder.AppendLine($"- {constraint}");
            }
        }

        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine($"## 预期交付物");
        userMessageBuilder.AppendLine(requirements.ExpectedDeliverable);
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine($"## 复杂度");
        userMessageBuilder.AppendLine(requirements.ComplexityLevel);

        // 构建 system prompt（可选追加模板）
        var systemPrompt = OrchestratorPrompts.PlanningExpert;
        if (template is not null)
        {
            systemPrompt += OrchestratorPrompts.PlanningWithTemplate;
            systemPrompt += FormatTemplateForPrompt(template);
        }

        var result = await _runner.RunJsonAsync(
            systemPrompt,
            userMessageBuilder.ToString(),
            TaskEngineJsonContext.Default.PlanningResponseDto,
            ct).ConfigureAwait(false);

        // 构建 ExecutionPlan
        var plan = BuildExecutionPlan(taskId, requirements, result, template?.TemplateId);

        _logger.LogInformation("P4 计划制定完成: {TaskId}（{StepCount} 个步骤）",
            taskId, plan.Steps.Count);

        return plan;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 阶段 3: 执行单步
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<StepResult> ExecuteStepAsync(
        string taskId, ExecutionPlan plan, PlanStep step, CancellationToken ct)
    {
        _logger.LogInformation("P4 步骤执行开始: {TaskId}/{StepId} ({Title})",
            taskId, step.StepId, step.Title);

        // 构建 system prompt（替换占位符）
        var previousSummary = BuildPreviousStepsSummary(plan, step);
        var systemPrompt = OrchestratorPrompts.StepExecutor
            .Replace("{AgentTypeDescription}", step.AgentTypeDescription.Length > 0
                ? step.AgentTypeDescription
                : "通用任务执行专家", StringComparison.Ordinal)
            .Replace("{StepTitle}", step.Title, StringComparison.Ordinal)
            .Replace("{StepDescription}", step.Description, StringComparison.Ordinal)
            .Replace("{PreviousStepsSummary}", previousSummary.Length > 0
                ? previousSummary
                : "（无前置步骤结果）", StringComparison.Ordinal);

        var userMessage = "请执行上述任务，完成后输出 JSON 格式的结果。";

        var result = await _runner.RunJsonAsync(
            systemPrompt,
            userMessage,
            TaskEngineJsonContext.Default.StepExecutionResponseDto,
            ct).ConfigureAwait(false);

        if (result is null)
        {
            // 如果 JSON 解析失败，尝试用原始文本作为结果
            var rawText = await _runner.RunAsync(systemPrompt, userMessage, ct).ConfigureAwait(false);
            return new StepResult
            {
                StepId = step.StepId,
                Summary = rawText.Length > 100 ? rawText[..100] + "…" : rawText,
                Detail = rawText,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        return new StepResult
        {
            StepId = step.StepId,
            Summary = result.Summary ?? "步骤已完成",
            Detail = result.Detail,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // 阶段 4: 验证
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task RunValidationPhaseAsync(string taskId, ExecutionPlan plan, CancellationToken ct)
    {
        _logger.LogInformation("P4 验证阶段开始: {TaskId}", taskId);

        // 构建 user message：需求 + 所有步骤结果 + 最终目标
        var userMessageBuilder = new StringBuilder();
        userMessageBuilder.AppendLine("请验证以下任务的执行结果：");
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 最终目标");
        userMessageBuilder.AppendLine(plan.FinalGoal);
        userMessageBuilder.AppendLine();

        if (plan.Requirements is not null)
        {
            userMessageBuilder.AppendLine("## 需求要点");
            foreach (var point in plan.Requirements.KeyPoints)
            {
                userMessageBuilder.AppendLine($"- {point}");
            }
            userMessageBuilder.AppendLine();
        }

        userMessageBuilder.AppendLine("## 各步骤执行结果");
        foreach (var step in plan.Steps.Where(s => s.Status == PlanStepStatus.Completed).OrderBy(s => s.Sequence))
        {
            userMessageBuilder.AppendLine($"### 步骤 {step.Sequence}: {step.Title}");
            userMessageBuilder.AppendLine(step.ResultSummary ?? "（无摘要）");
            userMessageBuilder.AppendLine();
        }

        var result = await _runner.RunJsonAsync(
            OrchestratorPrompts.ValidationReviewer,
            userMessageBuilder.ToString(),
            TaskEngineJsonContext.Default.ValidationResponseDto,
            ct).ConfigureAwait(false);

        if (result is not null)
        {
            _logger.LogInformation(
                "P4 验证完成: {TaskId}（通过={Passed}, 分数={Score}, 摘要={Summary}）",
                taskId, result.Passed, result.Score, result.Summary);
        }
        else
        {
            _logger.LogWarning("P4 验证响应解析失败，视为通过: {TaskId}", taskId);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 差异分析（P4-5 使用）
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<PlanDiffResult> AnalyzePlanDiffAsync(
        string taskId, ExecutionPlan oldPlan, ExecutionPlan newPlan, CancellationToken ct)
    {
        _logger.LogInformation("P4 差异分析开始: {TaskId}", taskId);

        var userMessageBuilder = new StringBuilder();
        userMessageBuilder.AppendLine("请分析以下两个版本的执行计划差异：");
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 修改前的计划");
        userMessageBuilder.AppendLine(FormatPlanForDiff(oldPlan));
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 修改后的计划");
        userMessageBuilder.AppendLine(FormatPlanForDiff(newPlan));

        var result = await _runner.RunJsonAsync(
            OrchestratorPrompts.DiffAnalyst,
            userMessageBuilder.ToString(),
            TaskEngineJsonContext.Default.PlanDiffResult,
            ct).ConfigureAwait(false);

        if (result is not null)
        {
            _logger.LogInformation("P4 差异分析完成: {TaskId}（重做={Redo}, 保留={Keep}）",
                taskId, result.StepsToRedo.Count, result.StepsToKeep.Count);
            return result;
        }

        // 解析失败时保守策略：全部重做
        _logger.LogWarning("P4 差异分析解析失败，保守策略全部重做: {TaskId}", taskId);
        return new PlanDiffResult
        {
            StepsToRedo = newPlan.Steps.Select(s => s.StepId).ToList(),
            StepsToKeep = [],
            UpdatedDependencies = [],
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // 内部辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 构建前置步骤的结果摘要（L2 上下文注入，doc 05 §2.3）。
    /// 包含当前步骤所依赖的已完成步骤的 ResultSummary。
    /// </summary>
    private static string BuildPreviousStepsSummary(ExecutionPlan plan, PlanStep currentStep)
    {
        var sb = new StringBuilder();
        IEnumerable<PlanStep> relevantSteps;

        if (currentStep.DependsOn.Count > 0)
        {
            // 包含所有显式依赖的步骤
            relevantSteps = plan.Steps
                .Where(s => currentStep.DependsOn.Contains(s.StepId) && s.Status == PlanStepStatus.Completed)
                .OrderBy(s => s.Sequence);
        }
        else
        {
            // 无显式依赖时，包含最近 2 个已完成步骤（滑动窗口）
            relevantSteps = plan.Steps
                .Where(s => s.Sequence < currentStep.Sequence && s.Status == PlanStepStatus.Completed)
                .OrderByDescending(s => s.Sequence)
                .Take(2)
                .OrderBy(s => s.Sequence);
        }

        foreach (var step in relevantSteps)
        {
            sb.AppendLine($"### 步骤 {step.Sequence} — {step.Title}");
            sb.AppendLine(step.ResultSummary ?? "（已完成，无摘要）");
            sb.AppendLine();
        }

        // 控制总长度不超过 ~3000 字符（约 2000 tokens）
        var result = sb.ToString();
        if (result.Length > 3000)
        {
            result = result[..3000] + "\n\n（摘要已截断）";
        }

        return result;
    }

    /// <summary>从 PlanningResponseDto 构建 ExecutionPlan 对象。</summary>
    private static ExecutionPlan BuildExecutionPlan(
        string taskId,
        RequirementsAnalysis requirements,
        PlanningResponseDto? dto,
        string? sourceTemplateId)
    {
        var now = DateTimeOffset.UtcNow;

        var plan = new ExecutionPlan
        {
            PlanId = $"plan-{taskId}",
            TaskId = taskId,
            Version = 1,
            TaskSummary = dto?.TaskSummary ?? "任务执行计划",
            Requirements = requirements,
            FinalGoal = dto?.FinalGoal ?? requirements.ExpectedDeliverable,
            CreatedAt = now,
            LastModifiedAt = now,
            Status = PlanStatus.Draft,
            SourceTemplateId = sourceTemplateId,
        };

        if (dto?.Steps is { Count: > 0 })
        {
            for (var i = 0; i < dto.Steps.Count; i++)
            {
                var stepDto = dto.Steps[i];
                var stepId = $"step-{i + 1:D2}";

                var step = new PlanStep
                {
                    StepId = stepId,
                    Sequence = i + 1,
                    Title = stepDto.Title ?? $"步骤 {i + 1}",
                    Description = stepDto.Description ?? string.Empty,
                    ExecutionMode = stepDto.ExecutionMode ?? "sequential",
                    AgentTypeDescription = stepDto.AgentTypeDescription ?? "通用任务执行专家",
                    RequiredTools = stepDto.RequiredTools ?? [],
                    RequireUserConfirmation = stepDto.RequireUserConfirmation,
                    MaxRetries = 3,
                };

                // 解析依赖关系（DTO 中用数组索引字符串，转换为 stepId）
                if (stepDto.DependsOn is { Count: > 0 })
                {
                    foreach (var dep in stepDto.DependsOn)
                    {
                        if (int.TryParse(dep, out var depIndex) && depIndex >= 0 && depIndex < dto.Steps.Count)
                        {
                            step.DependsOn.Add($"step-{depIndex + 1:D2}");
                        }
                    }
                }

                // 解析子任务
                if (stepDto.SubTasks is { Count: > 0 })
                {
                    for (var j = 0; j < stepDto.SubTasks.Count; j++)
                    {
                        var subDto = stepDto.SubTasks[j];
                        step.SubTasks.Add(new PlanSubTask
                        {
                            SubTaskId = $"{stepId}-sub-{j + 1:D2}",
                            Title = subDto.Title ?? $"子任务 {j + 1}",
                            Description = subDto.Description ?? string.Empty,
                            AgentTypeDescription = subDto.AgentTypeDescription ?? string.Empty,
                        });
                    }
                }

                plan.Steps.Add(step);
            }
        }
        else
        {
            // LLM 未能生成有效步骤，创建一个默认步骤
            plan.Steps.Add(new PlanStep
            {
                StepId = "step-01",
                Sequence = 1,
                Title = "执行任务",
                Description = requirements.ExpectedDeliverable,
                ExecutionMode = "sequential",
                AgentTypeDescription = "通用任务执行专家",
                MaxRetries = 3,
            });
        }

        return plan;
    }

    /// <summary>将模板结构格式化为 prompt 文本。</summary>
    private static string FormatTemplateForPrompt(ExecutionTemplate template)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"模板名称: {template.Name}");
        sb.AppendLine($"模板描述: {template.Description}");
        sb.AppendLine("步骤结构:");

        foreach (var step in template.Steps.OrderBy(s => s.Sequence))
        {
            sb.AppendLine($"  {step.Sequence}. [{step.ExecutionMode}] {step.Title} — {step.Description}");
            if (step.SubTasks.Count > 0)
            {
                foreach (var sub in step.SubTasks)
                {
                    sb.AppendLine($"     - {sub.Title}: {sub.Description}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>将计划格式化为差异分析用的文本。</summary>
    private static string FormatPlanForDiff(ExecutionPlan plan)
    {
        var sb = new StringBuilder();
        foreach (var step in plan.Steps.OrderBy(s => s.Sequence))
        {
            var status = step.Status switch
            {
                PlanStepStatus.Completed => "[已完成]",
                PlanStepStatus.Running => "[执行中]",
                PlanStepStatus.Failed => "[失败]",
                _ => "[待执行]",
            };

            sb.AppendLine($"步骤 {step.Sequence} (ID={step.StepId}) {status}: {step.Title}");
            sb.AppendLine($"  描述: {step.Description}");
            sb.AppendLine($"  模式: {step.ExecutionMode}");

            if (step.DependsOn.Count > 0)
                sb.AppendLine($"  依赖: {string.Join(", ", step.DependsOn)}");

            if (step.Status == PlanStepStatus.Completed && !string.IsNullOrEmpty(step.ResultSummary))
                sb.AppendLine($"  结果: {step.ResultSummary}");

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
