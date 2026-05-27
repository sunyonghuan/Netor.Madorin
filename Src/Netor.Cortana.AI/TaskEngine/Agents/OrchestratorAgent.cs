using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.TaskEngine.Models;
using Netor.Cortana.AI.TaskEngine.Persistence;

namespace Netor.Cortana.AI.TaskEngine.Agents;

/// <summary>
/// <see cref="IOrchestratorAgent"/> 的默认实现——纯编排器模式。
///
/// 每个阶段方法：构建 system prompt + user message → 调用 SubAgentRunner → 解析结果。
/// 不持有任何对话状态。每次调用都是独立的 LLM 请求（无状态决策模型，doc 05 §1）。
///
/// P4 重写要点（相较 P4-3 骨架版）：
/// - 阶段 1 使用 RunMultiTurnAsync 支持多轮澄清对话
/// - 阶段 3 步骤执行使用 RunMultiTurnAsync 支持长任务分段输出
/// - 更丰富的上下文注入（前置步骤 L2 滑动窗口 + 显式依赖）
/// - 验证阶段返回结构化结果供引擎决策
///
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §8.3。
/// </summary>
internal sealed class OrchestratorAgent : IOrchestratorAgent
{
    private readonly SubAgentRunner _runner;
    private readonly IPlanPersistence _persistence;
    private readonly AIAgentFactory _agentFactory;
    private readonly ILogger<OrchestratorAgent> _logger;

    /// <summary>需求分析多轮对话最大轮次。</summary>
    private const int RequirementsMaxTurns = 5;

    /// <summary>步骤执行多轮对话最大轮次。</summary>
    private const int StepExecutionMaxTurns = 8;

    public OrchestratorAgent(
        SubAgentRunner runner,
        IPlanPersistence persistence,
        AIAgentFactory agentFactory,
        ILogger<OrchestratorAgent> logger)
    {
        _runner = runner;
        _persistence = persistence;
        _agentFactory = agentFactory;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 阶段 1: 需求分析
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<RequirementsAnalysis> RunRequirementsPhaseAsync(
        string taskId, string userInput, Func<int, string, Task<string>>? onAskUser,
        Action<string>? onAiMessage, CancellationToken ct)
    {
        _logger.LogInformation("P4 需求分析阶段开始: {TaskId}", taskId);

        var userMessage = $"""
            请分析以下任务需求：

            ---
            {userInput}
            ---

            如果信息充足，直接输出结构化需求 JSON 并以 [DONE] 结尾。
            如果信息不足需要向用户确认，提出澄清问题并以 [ASK_USER] 结尾。
            """;

        string fullResponse;

        if (onAskUser is not null)
        {
            // 交互式模式：子智能体可通过 [ASK_USER] 向用户提问
            fullResponse = await _runner.RunInteractiveAsync(
                OrchestratorPrompts.RequirementsAnalyst,
                userMessage,
                RequirementsMaxTurns,
                onAskUser,
                (turn, text) =>
                {
                    _logger.LogDebug("P4 需求分析第{Turn}轮: {TaskId} ({Len}c)", turn, taskId, text.Length);
                    // [DONE] 轮次是最终结构化 JSON 输出，不对用户展示
                    if (!text.Contains("[DONE]", StringComparison.OrdinalIgnoreCase))
                        onAiMessage?.Invoke(CleanLlmMarkers(text));
                },
                ct).ConfigureAwait(false);
        }
        else
        {
            // 非交互模式：LLM 自我迭代（向后兼容）
            fullResponse = await _runner.RunMultiTurnAsync(
                OrchestratorPrompts.RequirementsAnalyst,
                userMessage,
                RequirementsMaxTurns,
                (turn, text) =>
                {
                    _logger.LogDebug("P4 需求分析第{Turn}轮: {TaskId} ({Len}c)", turn, taskId, text.Length);
                    if (!text.Contains("[DONE]", StringComparison.OrdinalIgnoreCase))
                        onAiMessage?.Invoke(CleanLlmMarkers(text));
                },
                ct).ConfigureAwait(false);
        }

        // 从多轮对话的最终响应中提取 JSON
        var result = TryParseJson<RequirementsAnalysisDto>(
            fullResponse, TaskEngineJsonContext.Default.RequirementsAnalysisDto);

        // 如果 JSON 解析失败，构建基础需求分析
        if (result is null)
        {
            _logger.LogWarning("P4 需求分析 JSON 解析失败，使用回退策略: {TaskId}", taskId);
            return new RequirementsAnalysis
            {
                OriginalInput = userInput,
                KeyPoints = [userInput.Length > 200 ? userInput[..200] : userInput],
                Constraints = [],
                ExpectedDeliverable = "完成用户描述的任务",
                ComplexityLevel = "medium",
            };
        }

        var analysis = new RequirementsAnalysis
        {
            OriginalInput = result.OriginalInput ?? userInput,
            KeyPoints = result.KeyPoints is { Count: > 0 } ? result.KeyPoints : [userInput],
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
        Func<int, string, Task<string>>? onAskUser,
        Action<string>? onAiMessage,
        CancellationToken ct)
    {
        _logger.LogInformation("P4 计划制定阶段开始: {TaskId}", taskId);

        // 构建 user message（将需求分析结果作为上下文传入）
        var userMessageBuilder = new StringBuilder(1024);
        userMessageBuilder.AppendLine("请根据以下需求分析结果制定执行计划：");
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 原始需求");
        userMessageBuilder.AppendLine(requirements.OriginalInput);
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
        userMessageBuilder.AppendLine("## 预期交付物");
        userMessageBuilder.AppendLine(requirements.ExpectedDeliverable);
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine($"## 复杂度评估: {requirements.ComplexityLevel}");

        // 注入当前系统可用工具列表（让 LLM 知道有哪些工具可分配给步骤）
        var availableTools = _agentFactory.GetAvailableToolNames();
        if (availableTools.Count > 0)
        {
            userMessageBuilder.AppendLine();
            userMessageBuilder.AppendLine("## 当前可用工具");
            userMessageBuilder.AppendLine("以下是系统中已注册的工具，你可以在步骤的 requiredTools 中引用它们的名称：");
            foreach (var toolName in availableTools)
            {
                userMessageBuilder.AppendLine($"- {toolName}");
            }
            userMessageBuilder.AppendLine();
            userMessageBuilder.AppendLine("注意：只有当步骤确实需要调用工具时才填写 requiredTools，纯文本分析/创作类步骤不需要工具。");
        }

        // 构建 system prompt（可选追加模板）
        var systemPrompt = OrchestratorPrompts.PlanningExpert;
        if (template is not null)
        {
            systemPrompt += OrchestratorPrompts.PlanningWithTemplate;
            systemPrompt += FormatTemplateForPrompt(template);
        }

        PlanningResponseDto? result;

        if (onAskUser is not null)
        {
            // 交互式模式：子智能体可通过 [ASK_USER] 向用户确认计划偏好
            var fullResponse = await _runner.RunInteractiveAsync(
                systemPrompt,
                userMessageBuilder.ToString(),
                RequirementsMaxTurns,
                onAskUser,
                (turn, text) =>
                {
                    _logger.LogDebug("P4 计划制定第{Turn}轮: {TaskId} ({Len}c)", turn, taskId, text.Length);
                    if (!text.Contains("[DONE]", StringComparison.OrdinalIgnoreCase))
                        onAiMessage?.Invoke(CleanLlmMarkers(text));
                },
                ct).ConfigureAwait(false);

            result = TryParseJson<PlanningResponseDto>(
                fullResponse, TaskEngineJsonContext.Default.PlanningResponseDto);
        }
        else
        {
            // 非交互模式：单轮 JSON 生成（向后兼容）
            result = await _runner.RunJsonAsync(
                systemPrompt,
                userMessageBuilder.ToString(),
                TaskEngineJsonContext.Default.PlanningResponseDto,
                ct).ConfigureAwait(false);
        }

        // 构建 ExecutionPlan
        var plan = BuildExecutionPlan(taskId, requirements, result, template?.TemplateId);

        _logger.LogInformation("P4 计划制定完成: {TaskId}（{StepCount} 个步骤，目标={Goal}）",
            taskId, plan.Steps.Count, plan.FinalGoal.Length > 50 ? plan.FinalGoal[..50] + "…" : plan.FinalGoal);

        // 向对话流推送 Markdown 格式的计划摘要，让用户可以查看并确认（问题 1）
        if (onAiMessage is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"好的，我已为您制定了执行计划（共 {plan.Steps.Count} 步）：");
            sb.AppendLine();
            foreach (var step in plan.Steps)
            {
                sb.AppendLine($"**步骤 {step.Sequence}：{step.Title}**");
                if (!string.IsNullOrWhiteSpace(step.Description))
                    sb.AppendLine(step.Description);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(plan.FinalGoal))
                sb.AppendLine($"最终目标：{plan.FinalGoal}");
            sb.AppendLine();
            sb.AppendLine("请告诉我是否确认执行，或需要调整哪些步骤。");
            onAiMessage.Invoke(sb.ToString());
        }

        return plan;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 阶段 3: 执行单步
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<StepResult> ExecuteStepAsync(
        string taskId, ExecutionPlan plan, PlanStep step,
        Action<string>? onAiMessage, string? workspaceDir, CancellationToken ct)
    {
        _logger.LogInformation("P4 步骤执行开始: {TaskId}/{StepId} (#{Seq} {Title})",
            taskId, step.StepId, step.Sequence, step.Title);

        // 确保工作目录存在
        if (!string.IsNullOrWhiteSpace(workspaceDir))
        {
            Directory.CreateDirectory(workspaceDir);
            _logger.LogDebug("P4 步骤工作目录: {WorkspaceDir}", workspaceDir);
        }

        // 构建 system prompt（替换占位符）
        var previousSummary = BuildPreviousStepsSummary(plan, step);
        var systemPrompt = OrchestratorPrompts.StepExecutor
            .Replace("{AgentTypeDescription}", step.AgentTypeDescription.Length > 0
                ? step.AgentTypeDescription
                : "通用任务执行专家", StringComparison.Ordinal)
            .Replace("{StepTitle}", step.Title, StringComparison.Ordinal)
            .Replace("{StepDescription}", step.Description, StringComparison.Ordinal)
            .Replace("{WorkspaceDirectory}", string.IsNullOrWhiteSpace(workspaceDir)
                ? "（未指定工作目录，请将文件写入当前目录）"
                : workspaceDir, StringComparison.Ordinal)
            .Replace("{PreviousStepsSummary}", previousSummary.Length > 0
                ? previousSummary
                : "（本步骤无前置依赖，独立执行）", StringComparison.Ordinal);

        // 构建 user message（包含任务总目标作为方向感）
        var userMessage = $"""
            请执行上述任务。

            任务总体目标：{plan.FinalGoal}
            当前是第 {step.Sequence}/{plan.Steps.Count} 步。

            完成后请输出 JSON 摘要并以 [DONE] 结尾。
            如果工作量较大需要分段完成，每段结束写 [CONTINUE]，最后一段写 [DONE]。
            """;

        // 执行步骤：有工具需求时走带工具的 AIAgent，否则走纯文本多轮对话
        string fullResponse;
        if (step.RequiredTools is { Count: > 0 })
        {
            _logger.LogDebug("P4 步骤 {StepId} 使用工具执行模式（工具={Tools}）",
                step.StepId, string.Join(",", step.RequiredTools));
            fullResponse = await _runner.RunWithToolsAsync(
                systemPrompt,
                userMessage,
                step.RequiredTools,
                $"step_{step.Sequence}_{step.Title}"[..Math.Min(32, $"step_{step.Sequence}_{step.Title}".Length)],
                text =>
                {
                    _logger.LogDebug("P4 步骤执行工具模式 {TaskId}/{StepId} 输出 ({Len}c)",
                        taskId, step.StepId, text.Length);
                    onAiMessage?.Invoke(text);
                },
                ct).ConfigureAwait(false);
        }
        else
        {
            // 无工具需求：纯文本多轮对话（支持长任务分段输出）
            fullResponse = await _runner.RunMultiTurnAsync(
                systemPrompt,
                userMessage,
                StepExecutionMaxTurns,
                (turn, text) =>
                {
                    _logger.LogDebug(
                        "P4 步骤执行 {TaskId}/{StepId} 第{Turn}轮完成 ({Len}c)",
                        taskId, step.StepId, turn, text.Length);
                    if (!text.Contains("[DONE]", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("[CONTINUE]", StringComparison.OrdinalIgnoreCase))
                        onAiMessage?.Invoke(CleanLlmMarkers(text));
                },
                ct).ConfigureAwait(false);
        }

        // 从最终响应中提取 JSON 摘要
        var resultDto = TryParseJson<StepExecutionResponseDto>(
            fullResponse, TaskEngineJsonContext.Default.StepExecutionResponseDto);

        if (resultDto is not null)
        {
            return new StepResult
            {
                StepId = step.StepId,
                Summary = resultDto.Summary ?? "步骤已完成",
                Detail = resultDto.Detail ?? ExtractNonJsonContent(fullResponse),
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        // JSON 解析失败时，使用完整响应文本作为结果
        _logger.LogDebug("P4 步骤执行 {StepId} JSON 摘要解析失败，使用原始文本", step.StepId);
        return new StepResult
        {
            StepId = step.StepId,
            Summary = fullResponse.Length > 100 ? fullResponse[..100] + "…" : fullResponse,
            Detail = fullResponse,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // 阶段 4: 验证
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<ValidationResult> RunValidationPhaseAsync(string taskId, ExecutionPlan plan, CancellationToken ct)
    {
        _logger.LogInformation("P4 验证阶段开始: {TaskId}", taskId);

        // 构建 user message：原始需求 + 最终目标 + 所有步骤结果
        var userMessageBuilder = new StringBuilder(2048);
        userMessageBuilder.AppendLine("请验证以下任务的执行结果是否满足需求：");
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 最终目标");
        userMessageBuilder.AppendLine(plan.FinalGoal);
        userMessageBuilder.AppendLine();

        if (plan.Requirements is not null)
        {
            userMessageBuilder.AppendLine("## 原始需求要点");
            foreach (var point in plan.Requirements.KeyPoints)
            {
                userMessageBuilder.AppendLine($"- {point}");
            }

            if (plan.Requirements.Constraints.Count > 0)
            {
                userMessageBuilder.AppendLine();
                userMessageBuilder.AppendLine("## 约束条件");
                foreach (var constraint in plan.Requirements.Constraints)
                {
                    userMessageBuilder.AppendLine($"- {constraint}");
                }
            }
            userMessageBuilder.AppendLine();
        }

        userMessageBuilder.AppendLine("## 各步骤执行结果");
        foreach (var step in plan.Steps.Where(s => s.Status == PlanStepStatus.Completed).OrderBy(s => s.Sequence))
        {
            userMessageBuilder.AppendLine($"### 步骤 {step.Sequence}: {step.Title}");
            userMessageBuilder.AppendLine($"**摘要**: {step.ResultSummary ?? "（无摘要）"}");
            if (!string.IsNullOrEmpty(step.ResultDetail))
            {
                // 截断过长的详情，避免超出上下文
                var detail = step.ResultDetail.Length > 1000
                    ? step.ResultDetail[..1000] + "\n（详情已截断）"
                    : step.ResultDetail;
                userMessageBuilder.AppendLine($"**详情**: {detail}");
            }
            userMessageBuilder.AppendLine();
        }

        // 跳过的步骤也报告（让验证者知道）
        var skippedSteps = plan.Steps
            .Where(s => s.Status == PlanStepStatus.Skipped)
            .OrderBy(s => s.Sequence)
            .ToList();
        if (skippedSteps.Count > 0)
        {
            userMessageBuilder.AppendLine("## 被跳过的步骤");
            foreach (var step in skippedSteps)
            {
                userMessageBuilder.AppendLine($"- 步骤 {step.Sequence}: {step.Title}（已跳过）");
            }
            userMessageBuilder.AppendLine();
        }

        var dto = await _runner.RunJsonAsync(
            OrchestratorPrompts.ValidationReviewer,
            userMessageBuilder.ToString(),
            TaskEngineJsonContext.Default.ValidationResponseDto,
            ct).ConfigureAwait(false);

        ValidationResult validationResult;

        if (dto is not null)
        {
            validationResult = new ValidationResult
            {
                Passed = dto.Passed,
                Score = dto.Score,
                Summary = dto.Summary ?? string.Empty,
                Issues = dto.Issues ?? [],
                Suggestions = dto.Suggestions ?? [],
                CompletedAt = DateTimeOffset.UtcNow,
            };

            _logger.LogInformation(
                "P4 验证完成: {TaskId}（通过={Passed}, 分数={Score}, 摘要={Summary}）",
                taskId, validationResult.Passed, validationResult.Score, validationResult.Summary);

            // NOTE P4-Phase2: 如果验证不通过（score < 70），可以触发修复流程
            // 当前版本仅记录日志，不阻断完成流程
            if (!validationResult.Passed)
            {
                _logger.LogWarning(
                    "P4 验证未通过: {TaskId}（分数={Score}，问题={Issues}）",
                    taskId, validationResult.Score,
                    validationResult.Issues.Count > 0 ? string.Join("; ", validationResult.Issues) : "无具体问题");
            }
        }
        else
        {
            _logger.LogWarning("P4 验证响应解析失败，视为通过: {TaskId}", taskId);
            validationResult = new ValidationResult
            {
                Passed = true,
                Score = 100,
                Summary = "验证响应解析失败，默认视为通过",
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        return validationResult;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 文档 08 §3.2：对话意图识别
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<ConversationIntentResponseDto> RecognizeConversationIntentAsync(
        string taskId,
        IReadOnlyList<(string Role, string Content)> conversationHistory,
        string userMessage,
        string? planContext,
        CancellationToken ct)
    {
        _logger.LogInformation("P4 对话意图识别开始: {TaskId} ({MsgLen}c)", taskId, userMessage.Length);

        // 构建 user message：对话历史 + 当前计划上下文 + 用户输入
        var sb = new StringBuilder(1024);

        if (!string.IsNullOrEmpty(planContext))
        {
            sb.AppendLine("## 当前执行计划");
            sb.AppendLine(planContext);
            sb.AppendLine();
        }

        if (conversationHistory.Count > 0)
        {
            sb.AppendLine("## 对话历史（最近若干轮）");
            foreach (var (role, content) in conversationHistory)
            {
                var label = role == "user" ? "用户" : "AI";
                var preview = content.Length > 200 ? content[..200] + "…" : content;
                sb.AppendLine($"[{label}]: {preview}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 用户当前输入");
        sb.AppendLine(userMessage);

        var result = await _runner.RunJsonAsync(
            OrchestratorPrompts.ConversationIntentRecognizer,
            sb.ToString(),
            TaskEngineJsonContext.Default.ConversationIntentResponseDto,
            ct).ConfigureAwait(false);

        if (result is not null)
        {
            _logger.LogInformation(
                "P4 意图识别完成: {TaskId} intent={Intent} response={ResponseLen}c",
                taskId, result.Intent, result.Response?.Length ?? 0);
            return result;
        }

        // 解析失败时降级：当作 other 意图，返回友好错误提示
        _logger.LogWarning("P4 意图识别 JSON 解析失败，降级为 other: {TaskId}", taskId);
        return new ConversationIntentResponseDto
        {
            Intent = "other",
            Response = "我收到了你的消息，但处理时遇到了一些问题。请再试一次，或者换个方式表达你的想法。",
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // 差异分析（P4-5 使用）
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task<PlanDiffResult> AnalyzePlanDiffAsync(
        string taskId, ExecutionPlan oldPlan, ExecutionPlan newPlan, CancellationToken ct)
    {
        _logger.LogInformation("P4 差异分析开始: {TaskId} (v{OldVer} → v{NewVer})",
            taskId, oldPlan.Version, newPlan.Version);

        var userMessageBuilder = new StringBuilder(2048);
        userMessageBuilder.AppendLine("请分析以下两个版本的执行计划差异，判断哪些步骤需要重做：");
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 修改前的计划 (v" + oldPlan.Version + ")");
        userMessageBuilder.AppendLine(FormatPlanForDiff(oldPlan));
        userMessageBuilder.AppendLine();
        userMessageBuilder.AppendLine("## 修改后的计划 (v" + newPlan.Version + ")");
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
    /// 策略：显式依赖的步骤全部包含 + 无显式依赖时包含最近 2 个已完成步骤。
    /// 总长度限制 ~3000 字符（约 2000 tokens）。
    /// </summary>
    private static string BuildPreviousStepsSummary(ExecutionPlan plan, PlanStep currentStep)
    {
        var sb = new StringBuilder();
        IEnumerable<PlanStep> relevantSteps;

        if (currentStep.DependsOn.Count > 0)
        {
            // 包含所有显式依赖的步骤（结果完整注入）
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
            sb.AppendLine($"**结果摘要**: {step.ResultSummary ?? "（已完成，无摘要）"}");

            // 显式依赖的步骤注入详情（如果有的话）
            if (currentStep.DependsOn.Contains(step.StepId) && !string.IsNullOrEmpty(step.ResultDetail))
            {
                var detail = step.ResultDetail.Length > 800
                    ? step.ResultDetail[..800] + "\n（详情已截断）"
                    : step.ResultDetail;
                sb.AppendLine($"**详情**: {detail}");
            }
            sb.AppendLine();
        }

        // 控制总长度不超过 ~3000 字符
        var result = sb.ToString();
        if (result.Length > 3000)
        {
            result = result[..3000] + "\n\n（上下文已截断，仅保留最相关部分）";
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
                PlanStepStatus.Skipped => "[已跳过]",
                _ => "[待执行]",
            };

            sb.AppendLine($"步骤 {step.Sequence} (ID={step.StepId}) {status}: {step.Title}");
            sb.AppendLine($"  描述: {step.Description}");
            sb.AppendLine($"  模式: {step.ExecutionMode}");
            sb.AppendLine($"  执行者: {step.AgentTypeDescription}");

            if (step.DependsOn.Count > 0)
                sb.AppendLine($"  依赖: {string.Join(", ", step.DependsOn)}");

            if (step.Status == PlanStepStatus.Completed && !string.IsNullOrEmpty(step.ResultSummary))
                sb.AppendLine($"  结果摘要: {step.ResultSummary}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 去掉 LLM 控制标记，得到适合展示给用户的干净文本。
    /// </summary>
    private static string CleanLlmMarkers(string text)
        => text
            .Replace("[DONE]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[CONTINUE]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[ASK_USER]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

    /// <summary>
    /// 尝试从文本响应中提取并反序列化 JSON。
    /// 使用 SubAgentRunner.ExtractJsonBlock 提取 JSON 片段。
    /// </summary>
    private T? TryParseJson<T>(string text, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var jsonText = SubAgentRunner.ExtractJsonBlock(text);
        try
        {
            return JsonSerializer.Deserialize(jsonText, typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "P4 JSON 反序列化失败，原文前200字: {Preview}",
                text.Length > 200 ? text[..200] : text);
            return null;
        }
    }

    /// <summary>
    /// 从完整响应中提取非 JSON 部分作为详情内容。
    /// 用于步骤执行时 JSON 摘要之外的工作内容。
    /// </summary>
    private static string ExtractNonJsonContent(string fullResponse)
    {
        // 找到最后一个 ```json 代码块的开始位置
        var jsonBlockStart = fullResponse.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonBlockStart > 0)
        {
            // 返回 JSON 块之前的内容
            return fullResponse[..jsonBlockStart].Trim();
        }

        // 没有 JSON 块，返回完整内容
        return fullResponse;
    }
}
