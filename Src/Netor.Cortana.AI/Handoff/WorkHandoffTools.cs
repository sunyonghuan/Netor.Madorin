using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Handoff;

/// <summary>
/// 专家模式和会议主持人共用的模式转交工具工厂。
/// </summary>
public sealed class WorkHandoffTools
{
    private readonly WorkTaskService _taskService;
    private readonly WorkflowExecutor _executor;
    private readonly MeetingMessageService _meetingMessages;
    private readonly AgentService _agentService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly WorkTaskFileService _fileService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WorkHandoffTools> _logger;

    public WorkHandoffTools(
        WorkTaskService taskService,
        WorkflowExecutor executor,
        MeetingMessageService meetingMessages,
        AgentService agentService,
        AiProviderService providerService,
        AiModelService modelService,
        WorkTaskFileService fileService,
        IServiceProvider serviceProvider,
        ILogger<WorkHandoffTools> logger)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _meetingMessages = meetingMessages ?? throw new ArgumentNullException(nameof(meetingMessages));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AIFunction CreateStartWorkTaskTool(
        string sourceKind,
        Func<HandoffRuntimeContext> contextFactory,
        Func<IReadOnlyList<AgentMention>>? defaultMentions = null,
        string? sourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentNullException.ThrowIfNull(contextFactory);

        [Description("""
        当用户表示要开始执行已经讨论清楚的方案时调用。
        调用前自行判断三条:
        1. 上下文是否包含明确目标和可执行步骤;
        2. 步骤是否已经达成共识;
        3. 用户最近是否表达了"开始做/去执行"的意图。
        任一不满足则不要调用,继续在当前模式追问对齐。
        """)]
        async Task<string> StartWorkTaskAsync(
            [Description("一句话任务标题(不超过 30 字)")]
            string title,
            [Description("交给工作模式总经理的目标陈述。写清 WHAT/边界,不复述步骤")]
            string goal,
            [Description("""
            已讨论好的执行计划(可选)。提供时工作模式将跳过 set_plan 直接派发。
            格式: {"main_steps":[{"title":"...","sub_steps":[{"title":"...","acceptance_criteria":"..."}]}]}
            """)]
            string? planJson,
            [Description("建议的子智能体 ID 列表(可选)。用 JSON 数组字符串或逗号/换行分隔文本表示；会议中默认 = 参会者除主持人外")]
            string? mentionAgentIds,
            CancellationToken ct)
        {
            try
            {
                return await StartWorkTaskCoreAsync(
                    sourceKind,
                    sourceId,
                    contextFactory,
                    defaultMentions,
                    title,
                    goal,
                    planJson,
                    mentionAgentIds,
                    ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return $"错误：计划 JSON 格式无效 - {ex.Message}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "模式转交创建工作任务失败，SourceKind={SourceKind}, SourceId={SourceId}", sourceKind, sourceId);
                return $"错误：创建工作任务失败 - {ex.Message}";
            }
        }

        return AIFunctionFactory.Create(StartWorkTaskAsync, new AIFunctionFactoryOptions
        {
            Name = "start_work_task",
            Description = "当用户明确要求开始执行已讨论清楚的方案时，创建工作模式任务并切换到工作模式。"
        });
    }

    private async Task<string> StartWorkTaskCoreAsync(
        string sourceKind,
        string? sourceId,
        Func<HandoffRuntimeContext> contextFactory,
        Func<IReadOnlyList<AgentMention>>? defaultMentions,
        string title,
        string goal,
        string? planJson,
        string? mentionAgentIds,
        CancellationToken cancellationToken)
    {
        var safeTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            return "错误：任务标题不能为空。";
        }

        var safeGoal = goal?.Trim();
        if (string.IsNullOrWhiteSpace(safeGoal))
        {
            return "错误：工作目标不能为空。";
        }

        var handoffSourceSummary = string.Empty;
        if (string.Equals(sourceKind, "meeting", StringComparison.OrdinalIgnoreCase))
        {
            var latestSummary = string.IsNullOrWhiteSpace(sourceId)
                ? null
                : _meetingMessages.GetLatestSummary(sourceId);
            if (latestSummary is null)
            {
                return "错误：请先用 output_summary 输出会议总结再转交工作。";
            }

            handoffSourceSummary = latestSummary.ContentMd;
        }

        var (normalizedPlanJson, planValidationError) = NormalizePlanJson(planJson);
        if (planValidationError is not null)
        {
            return planValidationError;
        }
        var context = ResolveContext(contextFactory());
        var activeTask = _taskService.GetActiveTask(context.SessionId!);
        if (activeTask is not null)
        {
            return $"错误：当前会话已有进行中的工作任务 {activeTask.Id}:{activeTask.Title}。请先在工作模式继续或结束该任务，再创建新的工作任务。";
        }

        var mentions = ResolveMentions(ParseStringListArgument(mentionAgentIds), defaultMentions);
        var mentionsJson = SerializeMentions(mentions);

        var task = new WorkTaskEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = context.SessionId!,
            WorkspaceId = context.WorkspaceId!,
            Title = safeTitle,
            InitialInput = safeGoal,
            IsActive = true,
            Provider = context.ProviderId!,
            Model = context.ModelId!,
            AgentName = context.AgentId!,
            MentionsJson = mentionsJson
        };

        _taskService.Create(task);
        SaveHandoffEnvironment(task.Id, safeGoal, sourceKind, sourceId, handoffSourceSummary, normalizedPlanJson is not null);
        if (!string.IsNullOrWhiteSpace(normalizedPlanJson))
        {
            var plan = JsonSerializer.Deserialize(normalizedPlanJson, WorkModeJsonContext.Default.WorkPlan)
                ?? throw new InvalidOperationException("结构化计划为空，无法生成 plan.yaml。");
            _fileService.SavePlan(WorkPlanLegacyConverter.ToPlanFile(plan, task));
        }

        var systemMessage = BuildHandoffSystemMessage(
            sourceKind,
            safeGoal,
            normalizedPlanJson is not null,
            handoffSourceSummary);
        _ = Task.Run(
            () => _executor.ExecuteFromHandoffAsync(
                task.Id,
                safeGoal,
                normalizedPlanJson,
                mentions,
                systemMessage,
                CancellationToken.None),
            CancellationToken.None);

        _serviceProvider.GetService<IWorkModeSwitcher>()?.SwitchToWorkMode(task.Id);

        await Task.CompletedTask.ConfigureAwait(false);
        return $"已创建工作任务 {task.Id}:{task.Title}。已切换到工作模式。";
    }

    private void SaveHandoffEnvironment(
        string taskId,
        string goal,
        string sourceKind,
        string? sourceId,
        string? sourceSummary,
        bool hasPrebuiltPlan)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace_dir"] = ResolveWorkspaceDirectory(),
            ["task_goal"] = goal,
            ["source_kind"] = sourceKind,
            ["has_prebuilt_plan"] = hasPrebuiltPlan ? "true" : "false"
        };

        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            values["source_id"] = sourceId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(sourceSummary))
        {
            values["source_requirements"] = sourceSummary.Trim();
        }

        _fileService.SaveEnvironment(new WorkTaskEnvironmentFile
        {
            TaskId = taskId,
            Values = values,
            Notes = "由模式转交自动生成的最小任务环境；总经理可在工作模式中继续补充 target_items、output_root、deliverables 和 constraints。"
        });
    }

    private string ResolveWorkspaceDirectory()
    {
        var tasksDirectory = Path.GetFullPath(_fileService.TasksDirectory);
        return Path.GetFullPath(Path.Combine(tasksDirectory, "..", ".."));
    }

    private HandoffRuntimeContext ResolveContext(HandoffRuntimeContext context)
    {
        var agent = !string.IsNullOrWhiteSpace(context.AgentId)
            ? _agentService.GetByName(context.AgentId)
            : null;
        agent ??= _agentService.GetDefaultOrFirst();

        var provider = !string.IsNullOrWhiteSpace(context.ProviderId)
            ? _providerService.GetById(context.ProviderId)
            : null;
        provider ??= _providerService.GetAll().FirstOrDefault(p => p.IsDefault)
            ?? _providerService.GetAll().FirstOrDefault();

        var model = !string.IsNullOrWhiteSpace(context.ModelId)
            ? _modelService.GetById(context.ModelId)
            : null;
        if (model is null && provider is not null)
        {
            var models = _modelService.GetByProviderId(provider.Id);
            model = models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            throw new InvalidOperationException("缺少当前会话，无法创建工作任务。");
        }

        if (string.IsNullOrWhiteSpace(context.WorkspaceId))
        {
            throw new InvalidOperationException("缺少当前工作区，无法创建工作任务。");
        }

        if (agent is null)
        {
            throw new InvalidOperationException("缺少可用智能体，无法创建工作任务。");
        }

        if (provider is null)
        {
            throw new InvalidOperationException("缺少可用 AI 提供商，无法创建工作任务。");
        }

        if (model is null)
        {
            throw new InvalidOperationException("缺少可用模型，无法创建工作任务。");
        }

        return context with
        {
            AgentId = agent.Id,
            ProviderId = provider.Id,
            ModelId = model.Id
        };
    }

    private List<AgentMention> ResolveMentions(
        IReadOnlyList<string>? mentionAgentIds,
        Func<IReadOnlyList<AgentMention>>? defaultMentions)
    {
        if (mentionAgentIds is { Count: > 0 })
        {
            return mentionAgentIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Select(id => _agentService.GetByName(id))
                .Where(static agent => agent is { IsEnabled: true })
                .Select(static agent => new AgentMention(agent!, -1, -1))
                .ToList();
        }

        return defaultMentions?.Invoke()
            .Where(static m => m.Agent.IsEnabled)
            .DistinctBy(static m => m.Agent.Id)
            .ToList() ?? [];
    }

    private static List<string>? ParseStringListArgument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(text, WorkModeJsonContext.Default.ListString);
                return parsed?
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item.Trim())
                    .ToList();
            }
            catch (JsonException)
            {
                // 回退到分隔符解析。
            }
        }

        return text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static (string? PlanJson, string? Error) NormalizePlanJson(string? planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson))
        {
            return (null, null);
        }

        var plan = JsonSerializer.Deserialize(planJson, WorkModeJsonContext.Default.WorkPlan);
        var validationError = PlanTools.ValidatePlan(plan);
        if (validationError is not null)
        {
            return (null, validationError);
        }

        return (JsonSerializer.Serialize(plan!, WorkModeJsonContext.Default.WorkPlan), null);
    }

    private static string? SerializeMentions(IReadOnlyList<AgentMention> mentions)
    {
        if (mentions.Count == 0)
        {
            return null;
        }

        var dto = mentions
            .Select(static m => new AgentMentionDto(m.Agent.Id, m.Agent.Name))
            .ToList();
        return JsonSerializer.Serialize(dto, WorkModeJsonContext.Default.ListAgentMentionDto);
    }

    private static string NormalizeTitle(string? title)
    {
        var normalized = title?.Trim() ?? string.Empty;
        return normalized.Length <= 30 ? normalized : normalized[..30];
    }

    private static string BuildHandoffSystemMessage(
        string sourceKind,
        string goal,
        bool hasPlan,
        string? sourceSummary)
    {
        var sourceName = string.Equals(sourceKind, "meeting", StringComparison.OrdinalIgnoreCase)
            ? "会议讨论"
            : "专家讨论";

        var summaryBlock = string.IsNullOrWhiteSpace(sourceSummary)
            ? string.Empty
            : $"""

            {sourceName}总结:
            {sourceSummary.Trim()}
            """;

        return hasPlan
            ? $"""
              以下方案已在{sourceName}中达成共识，当前工作任务已经预置结构化计划。
              目标: {goal}
              {summaryBlock}

              执行要求:
              1. 先调用 get_plan 查看已预置计划。
              2. 如果计划验收标准完整，直接调用 finalize_plan，不要重新调用 set_plan。
              3. 只有发现验收标准不齐或计划明显无法执行时，才重新调用 set_plan 修正。
              """
            : $"""
              以下目标来自{sourceName}，但本次转交没有提供结构化 planJson。
              目标: {goal}
              {summaryBlock}

              执行要求:
              1. 按工作模式正常流程继续：先快速形成可执行计划并调用 set_plan。
              2. 不要要求用户重复描述已经讨论过的目标；只能在关键信息缺失且无法合理推断时 ask_user。
              3. set_plan 后等待用户确认，再调用 finalize_plan。
              """;
    }
}
