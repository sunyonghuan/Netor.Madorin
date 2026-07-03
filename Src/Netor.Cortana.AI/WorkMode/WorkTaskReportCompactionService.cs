using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.AI.WorkMode.Prompts;
using Netor.Cortana.Entitys.Services;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作任务最终汇报缩略服务。
/// 仅在 final_report 返回给总经理前调用，避免把大量步骤摘要原文直接展示给用户。
/// </summary>
public sealed class WorkTaskReportCompactionService : IWorkTaskReportCompactionService
{
    private const int DefaultCompactionThreshold = 2400;

    private readonly AIAgentFactory _agentFactory;
    private readonly WorkTaskService _taskService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly AgentService _agentService;
    private readonly IChatCompactionClientResolver _clientResolver;
    private readonly IPromptProvider _promptProvider;
    private readonly ILogger<WorkTaskReportCompactionService> _logger;

    public WorkTaskReportCompactionService(
        AIAgentFactory agentFactory,
        WorkTaskService taskService,
        AiProviderService providerService,
        AiModelService modelService,
        AgentService agentService,
        IChatCompactionClientResolver clientResolver,
        IPromptProvider promptProvider,
        ILogger<WorkTaskReportCompactionService> logger)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));
        _promptProvider = promptProvider ?? throw new ArgumentNullException(nameof(promptProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> CompactAsync(string taskId, string rawReport, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        if (string.IsNullOrWhiteSpace(rawReport) || rawReport.Length < DefaultCompactionThreshold)
        {
            return rawReport;
        }

        try
        {
            var task = _taskService.GetById(taskId);
            if (task is null)
            {
                return rawReport;
            }

            var provider = _providerService.GetById(task.Provider);
            var model = _modelService.GetById(task.Model);
            var agent = _agentService.GetByName(task.AgentName);
            if (provider is null || model is null || agent is null)
            {
                return rawReport;
            }

            var aiAgent = _agentFactory.Build(agent, provider, model);
            var client = _clientResolver.Resolve(aiAgent);
            if (client is null)
            {
                return rawReport;
            }

            var prompt = await BuildPromptAsync(rawReport, cancellationToken).ConfigureAwait(false);
            using var _ = (client as TokenTrackingChatClient)?.SuppressUsage();
            var completion = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            var compacted = completion?.Text?.Trim();
            return string.IsNullOrWhiteSpace(compacted) ? rawReport : compacted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作任务 {TaskId} 汇报缩略失败，回退原文。", taskId);
            return rawReport;
        }
    }

    private async Task<List<AIChatMessage>> BuildPromptAsync(string rawReport, CancellationToken cancellationToken)
    {
        var systemPrompt = await _promptProvider
                .GetPromptAsync("work_mode.report_compaction", cancellationToken)
                .ConfigureAwait(false)
            ?? """
               你是工作任务汇报缩略助手。
               请把冗长的执行汇报压缩为适合老板快速浏览的短版总结。
               必须保留：当前状态、完成进度、关键产出、失败/阻塞、下一步建议。
               不要逐条复述所有步骤，不要大段照抄原文。
               """;

        return
        [
            new AIChatMessage(ChatRole.System, systemPrompt),
            new AIChatMessage(ChatRole.User, rawReport),
            new AIChatMessage(ChatRole.User, "请输出短版工作总结。")
        ];
    }
}
