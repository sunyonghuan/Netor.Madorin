using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Prompts;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 总经理 Agent 构建器。
/// 负责构建工作模式的主智能体（总经理），注入工作模式工具集。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §2.5。
/// </summary>
public sealed class GeneralManagerAgentBuilder
{
    private readonly AIAgentFactory _factory;
    private readonly WorkTaskService _taskService;
    private readonly WorkPendingInputService _pendingInputService;
    private readonly WorkPlanTemplateService _templateService;
    private readonly ChatMessageService _chatMessageService;
    private readonly WorkTaskFileService _fileService;
    private readonly WorkExecutionLogService _logService;
    private readonly ProjectLeadService _projectLeadService;
    private readonly ILogger<ProjectLeadTools> _projectLeadToolsLogger;
    private readonly WorkTaskCancellationRegistry _cancellationRegistry;
    private readonly WorkTaskTitleService? _titleService;
    private readonly IWorkTaskReportCompactionService? _reportCompactionService;
    private readonly IPublisher _publisher;
    private readonly IPromptProvider _promptProvider;

    public GeneralManagerAgentBuilder(
        AIAgentFactory factory,
        WorkTaskService taskService,
        WorkPendingInputService pendingInputService,
        WorkPlanTemplateService templateService,
        ChatMessageService chatMessageService,
        WorkTaskFileService fileService,
        WorkExecutionLogService logService,
        ProjectLeadService projectLeadService,
        ILogger<ProjectLeadTools> projectLeadToolsLogger,
        WorkTaskCancellationRegistry cancellationRegistry,
        WorkTaskTitleService? titleService,
        IWorkTaskReportCompactionService? reportCompactionService,
        IPublisher publisher,
        IPromptProvider promptProvider)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _pendingInputService = pendingInputService ?? throw new ArgumentNullException(nameof(pendingInputService));
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _chatMessageService = chatMessageService ?? throw new ArgumentNullException(nameof(chatMessageService));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _projectLeadService = projectLeadService ?? throw new ArgumentNullException(nameof(projectLeadService));
        _projectLeadToolsLogger = projectLeadToolsLogger ?? throw new ArgumentNullException(nameof(projectLeadToolsLogger));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _titleService = titleService;
        _reportCompactionService = reportCompactionService;
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _promptProvider = promptProvider ?? throw new ArgumentNullException(nameof(promptProvider));
    }

    public async Task<Microsoft.Agents.AI.AIAgent> BuildAsync(
        AgentEntity agent,
        AiProviderEntity provider,
        AiModelEntity model,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(taskId);

        var toolset = new WorkModeToolset(
            _taskService,
            _pendingInputService,
            _templateService,
            _chatMessageService,
            _fileService,
            _logService,
            _projectLeadService,
            _projectLeadToolsLogger,
            _publisher,
            _cancellationRegistry,
            _titleService,
            _reportCompactionService,
            taskId);
        var workModeTools = toolset.GetAllTools();

        // 克隆 agent 并替换为工作模式专用提示词
        var gmAgent = await CloneWithGmPromptAsync(agent, cancellationToken);

        return _factory.Build(
            gmAgent,
            provider,
            model,
            workModeTools,
            toolFilterMode: ToolFilterMode.WorkModeManager,
            enableChatHistory: false,
            skipAgentBoundTools: true);
    }

    public async Task<Microsoft.Agents.AI.AIAgent> BuildWithSubAgentsAsync(
        AgentEntity mainAgent,
        AiProviderEntity mainProvider,
        AiModelEntity mainModel,
        string taskId,
        List<AgentMention> mentions,
        AiProviderService providerService,
        AiModelService modelService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mainAgent);
        ArgumentNullException.ThrowIfNull(mainProvider);
        ArgumentNullException.ThrowIfNull(mainModel);
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(mentions);

        var toolset = new WorkModeToolset(
            _taskService,
            _pendingInputService,
            _templateService,
            _chatMessageService,
            _fileService,
            _logService,
            _projectLeadService,
            _projectLeadToolsLogger,
            _publisher,
            _cancellationRegistry,
            _titleService,
            _reportCompactionService,
            taskId);
        var workModeTools = toolset.GetAllTools();

        var gmAgent = await CloneWithGmPromptAsync(mainAgent, cancellationToken);

        return _factory.Build(
            gmAgent,
            mainProvider,
            mainModel,
            workModeTools,
            toolFilterMode: ToolFilterMode.WorkModeManager,
            skipAgentBoundTools: true,
            enableChatHistory: false);
    }

    private async Task<AgentEntity> CloneWithGmPromptAsync(AgentEntity source, CancellationToken cancellationToken)
    {
        var prompt = await _promptProvider.GetPromptAsync("work_mode.general_manager", cancellationToken)
            ?? "你是工作模式的总经理智能体。请先与用户确认需求，再制定计划并执行。";

        return new AgentEntity
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Instructions = prompt,
            IsEnabled = source.IsEnabled,
            SortOrder = source.SortOrder,
            BoundPlugins = [],
            BoundMcp = [],
            AllowWorkflowMemory = source.AllowWorkflowMemory,
        };
    }
}
