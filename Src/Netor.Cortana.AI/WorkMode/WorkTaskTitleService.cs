using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作任务标题生成服务。
/// 任务创建后生成初稿标题，任务完成后基于最终报告生成定稿标题。
/// </summary>
public sealed class WorkTaskTitleService
{
    private readonly AIAgentFactory _agentFactory;
    private readonly WorkTaskService _taskService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly AgentService _agentService;
    private readonly IChatCompactionClientResolver _clientResolver;
    private readonly IPublisher _publisher;
    private readonly ILogger<WorkTaskTitleService> _logger;

    public WorkTaskTitleService(
        AIAgentFactory agentFactory,
        WorkTaskService taskService,
        AiProviderService providerService,
        AiModelService modelService,
        AgentService agentService,
        IChatCompactionClientResolver clientResolver,
        IPublisher publisher,
        ILogger<WorkTaskTitleService> logger)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task GenerateInitialTitleAsync(string taskId, CancellationToken cancellationToken = default)
        => GenerateAndUpdateAsync(taskId, finalReport: null, isFinal: false, cancellationToken);

    public Task GenerateFinalTitleAsync(string taskId, string? finalReport, CancellationToken cancellationToken = default)
        => GenerateAndUpdateAsync(taskId, finalReport, isFinal: true, cancellationToken);

    private async Task GenerateAndUpdateAsync(
        string taskId,
        string? finalReport,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = _taskService.GetById(taskId);
            if (task is null)
            {
                return;
            }

            var provider = _providerService.GetById(task.Provider);
            var model = _modelService.GetById(task.Model);
            var agent = _agentService.GetByName(task.AgentName);
            if (provider is null || model is null || agent is null)
            {
                return;
            }

            var aiAgent = _agentFactory.Build(agent, provider, model);
            var client = _clientResolver.Resolve(aiAgent);
            if (client is null)
            {
                return;
            }

            var prompt = BuildPrompt(task, finalReport, isFinal);
            using var _ = (client as TokenTrackingChatClient)?.SuppressUsage();
            var completion = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            var title = NormalizeTitle(completion?.Text);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            _taskService.UpdateTitle(task.Id, title);
            await _publisher.PublishAsync(
                Events.OnWorkTaskTitleUpdated,
                new WorkTaskTitleUpdatedArgs(task.Id, title)).ConfigureAwait(false);

            _logger.LogInformation("工作任务 {TaskId} 标题已生成：{Title}", task.Id, title);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成工作任务 {TaskId} 标题失败，保留现有标题。", taskId);
        }
    }

    private static List<AIChatMessage> BuildPrompt(WorkTaskEntity task, string? finalReport, bool isFinal)
    {
        var stage = isFinal ? "最终标题" : "初稿标题";
        var user = $"""
            请为这个工作任务生成{stage}。

            初始需求：
            {task.InitialInput.Truncate(600)}

            工作计划：
            {(task.CurrentPlanJson ?? "暂无").Truncate(1200)}

            最终报告：
            {(finalReport ?? task.FinalReport ?? "暂无").Truncate(1200)}
            """;

        return
        [
            new AIChatMessage(ChatRole.System, """
                你是工作任务标题生成助手。
                要求：
                1. 输出 12 到 24 个中文字符。
                2. 不要引号，不要标点，不要解释。
                3. 优先体现任务目标、交付物或业务对象。
                4. 避免使用“关于”“处理”“任务”“工作模式”等空泛词。
                """),
            new AIChatMessage(ChatRole.User, user),
        ];
    }

    private static string? NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var title = value
            .Trim()
            .Trim('"', '“', '”', '「', '」', '\'', '`')
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        return string.IsNullOrWhiteSpace(title) ? null : title.Truncate(32);
    }
}
