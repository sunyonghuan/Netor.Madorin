using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Memory;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Memory;

/// <summary>
/// 在宿主构建 AI 上下文前主动请求长期记忆并注入指令片段。
/// </summary>
public sealed class LongMemoryContextProvider(
    IAppPaths appPaths,
    ILongMemorySupplyClient supplyClient,
    SystemSettingsService systemSettings,
    ILogger<LongMemoryContextProvider> logger) : AIContextProvider
{
    private const string EnabledKey = "memory.contextInjection.enabled";
    private const string MaxMemoryCountKey = "memory.contextInjection.maxMemoryCount";
    private const string MaxTokenBudgetKey = "memory.contextInjection.maxTokenBudget";
    private const string TimeoutMsKey = "memory.contextInjection.timeoutMs";
    private const string MinimumConfidenceKey = "memory.contextInjection.minimumConfidence";

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (!systemSettings.GetValue(EnabledKey, true))
        {
            return new AIContext();
        }

        var agentId = Normalize(context.Session?.StateBag.GetValue<string>("agentid"));
        if (agentId is null)
        {
            return new AIContext();
        }

            var timeoutMs = Math.Clamp(systemSettings.GetValue(TimeoutMsKey, 30_000), 50, 30_000);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var request = new MemoryContextSupplyRequest
            {
                AgentId = agentId,
                AgentName = Normalize(context.Session?.StateBag.GetValue<string>("agentname")),
                WorkspaceId = Normalize(appPaths.WorkspaceDirectory),
                WorkspaceDirectory = Normalize(appPaths.WorkspaceDirectory),
                SessionId = Normalize(context.Session?.StateBag.GetValue<string>("sessionid")),
                SessionTitle = Normalize(context.Session?.StateBag.GetValue<string>("sessiontitle")),
                TurnId = Normalize(context.Session?.StateBag.GetValue<string>("turnid")),
                MessageId = Normalize(context.Session?.StateBag.GetValue<string>("usermessageid")),
                Scenario = "chat",
                CurrentTask = Normalize(context.Session?.StateBag.GetValue<string>("currenttask")),
                RecentMessages = BuildRecentMessages(context),
                TriggerSource = "before-prompt",
                MaxMemoryCount = Math.Clamp(systemSettings.GetValue(MaxMemoryCountKey, 12), 1, 50),
                MaxTokenBudget = Math.Clamp(systemSettings.GetValue(MaxTokenBudgetKey, 1200), 128, 8000),
                TimeoutMs = timeoutMs,
                TraceId = Normalize(context.Session?.StateBag.GetValue<string>("traceid")) ?? Guid.NewGuid().ToString("N")
            };

            var package = await supplyClient.SupplyAsync(request, cts.Token).ConfigureAwait(false);
            if (package is null)
            {
                logger.LogDebug(
                    "长期记忆上下文供应为空：RequestId={RequestId}, AgentId={AgentId}, Workspace={WorkspaceId}, TraceId={TraceId}",
                    request.RequestId,
                    request.AgentId,
                    request.WorkspaceId,
                    request.TraceId);
                return new AIContext();
            }

            var minimumConfidence = systemSettings.GetValue(MinimumConfidenceKey, 0.2d);
            var instructions = LongMemoryPromptFormatter.Format(package, minimumConfidence);
            var suppliedCount = package.Items.Count;
            var eligibleCount = CountEligibleItems(package, minimumConfidence);
            var injectedCount = CountInjectedItems(package, minimumConfidence);
            logger.LogInformation(
                "长期记忆上下文供应完成：RequestId={RequestId}, AgentId={AgentId}, Workspace={WorkspaceId}, Supplied={SuppliedCount}, Eligible={EligibleCount}, Injected={InjectedCount}, Groups={GroupCount}, Confidence={Confidence:F4}, InstructionLength={InstructionLength}, TraceId={TraceId}",
                package.RequestId,
                request.AgentId,
                request.WorkspaceId,
                suppliedCount,
                eligibleCount,
                injectedCount,
                package.Groups.Count,
                package.Confidence,
                instructions.Length,
                request.TraceId);

            return string.IsNullOrWhiteSpace(instructions)
                ? new AIContext()
                : new AIContext { Instructions = instructions };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("长期记忆上下文供应超时，已降级为空上下文。");
            return new AIContext();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "长期记忆上下文供应失败，已降级为空上下文。");
            return new AIContext();
        }
    }

    private static IReadOnlyList<MemoryContextMessage> BuildRecentMessages(InvokingContext context)
    {
        var currentTask = Normalize(context.Session?.StateBag.GetValue<string>("currenttask"));
        if (currentTask is null)
        {
            return [];
        }

        // currenttask 就是用户最新一条消息，截断到 500 字符避免过长
        return
        [
            new MemoryContextMessage
            {
                MessageId = Normalize(context.Session?.StateBag.GetValue<string>("usermessageid")),
                Role = "user",
                Content = currentTask.Length > 500 ? currentTask[..500] : currentTask,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            }
        ];
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int CountEligibleItems(MemoryContextSupplyPackage package, double minimumConfidence)
    {
        return package.Items.Count(item => !string.IsNullOrWhiteSpace(item.Content) && item.Confidence >= minimumConfidence);
    }

    private static int CountInjectedItems(MemoryContextSupplyPackage package, double minimumConfidence)
    {
        return package.Groups
            .SelectMany(static group => group.Items)
            .Where(item => !string.IsNullOrWhiteSpace(item.Content) && item.Confidence >= minimumConfidence)
            .GroupBy(static item => item.Content.Trim(), StringComparer.OrdinalIgnoreCase)
            .Count();
    }
}
