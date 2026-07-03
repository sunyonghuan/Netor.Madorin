using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Hitl;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Hitl;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作模式工具集，提供总经理 Agent 使用的所有工具。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §3。
/// </summary>
public sealed class WorkModeToolset
{
    private readonly WorkTaskService _taskService;
    private readonly WorkPendingInputService _pendingInputService;
    private readonly WorkPlanTemplateService _templateService;
    private readonly ChatMessageService _chatMessageService;
    private readonly WorkTaskFileService _fileService;
    private readonly WorkExecutionLogService _logService;
    private readonly ProjectLeadService _projectLeadService;
    private readonly ILogger<ProjectLeadTools> _projectLeadToolsLogger;
    private readonly IPublisher _publisher;
    private readonly WorkTaskCancellationRegistry _cancellationRegistry;
    private readonly WorkTaskTitleService? _titleService;
    private readonly IWorkTaskReportCompactionService? _reportCompactionService;
    private readonly string _taskId;

    public WorkModeToolset(
        WorkTaskService taskService,
        WorkPendingInputService pendingInputService,
        WorkPlanTemplateService templateService,
        ChatMessageService chatMessageService,
        WorkTaskFileService fileService,
        WorkExecutionLogService logService,
        ProjectLeadService projectLeadService,
        ILogger<ProjectLeadTools> projectLeadToolsLogger,
        IPublisher publisher,
        WorkTaskCancellationRegistry cancellationRegistry,
        WorkTaskTitleService? titleService,
        IWorkTaskReportCompactionService? reportCompactionService,
        string taskId)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _pendingInputService = pendingInputService ?? throw new ArgumentNullException(nameof(pendingInputService));
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _chatMessageService = chatMessageService ?? throw new ArgumentNullException(nameof(chatMessageService));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _projectLeadService = projectLeadService ?? throw new ArgumentNullException(nameof(projectLeadService));
        _projectLeadToolsLogger = projectLeadToolsLogger ?? throw new ArgumentNullException(nameof(projectLeadToolsLogger));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _titleService = titleService;
        _reportCompactionService = reportCompactionService;
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public IReadOnlyList<AIFunction> GetAllTools()
    {
        var tools = new List<AIFunction>();

        // 阶段 1：计划工具
        var planTools = new PlanTools(_taskService, _fileService, _publisher, _taskId, _titleService, _reportCompactionService);
        tools.Add(planTools.CreateSetPlanTool());
        tools.Add(planTools.CreateGetPlanTool());
        tools.Add(planTools.CreateFinalReportTool());

        // 三层架构阶段 1：任务环境文件，供后续 ProjectLeadService/C 专员读取。
        var environmentTools = new EnvironmentTools(_fileService, _taskId);
        tools.Add(environmentTools.CreateSetEnvironmentTool());

        var projectLeadTools = new ProjectLeadTools(
            _fileService,
            _projectLeadService,
            _projectLeadToolsLogger,
            _taskId,
            _taskService,
            _cancellationRegistry);
        tools.Add(projectLeadTools.CreateFinalizePlanTool());
        tools.Add(projectLeadTools.CreatePauseOrchestratorTool());
        tools.Add(projectLeadTools.CreateResumeOrchestratorTool());
        tools.Add(projectLeadTools.CreateCancelOrchestratorTool());
        tools.Add(projectLeadTools.CreateUpdatePlanTool());

        // 阶段 4：计划模板复用
        var templateTools = new PlanTemplateTools(_taskService, _templateService, _fileService, _publisher, _taskId);
        tools.Add(templateTools.CreateListPlanTemplatesTool());
        tools.Add(templateTools.CreateLoadPlanFromTemplateTool());
        tools.Add(templateTools.CreateSaveCurrentPlanAsTemplateTool());

        // 阶段 4：从对话历史提取计划参考
        var chatHistoryPlanTools = new ChatHistoryPlanTools(_taskService, _chatMessageService, _taskId);
        tools.Add(chatHistoryPlanTools.CreateLoadPlanFromChatHistoryTool());
        tools.Add(chatHistoryPlanTools.CreateLoadPlanFromGroupChatTool());

        // 阶段 5：最近完成任务复用
        var recentTaskTools = new RecentTaskTools(_taskService, _taskId);
        tools.Add(recentTaskTools.CreateGetRecentCompletedTaskPlanTool());

        // 当前任务执行痕迹只读查询，供总经理在用户追问过程/证据时查看 B/C 实际日志。
        var executionLogTools = new ExecutionLogTools(_logService, _taskId);
        tools.Add(executionLogTools.CreateGetExecutionLogsTool());

        // 阶段 3 实现：HITL 工具
        var hitlTools = new HitlTools(
            new WorkTaskHitlContext(_taskService, _taskId),
            new WorkHitlNotifier(_publisher));
        tools.Add(hitlTools.CreateAskUserTool());

        // 阶段 3 实现：软抢占输入检查
        var pendingInputTools = new PendingInputTools(_taskService, _pendingInputService, _taskId);
        tools.Add(pendingInputTools.CreateCheckPendingUserInputTool());

        // 阶段 5：任务控制
        var taskControlTools = new TaskControlTools(_taskService, _publisher, _cancellationRegistry, _taskId);
        tools.Add(taskControlTools.CreateCancelTaskTool());
        tools.Add(taskControlTools.CreateCloseTaskRecordTool());

        return tools;
    }
}
